using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuickSearch.App.Services;
using QuickSearch.Core.Autostart;
using QuickSearch.Core.Browser;
using QuickSearch.Core.Settings;
using MessageBox = System.Windows.MessageBox;

namespace QuickSearch.App.Views;

public partial class SettingsWindow : Window
{
    private const string SystemDefaultBrowserTag = "";

    private readonly SettingsService? _settingsService;
    private readonly AutostartManager _autostartManager;
    private readonly BrowserManager _browserManager = new();
    private readonly ObservableCollection<DriveChoice> _drives = new();
    private string? _backgroundPath;
    private bool _backgroundChanged;

    // Animation state
    private FrameworkElement[] _cards = Array.Empty<FrameworkElement>();
    private System.Windows.Controls.RadioButton[] _navItems = Array.Empty<System.Windows.Controls.RadioButton>();
    private bool _allowClose;
    private bool _isClosingAnimated;
    private bool _isScrollAnimating;
    private double _scrollFrom;
    private double _scrollTo;
    private DateTime _scrollStarted;
    private DateTime _suppressNavSyncUntil;
    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(520);

    public SettingsWindow()
    {
        InitializeComponent();

        _settingsService = App.GetSettingsService();
        _autostartManager = new AutostartManager();
        DrivesList.ItemsSource = _drives;

        LoadSettings();

        _cards = new FrameworkElement[] { CardDrives, CardAudio, CardBackground, CardInterface, CardWeb };
        _navItems = new[] { NavDrives, NavAudio, NavBackground, NavInterface, NavWeb };

        AudioColorComboBox.SelectionChanged += (_, _) => UpdateEqualizerPreviewColor();
        UpdateEqualizerPreviewColor();

        Loaded += OnLoadedAnimate;
        Closing += OnClosingAnimate;
        Closed += (_, _) => StopScrollAnimation();
        KeyDown += OnWindowKeyDown;
        Scroller.ScrollChanged += (_, _) => SyncNavWithScroll();
    }

    #region Animations

    private static DoubleAnimation Anim(double to, double ms, double delayMs = 0, IEasingFunction? ease = null) =>
        new(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut }
        };

    /// <summary>Window pops in, then the sidebar and cards slide in one after another.</summary>
    private void OnLoadedAnimate(object sender, RoutedEventArgs e)
    {
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        Shell.BeginAnimation(OpacityProperty, Anim(1, 260));
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 420, 0, pop));
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 420, 0, pop));
        ShellShift.BeginAnimation(TranslateTransform.YProperty, Anim(0, 420));

        for (var i = 0; i < _navItems.Length; i++)
            SlideIn(_navItems[i], -18, 0, 140 + i * 55);

        SlideIn(Footer, 0, 14, 260);

        for (var i = 0; i < _cards.Length; i++)
            SlideIn(_cards[i], 0, 28, 160 + i * 80);
    }

    private static void SlideIn(FrameworkElement element, double fromX, double fromY, double delayMs)
    {
        var shift = new TranslateTransform(fromX, fromY);
        element.RenderTransform = shift;
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, Anim(1, 380, delayMs));
        if (fromX != 0) shift.BeginAnimation(TranslateTransform.XProperty, Anim(0, 480, delayMs));
        if (fromY != 0) shift.BeginAnimation(TranslateTransform.YProperty, Anim(0, 480, delayMs));
    }

    /// <summary>Fades/shrinks the window, then really closes it with the given dialog result.</summary>
    private void CloseAnimated(bool result)
    {
        if (_isClosingAnimated) return;
        _isClosingAnimated = true;
        IsHitTestVisible = false;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = Anim(0, 170, 0, ease);
        fade.Completed += (_, _) =>
        {
            _allowClose = true;
            try { DialogResult = result; }      // closes a dialog
            catch (InvalidOperationException) { Close(); } // shown non-modally
        };
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.95, 170, 0, ease));
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.95, 170, 0, ease));
        ShellShift.BeginAnimation(TranslateTransform.YProperty, Anim(10, 170, 0, ease));
        Shell.BeginAnimation(OpacityProperty, fade);
    }

    private void OnClosingAnimate(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;          // Alt+F4 etc. — play the close animation first
        CloseAnimated(false);
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseAnimated(false);
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            SaveButton_Click(this, new RoutedEventArgs());
        }
    }

    private void UpdateEqualizerPreviewColor()
    {
        var hex = (AudioColorComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "#38BDF8";
        System.Windows.Media.Color color;
        try { color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); }
        catch { color = System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8); }

        foreach (var bar in new[] { Eq1, Eq2, Eq3, Eq4, Eq5 })
        {
            var from = (bar.Fill as SolidColorBrush)?.Color ?? color;
            var brush = new SolidColorBrush(from);
            bar.Fill = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(color, TimeSpan.FromMilliseconds(350)));
        }
    }

    private double CardTop(int index)
    {
        if (index <= 0) return 0;
        var top = _cards[index].TranslatePoint(new System.Windows.Point(0, 0), CardsPanel).Y;
        if (_cards[index].RenderTransform is TranslateTransform t) top -= t.Y; // ignore slide-in offset
        return top;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var index = Array.IndexOf(_navItems, sender);
        if (index < 0) return;
        _navItems[index].IsChecked = true;
        _suppressNavSyncUntil = DateTime.UtcNow + ScrollDuration + TimeSpan.FromMilliseconds(250);
        AnimateScrollTo(CardTop(index));
    }

    /// <summary>Smooth eased scroll (ScrollViewer offsets are not animatable directly).</summary>
    private void AnimateScrollTo(double target)
    {
        _scrollFrom = Scroller.VerticalOffset;
        _scrollTo = Math.Clamp(target, 0, Scroller.ScrollableHeight);
        _scrollStarted = DateTime.UtcNow;
        if (_isScrollAnimating) return;
        _isScrollAnimating = true;
        CompositionTarget.Rendering += OnScrollFrame;
    }

    private void OnScrollFrame(object? sender, EventArgs e)
    {
        var t = Math.Min(1.0, (DateTime.UtcNow - _scrollStarted).TotalMilliseconds / ScrollDuration.TotalMilliseconds);
        var eased = 1 - Math.Pow(1 - t, 4); // quartic ease-out
        Scroller.ScrollToVerticalOffset(_scrollFrom + (_scrollTo - _scrollFrom) * eased);
        if (t >= 1) StopScrollAnimation();
    }

    private void StopScrollAnimation()
    {
        if (!_isScrollAnimating) return;
        _isScrollAnimating = false;
        CompositionTarget.Rendering -= OnScrollFrame;
    }

    /// <summary>Highlights the sidebar item of the section currently at the top of the list.</summary>
    private void SyncNavWithScroll()
    {
        if (_cards.Length == 0 || _isScrollAnimating || DateTime.UtcNow < _suppressNavSyncUntil) return;

        var offset = Scroller.VerticalOffset;
        var index = 0;
        if (Scroller.ScrollableHeight > 0 && offset >= Scroller.ScrollableHeight - 2)
            index = _cards.Length - 1;
        else
            for (var i = 0; i < _cards.Length; i++)
                if (CardTop(i) <= offset + 80) index = i;

        if (_navItems[index].IsChecked != true) _navItems[index].IsChecked = true;
    }

    #endregion

    private void LoadSettings()
    {
        if (_settingsService == null) return;

        var settings = _settingsService.Settings;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            _drives.Add(new DriveChoice
            {
                Path = drive.RootDirectory.FullName,
                Label = $"{drive.Name}    {drive.VolumeLabel}    ·    {drive.TotalSize / (1024L * 1024 * 1024)} GB",
                IsSelected = settings.IndexedDrives.Contains(drive.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase)
            });

        // Window size
        if (settings.WindowWidth <= 600)
            SmallSizeRadio.IsChecked = true;
        else if (settings.WindowWidth >= 1000)
            LargeSizeRadio.IsChecked = true;
        else
            MediumSizeRadio.IsChecked = true;

        // Now Playing widget
        ShowNowPlayingCheckBox.IsChecked = settings.ShowNowPlaying;
        ShowAudioBarsCheckBox.IsChecked = settings.ShowAudioBars;
        KeepAudioBarsVisibleCheckBox.IsChecked = settings.KeepAudioBarsVisible;
        AudioColorComboBox.SelectedItem = AudioColorComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, settings.AudioBarColor, StringComparison.OrdinalIgnoreCase))
            ?? AudioColorComboBox.Items[0];
        AudioCountComboBox.SelectedItem = AudioCountComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == settings.AudioBarCount.ToString())
            ?? AudioCountComboBox.Items[2];

        // Decoration-only equalizer overlay
        OverlayEnabledCheckBox.IsChecked = settings.EqualizerOverlayEnabled;
        OverlayOptionsPanel.IsEnabled = settings.EqualizerOverlayEnabled;
        OverlayTopmostCheckBox.IsChecked = string.Equals(settings.EqualizerOverlayMode, "Topmost", StringComparison.OrdinalIgnoreCase);
        OverlayWidthComboBox.SelectedItem = OverlayWidthComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == settings.EqualizerOverlayWidthPercent.ToString())
            ?? OverlayWidthComboBox.Items[3];

        // Wallpaper
        _backgroundPath = settings.BackgroundImagePath;
        BackgroundDimComboBox.SelectedItem = BackgroundDimComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == settings.BackgroundDimPercent.ToString())
            ?? BackgroundDimComboBox.Items[1];
        BackgroundDimComboBox.SelectionChanged += (_, _) => ApplyBackgroundPreview();
        ApplyBackgroundPreview();

        // Load auto-start setting
        AutoStartCheckBox.IsChecked = _autostartManager.IsEnabled();

        // Load search engine
        SearchEngineComboBox.SelectedIndex = settings.SearchEngine.ToLower() switch
        {
            "google" => 0,
            "bing" => 1,
            "duckduckgo" => 2,
            "yandex" => 3,
            _ => 0
        };

        // Load preferred browser
        PopulateBrowserComboBox(settings.DefaultBrowser);
    }

    private void PopulateBrowserComboBox(string? selectedPath)
    {
        BrowserComboBox.Items.Clear();
        BrowserComboBox.Items.Add(new ComboBoxItem { Content = "System Default", Tag = SystemDefaultBrowserTag });

        List<BrowserInfo> browsers;
        try
        {
            browsers = _browserManager.GetInstalledBrowsers();
        }
        catch
        {
            browsers = new List<BrowserInfo>();
        }

        foreach (var browser in browsers.OrderBy(b => b.Name))
        {
            BrowserComboBox.Items.Add(new ComboBoxItem { Content = browser.Name, Tag = browser.ExecutablePath });
        }

        var matchIndex = 0;
        if (!string.IsNullOrEmpty(selectedPath))
        {
            for (var i = 0; i < BrowserComboBox.Items.Count; i++)
            {
                if (BrowserComboBox.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag as string, selectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    break;
                }
            }
        }

        BrowserComboBox.SelectedIndex = matchIndex;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsCore();
        }
        catch (Exception ex)
        {
            App.LogError("SettingsWindow.SaveButton_Click", ex);
            MessageBox.Show(
                "Не удалось сохранить настройки из-за непредвиденной ошибки. Подробности записаны в crash.log.",
                "QuickSearch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveSettingsCore()
    {
        if (_settingsService == null) return;

        var (width, height) = SmallSizeRadio.IsChecked == true
            ? (600, 400)
            : LargeSizeRadio.IsChecked == true
                ? (1000, 700)
                : (800, 600);

        var selectedBrowserPath = (BrowserComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var selectedDrives = _drives.Where(d => d.IsSelected).Select(d => d.Path).ToList();
        if (selectedDrives.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы один диск для поиска.", "QuickSearch");
            return;
        }
        var previousDrives = _settingsService.Settings.IndexedDrives.ToList();

        var savedBackground = _settingsService.Settings.BackgroundImagePath;
        if (_backgroundChanged)
            savedBackground = PersistBackground(_backgroundPath, savedBackground);

        _settingsService.UpdateSettings(settings =>
        {
            settings.WindowWidth = width;
            settings.WindowHeight = height;
            settings.ShowNowPlaying = ShowNowPlayingCheckBox.IsChecked == true;

            settings.SearchEngine = SearchEngineComboBox.SelectedIndex switch
            {
                0 => "google",
                1 => "bing",
                2 => "duckduckgo",
                3 => "yandex",
                _ => "google"
            };

            settings.DefaultBrowser = string.IsNullOrEmpty(selectedBrowserPath) ? null : selectedBrowserPath;
            settings.IndexedDrives = selectedDrives;
            settings.ShowAudioBars = ShowAudioBarsCheckBox.IsChecked == true;
            settings.KeepAudioBarsVisible = KeepAudioBarsVisibleCheckBox.IsChecked == true;
            settings.AudioBarColor = (AudioColorComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "#38BDF8";
            settings.AudioBarCount = int.TryParse((AudioCountComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out var count) ? count : 48;
            settings.EqualizerOverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
            settings.EqualizerOverlayMode = OverlayTopmostCheckBox.IsChecked == true ? "Topmost" : "Desktop";
            settings.EqualizerOverlayWidthPercent = int.TryParse((OverlayWidthComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out var widthPercent) ? widthPercent : 92;
            settings.BackgroundImagePath = savedBackground;
            settings.BackgroundDimPercent = SelectedDimPercent();
        });
        if (AutoStartCheckBox.IsChecked == true) _autostartManager.Enable();
        else _autostartManager.Disable();
        if (!previousDrives.SequenceEqual(selectedDrives, StringComparer.OrdinalIgnoreCase))
            (System.Windows.Application.Current as App)?.RefreshIndexing();

        // Apply the new size (and refreshed search engine/browser) to the running search
        // window right away, instead of requiring a restart.
        (Owner as MainSearchWindow)?.ApplySettings();

        // And the decoration equalizer: show/hide/repin without a restart.
        (System.Windows.Application.Current as App)?.RefreshOverlaySettings();

        CloseAnimated(true);
    }

    private int SelectedDimPercent() =>
        int.TryParse((BackgroundDimComboBox.SelectedItem as ComboBoxItem)?.Tag as string, out var dim) ? dim : 75;

    /// <summary>Shows the chosen wallpaper on this window right away (saved only on "Сохранить").</summary>
    private void ApplyBackgroundPreview()
    {
        var brush = AppBackground.TryCreate(_backgroundPath, SelectedDimPercent());
        if (brush != null)
        {
            RootBorder.Background = brush;
            BackgroundNameText.Text = System.IO.Path.GetFileName(_backgroundPath);
        }
        else
        {
            RootBorder.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xE6, 0x0A, 0x0E, 0x15));
            BackgroundNameText.Text = string.IsNullOrEmpty(_backgroundPath)
                ? "Картинка не выбрана"
                : "Не удалось открыть картинку";
        }
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите картинку для фона",
            Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|Все файлы|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        _backgroundPath = dialog.FileName;
        _backgroundChanged = true;
        ApplyBackgroundPreview();
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        _backgroundPath = null;
        _backgroundChanged = true;
        ApplyBackgroundPreview();
    }

    /// <summary>
    /// Copies the wallpaper into the app's data folder so it keeps working even if the
    /// original is moved or deleted; removes the previously saved copy.
    /// </summary>
    private static string? PersistBackground(string? sourcePath, string? previousSaved)
    {
        try
        {
            string? result = null;
            if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickSearch");
                Directory.CreateDirectory(dir);
                result = Path.Combine(dir, $"background_{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(sourcePath)}");
                File.Copy(sourcePath, result, overwrite: true);
            }

            if (!string.IsNullOrEmpty(previousSaved) && !string.Equals(previousSaved, result, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(previousSaved); } catch { }
            }
            return result;
        }
        catch (Exception ex)
        {
            App.LogError("PersistBackground", ex);
            return previousSaved;
        }
    }

    private void OverlayEnabled_Changed(object sender, RoutedEventArgs e)
    {
        OverlayOptionsPanel.IsEnabled = OverlayEnabledCheckBox.IsChecked == true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CloseAnimated(false);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseAnimated(false);

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void ResetBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;

        _settingsService.UpdateSettings(settings =>
        {
            settings.DefaultBrowser = null;
        });

        BrowserComboBox.SelectedIndex = 0;

        MessageBox.Show("Browser choice has been reset.", "QuickSearch", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed class DriveChoice
    {
        public string Path { get; set; } = "";
        public string Label { get; set; } = "";
        public bool IsSelected { get; set; }
    }
}

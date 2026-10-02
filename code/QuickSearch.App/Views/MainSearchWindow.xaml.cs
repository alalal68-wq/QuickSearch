using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickSearch.App.Services;
using QuickSearch.App.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QuickSearch.App.Views;

public partial class MainSearchWindow : Window
{
    public static readonly DependencyProperty ResultsMaxHeightProperty =
        DependencyProperty.Register(nameof(ResultsMaxHeight), typeof(double), typeof(MainSearchWindow), new PropertyMetadata(336.0));

    /// <summary>Max height of the results panel below the search bar; driven by the Window Size setting.</summary>
    public double ResultsMaxHeight
    {
        get => (double)GetValue(ResultsMaxHeightProperty);
        set => SetValue(ResultsMaxHeightProperty, value);
    }

    private const double BottomMargin = 24; // gap above the taskbar

    private MainSearchViewModel ViewModel => (MainSearchViewModel)DataContext;
    private AudioLevelMonitor? _audioMonitor;
    private readonly DispatcherTimer _audioTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly List<Border> _audioBars = new();
    private bool _allowHide = false; // Don't hide on first launch

    public MainSearchWindow()
    {
        InitializeComponent();
        SizeChanged += (s, e) => RepositionWindow();
        _audioTimer.Tick += (_, _) => PaintAudioBars();
        ApplySettings();

        // Allow hiding only after a short delay so the window is visible on startup
        var startupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        startupTimer.Tick += (_, _) => { _allowHide = true; startupTimer.Stop(); };
        startupTimer.Start();
    }

    /// <summary>
    /// Applies the current Window Size setting to the app (bar width stays fixed;
    /// the results panel height is capped, and the whole thing grows/shrinks
    /// automatically via SizeToContent as results appear/disappear).
    /// Call this again after the Settings dialog is saved.
    /// </summary>
    public void ApplySettings()
    {
        var settings = App.GetSettingsService()?.Settings;
        if (settings == null) return;

        Width = settings.WindowWidth;
        // Reserve space for the bar itself plus the margin between bar and results.
        ResultsMaxHeight = Math.Max(160, settings.WindowHeight - 80);

        RepositionWindow();

        ViewModel?.RefreshFromSettings();
        ConfigureAudioBars(settings);
        ApplyBackground(settings);
    }

    /// <summary>Wallpaper + dark layer on the search bar and results panel (plain dark if none).</summary>
    private void ApplyBackground(QuickSearch.Core.Settings.AppSettings settings)
    {
        var brush = AppBackground.TryCreate(settings.BackgroundImagePath, settings.BackgroundDimPercent)
                    ?? AppBackground.DefaultPanel;
        SearchBarBorder.Background = brush;
        ResultsPanel.Background = brush;
    }

    private void ConfigureAudioBars(QuickSearch.Core.Settings.AppSettings settings)
    {
        AudioBarsPanel.Children.Clear();
        _audioBars.Clear();

        // Always show container if KeepAudioBarsVisible is true, otherwise show only when ShowAudioBars is true
        if (settings.KeepAudioBarsVisible)
        {
            AudioBarsContainer.Visibility = Visibility.Visible;
        }
        else
        {
            AudioBarsContainer.Visibility = settings.ShowAudioBars ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!settings.ShowAudioBars)
        {
            _audioTimer.Stop();
            _audioMonitor?.Stop();
            return;
        }

        var color = System.Windows.Media.Colors.DeepSkyBlue;
        try { color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(settings.AudioBarColor); } catch { }
        var brush = new SolidColorBrush(color);
        var count = Math.Clamp(settings.AudioBarCount, 12, 64);

        // Calculate bar width to fill the entire container
        var totalWidth = Width - 16; // Account for padding
        var barSpacing = 2;
        var barWidth = (totalWidth - (count - 1) * barSpacing) / count;
        AudioBarsPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;

        for (var i = 0; i < count; i++)
        {
            var bar = new Border
            {
                Width = Math.Max(2, barWidth),
                Height = 4,
                Margin = new Thickness(barSpacing / 2.0, 0, barSpacing / 2.0, 0),
                CornerRadius = new CornerRadius(2),
                Background = brush,
                Opacity = 0.9,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom
            };
            _audioBars.Add(bar);
            AudioBarsPanel.Children.Add(bar);
        }

        _audioMonitor?.Dispose();
        _audioMonitor = new AudioLevelMonitor(count);
        _audioMonitor.Start();
        _audioTimer.Start();
    }

    private void PaintAudioBars()
    {
        if (_audioMonitor == null) return;
        var levels = _audioMonitor.Snapshot();
        for (var i = 0; i < _audioBars.Count && i < levels.Length; i++)
        {
            // Taller range (was 20) now that the container itself is taller too,
            // so quiet audio is easier to see and loud audio has room to read as "loud"
            // instead of every bar just pinning to the same max height.
            var heightMultiplier = 28.0;
            _audioBars[i].Height = 4 + levels[i] * heightMultiplier;
        }
    }

    /// <summary>
    /// Keeps the window horizontally centered and its bottom edge anchored just
    /// above the taskbar, so it grows upward (like a launcher bar) as the results
    /// panel appears, rather than growing downward off the bottom of the screen.
    /// </summary>
    private void RepositionWindow()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;

        var height = ActualHeight > 0 ? ActualHeight : SearchBarBorder.Height;
        Top = workArea.Bottom - BottomMargin - height;
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Right after launch the window may lose focus immediately (e.g. started
        // from a terminal that keeps the foreground) — without this guard the
        // window would hide in the same instant it appeared, looking like the app
        // never started at all.
        if (!_allowHide) return;

        // Hide window when it loses focus
        Hide();
    }

    private void ResultsListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ViewModel.SelectResultCommand.Execute(ViewModel.SelectedResult);
        Hide();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;

            case Key.Enter:
                ViewModel.SearchCommand.Execute(null);
                Hide();
                e.Handled = true;
                break;

            case Key.Down:
                ViewModel.SelectNext();
                e.Handled = true;
                break;

            case Key.Up:
                ViewModel.SelectPrevious();
                e.Handled = true;
                break;

            case Key.Tab:
                // Toggle between Files and Web mode
                ViewModel.CurrentMode = ViewModel.CurrentMode == SearchMode.Files
                    ? SearchMode.Web
                    : SearchMode.Files;
                e.Handled = true;
                break;
        }
    }

    public void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Clear search when window is shown
        ViewModel.ClearSearch();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FocusSearchBox();
    }

    protected override void OnClosed(EventArgs e)
    {
        _audioTimer.Stop();
        _audioMonitor?.Dispose();
        base.OnClosed(e);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }
}

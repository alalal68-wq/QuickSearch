using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using QuickSearch.App.Services;
using QuickSearch.App.Views;
using QuickSearch.Core.Settings;
using QuickSearch.Core.FileIndex;
using QuickSearch.Core.Media;
using QuickSearch.Core.Autostart;
using Application = System.Windows.Application;

namespace QuickSearch.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private NotifyIcon? _notifyIcon;
    private HotkeyManager? _hotkeyManager;
    private MainSearchWindow? _mainWindow;
    private SettingsService? _settingsService;
    private IndexStore? _indexStore;
    private UsnJournalWatcher? _journalWatcher;
    private NowPlayingService? _nowPlayingService;
    private EqualizerOverlayWindow? _overlayWindow;

    private readonly System.Diagnostics.Stopwatch _startupClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<string> _startupLog = new();

    /// <summary>Records a startup phase; written to %AppData%\QuickSearch\startup.log.</summary>
    private void Mark(string phase)
    {
        lock (_startupLog)
            _startupLog.Add($"{_startupClock.ElapsedMilliseconds,6} ms  {phase}");
    }

    private void FlushStartupLog()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickSearch");
            string[] lines;
            lock (_startupLog) lines = _startupLog.ToArray();
            File.WriteAllLines(Path.Combine(dir, "startup.log"), lines);
        }
        catch { /* diagnostics only */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Mark("OnStartup begin");

        // A second instance would fight the first one over the index database and
        // the Alt+Space hotkey — just let the existing one keep running.
        _singleInstanceMutex = new Mutex(true, "QuickSearch.App.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        // Catch anything that would otherwise silently kill the whole app: an unhandled
        // exception on the UI thread, on a background thread, or from a forgotten Task.
        DispatcherUnhandledException += (s, args) =>
        {
            LogError("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // keep the app alive instead of crashing to the desktop
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogError("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogError("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Check if started minimized
        bool startMinimized = e.Args.Contains("--minimized");

        try
        {
            // Initialize services
            InitializeServices();
            Mark("services ready (settings + index database opened)");

            // Create main window but don't show it yet
            _mainWindow = new MainSearchWindow();
            Mark("main window created");

            // Setup tray icon
            SetupTrayIcon();

            // Setup global hotkey
            SetupHotkey();

            // Start file indexing in background
            StartIndexing();

            // Initialize Now Playing if enabled
            if (_settingsService?.Settings.ShowNowPlaying == true)
            {
                _ = InitializeNowPlaying();
            }

            // Decoration-only equalizer (independent of the search window)
            ApplyOverlaySettings();

            // Show window if not minimized
            if (!startMinimized)
            {
                ShowWindow();
            }
            Mark("window shown");

            var store = _indexStore;
            if (store != null)
            {
                _ = store.Ready.ContinueWith(_ =>
                {
                    Mark($"index cache loaded in background: {store.Count()} entries, {store.LoadDuration.TotalMilliseconds:0} ms");
                    FlushStartupLog();
                });
            }
            FlushStartupLog();
        }
        catch (Exception ex)
        {
            // Startup must never die silently: show what went wrong and leave a
            // trace in the log, otherwise the app "just doesn't appear".
            LogError("Startup", ex);
            System.Windows.MessageBox.Show(
                $"QuickSearch failed to start:\n\n{ex.Message}\n\nDetails: %AppData%\\QuickSearch\\crash.log",
                "QuickSearch", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void InitializeServices()
    {
        _settingsService = new SettingsService();

        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickSearch"
        );

        Directory.CreateDirectory(appDataPath);
        var dbPath = Path.Combine(appDataPath, "fileindex.db");

        _indexStore = OpenIndexStore(dbPath);
        _journalWatcher = new UsnJournalWatcher(_indexStore);
    }

    /// <summary>
    /// Opens the index database, and if the files are damaged beyond LiteDB's ability
    /// to open them, moves them aside and starts from an empty index instead of
    /// crashing — the background indexer rebuilds the content anyway.
    /// </summary>
    private static IndexStore OpenIndexStore(string dbPath)
    {
        try
        {
            return new IndexStore(dbPath);
        }
        catch (Exception)
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            foreach (var path in new[] { dbPath, dbPath + "-log.db" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Move(path, path + $".corrupt-{timestamp}");
                }
                catch
                {
                    // Best effort — if even the move fails the retry below surfaces the real error.
                }
            }

            return new IndexStore(dbPath);
        }
    }

    /// <summary>The cat from icon.ico, in the size that fits the tray (16/24/32 px depending on DPI).</summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("/icon.ico", UriKind.Relative))?.Stream;
            if (stream != null)
                using (stream)
                    return new System.Drawing.Icon(stream, SystemInformation.SmallIconSize);
        }
        catch
        {
            // fall through to the exe icon
        }

        try
        {
            var exe = Environment.ProcessPath;
            var icon = exe == null ? null : System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon != null) return icon;
        }
        catch
        {
            // fall through to the generic icon
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void SetupTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "QuickSearch"
        };

        _notifyIcon.Click += (s, e) =>
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
            {
                ToggleWindow();
            }
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open Search", null, (s, e) => ShowWindow());
        contextMenu.Items.Add("Settings", null, (s, e) => ShowSettings());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("Exit", null, (s, e) => Shutdown());

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private void SetupHotkey()
    {
        if (_mainWindow == null) return;

        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(_mainWindow).EnsureHandle());
        _hotkeyManager = new HotkeyManager(hwndSource);
        _hotkeyManager.RegisterHotkey(HotkeyManager.MOD_ALT, 0x20); // Alt+Space

        _hotkeyManager.HotkeyPressed += (s, e) => ToggleWindow();
    }

    private void ToggleWindow()
    {
        if (_mainWindow == null) return;

        if (_mainWindow.IsVisible)
        {
            _mainWindow.Hide();
        }
        else
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_mainWindow == null) return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.FocusSearchBox();
    }

    private void ShowSettings()
    {
        var settingsWindow = new SettingsWindow
        {
            Owner = _mainWindow
        };
        settingsWindow.ShowDialog();
    }

    private void StartIndexing()
    {
        if (_indexStore == null || _settingsService == null) return;

        Task.Run(async () =>
        {
            try
            {
                // Wait for the saved index to be in memory first, so fresh scan results
                // are never overwritten by older copies from the database.
                await _indexStore.Ready;

                var drives = _settingsService.Settings.IndexedDrives
                    .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // Index each selected drive even when the database already contains another drive.
                var mftReader = new MftReader(_indexStore, _settingsService.Settings.ExcludedFolders);
                foreach (var drive in drives)
                {
                    await mftReader.IndexDriveAsync(drive);
                }
                _journalWatcher?.StartWatching(drives);
            }
            catch (Exception ex)
            {
                // This Task is fire-and-forget: without this catch an indexing error
                // would surface only as an unobserved task exception.
                LogError("StartIndexing", ex);
            }
        });
    }

    public void RefreshIndexing()
    {
        _journalWatcher?.StopWatching();
        StartIndexing();
    }

    /// <summary>Re-reads the overlay settings and shows/hides/moves the decoration equalizer.</summary>
    public void RefreshOverlaySettings()
    {
        ApplyOverlaySettings();
    }

    private void ApplyOverlaySettings()
    {
        try
        {
            var settings = _settingsService?.Settings;

            // Never reuse the overlay window across settings changes. A window that
            // has been re-parented to the desktop WorkerW keeps corrupted z-order /
            // visibility state, so switching modes in place left it invisible.
            // Rebuilding a fresh window each time is cheap and always correct.
            _overlayWindow?.Dispose();
            _overlayWindow = null;

            if (settings?.EqualizerOverlayEnabled == true)
            {
                _overlayWindow = new EqualizerOverlayWindow();
                _overlayWindow.ApplySettings(settings);
            }
        }
        catch (Exception ex)
        {
            // A decoration bug must never take the app down.
            LogError("ApplyOverlaySettings", ex);
        }
    }

    private async Task InitializeNowPlaying()
    {
        try
        {
            _nowPlayingService = new NowPlayingService();
            await _nowPlayingService.InitializeAsync();
        }
        catch (Exception ex)
        {
            LogError("InitializeNowPlaying", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeyManager?.Dispose();
        _notifyIcon?.Dispose();
        _journalWatcher?.Dispose();
        _indexStore?.Dispose();
        _nowPlayingService?.Dispose();
        _overlayWindow?.Dispose();
        // No ReleaseMutex: the OS releases it on process exit, and a second
        // instance never owned it in the first place.

        base.OnExit(e);
    }

    public static SettingsService? GetSettingsService()
    {
        return (Current as App)?._settingsService;
    }

    public static IndexStore? GetIndexStore()
    {
        return (Current as App)?._indexStore;
    }

    public static NowPlayingService? GetNowPlayingService()
    {
        return (Current as App)?._nowPlayingService;
    }

    /// <summary>
    /// Writes an exception to %AppData%\QuickSearch\crash.log instead of letting the
    /// app die silently, so a crash can actually be diagnosed.
    /// </summary>
    public static void LogError(string context, Exception? ex)
    {
        try
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuickSearch");
            Directory.CreateDirectory(appDataPath);
            var logPath = Path.Combine(appDataPath, "crash.log");
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}\n\n";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
            // If we can't even log the error, there's nothing more we can do here.
        }
    }
}

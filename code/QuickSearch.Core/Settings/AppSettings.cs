namespace QuickSearch.Core.Settings;

public class AppSettings
{
    public List<string> IndexedDrives { get; set; } = new() { "C:\\" };
    public bool ShowNowPlaying { get; set; } = true;

    /// <summary>Thin bars drawn above the search field that follow the system audio output.</summary>
    public bool ShowAudioBars { get; set; } = true;

    /// <summary>Keep audio bars visible even when search box is empty.</summary>
    public bool KeepAudioBarsVisible { get; set; } = true;

    /// <summary>Hex color of the audio bars, for example #38BDF8.</summary>
    public string AudioBarColor { get; set; } = "#38BDF8";

    /// <summary>How many bars sit in the strip. Kept small so the row stays a thin decoration.</summary>
    public int AudioBarCount { get; set; } = 48;

    /// <summary>Show the decoration-only equalizer (bars without the search window) on screen.</summary>
    public bool EqualizerOverlayEnabled { get; set; } = false;

    /// <summary>
    /// Where the decoration equalizer lives: "Desktop" pins it to the wallpaper level
    /// (behind the desktop icons, like Rainmeter skins), "Topmost" keeps it floating
    /// above every window (click-through, never steals focus).
    /// </summary>
    public string EqualizerOverlayMode { get; set; } = "Desktop";

    /// <summary>Width of the decoration strip as a percentage of the screen's work area.</summary>
    public int EqualizerOverlayWidthPercent { get; set; } = 92;

    /// <summary>Custom app wallpaper (copied into %AppData%\QuickSearch). Null = plain dark theme.</summary>
    public string? BackgroundImagePath { get; set; }

    /// <summary>Opacity (0-100) of the dark layer drawn over the wallpaper, so text stays readable.</summary>
    public int BackgroundDimPercent { get; set; } = 75;

    public bool AutoStart { get; set; } = false;
    public string SearchEngine { get; set; } = "Google";
    public string? DefaultBrowser { get; set; }
    public int WindowWidth { get; set; } = 760;
    public int WindowHeight { get; set; } = 560;
    public List<string> ExcludedFolders { get; set; } = new()
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "$Recycle.Bin",
        "node_modules",
        ".git",
        "AppData\\Local\\Temp"
    };
}

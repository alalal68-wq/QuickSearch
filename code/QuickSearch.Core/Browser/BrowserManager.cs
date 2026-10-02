using Microsoft.Win32;

namespace QuickSearch.Core.Browser;

public class BrowserManager
{
    private const string BrowsersRegistryPath = @"SOFTWARE\Clients\StartMenuInternet";

    public List<BrowserInfo> GetInstalledBrowsers()
    {
        var browsers = new List<BrowserInfo>();

        try
        {
            // Check HKEY_LOCAL_MACHINE
            using var localKey = Registry.LocalMachine.OpenSubKey(BrowsersRegistryPath);
            if (localKey != null)
            {
                browsers.AddRange(ReadBrowsersFromKey(localKey));
            }

            // Check HKEY_CURRENT_USER
            using var currentUserKey = Registry.CurrentUser.OpenSubKey(BrowsersRegistryPath);
            if (currentUserKey != null)
            {
                browsers.AddRange(ReadBrowsersFromKey(currentUserKey));
            }
        }
        catch
        {
            // If registry reading fails, add common browsers
            AddCommonBrowsers(browsers);
        }

        return browsers.DistinctBy(b => b.ExecutablePath).ToList();
    }

    private List<BrowserInfo> ReadBrowsersFromKey(RegistryKey key)
    {
        var browsers = new List<BrowserInfo>();

        foreach (var browserName in key.GetSubKeyNames())
        {
            try
            {
                using var browserKey = key.OpenSubKey(browserName);
                using var commandKey = browserKey?.OpenSubKey(@"shell\open\command");

                var commandValue = commandKey?.GetValue(null)?.ToString();
                if (string.IsNullOrEmpty(commandValue))
                    continue;

                // Extract exe path from command
                var exePath = ExtractExePath(commandValue);
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    browsers.Add(new BrowserInfo
                    {
                        Name = browserName,
                        ExecutablePath = exePath,
                        IconPath = exePath
                    });
                }
            }
            catch
            {
                // Skip this browser if we can't read its info
            }
        }

        return browsers;
    }

    private string ExtractExePath(string command)
    {
        // Remove quotes and arguments
        var path = command.Trim('"');
        var spaceIndex = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);

        if (spaceIndex >= 0)
        {
            path = path.Substring(0, spaceIndex + 4);
        }

        return path;
    }

    private void AddCommonBrowsers(List<BrowserInfo> browsers)
    {
        var commonPaths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe",
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                browsers.Add(new BrowserInfo
                {
                    Name = char.ToUpper(name[0]) + name.Substring(1),
                    ExecutablePath = path,
                    IconPath = path
                });
            }
        }
    }

    public BrowserInfo? GetDefaultBrowser()
    {
        try
        {
            using var userChoiceKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");

            var progId = userChoiceKey?.GetValue("ProgId")?.ToString();
            if (string.IsNullOrEmpty(progId))
                return null;

            using var progIdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var commandValue = progIdKey?.GetValue(null)?.ToString();

            if (string.IsNullOrEmpty(commandValue))
                return null;

            var exePath = ExtractExePath(commandValue);
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                return new BrowserInfo
                {
                    Name = Path.GetFileNameWithoutExtension(exePath),
                    ExecutablePath = exePath,
                    IconPath = exePath
                };
            }
        }
        catch
        {
            // Return null if we can't determine default browser
        }

        return null;
    }
}

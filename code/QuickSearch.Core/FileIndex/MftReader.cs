namespace QuickSearch.Core.FileIndex;

public class MftReader
{
    private readonly IndexStore _indexStore;
    private readonly HashSet<string> _excludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "$Recycle.Bin",
        "node_modules", ".git", "AppData\\Local\\Temp", "ProgramData"
    };

    public MftReader(IndexStore indexStore, IEnumerable<string>? excludedFolders = null)
    {
        _indexStore = indexStore;
        if (excludedFolders != null)
        {
            foreach (var folder in excludedFolders)
            {
                if (!string.IsNullOrWhiteSpace(folder))
                    _excludedFolders.Add(folder.Trim().Trim('\\'));
            }
        }
    }

    public async Task IndexDriveAsync(string drivePath)
    {
        await Task.Run(() => IndexDirectory(drivePath));
    }

    private void IndexDirectory(string path)
    {
        try
        {
            // Check if path should be excluded
            if (ShouldExclude(path))
                return;

            var dirInfo = new DirectoryInfo(path);

            // One batch per directory level: the store persists whole batches in a
            // single bulk transaction instead of one write per file.
            var batch = new List<FileEntry>();

            // Index directories
            foreach (var dir in dirInfo.EnumerateDirectories())
            {
                try
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || ShouldExclude(dir.FullName))
                        continue;
                    var entry = new FileEntry
                    {
                        Name = dir.Name,
                        FullPath = dir.FullName,
                        Extension = string.Empty,
                        Size = 0,
                        LastModified = dir.LastWriteTime,
                        IsDirectory = true
                    };

                    batch.Add(entry);

                    // Recursive indexing
                    IndexDirectory(dir.FullName);
                }
                catch
                {
                    // Skip inaccessible directories
                }
            }

            // Index files
            foreach (var file in dirInfo.EnumerateFiles())
            {
                try
                {
                    var entry = new FileEntry
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        Extension = file.Extension,
                        Size = file.Length,
                        LastModified = file.LastWriteTime,
                        IsDirectory = false
                    };

                    batch.Add(entry);
                }
                catch
                {
                    // Skip inaccessible files
                }
            }

            _indexStore.AddOrUpdateBatch(batch);
        }
        catch
        {
            // Skip if directory is inaccessible
        }
    }

    private bool ShouldExclude(string path)
    {
        // Wrap the path in separators so a segment match ("...\Windows\...") can be
        // found reliably, without matching an arbitrary substring like "WindowsNotes".
        var wrappedPath = "\\" + path.Trim('\\') + "\\";

        foreach (var excluded in _excludedFolders)
        {
            var wrappedExcluded = "\\" + excluded.Trim('\\') + "\\";
            if (wrappedPath.Contains(wrappedExcluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

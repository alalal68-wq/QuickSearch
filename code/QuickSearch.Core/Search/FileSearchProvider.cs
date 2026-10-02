using System.IO;
using QuickSearch.Core.FileIndex;

namespace QuickSearch.Core.Search;

public class FileSearchProvider : ISearchProvider
{
    private readonly IndexStore _indexStore;

    /// <summary>
    /// Roots the user connected in Settings (for example "C:\" and "D:\").
    /// Search and the idle "recent files" list stay inside these drives.
    /// </summary>
    public IReadOnlyList<string> ConnectedDrives { get; set; } = new List<string> { "C:\\" };

    public FileSearchProvider(IndexStore indexStore)
    {
        _indexStore = indexStore;
    }

    public Task<IEnumerable<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (ConnectedDrives.Count == 0)
            return Task.FromResult<IEnumerable<SearchResult>>(Array.Empty<SearchResult>());
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(RecentResults());
        }

        // Users often paste a path with surrounding quotes (e.g. copied from Explorer's address bar)
        var trimmedQuery = query.Trim().Trim('"');

        var results = new List<SearchResult>();

        // If the query is itself a real, existing path, always surface it as the top result —
        // this works even if the background indexer hasn't reached that folder/file yet.
        if (TryResolveDirectPath(trimmedQuery, out var directResult) && directResult != null &&
            IsOnConnectedDrive(directResult.Path))
        {
            results.Add(directResult);
        }

        var indexed = _indexStore.Search(trimmedQuery, 40, ConnectedDrives)
            .Where(file => !string.Equals(file.FullPath, directResult?.Path, StringComparison.OrdinalIgnoreCase))
            .Select(ToResult);

        results.AddRange(indexed);

        return Task.FromResult<IEnumerable<SearchResult>>(results);
    }

    private IEnumerable<SearchResult> RecentResults()
    {
        return _indexStore.Recent(14, ConnectedDrives).Select(ToResult);
    }

    private static SearchResult ToResult(FileEntry file)
    {
        return new SearchResult
        {
            Title = file.Name,
            Subtitle = file.FullPath,
            Path = file.FullPath,
            Type = file.IsDirectory ? SearchResultType.Folder : SearchResultType.File,
            Relevance = 0.5,
            Action = () => OpenFile(file.FullPath)
        };
    }

    private bool IsOnConnectedDrive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || ConnectedDrives.Count == 0)
            return true;

        return ConnectedDrives.Any(root =>
        {
            var normalized = root.EndsWith('\\') ? root : root + "\\";
            return path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool TryResolveDirectPath(string query, out SearchResult? result)
    {
        result = null;

        try
        {
            if (!Path.IsPathRooted(query))
                return false;

            if (Directory.Exists(query))
            {
                var name = new DirectoryInfo(query).Name;
                if (string.IsNullOrEmpty(name))
                    name = query; // e.g. a drive root like "C:\"

                result = new SearchResult
                {
                    Title = name,
                    Subtitle = query,
                    Path = query,
                    Type = SearchResultType.Folder,
                    Relevance = 1.0,
                    Action = () => OpenFile(query)
                };
                return true;
            }

            if (File.Exists(query))
            {
                result = new SearchResult
                {
                    Title = Path.GetFileName(query),
                    Subtitle = query,
                    Path = query,
                    Type = SearchResultType.File,
                    Relevance = 1.0,
                    Action = () => OpenFile(query)
                };
                return true;
            }
        }
        catch
        {
            // Malformed path (illegal characters, etc.) — just fall back to the indexed search
        }

        return false;
    }

    public bool CanHandle(string query)
    {
        return true;
    }

    private static void OpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Handle error silently or log
        }
    }
}

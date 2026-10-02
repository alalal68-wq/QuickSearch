namespace QuickSearch.Core.Search;

public class WebSearchProvider : ISearchProvider
{
    private readonly Dictionary<string, string> _searchEngines = new()
    {
        { "google", "https://www.google.com/search?q={0}" },
        { "bing", "https://www.bing.com/search?q={0}" },
        { "duckduckgo", "https://duckduckgo.com/?q={0}" },
        { "yandex", "https://yandex.ru/search/?text={0}" }
    };

    public string CurrentEngine { get; set; } = "google";
    public string? DefaultBrowserPath { get; set; }

    public Task<IEnumerable<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(Enumerable.Empty<SearchResult>());
        }

        var results = new List<SearchResult>();

        foreach (var engine in _searchEngines)
        {
            results.Add(new SearchResult
            {
                Title = $"Search '{query}' on {char.ToUpper(engine.Key[0]) + engine.Key[1..]}",
                Subtitle = string.Format(engine.Value, Uri.EscapeDataString(query)),
                Type = SearchResultType.WebSearch,
                Relevance = engine.Key == CurrentEngine ? 1.0 : 0.5,
                Action = () => OpenUrl(string.Format(engine.Value, Uri.EscapeDataString(query)), DefaultBrowserPath)
            });
        }

        return Task.FromResult(results.OrderByDescending(x => x.Relevance).AsEnumerable());
    }

    public bool CanHandle(string query)
    {
        return !string.IsNullOrWhiteSpace(query);
    }

    public void OpenUrl(string url, string? browserPath = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(browserPath) && File.Exists(browserPath))
            {
                System.Diagnostics.Process.Start(browserPath, url);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Handle error silently or log
        }
    }
}

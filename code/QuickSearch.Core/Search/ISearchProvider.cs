namespace QuickSearch.Core.Search;

public interface ISearchProvider
{
    Task<IEnumerable<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
    bool CanHandle(string query);
}

public class SearchResult
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? Path { get; set; }
    public string? IconPath { get; set; }
    public SearchResultType Type { get; set; }
    public double Relevance { get; set; }
    public Action? Action { get; set; }
}

public enum SearchResultType
{
    File,
    Folder,
    WebSearch,
    Application
}

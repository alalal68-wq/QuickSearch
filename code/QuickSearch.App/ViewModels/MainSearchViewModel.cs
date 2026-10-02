using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using QuickSearch.Core.Search;

namespace QuickSearch.App.ViewModels;

public class MainSearchViewModel : INotifyPropertyChanged
{
    private string _searchQuery = string.Empty;
    private SearchMode _currentMode = SearchMode.Files;
    private ObservableCollection<SearchResult> _searchResults = new();
    private SearchResult? _selectedResult;
    private bool _isSearching;

    private readonly FileSearchProvider? _fileSearchProvider;
    private readonly WebSearchProvider _webSearchProvider;
    private CancellationTokenSource? _searchCts;

    public MainSearchViewModel()
    {
        var indexStore = App.GetIndexStore();
        if (indexStore != null)
        {
            _fileSearchProvider = new FileSearchProvider(indexStore);
        }

        _webSearchProvider = new WebSearchProvider();
        RefreshFromSettings();

        _searchResults.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasResults));

        SearchCommand = new RelayCommand(ExecuteSearch);
        SelectResultCommand = new RelayCommand<SearchResult>(ExecuteSelectResult);
    }

    /// <summary>
    /// Re-reads the search engine and default browser choice from settings.
    /// Called at startup and again whenever the Settings dialog is saved.
    /// </summary>
    public void RefreshFromSettings()
    {
        var settings = App.GetSettingsService()?.Settings;
        if (settings == null) return;

        _webSearchProvider.CurrentEngine = settings.SearchEngine.ToLowerInvariant();
        _webSearchProvider.DefaultBrowserPath = settings.DefaultBrowser;
        if (_fileSearchProvider != null)
            _fileSearchProvider.ConnectedDrives = settings.IndexedDrives
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery != value)
            {
                _searchQuery = value;
                OnPropertyChanged();
                PerformSearch();
            }
        }
    }

    public SearchMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (_currentMode != value)
            {
                _currentMode = value;
                OnPropertyChanged();
                PerformSearch();
            }
        }
    }

    public ObservableCollection<SearchResult> SearchResults
    {
        get => _searchResults;
        set
        {
            _searchResults = value;
            OnPropertyChanged();
        }
    }

    public SearchResult? SelectedResult
    {
        get => _selectedResult;
        set
        {
            _selectedResult = value;
            OnPropertyChanged();
        }
    }

    public bool HasResults => SearchResults.Count > 0;

    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            _isSearching = value;
            OnPropertyChanged();
        }
    }

    public ICommand SearchCommand { get; }
    public ICommand SelectResultCommand { get; }

    private async void PerformSearch()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(150, _searchCts.Token); // Debounce

            IsSearching = true;

            IEnumerable<SearchResult> results;

            if (_currentMode == SearchMode.Files && _fileSearchProvider != null)
            {
                results = await _fileSearchProvider.SearchAsync(_searchQuery, _searchCts.Token);
            }
            else
            {
                results = await _webSearchProvider.SearchAsync(_searchQuery, _searchCts.Token);
            }

            SearchResults.Clear();
            foreach (var result in results)
            {
                SearchResults.Add(result);
            }

            if (SearchResults.Count > 0)
            {
                SelectedResult = SearchResults[0];
            }
        }
        catch (OperationCanceledException)
        {
            // Search was cancelled (new keystroke came in), ignore
        }
        catch (Exception ex)
        {
            // A search provider failed (locked file, bad path, offline web check, etc.).
            // Never let this escape an `async void` method — that would crash the whole app.
            App.LogError("PerformSearch", ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void ExecuteSearch()
    {
        if (SelectedResult?.Action != null)
        {
            SelectedResult.Action.Invoke();
        }
    }

    private void ExecuteSelectResult(SearchResult? result)
    {
        if (result?.Action != null)
        {
            result.Action.Invoke();
        }
    }

    public void ClearSearch()
    {
        SearchQuery = string.Empty;
        SelectedResult = null;
    }

    public void SelectNext()
    {
        if (SearchResults.Count == 0 || SelectedResult == null) return;

        var currentIndex = SearchResults.IndexOf(SelectedResult);
        if (currentIndex < SearchResults.Count - 1)
        {
            SelectedResult = SearchResults[currentIndex + 1];
        }
    }

    public void SelectPrevious()
    {
        if (SearchResults.Count == 0 || SelectedResult == null) return;

        var currentIndex = SearchResults.IndexOf(SelectedResult);
        if (currentIndex > 0)
        {
            SelectedResult = SearchResults[currentIndex - 1];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum SearchMode
{
    Files,
    Web
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;

    public void Execute(object? parameter) => _execute((T?)parameter);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}

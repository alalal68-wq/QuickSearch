using System.Collections.Concurrent;

namespace QuickSearch.Core.FileIndex;

/// <summary>
/// Keeps the index in sync with live file system changes.
///
/// Events are buffered and applied in batches on a timer instead of hitting the
/// store straight from every FileSystemWatcher callback: a rename or build storm
/// fires thousands of events per second on threadpool threads, which both flooded
/// the index store and (via background-thread exceptions) took the process down.
/// </summary>
public class UsnJournalWatcher : IDisposable
{
    private readonly IndexStore _indexStore;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentQueue<FileSystemEventArgs> _pending = new();
    private readonly Timer _flushTimer;
    private bool _disposed;

    public UsnJournalWatcher(IndexStore indexStore)
    {
        _indexStore = indexStore;
        _flushTimer = new Timer(_ => FlushPending(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>Stops every watcher so a new drive list can be applied without leaking handles.</summary>
    public void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void StartWatching(params string[] paths)
    {
        StopWatching();

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
                continue;

            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                              NotifyFilters.CreationTime
            };

            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemRenamed;
            // LastWrite/Changed is omitted on purpose: it is by far the noisiest
            // event and only refreshes a timestamp the ranking barely uses.

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        _pending.Enqueue(e);
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        _pending.Enqueue(e);
    }

    private void FlushPending()
    {
        try
        {
            var deletes = new List<string>();
            var upserts = new List<FileEntry>();

            while (_pending.TryDequeue(out var e))
            {
                if (e.ChangeType == WatcherChangeTypes.Deleted)
                {
                    deletes.Add(e.FullPath);
                }
                else if (e is RenamedEventArgs renamed)
                {
                    deletes.Add(renamed.OldFullPath);
                    AddToUpserts(upserts, renamed.FullPath);
                }
                else
                {
                    AddToUpserts(upserts, e.FullPath);
                }
            }

            foreach (var path in deletes)
                _indexStore.Remove(path);

            if (upserts.Count > 0)
                _indexStore.AddOrUpdateBatch(upserts);
        }
        catch
        {
            // A failed sync round must never kill the process; the next timer
            // tick (or a full reindex) reconciles the index.
        }
    }

    private static void AddToUpserts(List<FileEntry> upserts, string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                var fileInfo = new FileInfo(fullPath);
                upserts.Add(new FileEntry
                {
                    Name = fileInfo.Name,
                    FullPath = fileInfo.FullName,
                    Extension = fileInfo.Extension,
                    Size = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTime,
                    IsDirectory = false
                });
            }
            else if (Directory.Exists(fullPath))
            {
                var dirInfo = new DirectoryInfo(fullPath);
                upserts.Add(new FileEntry
                {
                    Name = dirInfo.Name,
                    FullPath = dirInfo.FullName,
                    Extension = string.Empty,
                    Size = 0,
                    LastModified = dirInfo.LastWriteTime,
                    IsDirectory = true
                });
            }
            // Neither exists (already deleted by the time we flushed) — skip.
        }
        catch
        {
            // Path became inaccessible between the event and the flush — skip it.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        StopWatching();
        _flushTimer.Dispose();
        FlushPending();
        _disposed = true;
    }
}

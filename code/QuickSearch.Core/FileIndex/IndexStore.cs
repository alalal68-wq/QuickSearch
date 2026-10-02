using System.Collections.Concurrent;
using System.Threading.Channels;
using LiteDB;

namespace QuickSearch.Core.FileIndex;

/// <summary>
/// File index with an in-memory cache as the source of truth for reads and a
/// single background writer for persistence.
///
/// Why not query LiteDB directly: LiteDB 5.0.x releases a query-only transaction
/// only when the result enumerable is fully consumed (QueryExecutor.RunQuery runs
/// its cleanup after the yield loop). Every early stop — FindOne/FirstOrDefault,
/// Take(n) — leaks one transaction into a pool capped at 500, after which every
/// DB call throws "Maximum number of transactions reached" (this is what crashed
/// the app mid-indexing). Reads therefore never touch LiteDB; the only remaining
/// DB calls are fully-drained bulk reads/writes that cannot leak.
/// </summary>
public class IndexStore : IDisposable
{
    private const int SchemaVersion = 2; // 1 = int auto-id PK; 2 = FullPath PK
    private const int BatchSize = 500;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<FileEntry> _files;
    private readonly ConcurrentDictionary<string, FileEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Channel<WriteOp> _writes = Channel.CreateUnbounded<WriteOp>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Thread _writerThread;
    private readonly Task _loadTask;
    private volatile bool _disposed;

    /// <summary>Completes when the persisted index has been read into memory.</summary>
    public Task Ready => _loadTask;

    /// <summary>True once the persisted index is fully in memory.</summary>
    public bool IsLoaded => _loadTask.IsCompleted;

    /// <summary>How long reading the persisted index into memory took.</summary>
    public TimeSpan LoadDuration { get; private set; }

    private readonly record struct WriteOp(string? DeletePath, FileEntry? Entry);

    public IndexStore(string dbPath)
    {
        // Use a connection string with explicit mode=exclusive to ensure only one
        // connection to the database exists, preventing the "Maximum number of
        // transactions reached" error when multiple threads try to access the DB.
        var connectionString = new ConnectionString
        {
            Filename = dbPath,
            Connection = ConnectionType.Direct
        };

        _db = new LiteDatabase(connectionString);
        _files = _db.GetCollection<FileEntry>("files");

        MigrateIfNeeded();
        _files.EnsureIndex(x => x.Name);
        _files.EnsureIndex(x => x.NameLower);
        _files.EnsureIndex(x => x.Extension);

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "QuickSearch.IndexWriter"
        };
        _writerThread.Start();

        // Load the persisted index into memory in the background: with a whole-drive
        // index this takes seconds, and doing it here froze the app before any window
        // appeared. Searches work on whatever is loaded so far.
        _loadTask = Task.Run(LoadCache);
    }

    private void LoadCache()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // FindAll is drained to the end, which is the one safe way to enumerate
            // LiteDB query results (see class comment).
            foreach (var entry in _files.FindAll())
            {
                if (_disposed)
                    return;

                if (string.IsNullOrEmpty(entry.NameLower))
                    entry.NameLower = entry.Name.ToLowerInvariant();

                // TryAdd: anything the indexer has already put in memory is newer than the DB copy.
                _cache.TryAdd(entry.FullPath, entry);
            }
        }
        catch (Exception)
        {
            // A damaged page must not take the app down: keep what was read,
            // the background reindex rebuilds the rest.
        }
        finally
        {
            LoadDuration = started.Elapsed;
        }
    }

    /// <summary>
    /// Drops data written by an older schema (int auto-id primary key) — the index
    /// is derived data, so a wipe just means the background indexer rebuilds it.
    /// </summary>
    private void MigrateIfNeeded()
    {
        var meta = _db.GetCollection("meta");
        var marker = meta.Query().Where("_id = @0", "schema").ToList(); // full drain: safe
        var current = marker.FirstOrDefault()?["value"].AsInt32 ?? 0;

        if (current == SchemaVersion)
            return;

        _files.DeleteAll();
        meta.Upsert(new BsonDocument { ["_id"] = "schema", ["value"] = SchemaVersion });
    }

    public void AddOrUpdate(FileEntry entry)
    {
        entry.NameLower = entry.Name.ToLowerInvariant();
        _cache[entry.FullPath] = entry;

        if (!_disposed)
            _writes.Writer.TryWrite(new WriteOp(null, entry));
    }

    /// <summary>
    /// Cache-friendly bulk variant used by the drive indexer and the file system
    /// watcher: one call for a whole directory batch instead of one per file.
    /// </summary>
    public void AddOrUpdateBatch(IEnumerable<FileEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.NameLower = entry.Name.ToLowerInvariant();
            _cache[entry.FullPath] = entry;
        }

        if (_disposed)
            return;

        foreach (var entry in entries)
            _writes.Writer.TryWrite(new WriteOp(null, entry));
    }

    public void Remove(string fullPath)
    {
        _cache.TryRemove(fullPath, out _);

        if (!_disposed)
            _writes.Writer.TryWrite(new WriteOp(fullPath, null));
    }

    /// <summary>
    /// Drops every indexed path that does not live on one of the connected drives.
    /// Called when the user disconnects a drive so stale hits stop showing up.
    /// </summary>
    public int RemoveOutsideDrives(IReadOnlyCollection<string> driveRoots)
    {
        var roots = NormalizeRoots(driveRoots);
        if (roots.Count == 0)
            return 0;

        var stale = _cache.Keys
            .Where(key => !roots.Any(root => key.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var key in stale)
            Remove(key);

        return stale.Count;
    }

    public IEnumerable<FileEntry> Search(string query, int limit = 50, IReadOnlyCollection<string>? driveRoots = null)
    {
        var queryLower = query.ToLowerInvariant();
        var roots = NormalizeRoots(driveRoots);
        if (driveRoots != null && roots.Count == 0) return Enumerable.Empty<FileEntry>();

        bool OnConnectedDrive(FileEntry entry) =>
            roots.Count == 0 || roots.Any(root =>
                entry.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));

        // Pull a wider window than the final limit so a drive filter still leaves enough hits.
        var pool = Math.Max(limit * 8, 200);

        var tiers = new[]
        {
            new List<FileEntry>(), // exact
            new List<FileEntry>(), // starts with
            new List<FileEntry>(), // contains in name
            new List<FileEntry>()  // contains in path
        };

        foreach (var entry in _cache.Values)
        {
            if (!OnConnectedDrive(entry))
                continue;

            var tier = entry.NameLower == queryLower ? 0
                : entry.NameLower.StartsWith(queryLower) ? 1
                : entry.NameLower.Contains(queryLower) ? 2
                : entry.FullPath.ToLower().Contains(queryLower) ? 3
                : -1;

            if (tier >= 0 && tiers[tier].Count < pool)
                tiers[tier].Add(entry);
        }

        return tiers.SelectMany(t => t)
            .OrderByDescending(x => Rank(x, queryLower))
            .ThenByDescending(x => x.LastModified)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Files and folders touched most recently on the connected drives.
    /// Shown when the search box is empty so the launcher is useful before typing.
    /// </summary>
    public IEnumerable<FileEntry> Recent(int limit = 12, IReadOnlyCollection<string>? driveRoots = null)
    {
        var roots = NormalizeRoots(driveRoots);
        if (driveRoots != null && roots.Count == 0) return Enumerable.Empty<FileEntry>();

        // Bounded top-N selection: cheaper than sorting the whole index on every show.
        var best = new List<FileEntry>(limit + 1);

        foreach (var entry in _cache.Values)
        {
            if (entry.IsDirectory)
                continue;

            if (roots.Count > 0 && !roots.Any(root =>
                    entry.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                continue;

            var position = best.FindLastIndex(x => x.LastModified >= entry.LastModified) + 1;
            best.Insert(position, entry);
            if (best.Count > limit)
                best.RemoveAt(best.Count - 1);
        }

        return best;
    }

    private static double Rank(FileEntry entry, string queryLower)
    {
        var name = entry.NameLower;
        if (name == queryLower) return 1.0;
        if (name.StartsWith(queryLower)) return 0.9;
        if (name.Contains(queryLower)) return 0.6;
        return 0.3;
    }

    private static List<string> NormalizeRoots(IReadOnlyCollection<string>? driveRoots)
    {
        if (driveRoots == null || driveRoots.Count == 0)
            return new List<string>();

        return driveRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root =>
            {
                var trimmed = root.Trim();
                return trimmed.EndsWith('\\') ? trimmed : trimmed + "\\";
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Clear()
    {
        _cache.Clear();
        _files.DeleteAll();
    }

    public long Count()
    {
        return _cache.Count;
    }

    /// <summary>
    /// Drains queued writes into bulk LiteDB transactions. One upsert call per batch
    /// instead of two transactions per file is what keeps the engine's transaction
    /// pool (and the WAL) healthy on multi-hundred-thousand-file drives.
    /// </summary>
    private void WriterLoop()
    {
        var upserts = new List<FileEntry>(BatchSize);
        var deletes = new List<string>(BatchSize);

        try
        {
            while (_writes.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                // Drain everything currently queued, then flush once per burst so a
                // whole directory batch lands in as few transactions as possible.
                while (_writes.Reader.TryRead(out var op))
                {
                    if (op.DeletePath != null)
                        deletes.Add(op.DeletePath);
                    else if (op.Entry != null)
                        upserts.Add(op.Entry);

                    if (upserts.Count >= BatchSize)
                        Flush(upserts);

                    if (deletes.Count >= BatchSize)
                        FlushDeletes(deletes);
                }

                Flush(upserts);
                FlushDeletes(deletes);
            }
        }
        catch (Exception)
        {
            // The writer must never die on a persistence error: the in-memory cache
            // stays authoritative and self-heals on the next full reindex.
        }

        Flush(upserts);
        FlushDeletes(deletes);
    }

    private void Flush(List<FileEntry> upserts)
    {
        if (upserts.Count == 0)
            return;

        try
        {
            _files.Upsert(upserts);
        }
        catch (Exception)
        {
            // Drop a failed batch; the next full reindex re-persists everything.
        }

        upserts.Clear();
    }

    private void FlushDeletes(List<string> deletes)
    {
        if (deletes.Count == 0)
            return;

        try
        {
            // Delete by primary key (FullPath) — no query cursor involved.
            foreach (var path in deletes)
                _files.Delete(path);
        }
        catch (Exception)
        {
            // Same policy as upserts: skip, cache stays authoritative.
        }

        deletes.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _writes.Writer.TryComplete();

        // Let the loader stop and the writer drain what is still queued before closing the database.
        try { _loadTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _writerThread.Join(TimeSpan.FromSeconds(5));
        _db.Dispose();
    }
}

public class FileEntry
{
    [BsonId] public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NameLower { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsDirectory { get; set; }
}

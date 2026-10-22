using System.Collections.Concurrent;
using Nexus.Core.Models;

namespace Nexus.Core.SystemServices;

public enum FileSearchSort { Relevance, Name, Type, Size, Location }
public enum FileSearchKind { All, Files, Directories }

public sealed record FileSearchResult(
    IReadOnlyList<FileGraphNode> Items,
    int TotalMatches,
    int IndexedFiles,
    int IndexedDirectories,
    bool IsLimited);

/// <summary>
/// Searches the already-persisted filesystem graph without copying millions of names or
/// rescanning drives. Watcher deltas remain searchable immediately while the graph catches up.
/// </summary>
public sealed class IndexedFileSearchService : IDisposable
{
    private const int MaximumCachedMatches = 250_000;
    private readonly FileGraphService _graph;
    private readonly FileActivityService _activity;
    private readonly ConcurrentDictionary<string, LiveChange> _liveChanges =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheGate = new();
    private SearchCache? _searchCache;
    private FileGraphSnapshot _commonLocations = FileGraphSnapshot.Empty;
    private FileGraphSnapshot? _combinedPrimary;
    private FileGraphSnapshot? _combinedCommon;
    private FileGraphSnapshot? _combinedSnapshot;
    private long _changeVersion;

    public IndexedFileSearchService(FileGraphService graph, FileActivityService activity)
    {
        _graph = graph;
        _activity = activity;
        _activity.ActivityDetected += OnActivityDetected;
    }

    public event EventHandler? IndexChanged;

    public FileGraphSnapshot CurrentSnapshot => GetSearchSnapshot();

    public async Task RefreshCommonLocationsAsync(CancellationToken cancellationToken = default)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            string.IsNullOrWhiteSpace(userProfile) ? null : Path.Combine(userProfile, "Downloads"),
            Environment.GetEnvironmentVariable("OneDrive")
        };
        var roots = candidates.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0) return;

        var commonGraph = new FileGraphService();
        var snapshot = await commonGraph.BuildAsync(roots, maxNodes: 300_000,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_cacheGate)
        {
            _commonLocations = snapshot;
            _combinedPrimary = null;
            _combinedCommon = null;
            _combinedSnapshot = null;
            _searchCache = null;
        }
        Interlocked.Increment(ref _changeVersion);
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<FileSearchResult> SearchAsync(string query, FileSearchSort sort = FileSearchSort.Relevance,
        int maximumResults = 1000, CancellationToken cancellationToken = default,
        FileSearchKind kind = FileSearchKind.All) =>
        Task.Run(() => SearchWithCache(query, sort, maximumResults, kind, cancellationToken), cancellationToken);

    public static FileSearchResult Search(FileGraphSnapshot snapshot, string query,
        FileSearchSort sort = FileSearchSort.Relevance, int maximumResults = 1000,
        CancellationToken cancellationToken = default, FileSearchKind kind = FileSearchKind.All) =>
        Search(snapshot, query, sort, maximumResults, null, cancellationToken, null, false, kind).Result;

    private FileSearchResult SearchWithCache(string query, FileSearchSort sort, int maximumResults,
        FileSearchKind kind, CancellationToken cancellationToken)
    {
        var snapshot = GetSearchSnapshot();
        var normalizedQuery = query.Trim();
        var version = Volatile.Read(ref _changeVersion);
        SearchCache? cache;
        lock (_cacheGate) cache = _searchCache;

        IReadOnlyList<FileGraphNode>? candidates = null;
        if (!string.IsNullOrEmpty(normalizedQuery) && cache is not null &&
            ReferenceEquals(cache.Snapshot, snapshot) && cache.ChangeVersion == version &&
            cache.Kind == kind &&
            normalizedQuery.StartsWith(cache.Query, StringComparison.OrdinalIgnoreCase))
        {
            candidates = cache.Matches;
        }

        var computation = Search(snapshot, normalizedQuery, sort, maximumResults, _liveChanges,
            cancellationToken, candidates, captureMatches: !string.IsNullOrEmpty(normalizedQuery), kind);
        cancellationToken.ThrowIfCancellationRequested();

        if (computation.Matches is not null)
        {
            lock (_cacheGate)
            {
                if (ReferenceEquals(snapshot, _graph.Current) && version == Volatile.Read(ref _changeVersion))
                    _searchCache = new SearchCache(snapshot, normalizedQuery, computation.Matches, version, kind);
            }
        }
        return computation.Result;
    }

    private FileGraphSnapshot GetSearchSnapshot()
    {
        var primary = _graph.Current;
        var common = _commonLocations;
        if (common.Nodes.Count == 0) return primary;
        lock (_cacheGate)
        {
            if (ReferenceEquals(primary, _combinedPrimary) && ReferenceEquals(common, _combinedCommon) &&
                _combinedSnapshot is not null) return _combinedSnapshot;

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nodes = new List<FileGraphNode>(primary.Nodes.Count + common.Nodes.Count);
            foreach (var source in primary.Nodes.Concat(common.Nodes))
            {
                if (!paths.Add(source.Path)) continue;
                nodes.Add(source with { Id = nodes.Count, ParentId = null });
            }
            _combinedPrimary = primary;
            _combinedCommon = common;
            _combinedSnapshot = new FileGraphSnapshot(nodes,
                nodes.Count(node => !node.IsDirectory), nodes.Count(node => node.IsDirectory),
                primary.RootCount + common.RootCount, nodes.Count,
                primary.IsTruncated || common.IsTruncated,
                primary.IndexedAt > common.IndexedAt ? primary.IndexedAt : common.IndexedAt);
            return _combinedSnapshot;
        }
    }

    private static SearchComputation Search(FileGraphSnapshot snapshot, string query, FileSearchSort sort,
        int maximumResults, ConcurrentDictionary<string, LiveChange>? changes,
        CancellationToken cancellationToken, IReadOnlyList<FileGraphNode>? candidates,
        bool captureMatches, FileSearchKind kind)
    {
        maximumResults = Math.Clamp(maximumResults, 1, 20_000);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length)
            .ToArray();
        var comparer = new SearchHitComparer(sort);
        var worstFirst = Comparer<SearchHit>.Create((left, right) => comparer.Compare(right, left));
        var best = new PriorityQueue<SearchHit, SearchHit>(worstFirst);
        var total = 0;
        var hasChanges = changes is { IsEmpty: false };
        var checkedCount = 0;
        List<FileGraphNode>? matches = captureMatches ? [] : null;
        var cacheOverflowed = false;

        void Consider(FileGraphNode node)
        {
            if (kind == FileSearchKind.Files && node.IsDirectory ||
                kind == FileSearchKind.Directories && !node.IsDirectory) return;
            if ((++checkedCount & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var score = Score(node, terms);
            if (score < 0) return;
            total++;
            if (matches is not null)
            {
                if (matches.Count < MaximumCachedMatches) matches.Add(node);
                else { matches = null; cacheOverflowed = true; }
            }
            var hit = new SearchHit(node, score);
            if (best.Count < maximumResults) best.Enqueue(hit, hit);
            else if (best.TryPeek(out var worst, out _) && comparer.Compare(hit, worst) < 0)
            {
                best.Dequeue();
                best.Enqueue(hit, hit);
            }
        }

        if (candidates is not null)
        {
            foreach (var node in candidates) Consider(node);
        }
        else
        {
            foreach (var node in snapshot.Nodes)
            {
                if (hasChanges && changes!.ContainsKey(node.Path)) continue;
                Consider(node);
            }
            if (hasChanges)
                foreach (var (_, change) in changes!) if (change.Node is not null) Consider(change.Node);
        }

        var ordered = best.UnorderedItems.Select(item => item.Element).ToList();
        ordered.Sort(comparer);
        var result = new FileSearchResult(ordered.Select(hit => hit.Node).ToArray(), total,
            snapshot.FileCount, snapshot.DirectoryCount, total > maximumResults);
        return new SearchComputation(result, cacheOverflowed ? null : matches?.ToArray());
    }

    private static int Score(FileGraphNode node, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return node.IsDirectory ? 5 : 0;
        var score = node.IsDirectory ? 15 : 0;
        foreach (var term in terms)
        {
            var index = node.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;
            score += index == 0 ? 0 : index + 6;
        }
        return score + Math.Min(node.Name.Length, 200);
    }

    private void OnActivityDetected(object? sender, FileActivityEventArgs args)
    {
        var activity = args.Activity;
        if (activity.Kind == FileActivityKind.Accessed) return;
        if (activity.Kind == FileActivityKind.Moved && !string.IsNullOrWhiteSpace(activity.PreviousPath))
            _liveChanges[activity.PreviousPath] = new LiveChange(null);

        if (activity.Kind == FileActivityKind.Deleted)
            _liveChanges[activity.Path] = new LiveChange(null);
        else
        {
            var name = Path.GetFileName(activity.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = activity.Path;
            _liveChanges[activity.Path] = new LiveChange(new FileGraphNode(-1, null, name, activity.Path,
                activity.IsDirectory, 0, 0, activity.SizeBytes));
        }
        Interlocked.Increment(ref _changeVersion);
        lock (_cacheGate) _searchCache = null;
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _activity.ActivityDetected -= OnActivityDetected;

    private readonly record struct SearchHit(FileGraphNode Node, int Score);
    private readonly record struct LiveChange(FileGraphNode? Node);
    private sealed record SearchCache(FileGraphSnapshot Snapshot, string Query,
        IReadOnlyList<FileGraphNode> Matches, long ChangeVersion, FileSearchKind Kind);
    private sealed record SearchComputation(FileSearchResult Result, FileGraphNode[]? Matches);

    private sealed class SearchHitComparer(FileSearchSort sort) : IComparer<SearchHit>
    {
        public int Compare(SearchHit left, SearchHit right)
        {
            var comparison = sort switch
            {
                FileSearchSort.Name => StringComparer.OrdinalIgnoreCase.Compare(left.Node.Name, right.Node.Name),
                FileSearchSort.Type => StringComparer.OrdinalIgnoreCase.Compare(
                    left.Node.IsDirectory ? string.Empty : Path.GetExtension(left.Node.Name),
                    right.Node.IsDirectory ? string.Empty : Path.GetExtension(right.Node.Name)),
                FileSearchSort.Size => right.Node.SizeBytes.CompareTo(left.Node.SizeBytes),
                FileSearchSort.Location => StringComparer.OrdinalIgnoreCase.Compare(left.Node.Path, right.Node.Path),
                _ => left.Score.CompareTo(right.Score)
            };
            if (comparison == 0) comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Node.Name, right.Node.Name);
            return comparison != 0 ? comparison : StringComparer.OrdinalIgnoreCase.Compare(left.Node.Path, right.Node.Path);
        }
    }
}

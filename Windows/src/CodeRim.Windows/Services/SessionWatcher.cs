using CodeRim.Core.Services;
using System.IO;

namespace CodeRim.Windows.Services;

internal sealed class SessionWatcher : IDisposable
{
    private readonly Action<IReadOnlyCollection<string>?> onChange;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly HashSet<string> pendingChangedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object stateLock = new();
    private System.Threading.Timer? debounceTimer;
    private bool requiresFullRefresh;
    private bool disposed;

    public SessionWatcher(Action<IReadOnlyCollection<string>?> onChange)
    {
        this.onChange = onChange;
        Rebuild();
    }

    public void Rebuild()
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            DisposeWatchers();

            foreach (var root in UsageScanner.DefaultRoots().Concat(UsageScanner.DefaultRoots("claude")).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(root)) watchers.Add(CreateWatcher(root, "*.jsonl", includeSubdirectories: true));
            }
        }
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            disposed = true;
            DisposeWatchers();
            debounceTimer?.Dispose();
            debounceTimer = null;
            pendingChangedPaths.Clear();
        }
    }

    private FileSystemWatcher CreateWatcher(
        string root,
        string filter,
        bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(root, filter)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.DirectoryName
                | NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
            InternalBufferSize = 16 * 1024,
            EnableRaisingEvents = false
        };
        watcher.Changed += HandleChange;
        watcher.Created += HandleChange;
        watcher.Deleted += HandleChange;
        watcher.Renamed += HandleChange;
        watcher.Error += HandleError;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in watchers)
        {
            watcher.Dispose();
        }
        watchers.Clear();
    }

    private void HandleChange(object sender, FileSystemEventArgs e)
    {
        if (string.Equals(Path.GetFileName(e.FullPath), ".codex", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(e.FullPath))
        {
            Rebuild();
            onChange(null);
            return;
        }
        ScheduleRefresh(e.FullPath);
    }

    private void HandleError(object sender, ErrorEventArgs e)
    {
        ThreadPool.QueueUserWorkItem(_ => Rebuild());
        ScheduleRefresh(null);
    }

    private void ScheduleRefresh(string? changedPath)
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }
            if (changedPath is null)
            {
                requiresFullRefresh = true;
                pendingChangedPaths.Clear();
            }
            else if (!requiresFullRefresh)
            {
                pendingChangedPaths.Add(Path.GetFullPath(changedPath));
            }
            debounceTimer?.Dispose();
            debounceTimer = new System.Threading.Timer(
                _ => NotifyChangeIfActive(),
                null,
                TimeSpan.FromSeconds(2),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void NotifyChangeIfActive()
    {
        IReadOnlyCollection<string>? changedPaths;
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            changedPaths = requiresFullRefresh ? null : pendingChangedPaths.ToArray();
            requiresFullRefresh = false;
            pendingChangedPaths.Clear();
        }

        onChange(changedPaths);
    }
}

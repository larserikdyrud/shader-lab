namespace ShaderLab.Watch;

/// <summary>
/// Overvåker filterfilene i kjeden og shaders/core for --watch. FileSystemWatcher-hendelser kommer på en annen tråd,
/// så de bare noterer hva som endret seg; all GPU-jobb skjer på hovedtråden etter <see cref="WaitForChange"/>.
/// </summary>
internal sealed class HotReload : IDisposable
{
    // Editorer skriver ofte filen flere ganger (eller via en midlertidig fil og rename); vent til det har roet seg.
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly HashSet<string> _filterPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _coreDir;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lock = new();
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastEvent;

    public HotReload(IEnumerable<string> filterPaths, string coreDir)
    {
        foreach (string path in filterPaths) _filterPaths.Add(Path.GetFullPath(path));
        _coreDir = Path.GetFullPath(coreDir);
        foreach (string dir in _filterPaths.Select(p => Path.GetDirectoryName(p)!).Append(_coreDir).Distinct(StringComparer.OrdinalIgnoreCase))
            Watch(dir);
    }

    /// <summary>
    /// Blokkerer til en av filterfilene eller noe i shaders/core er endret og har vært i ro i 200 ms.
    /// Returnerer filene som endret seg, eller null hvis <paramref name="cancelled"/> blir sann først.
    /// </summary>
    public IReadOnlyList<string>? WaitForChange(Func<bool> cancelled)
    {
        while (!cancelled())
        {
            lock (_lock)
            {
                if (_changed.Count > 0 && DateTime.UtcNow - _lastEvent >= Debounce)
                {
                    var files = _changed.Select(Paths.Display).Order().ToList();
                    _changed.Clear();
                    return files;
                }
            }
            Thread.Sleep(50);
        }
        return null;
    }

    private void Watch(string dir)
    {
        var watcher = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false
        };
        watcher.Changed += (_, e) => Note(e.FullPath);
        watcher.Created += (_, e) => Note(e.FullPath);
        watcher.Renamed += (_, e) => Note(e.FullPath);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void Note(string path)
    {
        string full = Path.GetFullPath(path);
        bool relevant = _filterPaths.Contains(full)
            || (string.Equals(Path.GetDirectoryName(full), _coreDir, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(full) is ".hlsl" or ".hlsli");
        if (!relevant) return;

        lock (_lock)
        {
            _changed.Add(full);
            _lastEvent = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
    }
}

using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ServerBrain;

/// <summary>Fan-out of server-sent events to every connected dashboard.</summary>
public sealed class EventHub
{
    private readonly List<Channel<string>> _subs = new();
    private readonly object _gate = new();

    public int SubscriberCount { get { lock (_gate) return _subs.Count; } }

    public ChannelReader<string> Subscribe(out Action unsubscribe)
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subs.Add(ch);
        unsubscribe = () => { lock (_gate) _subs.Remove(ch); ch.Writer.TryComplete(); };
        return ch.Reader;
    }

    public void Publish(string type, object data)
    {
        if (SubscriberCount == 0) return;
        var frame = $"event: {type}\ndata: {JsonSerializer.Serialize(data, Json.Options)}\n\n";
        lock (_gate) foreach (var s in _subs) s.Writer.TryWrite(frame);
    }
}

/// <summary>Rolling log of what happened (crashes, restarts, conflicts...), persisted across restarts.</summary>
public sealed class ActivityLog
{
    private readonly List<ActivityEntry> _items = new();
    private readonly JsonFile<List<ActivityEntry>> _file = new(AppPaths.ActivityFile);
    private readonly EventHub _hub;
    private readonly object _gate = new();
    private Timer? _flush;
    private bool _dirty;
    private const int Max = 400;

    public event Action<ActivityEntry>? Added;

    public ActivityLog(EventHub hub)
    {
        _hub = hub;
        _items.AddRange(_file.Load());
    }

    public void Add(string level, string kind, string message, ProjectConfig? project = null)
    {
        var e = new ActivityEntry
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Level = level, Kind = kind, Message = message,
            ProjectId = project?.Id, ProjectName = project?.Name,
        };
        lock (_gate)
        {
            _items.Add(e);
            if (_items.Count > Max) _items.RemoveRange(0, _items.Count - Max);
            _dirty = true;
            _flush ??= new Timer(_ => Flush(), null, 3000, Timeout.Infinite);
        }
        _hub.Publish("activity", e);
        try { Added?.Invoke(e); } catch { }
    }

    public List<ActivityEntry> Recent(int n = 200)
    {
        lock (_gate) return _items.TakeLast(n).Reverse().ToList();
    }

    public void Clear()
    {
        lock (_gate) { _items.Clear(); _dirty = true; }
        Flush();
        _hub.Publish("activity-cleared", new { });
    }

    public void Flush()
    {
        List<ActivityEntry> copy;
        lock (_gate)
        {
            _flush?.Dispose(); _flush = null;
            if (!_dirty) return;
            _dirty = false;
            copy = _items.ToList();
        }
        try { _file.Save(copy); } catch { }
    }
}

/// <summary>Last N lines of a project's output, mirrored to a size-capped file.</summary>
public sealed class LogBuffer : IDisposable
{
    private readonly LinkedList<LogLine> _lines = new();
    private readonly object _gate = new();
    private readonly string _file;
    private StreamWriter? _writer;
    private long _written;
    private long _seq;
    private const int MaxLines = 3000;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    public event Action<LogLine>? Line;

    public LogBuffer(string projectId)
    {
        _file = Path.Combine(AppPaths.LogsDir, projectId + ".log");
    }

    public string FilePath => _file;

    public void Add(string text, bool isError = false, bool system = false)
    {
        LogLine l;
        lock (_gate)
        {
            l = new LogLine(++_seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), text, isError, system);
            _lines.AddLast(l);
            while (_lines.Count > MaxLines) _lines.RemoveFirst();
            try { WriteFile(l); } catch { }
        }
        try { Line?.Invoke(l); } catch { }
    }

    private void WriteFile(LogLine l)
    {
        if (_writer == null)
        {
            _writer = new StreamWriter(new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            _written = new FileInfo(_file).Length;
        }
        var line = $"{DateTimeOffset.FromUnixTimeMilliseconds(l.Ts).ToLocalTime():yyyy-MM-dd HH:mm:ss} {(l.Err ? "ERR" : l.Sys ? "SYS" : "OUT")} {l.Text}";
        _writer.WriteLine(line);
        _written += line.Length + 2;
        if (_written > MaxFileBytes)
        {
            _writer.Dispose(); _writer = null;
            try { File.Move(_file, _file + ".old", overwrite: true); } catch { }
        }
    }

    public List<LogLine> Tail(int n = 500)
    {
        lock (_gate) return _lines.TakeLast(n).ToList();
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}

public sealed record LogLine(long Seq, long Ts, string Text, bool Err, bool Sys);

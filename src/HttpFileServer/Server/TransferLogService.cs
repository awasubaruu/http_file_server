namespace HttpFileServer.Server;

public enum TransferKind
{
    Download,
    Upload,
    Zip,
    Notice,
}

public sealed record TransferEntry(
    DateTime Time,
    TransferKind Kind,
    string FileName,
    string Remote,
    long Bytes,
    TimeSpan Duration,
    string Detail);

/// <summary>线程安全的传输日志，事件在服务器线程上触发，订阅方负责回 UI 线程。</summary>
public sealed class TransferLogService
{
    private readonly object _lock = new();
    private readonly List<TransferEntry> _entries = new();
    private const int MaxEntries = 500;

    public event Action<TransferEntry>? Added;

    public void Add(TransferEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        Added?.Invoke(entry);
    }

    public List<TransferEntry> Snapshot()
    {
        lock (_lock)
            return new List<TransferEntry>(_entries);
    }
}

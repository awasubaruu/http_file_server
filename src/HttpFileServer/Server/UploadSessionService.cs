using System.Text.Json;
using System.Text.Json.Serialization;

namespace HttpFileServer.Server;

public sealed class UploadSession
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string RelDir { get; init; }
    public required long Size { get; init; }
    public required int ChunkSize { get; init; }
    public required string Fingerprint { get; init; }
    public required string WorkDir { get; init; }

    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public long ReceivedBytes { get; set; }
    public string Remote { get; set; } = "";

    [JsonIgnore]
    public int ChunkCount => Math.Max(1, (int)Math.Ceiling((double)Size / ChunkSize));

    public long ExpectedChunkSize(int index) =>
        index == ChunkCount - 1 ? Size - (long)(ChunkCount - 1) * ChunkSize : ChunkSize;

    private sealed class SessionMeta
    {
        public string? Name { get; set; }
        public string? RelDir { get; set; }
        public long Size { get; set; }
        public int ChunkSize { get; set; }
        public long LastModified { get; set; }
    }

    public string WriteMeta(long lastModified)
    {
        var meta = new SessionMeta
        {
            Name = Name,
            RelDir = RelDir,
            Size = Size,
            ChunkSize = ChunkSize,
            LastModified = lastModified,
        };
        var path = Path.Combine(WorkDir, "meta.json");
        File.WriteAllText(path, JsonSerializer.Serialize(meta));
        return path;
    }

    public static (UploadSession? session, long lastModified)? TryReadMeta(string workDir)
    {
        try
        {
            var meta = JsonSerializer.Deserialize<SessionMeta>(
                File.ReadAllText(Path.Combine(workDir, "meta.json")));
            if (meta?.Name is null || meta.ChunkSize <= 0)
                return null;
            var id = Path.GetFileName(workDir);
            var session = new UploadSession
            {
                Id = id,
                Name = meta.Name,
                RelDir = meta.RelDir ?? "",
                Size = meta.Size,
                ChunkSize = meta.ChunkSize,
                Fingerprint = FingerprintOf(meta.RelDir ?? "", meta.Name, meta.Size, meta.LastModified),
                WorkDir = workDir,
            };
            session.ReceivedBytes = SumChunkBytes(session);
            return (session, meta.LastModified);
        }
        catch
        {
            return null;
        }
    }

    public static string FingerprintOf(string relDir, string name, long size, long lastModified) =>
        $"{relDir}|{name}|{size}|{lastModified}";

    public static long SumChunkBytes(UploadSession session)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(session.WorkDir, "*.part"))
            {
                var index = Path.GetFileNameWithoutExtension(f);
                if (int.TryParse(index, out var i) && i >= 0 && i < session.ChunkCount)
                    total += new FileInfo(f).Length;
            }
        }
        catch
        {
        }
        return Math.Min(total, session.Size);
    }
}

public sealed record UploadProgressEventArgs(
    string Id, string Name, long Received, long Total, string Remote);

public sealed record UploadFinishedEventArgs(
    string Id, string Name, bool Success, string Message,
    long Bytes, TimeSpan Duration, string Remote);

public sealed record ChunkResult(bool Ok, string? Error, long Received);

public sealed record CompleteResult(bool Ok, string? Error, string? FileName, long Bytes, TimeSpan Duration);

/// <summary>分块上传会话管理：会话落盘到 %TEMP%，进程重启后可按文件指纹恢复续传，空闲超 24 小时自动清理。</summary>
public sealed class UploadSessionService : IDisposable
{
    private static string TempRoot => Path.Combine(Path.GetTempPath(), "HttpFileServer", "uploads");

    private readonly object _lock = new();
    private readonly Dictionary<string, UploadSession> _sessions = new();
    private readonly Dictionary<string, string> _fingerprints = new(); // fingerprint -> sessionId
    private readonly System.Threading.Timer _cleanupTimer;

    public event Action<UploadProgressEventArgs>? Progress;
    public event Action<UploadFinishedEventArgs>? Finished;

    public UploadSessionService()
    {
        Directory.CreateDirectory(TempRoot);
        LoadExistingSessions();
        _cleanupTimer = new System.Threading.Timer(_ => Cleanup(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    public string TempRootPath => TempRoot;

    private void LoadExistingSessions()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(TempRoot))
            {
                var loaded = UploadSession.TryReadMeta(dir);
                if (loaded is not { } pair || pair.session is not { } session)
                {
                    TryDeleteDir(dir);
                    continue;
                }
                _sessions[session.Id] = session;
                _fingerprints[session.Fingerprint] = session.Id;
            }
        }
        catch
        {
        }
    }

    /// <summary>初始化（或按指纹恢复）一个上传会话，返回已完整收到的分块索引。</summary>
    public (UploadSession Session, List<int> Received) Init(
        string name, string relDir, long size, int chunkSize, long lastModified)
    {
        var fingerprint = UploadSession.FingerprintOf(relDir, name, size, lastModified);
        lock (_lock)
        {
            if (_fingerprints.TryGetValue(fingerprint, out var existingId) &&
                _sessions.TryGetValue(existingId, out var existing))
            {
                existing.LastActivity = DateTime.UtcNow;
                return (existing, ListReceived(existing));
            }

            var id = Guid.NewGuid().ToString("N");
            var workDir = Path.Combine(TempRoot, id);
            Directory.CreateDirectory(workDir);
            var session = new UploadSession
            {
                Id = id,
                Name = name,
                RelDir = relDir,
                Size = size,
                ChunkSize = chunkSize,
                Fingerprint = fingerprint,
                WorkDir = workDir,
            };
            session.WriteMeta(lastModified);
            _sessions[id] = session;
            _fingerprints[fingerprint] = id;
            return (session, new List<int>());
        }
    }

    public UploadSession? Get(string id)
    {
        lock (_lock)
            return _sessions.GetValueOrDefault(id);
    }

    public async Task<ChunkResult> WriteChunkAsync(UploadSession session, int index, Stream body, string remote)
    {
        if (index < 0 || index >= session.ChunkCount)
            return new ChunkResult(false, "分块索引越界", 0);
        long expected = session.ExpectedChunkSize(index);

        var finalPath = Path.Combine(session.WorkDir, $"{index}.part");
        var tmpPath = Path.Combine(session.WorkDir, $"{index}.{Guid.NewGuid():N}.tmp");

        try
        {
            long written;
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await body.CopyToAsync(fs);
                await fs.FlushAsync();
                written = fs.Length;
            }

            if (written != expected)
            {
                File.Delete(tmpPath);
                return new ChunkResult(false, $"分块大小不符（收到 {written}，期望 {expected}）", written);
            }

            bool hadChunk;
            lock (session)
            {
                hadChunk = File.Exists(finalPath);
                File.Move(tmpPath, finalPath, overwrite: true);
                session.ReceivedBytes += hadChunk ? 0 : expected;
                session.LastActivity = DateTime.UtcNow;
                session.StartedAt ??= DateTime.UtcNow;
                session.Remote = remote;
            }

            Progress?.Invoke(new UploadProgressEventArgs(
                session.Id, session.Name, session.ReceivedBytes, session.Size, remote));

            return new ChunkResult(true, null, expected);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmpPath); } catch { }
            return new ChunkResult(false, ex.Message, 0);
        }
    }

    public async Task<CompleteResult> CompleteAsync(UploadSession session, string rootPath)
    {
        var started = DateTime.UtcNow;

        for (int i = 0; i < session.ChunkCount; i++)
        {
            var part = Path.Combine(session.WorkDir, $"{i}.part");
            if (!File.Exists(part) || new FileInfo(part).Length != session.ExpectedChunkSize(i))
                return new CompleteResult(false, $"分块 {i} 缺失或不完整，请重新上传", null, 0, TimeSpan.Zero);
        }

        var targetDir = Path.Combine(rootPath, session.RelDir.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (Exception ex)
        {
            return new CompleteResult(false, "无法创建目标目录：" + ex.Message, null, 0, TimeSpan.Zero);
        }

        var finalPath = GetAvailablePath(targetDir, session.Name);
        var finalName = Path.GetFileName(finalPath);

        try
        {
            await using (var output = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                for (int i = 0; i < session.ChunkCount; i++)
                {
                    var part = Path.Combine(session.WorkDir, $"{i}.part");
                    await using var input = File.OpenRead(part);
                    await input.CopyToAsync(output, 1 << 20);
                }
            }

            var actual = new FileInfo(finalPath).Length;
            if (actual != session.Size)
            {
                File.Delete(finalPath);
                return new CompleteResult(false, $"合并后大小不符（{actual} ≠ {session.Size}）", null, 0, TimeSpan.Zero);
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(finalPath); } catch { }
            return new CompleteResult(false, "写入目标文件失败：" + ex.Message, null, 0, TimeSpan.Zero);
        }

        long bytes = session.Size;
        var remote = session.Remote;
        Remove(session.Id);

        var duration = session.StartedAt is { } t ? started - t : TimeSpan.Zero;
        Finished?.Invoke(new UploadFinishedEventArgs(
            session.Id, finalName, true, "完成", bytes, duration, remote));

        return new CompleteResult(true, null, finalName, bytes, duration);
    }

    public void Abort(string id, string reason = "已取消")
    {
        UploadSession? session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out session))
                return;
            _sessions.Remove(id);
            _fingerprints.Remove(session.Fingerprint);
        }

        TryDeleteDir(session.WorkDir);
        Finished?.Invoke(new UploadFinishedEventArgs(
            session.Id, session.Name, false, reason, session.ReceivedBytes,
            session.StartedAt is { } t ? DateTime.UtcNow - t : TimeSpan.Zero, session.Remote));
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_sessions.Remove(id, out var session))
            {
                _fingerprints.Remove(session.Fingerprint);
                TryDeleteDir(session.WorkDir);
            }
        }
    }

    private void Cleanup()
    {
        lock (_lock)
        {
            var deadline = DateTime.UtcNow.AddHours(-24);
            foreach (var session in _sessions.Values.ToList())
            {
                if (session.LastActivity >= deadline)
                    continue;
                _sessions.Remove(session.Id);
                _fingerprints.Remove(session.Fingerprint);
                TryDeleteDir(session.WorkDir);
            }
        }
    }

    private static List<int> ListReceived(UploadSession session)
    {
        var list = new List<int>();
        for (int i = 0; i < session.ChunkCount; i++)
        {
            var part = Path.Combine(session.WorkDir, $"{i}.part");
            if (File.Exists(part) && new FileInfo(part).Length == session.ExpectedChunkSize(i))
                list.Add(i);
        }
        return list;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }

    private static string GetAvailablePath(string dir, string name)
    {
        var candidate = Path.Combine(dir, name);
        if (!File.Exists(candidate))
            return candidate;
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}

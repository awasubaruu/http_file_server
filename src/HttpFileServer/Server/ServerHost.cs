using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HttpFileServer.Server;

/// <summary>封装 Kestrel 的生命周期。WinForms 主线程负责调用 Start/Stop，Kestrel 使用自身线程池。</summary>
public sealed class ServerHost
{
    private WebApplication? _app;

    public TransferLogService Log { get; } = new();
    public UploadSessionService Uploads { get; } = new();

    public int Port { get; private set; }
    public string RootPath { get; private set; } = "";
    public bool IsRunning { get; private set; }

    public event Action<int>? Started;
    public event Action? Stopped;

    public void Start(string rootPath, int port, string listenAddress, bool allowUpload)
    {
        if (IsRunning)
            throw new InvalidOperationException("服务器已在运行");

        RootPath = rootPath;
        Port = port;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
            // 手机 Wi-Fi 暂停/卡顿时不断开连接
            o.Limits.MinRequestBodyDataRate = null;
            o.Limits.MinResponseDataRate = null;
            // ZipArchive 写中央目录等场景会使用同步 IO
            o.AllowSynchronousIO = true;
        });
        var ip = string.IsNullOrWhiteSpace(listenAddress) ? "0.0.0.0" : listenAddress;
        builder.WebHost.UseUrls($"http://{ip}:{port}");

        var app = builder.Build();

        app.UseDeveloperExceptionPage(); // TODO: 定位 /files 500 后移除
        app.Use(MonitorTransferMiddleware);
        app.UseDefaultFiles();
        app.UseStaticFiles(); // 浏览器端 Web UI (wwwroot)

        var rootProvider = new PhysicalFileProvider(rootPath);
        // 共享文件下载：/files/...，自带 Range 断点续传与视频拖动播放
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = rootProvider,
            RequestPath = "/files",
            ServeUnknownFileTypes = true,
        });

        ApiEndpoints.Map(app, this, rootPath, allowUpload);

        _app = app;
        app.Start();

        IsRunning = true;
        Started?.Invoke(port);
    }

    public async Task StopAsync()
    {
        if (_app is null)
            return;
        var app = _app;
        _app = null;
        IsRunning = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cts.Token);
        }
        finally
        {
            await app.DisposeAsync();
        }
        Stopped?.Invoke();
    }

    /// <summary>统计 /files 与 /zip 响应的字节数，写入传输日志（含 206 续传）。</summary>
    private async Task MonitorTransferMiddleware(HttpContext ctx, Func<Task> next)
    {
        if (!ctx.Request.Path.StartsWithSegments("/files") && !ctx.Request.Path.StartsWithSegments("/zip"))
        {
            await next();
            return;
        }

        var originalBody = ctx.Response.Body;
        var counter = new CountingStream(originalBody);
        ctx.Response.Body = counter;
        var sw = Stopwatch.StartNew();
        try
        {
            await next();
        }
        finally
        {
            ctx.Response.Body = originalBody;
            sw.Stop();
            if (ctx.Response.StatusCode is 200 or 206)
            {
                var isZip = ctx.Request.Path.StartsWithSegments("/zip");
                var name = isZip
                    ? "打包下载"
                    : Uri.UnescapeDataString(ctx.Request.Path.Value!["/files".Length..].TrimStart('/'));
                Log.Add(new TransferEntry(
                    DateTime.Now,
                    isZip ? TransferKind.Zip : TransferKind.Download,
                    name,
                    DescribeRemote(ctx),
                    counter.Bytes,
                    sw.Elapsed,
                    ctx.Response.StatusCode == 206 ? "续传" : ""));
            }
        }
    }

    public static string DescribeRemote(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null)
            return "?";
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return ip.ToString();
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long Bytes { get; private set; }

        public CountingStream(Stream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        // 本流不做缓冲，Flush 是空操作；转发异步 Flush 以兼容 Kestrel 的 AllowSynchronousIO=false
        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Bytes += count;
            _inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Bytes += buffer.Length;
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace HttpFileServer.Server;

public static class ApiEndpoints
{
    public static void Map(WebApplication app, ServerHost host, string root, bool allowUpload)
    {
        var log = host.Log;
        var uploads = host.Uploads;

        app.MapGet("/api/info", () => Results.Json(new
        {
            allowUpload,
            rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)),
        }));

        app.MapGet("/api/list", (string? path) =>
        {
            var (ok, dir, err) = ResolveUnder(root, path);
            if (!ok)
                return Results.BadRequest(new { error = err });

            var entries = new List<object>();
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (e.Attributes.HasFlag(FileAttributes.Hidden) || e.Attributes.HasFlag(FileAttributes.System))
                        continue;
                    if (e is DirectoryInfo d)
                        entries.Add(new { name = d.Name, isDir = true, size = 0L, modified = ToUnixMs(d.LastWriteTimeUtc) });
                    else if (e is FileInfo f)
                        entries.Add(new { name = f.Name, isDir = false, size = f.Length, modified = ToUnixMs(f.LastWriteTimeUtc) });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 部分目录无权限时返回已枚举到的内容
            }
            catch (IOException)
            {
            }

            var rel = ToRelative(root, dir);
            return Results.Json(new
            {
                path = rel,
                rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)),
                entries,
            });
        });

        // 多选打包下载：/zip?path=a&path=b（流式写出，不落盘）
        app.MapGet("/zip", async (HttpContext ctx) =>
        {
            var rels = ctx.Request.Query["path"].Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (rels.Count == 0)
                return Results.BadRequest(new { error = "未指定要打包的路径" });

            var items = new List<(string Name, string FullPath, bool IsDir, DateTime Modified)>();
            foreach (var rel in rels)
            {
                var (ok, full, err) = ResolveUnder(root, rel);
                if (!ok)
                    return Results.BadRequest(new { error = err });
                var fi = new FileInfo(full);
                var di = new DirectoryInfo(full);
                bool isDir = di.Exists && !fi.Exists;
                if (!isDir && !fi.Exists)
                    return Results.NotFound(new { error = $"不存在：{rel}" });
                items.Add((Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)), full, isDir,
                    isDir ? di.LastWriteTimeUtc : fi.LastWriteTimeUtc));
            }

            var zipName = items.Count == 1 ? $"{items[0].Name}.zip" : "打包下载.zip";
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(zipName);

            // Kestrel 会以 chunked 方式流式发送
            await using (var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var item in items)
                {
                    if (item.IsDir)
                        await AddDirToZipAsync(zip, item.FullPath, item.Name, ctx.RequestAborted);
                    else
                        await AddFileToZipAsync(zip, item.FullPath, item.Name, item.Modified, ctx.RequestAborted);
                }
            }
            return Results.Empty;
        });

        MapUploadEndpoints(app, host, uploads, root, allowUpload);
    }

    private static void MapUploadEndpoints(WebApplication app, ServerHost host, UploadSessionService uploads, string root, bool allowUpload)
    {
        if (!allowUpload)
        {
            app.MapPost("/api/upload/init", () => Results.Json(new { error = "服务器未开启上传" }, statusCode: 403));
            return;
        }

        app.MapPost("/api/upload/init", (InitRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Size < 0 || req.ChunkSize is < 64 * 1024 or > 32 * 1024 * 1024)
                return Results.BadRequest(new { error = "参数不合法" });
            var (ok, _, err) = ResolveUnder(root, req.Path);
            if (!ok)
                return Results.BadRequest(new { error = err });

            var (session, received) = uploads.Init(SanitizeFileName(req.Name), req.Path, req.Size, req.ChunkSize, req.LastModified);
            return Results.Json(new
            {
                uploadId = session.Id,
                received,
            });
        });

        app.MapPut("/api/upload/chunk/{id}/{index:int}", async (string id, int index, HttpContext ctx) =>
        {
            var session = uploads.Get(id);
            if (session is null)
                return Results.Json(new { ok = false, error = "上传会话不存在或已过期" }, statusCode: 404);
            var remote = ServerHost.DescribeRemote(ctx);
            var result = await uploads.WriteChunkAsync(session, index, ctx.Request.Body, remote);
            return result.Ok
                ? Results.Json(new { ok = true, received = result.Received })
                : Results.Json(new { ok = false, error = result.Error }, statusCode: 400);
        });

        app.MapPost("/api/upload/complete", async (CompleteRequest req) =>
        {
            var session = uploads.Get(req.UploadId);
            if (session is null)
                return Results.Json(new { ok = false, error = "上传会话不存在或已过期" }, statusCode: 404);
            var result = await uploads.CompleteAsync(session, root);
            if (!result.Ok)
                return Results.Json(new { ok = false, error = result.Error }, statusCode: 409);
            return Results.Json(new { ok = true, name = result.FileName, bytes = result.Bytes });
        });

        app.MapDelete("/api/upload/{id}", (string id) =>
        {
            uploads.Abort(id);
            return Results.Json(new { ok = true });
        });
    }

    public static (bool Ok, string FullPath, string? Error) ResolveUnder(string root, string? rel)
    {
        rel = rel?.Replace('\\', '/').Trim('/') ?? "";
        if (rel.Length == 0)
            return (true, root, null);

        foreach (var seg in rel.Split('/'))
        {
            if (seg is "." or ".." || seg.Length == 0 ||
                seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return (false, "", "非法路径");
        }

        var full = Path.GetFullPath(Path.Combine(root, rel));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return (false, "", "非法路径");
        return (true, full, null);
    }

    private static string ToRelative(string root, string dir)
    {
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rel = dir.Length > rootPrefix.Length ? dir[rootPrefix.Length..] : "";
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    /// <summary>去掉文件名中的路径分隔符等危险字符（浏览器可能传来包含路径的 name）。</summary>
    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace('/', '_').Replace('\\', '_').Trim().TrimStart('.');
        return name.Length == 0 ? "unnamed" : name;
    }

    private static async Task AddDirToZipAsync(ZipArchive zip, string dirPath, string prefix, CancellationToken ct)
    {
        var di = new DirectoryInfo(dirPath);
        try
        {
            var entries = di.EnumerateFileSystemInfos().ToList();
            if (entries.Count == 0)
            {
                var empty = zip.CreateEntry(prefix + "/", CompressionLevel.NoCompression);
                empty.LastWriteTime = di.LastWriteTime;
                return;
            }
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (e.Attributes.HasFlag(FileAttributes.Hidden) || e.Attributes.HasFlag(FileAttributes.System))
                    continue;
                if (e is DirectoryInfo sub)
                    await AddDirToZipAsync(zip, sub.FullName, prefix + "/" + sub.Name, ct);
                else if (e is FileInfo file)
                    await AddFileToZipAsync(zip, file.FullName, prefix + "/" + file.Name, file.LastWriteTimeUtc, ct);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task AddFileToZipAsync(ZipArchive zip, string filePath, string entryName, DateTime modifiedUtc, CancellationToken ct)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        entry.LastWriteTime = modifiedUtc;
        await using var entryStream = await entry.OpenAsync(ct);
        await using var fileStream = File.OpenRead(filePath);
        await fileStream.CopyToAsync(entryStream, ct);
    }

    public sealed record InitRequest(string Name, string Path, long Size, int ChunkSize, long LastModified);

    public sealed record CompleteRequest(string UploadId);
}

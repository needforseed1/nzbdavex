using System.Diagnostics;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore
    };

    public async Task<AddFileResponse> AddFileAsync(AddFileRequest request)
    {
        ValidateCategory(request.Category);
        queueManager.BeginQueuePrewarm();
        var id = Guid.NewGuid();
        var timer = Stopwatch.StartNew();
        Log.Information("queue-intake nzo={NzoId} file={FileName} stage=store start", id, request.FileName);

        // write the file to the blob-store
        await using var stream = request.NzbFileStream;
        await BlobStore.WriteBlob(id, stream);
        var storeMs = timer.ElapsedMilliseconds;

        // save the queue item to the database
        QueueItem? queueItem;
        try
        {
            // backup the nzb file if enabled
            if (configManager.IsNzbBackupEnabled())
            {
                var backupLocation = configManager.GetNzbBackupLocation();
                if (backupLocation != null)
                {
                    await BackupNzbAsync(id, request.FileName, request.Category, backupLocation);
                }
            }

            // compute the total segment bytes
            await using var nzbFileStream = BlobStore.ReadBlob(id);
            var totalSegmentBytes = ComputeTotalSegmentBytes(nzbFileStream);
            var scanMs = timer.ElapsedMilliseconds - storeMs;

            // create the queue item record
            queueItem = new QueueItem
            {
                Id = id,
                CreatedAt = DateTime.Now,
                FileName = request.FileName,
                JobName = FilenameUtil.GetJobName(request.FileName),
                NzbFileSize = nzbFileStream.Length,
                TotalSegmentBytes = totalSegmentBytes,
                Category = request.Category,
                Priority = request.Priority,
                PostProcessing = request.PostProcessing,
                PauseUntil = request.PauseUntil,
                IndexerName = request.IndexerName,
                ContentGroupKey = request.ContentGroupKey,
            };

            // record the original NZB filename so it can be served at download time
            var nzbName = new NzbName
            {
                Id = id,
                FileName = request.FileName
            };

            // save
            dbClient.Ctx.QueueItems.Add(queueItem);
            dbClient.Ctx.NzbNames.Add(nzbName);
            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
            _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"]);

            Log.Information(
                "queue-intake nzo={NzoId} file={FileName} stage=stored bytes={Bytes} storeMs={StoreMs} scanAndBackupMs={ScanMs} totalMs={TotalMs}",
                id, request.FileName, nzbFileStream.Length, storeMs, scanMs, timer.ElapsedMilliseconds);
        }
        catch
        {
            // in case of any errors writing to the database
            // delete the nzb file blob
            BlobStore.Delete(id);
            throw;
        }

        // inform the frontend that a new item was added to the queue
        var message = GetQueueResponse.QueueSlot.FromQueueItem(queueItem).ToJson();
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemAdded, message);

        // awaken the queue if it is sleeping
        queueManager.AwakenQueue(request.PauseUntil);

        // return response
        return new AddFileResponse()
        {
            Status = true,
            NzoIds = [queueItem.Id.ToString()],
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await AddFileRequest.New(httpContext, configManager).ConfigureAwait(false);
        return Ok(await AddFileAsync(request).ConfigureAwait(false));
    }

    private static async Task BackupNzbAsync(Guid id, string fileName, string category, string backupLocation)
    {
        try
        {
            if (!Directory.Exists(backupLocation))
                Directory.CreateDirectory(backupLocation);

            var backupRoot = Path.GetFullPath(backupLocation);
            var destDir = Path.GetFullPath(Path.Combine(backupRoot, category));
            if (!IsWithinRoot(backupRoot, destDir))
                throw new InvalidOperationException("Category escapes the configured NZB backup directory.");
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) ext = ".nzb";

            var destPath = Path.Combine(destDir, $"{baseName}{ext}");
            var counter = 2;
            while (System.IO.File.Exists(destPath))
            {
                destPath = Path.Combine(destDir, $"{baseName} ({counter}){ext}");
                counter++;
            }

            await using var src = BlobStore.ReadBlob(id);
            await using var dst = System.IO.File.Create(destPath);
            await src.CopyToAsync(dst);
        }
        catch (Exception e)
        {
            throw new Exception($"Could not save nzb to `{backupLocation}`", e);
        }
    }

    internal static void ValidateCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)
            || category.Length > 128
            || category is "." or ".."
            || category.Contains('/')
            || category.Contains('\\')
            || category.Any(char.IsControl))
        {
            throw new BadHttpRequestException(
                "Invalid category. Use one safe name without path separators.");
        }
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return string.Equals(root, candidate, comparison)
            || candidate.StartsWith(rootPrefix, comparison);
    }

    private static long ComputeTotalSegmentBytes(Stream stream)
    {
        long totalBytes = 0;
        using var reader = XmlReader.Create(stream, XmlSettings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "segment") continue;
            var bytesAttr = reader.GetAttribute("bytes");
            if (bytesAttr != null && long.TryParse(bytesAttr, out var bytes))
            {
                totalBytes += bytes;
            }
        }

        return totalBytes;
    }
}

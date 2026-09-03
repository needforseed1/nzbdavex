using System.Web;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Tasks;

public class StrmToSymlinksTask(
    ConfigManager configManager,
    DavDatabaseClient dbClient,
    WebsocketManager websocketManager
) : BaseTask
{
    protected override async Task ExecuteInternal()
    {
        try
        {
            var ct = SigtermUtil.GetCancellationToken();
            await ConvertAllStrmFilesToSymlinks(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report($"Failed: {e.Message}");
            Log.Error(e, "Failed to convert *.strm files to symlinks.");
        }
    }

    private async Task ConvertAllStrmFilesToSymlinks(CancellationToken token)
    {
        var completedCount = 0;
        using var debounce = DebounceUtil.CreateCancellableDebounce(TimeSpan.FromMilliseconds(200));
        ReportProgress("Scanning library for strm files...", completedCount);
        var scan = await LibraryLinkScanner.ScanAsync(configManager, cancellationToken: token).ConfigureAwait(false);
        if (!scan.IsComplete)
            throw new IOException($"Library link discovery failed: {scan.ErrorMessage}");
        var batches = scan.Links
            .Where(x => x.SymlinkOrStrmInfo is SymlinkAndStrmUtil.StrmInfo)
            .ToBatches(batchSize: 100);

        foreach (var batch in batches)
            await ConvertBatchOfStrmFilesToSymlinks(batch, OnItemCompleted, token).ConfigureAwait(false);
        debounce.CancelPending();
        ReportProgress("Done!", completedCount);
        return;

        void OnItemCompleted()
        {
            completedCount++;
            debounce.Invoke(() => ReportProgress("Scanning library for strm files...", completedCount));
        }
    }

    private async Task ConvertBatchOfStrmFilesToSymlinks
    (
        List<LibraryLinkScanner.DavItemLink> batch,
        Action onItemCompleted,
        CancellationToken token
    )
    {
        var items = batch
            .Select(x => new { Link = x, Extension = GetExtension(x) })
            .ToList();
        var davItemsToFetch = items
            .Where(x => x.Extension is null)
            .Select(x => x.Link.DavItemId)
            .ToList();
        var davItems = await dbClient.Ctx.Items
            .Where(x => davItemsToFetch.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x, token).ConfigureAwait(false);
        var itemsWithExtension = items
            .Select(x => new
            {
                Link = x.Link,
                Extension = x.Extension ?? Path.GetExtension(davItems[x.Link.DavItemId].Name).TrimStart('.'),
            })
            .ToList();

        var mountDir = configManager.GetRcloneMountDir();
        foreach (var item in itemsWithExtension)
        {
            var symlinkPath = PathUtil.ReplaceExtension(item.Link.LinkPath, item.Extension);
            var symlinkTarget = DatabaseStoreSymlinkFile.GetTargetPath(item.Link.DavItemId, mountDir);
            await Task.Run(() =>
            {
                File.CreateSymbolicLink(symlinkPath, symlinkTarget);
                File.Delete(item.Link.LinkPath);
            }).ConfigureAwait(false);
            onItemCompleted?.Invoke();
        }
    }

    private string? GetExtension(LibraryLinkScanner.DavItemLink link)
    {
        if (link.SymlinkOrStrmInfo is not SymlinkAndStrmUtil.StrmInfo strmInfo) return null;
        var queryParams = HttpUtility.ParseQueryString(new Uri(strmInfo.TargetUrl).Query);
        return queryParams.Get("extension");
    }

    private void Report(string message)
    {
        _ = websocketManager.SendMessage(WebsocketTopic.StrmToSymlinksTaskProgress, message);
    }

    private void ReportProgress(string message, int completedCount)
    {
        Report($"{message}\nConverted: {completedCount} strm file(s) to symlinks.");
    }
}

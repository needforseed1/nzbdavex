using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.Controllers.DeleteWebdavItem;

[ApiController]
[Route("api/delete-webdav-item")]
public class DeleteWebdavItemController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    WebsocketManager websocketManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (configManager.IsEnforceReadonlyWebdavEnabled())
            return StatusCode(403, new BaseApiResponse
            {
                Status = false,
                Error = "WebDAV is read-only. Disable 'Enforce Read-Only' in Settings → WebDAV."
            });

        var path = HttpContext.Request.Form["path"].FirstOrDefault()
                   ?? throw new BadHttpRequestException("path is required");
        var ct = HttpContext.RequestAborted;

        var item = await ResolvePathAsync(path, ct).ConfigureAwait(false);
        if (item is null) return NotFound(new BaseApiResponse { Status = false, Error = "Item not found." });
        if (item.IsProtected())
            return StatusCode(403, new BaseApiResponse { Status = false, Error = "Cannot delete protected item." });

        var deletedItems = new List<DavItem>();
        await DeleteRecursiveAsync(item.Id, deletedItems, ct).ConfigureAwait(false);
        var historyIds = deletedItems
            .Select(x => x.HistoryItemId)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        await DavDatabaseContext.RcloneVfsForget(deletedItems).ConfigureAwait(false);
        await PruneEmptyHistoryAsync(historyIds, ct).ConfigureAwait(false);
        return Ok(new BaseApiResponse { Status = true });
    }

    private async Task<DavItem?> ResolvePathAsync(string path, CancellationToken ct)
    {
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var current = DavItem.Root;
        foreach (var raw in parts)
        {
            var name = Uri.UnescapeDataString(raw);
            var child = await dbClient.GetDirectoryChildAsync(current.Id, name, ct).ConfigureAwait(false);
            if (child is null) return null;
            current = child;
        }
        return current;
    }

    private async Task DeleteRecursiveAsync(Guid id, List<DavItem> deletedItems, CancellationToken ct)
    {
        var childIds = await dbClient.Ctx.Items
            .Where(x => x.ParentId == id)
            .Select(x => x.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var childId in childIds)
            await DeleteRecursiveAsync(childId, deletedItems, ct).ConfigureAwait(false);
        var item = await dbClient.Ctx.Items.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (item is null) return;
        deletedItems.Add(item);
        dbClient.Ctx.Items.Remove(item);
    }

    private async Task PruneEmptyHistoryAsync(IReadOnlyList<Guid> historyIds, CancellationToken ct)
    {
        var removedIds = new List<Guid>();
        foreach (var historyId in historyIds)
        {
            var stillReferenced = await dbClient.Ctx.Items
                .AsNoTracking()
                .AnyAsync(x => x.HistoryItemId == historyId, ct)
                .ConfigureAwait(false);
            if (stillReferenced) continue;

            var history = await dbClient.Ctx.HistoryItems
                .FirstOrDefaultAsync(h => h.Id == historyId, ct)
                .ConfigureAwait(false);
            if (history is null) continue;

            dbClient.Ctx.HistoryItems.Remove(history);
            removedIds.Add(historyId);
        }

        if (dbClient.Ctx.ChangeTracker.HasChanges())
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        if (removedIds.Count > 0)
            _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", removedIds));
    }
}

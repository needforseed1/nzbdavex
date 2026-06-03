using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

public static class WtReconcile
{
    public static string ExpanderTag(string expanderKey) => "exp:" + expanderKey;

    public static async Task RemoveWithChildrenAsync(DavDatabaseContext ctx, WantedItem item, long now, CancellationToken ct)
    {
        if (item.State == WantedItem.StateExpander)
        {
            var tag = ExpanderTag(item.Key);
            var children = await ctx.WantedItems
                .Where(w => w.Provenance.Contains(tag))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var child in children)
            {
                var prov = WtJson.ReadStrings(child.Provenance);
                if (!prov.Contains(tag)) continue;
                prov.Remove(tag);
                if (prov.Count == 0)
                {
                    ctx.WantedItems.Remove(child);
                }
                else
                {
                    child.Provenance = WtJson.WriteStrings(prov);
                    child.UpdatedAtUnix = now;
                }
            }
        }

        ctx.WantedItems.Remove(item);
    }
}

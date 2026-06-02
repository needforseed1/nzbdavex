using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

public class WatchtowerStore(PreflightCache preflightCache)
{
    public async Task TryWarmForPlayAsync(string type, string contentId, CancellationToken ct)
    {
        try
        {
            var key = $"{type}:{contentId}";
            await using var ctx = new DavDatabaseContext();
            var item = await ctx.WantedItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Key == key, ct)
                .ConfigureAwait(false);
            if (item is null || item.State != WantedItem.StateReady || item.WinnerNzb is null) return;

            var winner = WtJson.ReadPointers(item.Shortlist).FirstOrDefault();
            if (winner is null || winner.Verdict != "available") return;

            preflightCache.SetVerified(winner.NzbUrl, item.WinnerNzb,
                PlaybackFastVerifier.Verdict.Available, item.ResponderHost);
            Log.Debug("Watchtower: warmed play cache for {Key} ({Title})", key, winner.Title);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Watchtower: warm-for-play failed for {Type}/{Id}", type, contentId);
        }
    }
}

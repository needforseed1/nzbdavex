# Watchtower

Watchtower keeps the titles on your lists **ready** — pre-resolved and proven alive — so
playback starts with no search and no spinner. It's the **watchdog, time-shifted**: the same
ranker, the same STAT health check, the same indexer caps — run *ahead* of the click instead
of *at* it.

**Pointer-only by design.** It stores the winning NZB's segment map (kilobytes) and a small
verified shortlist — never the video. It's a window kept clean, not a library. The one default
that defines the product is `resolve-only`: nothing is downloaded until a human presses play.

## How it works

It's a source-agnostic list feeding the existing playback engine. A background service runs
three loops:

1. **Sync** — every enabled source enumerates into content identities (`type:id`, canonical
   imdb). They merge into one **deduped wanted-set**; an item stays wanted while ≥1 source
   claims it. Dropping off every list removes the row — **downloaded files are never touched**.
2. **Resolve** — for items with no fresh winner: search once (cheap), filter by size
   floor/ceiling, take **biggest-first**, then STAT-verify down the list. The first healthy one
   wins; up to `shortlist-depth` healthy pointers are kept as backups. Bounded by a daily
   resolve budget, an active warm-set cap, and a grab cap (NZB fetches are the scarce indexer
   bucket, so it's deliberately stingy).
3. **Keep-fresh** — re-STAT the winner **grab-free** from its stored segment map on age-based
   backoff. If it died, promote a backup; if the shortlist empties, re-resolve.

**Instant playback, zero hot-path surgery.** On a Play click, `ProfilePlayController` already
consults `PreflightCache`. Watchtower simply warms that cache with the verified winner's bytes
(`WatchtowerStore.TryWarmForPlayAsync`), so the pre-verify is an instant hit — no fetch, no
STAT. On a miss it degrades to exactly today's behavior.

## Sources (agnostic)

- **Manual** — add a title by imdb id on the Watchtower page.
- **Stremio catalog** — any catalog URL (`…/catalog/movie/xyz.json`). This transitively
  supports every list wrapped as a Stremio catalog addon (Trakt, MDBList, Letterboxd, …).
- **URL list** — a JSON array / `{items:[…]}` or plain newline-delimited `type:id` / imdb ids.

Adding a new source kind = one `switch` case in `ListSourceEnumerator`; the engine is unchanged.

## Safety

- Reuses `IndexerHitTracker` (per-indexer query + grab caps, disable-at-cap) and
  `NewznabRateLimiter`. Same discipline that keeps Sonarr/Radarr safe on the same indexers.
- **Safe defaults, opt-in escalation** — off until enabled; conservative budget, cap, and
  resolve-only. The knobs only *raise* limits.
- **Active warm-set cap** bounds standing load no matter how large the lists get: beyond the
  cap, items are listed but parked.

## Configuration (`watchtower.*`, Settings → Watchtower)

| Key | Default | Meaning |
|-----|---------|---------|
| `enabled` | `false` | Master switch. |
| `profile-token` | `""` | Search profile to resolve with (empty = first profile). |
| `ranking` | `watchdog` | `watchdog` = pick what a Play click would pick (warm is reliably used); `largest` = biggest healthy release. |
| `size-floor-bytes` | `524288000` | Junk floor (~0.5 GB). |
| `size-ceiling-bytes` | `0` | Bandwidth ceiling (0 = none). |
| `min-grabs` | `0` | Optional fake filter. |
| `shortlist-depth` | `2` | Live winner + backups. |
| `grab-cap-per-resolve` | `3` | Max NZB fetches per pass (scarce bucket). |
| `verify-sample-count` | `3` | STAT sample segments. |
| `active-set-cap` | `100` | Items kept actively ready. |
| `daily-resolve-budget` | `60` | Soft new-resolves/day (0 = unlimited). |
| `sync-interval-seconds` | `3600` | Remote list refresh cadence. |
| `keepfresh-base/max-seconds` | `21600` / `604800` | Re-verify backoff window. |
| `unavailable-retry-seconds` | `21600` | Re-search cadence when nothing healthy found. |

## Code map

- Models: `Database/Models/{ListSource,WantedItem}.cs` (+ migration + snapshot).
- Engine: `Services/Watchtower/{WatchtowerService,WatchtowerStore,ListSourceEnumerator,WatchtowerModels}.cs`.
- Config: `Config/ConfigManager.cs` (`watchtower.*` getters).
- API: `Api/Controllers/Watchtower/{GetWatchtower,WatchtowerMutate}Controller.cs`.
- Play hook: `Api/Controllers/Profiles/ProfilePlayController.cs` (one warm call).
- UI: `frontend/app/routes/watchtower/` (page) + `routes/settings/watchtower/` (tuning tab).

## Status / not yet

- Movies + explicit episodes only (no whole-series auto-tracking — that's Sonarr's job).
- Dedup is exact-key; cross-namespace collapse (tmdb↔imdb for the same title) is a follow-up.
- Optional later: head-prebuffer for a tiny "next up" set; RSS-sync matching; expose the
  wanted-set *as* a Stremio catalog.

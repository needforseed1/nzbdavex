# Activity page

A companion to Watchdog. Watchdog answers *"why was this NZB picked?"*. Activity
answers *"what read it, and how did it stream?"* — which mount activity occurred,
what was played, which providers served the articles, and whether the source
showed signs that could affect playback.

Status legend: ☐ not started · ◐ in progress · ☑ done

## Current state of the code (pre-existing)

| Piece | Location | What it holds |
|---|---|---|
| `PlaybackRequestDiagnostics` | `backend/Services/PlaybackDiagnosticContext.cs` | Per-HTTP-request: first-byte ms, upstream/downstream stalls, fallback rescues, provider rotations, fallback-budget exhaustions, cache hit/miss, connection-permit and provider-pool waits, per-backup-provider attempts/rescues/misses/timeouts/errors. **Log-only.** |
| `ActiveReadRegistry` | `backend/Services/ActiveReadRegistry.cs` | Live session keyed by `(path, clientKey)`, 15 s idle window (suspended while an HTTP request is open), bytes served, current offset, client IP/UA, end reason. |
| `ReadSession` | `backend/Database/Models/Metrics/ReadSession.cs` | Terminal row per session, written on prune by `ActiveReadsBroadcaster`. 90-day TTL. |
| `SegmentFetch` | `backend/Database/Models/Metrics/SegmentFetch.cs` | Per-article provider + status + duration, linked by `ReadSessionId`. **24-hour TTL.** |
| `ProviderUsageTracker` | `backend/Services/ProviderUsageTracker.cs` | Per-session provider → segment counts and byte counts, failover saves. |
| Page pattern to mirror | `backend/Api/Controllers/GetWatchdogEntries/*`, `frontend/app/routes/watchdog/*` | Controller + DTO + nickname resolution + polling page + pure-logic modules with unit tests. |

## Gaps this feature closes

1. Request diagnostics are discarded at request end; a session spans many range
   requests (each seek starts a new one).
2. `ReadSession` has no content identity — only an opaque `/content/{guid}` path.
3. `SegmentFetch` expires after 24 h while sessions live 90 days, so per-provider
   detail must be denormalised onto the session row.
4. Sessions fragment: a 15 s idle gap ends one and starts another, so one film is
   several rows. The page groups them back into a "play".
5. On SIGTERM the broadcaster returns without a final prune, so in-flight reads are
   never persisted.

---

## Phase 1 — persist rich playback sessions ☑

Backend only, no UI. Everything the log already knows becomes a durable row.

- ☑ `backend/Services/PlaybackSessionStats.cs` — singleton keyed by session id.
  Requests fold their totals in on completion; counters sum, `Max*Ms` take the
  maximum, first-byte takes the chronologically first request's, per-provider
  backup activity merges by id.
  `Take` is snapshot-and-forget so a session persists exactly once; `DropStale`
  is the safety net for accumulators that never reach the prune path.
- ☑ `PlaybackDiagnosticContext.cs` — `Complete()` folds a `PlaybackRequestDelta`
  into `PlaybackSessionStats` before logging; existing log output unchanged. Also
  tracks a `_maxOffset` that does not rewind on seek.
- ☑ `ReadSession` + `MetricsDbContext` + metrics migration
  (`20260726000000_Add-Playback-Session-Diagnostics`) — new columns:
  `FileName`, `DavItemId`, `HistoryItemId`, `FirstByteMs`, `RequestCount`,
  `MaxOffset`, `UpstreamStalls`, `MaxUpstreamStallMs`, `DownstreamStalls`,
  `MaxDownstreamStallMs`, `FallbackRescues`, `ProviderRotations`,
  `FallbackBudgetExhaustions`, `CacheHits`, `CacheMisses`,
  `ConnectionPermitWaits`, `MaxConnectionPermitWaitMs`, `ProviderPoolWaits`,
  `MaxProviderPoolWaitMs`, `ProviderStatsJson`, `ErrorNote`. Index on
  `(DavItemId, StartedAt)`. Model snapshot updated to match.
- ☑ `ActiveReadRegistry` — entries carry `DavItemId`, `HistoryItemId` and a
  non-rewinding `MaxOffset`; `UpdateContentIds` attaches the identity once the
  dav item is resolved; `DrainAll` supports the shutdown flush.
- ☑ Playback entry points (`WebDav/Base/GetAndHeadHandlerPatch.cs`,
  `Api/Controllers/GetWebdavItem/GetWebdavItemController.cs`) — pass the stats
  singleton to the diagnostics object and register the content ids.
  `DatabaseStoreIdFile` now exposes `DavItemId` alongside `HistoryItemId`.
- ☑ `ActiveReadsBroadcaster` — `PersistSession` fills the new columns on prune,
  merging tracker usage/bytes with the diagnostics backup activity into
  `ProviderStatsJson`; `StopAsync` drains the registry so reads still in flight at
  shutdown are persisted instead of lost.
- ☑ `Program.cs` — `PlaybackSessionStats` registered as a singleton.
- ☑ Tests — `backend.Tests/Services/PlaybackSessionStatsTests.cs` covers the fold
  arithmetic, backup merge, take-once semantics, stale drop, the diagnostics →
  session hand-off (including double-`Complete`) and the provider-stats merge.
  Full backend suite: 404 passing.

Verified separately with a throwaway harness (not kept — `MetricsDbContext`
caches its options in a static `Lazy`, so a test that repoints `CONFIG_PATH`
would be order-dependent): migrating a metrics database that sits at
`AddFailoverEdges` preserves existing rows, defaults the new counters to 0, and
accepts writes to every new column.

`ProviderStatsJson` shape (mirrors the watchdog prep-stats convention so the
frontend `ProviderSummary` component can be reused):

```json
[{"providerId":"...","segments":0,"bytes":0,"attempts":0,"rescued":0,
  "missing":0,"timeouts":0,"errors":0,"isBackup":false}]
```

Hosts and nicknames are resolved from live config at read time, never stored, so a
provider rename retroactively fixes old rows.

## Phase 2 — API ☑

- ☑ `GET /api/get-playback-sessions?limit&sinceUnix&filter=all|issues|failed` —
  reads `ReadSessions`, resolves content names from the operational database in a
  second query (metrics lives in its own SQLite file, so no SQL join), resolves
  provider hosts/nicknames from live config, and groups consecutive sessions
  sharing `(DavItemId ?? Path, ClientIp, ClientUserAgent)` within a 10-minute gap
  into one play. `limit` clamps to 200–2000 (default 500) and applies to sessions,
  before
  grouping — so the response also carries `sampledSessions` and `truncated`, and
  the page discloses that its counts cover a sample.
- ☑ `GET /api/get-playback-session-detail?id=<guid>` — one session in full plus the
  retained `SegmentFetch` rows (newest 500) and per-provider/status counts.
  `articleDetailAvailable` + `articleDetailExpired` + `articleRetentionHours` let
  the UI distinguish "expired after 24 h" from "never recorded" instead of
  claiming expiry for an empty table.
- ☑ `POST /api/clear-playback-sessions[?olderThanDays=N]` — wipes or trims history.
  **Side effect**: the overview page's session tiles read the same `ReadSessions`
  table, so a wipe clears those totals too. The UI must say so before confirming.
- ☑ Issue flags computed server-side in `PlaybackHistory.Issue`: `corrupted`,
  `body-stalled`, `stalled`, `rescued`, `backup-used`, `rotated`,
  `budget-exhausted`, `pool-starved`,
  `permit-starved`, `aborted`, `timeout`, `error`. All remain available as
  diagnostics, while the `issues` filter is limited to viewer-impact signals:
  damaged data, material source delays, timeout, or failure.
- ☑ Play-level aggregation: counters sum, `Max*` take the max, first-byte takes the
  chronologically first session's measurement (startup, not the fastest seek),
  providers merge by id, the **last** fragment decides `endReason`.
  `reachedPct` comes from `MaxOffset / FileSize`, `avgBytesPerSecond` from bytes
  served over watched time.
- ☑ Tests — `backend.Tests/Api/PlaybackHistoryTests.cs` (11 cases: provider naming,
  legacy rows, corrupt JSON, every issue flag, seek-fragment joining, long-gap and
  per-client splitting, cross-fragment merge, title fallbacks, progress/rate,
  filters). Full backend suite: 415 passing.

Files: `backend/Api/Controllers/GetPlaybackSessions/{GetPlaybackSessionsController,
GetPlaybackSessionsResponse,PlaybackHistory}.cs`,
`backend/Api/Controllers/GetPlaybackSessionDetail/*`,
`backend/Api/Controllers/ClearPlaybackSessions/*`.

Smoke-tested against a real instance (temporary `CONFIG_PATH`, `--db-migration`
then normal boot): the metrics migration applies through the production startup
path, all three endpoints respond, auth returns 401 without an API key, an invalid
id returns a 400 with a readable message, and two seeded fragments 100 s apart came
back as a single play with summed counters and merged providers.

## Phase 3 — frontend ☑

- ☑ `frontend/app/routes/playback/{route.tsx,route.module.css,route.module.css.d.ts,
  playback-view.ts,playback-view.test.ts}` — the page, styled to match Watchdog.
- ☑ `frontend/app/routes/settings.playback-sessions/route.tsx` — resource route
  behind the api key, serving the polling list, the lazily-expanded session detail
  (`?id=`) and the clear action, mirroring `settings.watchdog-attempts`.
- ☑ Nav entry (play-circle icon) between Watchdog and Watchtower; three client
  methods in `backend-client.server.ts` plus the response types; 5 s poll loop with
  an Auto-refresh/Paused toggle, and `playsEqual` so an unchanged poll skips the
  re-render.
- ☑ Card: verdict pill · title · category/client/session-count badges · age; then
  a summary line of Watched / Reached / Served / To client / Fetch avg / Startup,
  and provider chips (reused `ProviderSummary` popover).
- ☑ Expanded: **Delays** (worst first, or "Never waited on data"), **Retrieval**
  (rescues, switches, cache ratio, or "Served straight from the first provider"),
  **Source** (file, NZB, size, bytes fetched, client), the per-provider table, and
  the session list. Expanding a session lazily fetches its article breakdown and
  says so plainly when the 24 h raw detail has expired.
- ☑ The headline is outcome-first: **No source issue**, **Source delays**,
  **Damaged**, **Timed out**, or **Failed**. Successful fallback, connection
  replacement, and queue waits stay as neutral expanded diagnostics rather than
  making a successfully delivered play look unhealthy. Client user agents are
  named ("Infuse", "VLC", "Kodi", …) instead of dumped raw.
- ☑ Legacy rows render a plain notice that diagnostics were never captured, so
  zeroed counters are never mistaken for a flawless stream.
- ☑ The clear confirmation states that the overview session totals reset too.
- ☑ Tests — `playback-view.test.ts` (11 cases: badge ordering/tone, stop-is-not-
  trouble, verdict escalation, filters matching the backend, client naming, delay
  ordering, cache ratio, provider shares, host shortening, formatting, poll
  equality), registered in `npm test`. Frontend suite: 32 passing.
  `npm run typecheck` and `npm run build` both clean.

The resource route serves both the list and the lazily loaded `?id=` detail.

## Outcome-first UX (2026-07-27)

- The collapsed card answers one question: did the source show a condition that
  could affect playback? It does not expose internal recovery codes as competing
  warning badges. Backup use remains visible as a neutral **Backup used** marker,
  because it is useful provider context even when delivery succeeded.
- **Source delays** on a completed play means either one continuous source wait
  lasting ten seconds, or at least three seconds of non-overlapping source waits
  that consumed ten percent of the recorded activity. This is explicitly
  described as a buffering risk, not proof that the player paused; the expanded
  Delays panel shows the actual count, total, and longest wait. Request logs use
  the earlier operational threshold of three waits or one lasting three seconds,
  because an in-flight request has no final activity duration to compare against.
- Successful fallback, backup use, provider rotation, connection replacement,
  pool waits, permit waits, and retry-budget events remain visible after
  expansion with neutral styling. They only affect the headline when the play
  also failed, timed out, served damaged bytes, or crossed the source-delay
  threshold.
- The live status is transient: **Waiting on source** clears back to
  **Streaming** when the wait ends. Earlier recovered connections remain a
  secondary count rather than permanently changing the live verdict.
- The **Source issues** filter and scan classification use the same viewer-impact
  set. A small successful scan is still a scan even when the backend recovered a
  connection or changed provider.

- Connection-wait thresholds remain raised —
  `WaitIssueMinCount`/`WaitIssueMinMs`, mirroring the stall rule — because
  `> 0` fired on waits the buffer had comfortably absorbed.
- Client identity is resolved through `ClientAddressUtil` rather than read from
  the socket. Playback reaches viewers through a proxy on the same host, so
  every session recorded `::1` with the proxy's user agent — the field named
  the proxy while implying it named the viewer, and the session key collapsed
  every device into one. `X-Forwarded-For` is honoured only when the immediate
  peer is loopback or private, because the header is attacker-controlled from
  the public internet and this value groups sessions.

Completed playback history now judges non-overlapping source-wait time against
the duration of the activity. This keeps ordinary cold-start preparation from
being labelled as a source delay merely because it produced several short waits,
without special-casing preparation or hiding a sustained interruption.

## Phase 4 — polish ☐ (next)

- `playback.history-retention-days` setting feeding `MetricsRetentionService`
  (currently a hard-coded 90 days).
- Cross-link a play to the watchdog entry that grabbed it, via `HistoryItemId` →
  `HistoryItem.ContentGroupKey` → `WatchdogEntry`.
- Optional: a per-play throughput sparkline from `ThroughputMinute`, and a "worst
  plays this week" summary at the top of the page.

## What the stall counters actually mean

`PlaybackTransferPump` times both halves of every 64 KB chunk it moves: the read
from usenet and the write to the client socket.

- **Upstream stall** (`UpstreamStalls`) — the read took ≥1 s. The server was
  waiting on usenet. This is the one worth reporting, and even then the player's
  own buffer may have absorbed it, so the page says **Source delays** rather than
  "buffering" — the server cannot see whether the picture actually froze.
- **Downstream stall** (`DownstreamStalls`) — the write took ≥1 s. A write only
  blocks when the client stops reading, which happens because its buffer is full.
  **This is what healthy playback looks like**: the player races ahead, fills its
  buffer, then throttles. It is never an issue badge, never in the "with issues"
  filter, sorts last in the Delays panel as "Player buffer full (normal)", and
  logs at Debug rather than Warning.

Observed for real: a 1080p WEB-DL served 267 MB in 51 s with 0 upstream stalls and
8 downstream stalls, and played back perfectly. An early version badged that as
"Buffering" — precisely backwards.

The `stalled` issue additionally needs ≥3 upstream stalls or one lasting ≥3 s, so
a single one-second blip does not mark a whole play as a source issue.

## Library scans are not playback

Plex, Jellyfin and ffprobe open every file in the library and read a few kilobytes
of header. At the protocol level that is a GET like any other, so it becomes a
`ReadSession` — and there are a lot of them: one hour of this instance's history
holds 573 such reads across 536 distinct files, averaging 18.7 KB each. Roughly a
third of all recorded sessions are scans.

They are classified by **how little they served** (`< 8 MB`), because duration does
not discriminate — an observed scan read 330 KB while holding the file open for
14 s. A play that hit trouble is never classified as a scan regardless of size: a
stream that died after 20 KB is the most interesting row on the page.

The page therefore defaults to a **Watched** filter, with **Scans** as its own chip
and a `scan` badge on those cards. The API still returns everything;
`filter=plays|scans|issues|failed|all` selects.

## Phase 4 — review fixes ☑

A read-through of the implementation found the page reporting several real faults
as clean. What changed:

- ☑ **A live session is no longer pruned mid-play.** `ActiveReadRegistry` counts
  open HTTP requests; the 15 s idle window only applies once the last one has
  ended (30 min backstop for a leaked end signal). Previously a player that filled
  its buffer and paused, or a source that went quiet for 15 s, had its terminal row
  written mid-stream, its accumulators taken, and every stall, byte and provider
  fact that followed silently discarded — the row it left behind read as clean.
- ☑ **Zero-fills are counted.** `ZeroFilledSegments` / `ZeroFilledBytes` on the
  session row, a `corrupted` issue, a **Damaged** verdict (red, not amber) and a
  Retrieval row. Substituted zeros are the only failure here that means wrong data
  rather than late data, and nothing on the page had recorded them.
- ☑ **Pipelined bodies get the same treatment as plain ones**: the
  body-progress watchdog (they had none — a wedged socket had nothing to catch
  it), a retry budget before any zeros are served (they zero-filled on the first
  drain failure, while the sequential path retried twice), and `SegmentFetch`
  rows so the 24 h article detail is not empty for a fresh session.
- ☑ **Waits that end in a client abort are counted**, and long waits are reported
  *while they run* rather than only when they resolve, so the live view cannot show
  a healthy read for as long as it is stuck. A wait reported repeatedly still counts
  once. End-of-stream waits are deliberately not counted — the source owed nothing.
- ☑ **Recovered connections are counted** (`BodyStallRecoveries`, `body-stalled`
  diagnostic). A watchdog catch that a refetch recovered from used to be a Debug
  line; it is now visible after expansion without changing a successful play's
  headline.
- ☑ **One threshold, shared.** `PlaybackIssueThresholds` is used by both the
  Warning log level and the page's source-delay status, so a lone 1 s wait no
  longer warns while the page presents it as material.
- ☑ **Log and page diverge deliberately, and only where they should.** They
  answer different questions for different readers:

  | Signal | Page | Log | Why |
  |---|---|---|---|
  | Zero-filled articles | Damaged (bad) | Warning | The viewer got wrong data. |
  | Failed / timed out | bad | Warning | Terminal. |
  | Source waits past threshold | Source delays (warn) | Warning | The completed page uses duration-aware playback-risk thresholds; an in-flight request log uses count/worst-wait thresholds because its final duration is not yet known. |
  | Recovered wedged connection | neutral | **Warning** | The viewer saw nothing, so the page stays quiet — but a fault that heals leaves no other trace, and an operator wants it. |
  | Backup used / rescue / rotation / retry limit | neutral | **Information** | Routine failover on a healthy play. Warning on it drowns the rows above; the request-end line still carries the counts. |

  Both sides are commented where they diverge so the split does not read as an
  oversight later.
- ☑ **Startup is startup.** First-byte takes the first request *chronologically*,
  not the smallest measurement across the play; a 40 ms mid-play seek used to
  overwrite a 1.2 s cold start. Labelled "Startup" on the card.
- ☑ **"From usenet" renamed "Fetch avg"**, with a tooltip that says what it is:
  everything fetched over the length of the play, pauses and prefetch included. It
  was drawing provider-speed conclusions the metric cannot support.
- ☑ **Sample size is disclosed.** The API returns `sampledSessions` / `truncated`
  (default sample raised to 500 rows), and the page says so when the counts cover
  only the most recent reads rather than all history.
- ☑ **"Detail expired" is only said when retention explains it**
  (`articleDetailExpired`), not whenever there are no rows.
- ☑ **Playing now.** The page renders in-flight reads from the active-reads
  websocket above the history. A play used to be invisible for its whole duration
  and appear ~20 s after it ended, which is the wrong half of the problem; the
  toolbar toggle is honestly labelled "Auto-refresh", since that is what it does.
- ☑ The provider popover trigger is a sibling of the expand button rather than a
  button nested inside a button.

Tests: `ActiveReadRegistryTests` (prune guard, concurrent requests, leak backstop),
zero-fill/body-stall folding, wait-counted-once, abort-counts-the-wait, live wait
reporting, threshold-based log level and issue classification, damaged verdict.
Backend 460 passing, frontend 35 passing, typecheck clean.

## Notes and risks

- Hot-path cost is one dictionary fold per finished HTTP request; no new database
  writes, the existing `ReadSession` row just gets wider.
- Primary-provider segment counts come from `ProviderUsageTracker` while backup
  activity comes from the diagnostics object — merge by provider id so backups are
  not counted twice.
- `ProviderUsageTracker` keys usage and bytes by provider **id**, not host.
- Phase 1 stores nothing new for sessions that predate it; the page must treat
  zeroed counters and a null `ProviderStatsJson` as "not recorded", not "clean".
- `ReadSession.MaxOffset` is the furthest byte served, so watched-percentage is an
  approximation — a player that seeks to the end to read the index will inflate it.
- **Client identification is mostly absent**: only 42 of the last 200 sessions
  carry a user-agent, and those rows have no client IP either, so most cards show
  "unknown". Worth tracing which request path drops them before leaning on the
  client column for anything.
- **Cold starts look like slow sources.** The first play of a freshly grabbed NZB
  pays for the grab, prep and health check, which surfaces as upstream stalls
  mid-stream (observed: 7 stalls, worst 6.6 s, on a first play whose first byte
  arrived in 171 ms — the waits came later, while prep was still catching up).
  Playing an already-prepped NZB is far faster. Candidate refinement: compare the
  session start against `HistoryItem.CreatedAt` and label a first play as a cold
  start instead of flagging it as a source delay.

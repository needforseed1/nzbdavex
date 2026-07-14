# Changelog

## [1.3.2](https://github.com/needforseed1/nzbdavex/compare/v1.3.1...v1.3.2) (2026-07-14)

This release tightens provider recovery and live reconfiguration, removes several preparation stalls, and makes the resulting work substantially easier to inspect.

### Highlights

* Provider circuit breaking is concurrency-aware, recovers promptly from transient failure bursts, and uses a controlled STAT probe to restore providers isolated during BODY preparation.
* Provider graph reloads now drain active clients instead of disposing them underneath in-flight work.
* RAR preparation reuses retained prefixes, bounds expensive parser fallbacks, and avoids redundant full-volume reads for ordinary split archives.
* Watchdog entries persist expandable health-routing and preparation breakdowns, including provider shares, misses, failures, fallback counts, and phase timings.
* Settings metadata, shared validation, and frontend state handling were consolidated; provider testing remains mandatory before save, while connection and health-pipeline benchmark recommendations are shown independently.

### Fixes

* Shared-host provider accounts keep independent bandwidth histories through stable provider IDs.
* Queue imports warm eligible provider connections earlier, and health-only providers retain their configured authenticated capacity between jobs.
* Browsers without `crypto.randomUUID()` can create providers and indexers and regenerate API keys.
* Watchdog timing distinguishes health checks that did not run from completed checks.

## Local fork changes versus upstream (2026-07-10–2026-07-14)

This section records changes in this repository that are not yet present in the `qooode/nzbdavex` upstream repository. The work focuses on getting from an added NZB to verified, playable content faster, making high-connection Usenet setups more predictable, and completing an application-wide settings audit.

### Faster preparation and health checks

* NZB preparation now starts promptly and spreads work across eligible providers based on available connection capacity.
* Article health checks use bulk, pipelined `STAT` batches instead of checking articles one at a time. Batches remain intact when work falls back to another provider.
* Health-check lanes and pipeline depth can be tuned independently from playback. Providers can override the global pipeline depth.
* A per-provider health pipeline benchmark measures throughput and reliability, then recommends a suitable depth.
* Providers can be marked as health-check-only. This is useful for fast block or trial accounts because `STAT` checks transfer very little data and the account is kept out of playback traffic.
* Partial-coverage health providers are admitted when their measured STAT speed still reduces total completion time; their misses fall directly through to a full-coverage provider instead of being repeated across similar partial accounts.
* The default Usenet connection limit is now 64 and remains configurable.
* File-size probes read only the required yEnc header instead of waiting for an entire article to be cached, removing an intermittent processor-tail delay during prep.
* Eager RAR prep parses continuation headers directly from the 16KB prefix already retained during initial inspection, avoiding a second full BODY download for nearly every ordinary split volume. Final and multi-file boundary volumes retain the full scan, and memory-heavy parser fallbacks are capped independently of very large prep-connection budgets.
* Queue imports begin warming Pool provider connections as soon as an NZB arrives, up to the configured queue connection budget.
* Health-only and backup-plus-health providers now keep their full configured connection allowance authenticated between jobs, so the first Farm- or block-backed health check starts at full capacity. Plain backup providers remain cold and recovery-only.

### More reliable connections and failover

* Frequently used provider connections are prewarmed and retained between jobs, reducing repeated connection and authentication delays.
* Connection creation is bounded so a cold start does not overwhelm a provider, while queued work can immediately reuse connections that become idle.
* Provider-reported connection limits reduce that provider's effective pool capacity without stopping other successful connection attempts.
* Cancelled connection attempts no longer leak pool capacity, and concurrent connection handshakes can no longer incorrectly disable a provider.
* Incomplete pipelined batches now count as provider failures. Repeated partial failures trip the provider circuit breaker, and later batches rotate to another eligible provider.
* Successfully completed pipelined batches return their healthy connection to the pool instead of reconnecting for the next batch.
* Provider circuit breaking is now concurrency-aware: a few simultaneous socket timeouts during a large prep job no longer sideline an otherwise healthy provider, while serial and widespread failures still trip promptly. Successful fresh-socket retries no longer count as failed logical operations, and successful work already in flight now immediately recovers a provider tripped by a transient failure burst.
* A provider tripped by BODY preparation receives one controlled STAT recovery probe at the health-phase boundary. A successful lightweight probe restores its health capacity immediately; a failed probe remains isolated behind the circuit breaker.
* Legacy host-keyed bandwidth history is no longer duplicated across multiple provider accounts that share the same NNTP hostname. Shared-host providers now use their stable IDs for independent usage and burn-rate accounting.

### Diagnostics

* Connection and health-check logging now includes enough provider, lane, batch, and timing detail to diagnose stalls and unreliable pipelining.
* Queue percentages now update inline with completed work instead of lagging behind prep at 48–49% while callbacks drain in the background.
* Overlapped health timing now stops when STAT work finishes, rather than including time spent waiting for prep processors.
* Watchdog timing now labels a category-based health check that did not run as **Not run** instead of showing an ambiguous dash beside a valid prep time.
* Completed Watchdog summaries are now clickable and persist an expandable health-routing breakdown with per-account probe coverage, bulk article allocation, share, misses, failures, and STAT rate.
* New Watchdog entries also persist an expandable prep breakdown: queue wait, first-segment, PAR2, RAR-mapping and file-processing timings, plus first-segment provider article/data shares and fallback count.
* The in-app log view shows newest entries first.
* Live prep/health-check provider attribution now converts internal stable UUIDs back to configured provider hostnames and nicknames in queue, history, and Watchdog displays. Live usage lists include only providers that actually served articles instead of padding the list with every configured provider at 0%.

### Settings experience

* Usenet provider setup now uses clear operational roles and integrates protocol overrides and all provider benchmarks into the normal edit workflow.
* Queue preparation now always balances across Primary providers; the redundant routing toggle was removed.
* Bulk health checks use all health-eligible provider capacity, weighted by measured STAT speed and open connections.
* Each provider's role controls health-check participation: **Backup + health checks** joins bulk STAT work, while plain **Backup** remains recovery-only.
* Provider action tooltips appear promptly instead of waiting for the browser's native tooltip delay.
* Prep connection limits are optional; Automatic remains the default and recommended behavior.

### Settings audit, validation, and safety

* Every settings area was reviewed across the frontend, database-backed configuration, nested provider/indexer models, hidden switches, and operational environment variables. The detailed findings and disposition are recorded in `docs/settings-audit.md` and `docs/settings-review-summary.md`.
* Settings now load effective backend values and metadata from a typed registry instead of duplicating defaults in the frontend. Metadata includes the value source (stored, environment, or default), data type, allowed range or choices, secret status, and restart requirement.
* Shared settings saves now report backend failures correctly, validate dependent values across tabs, reject malformed JSON and invalid embedded models or regular expressions, and parse scalar values consistently.
* Creating providers or indexers and regenerating SAB API keys now works in browsers that provide Web Crypto randomness but do not implement `crypto.randomUUID()`.
* Secret values returned to the UI are masked more consistently, including WebDAV and rclone credentials, so visiting and saving a settings page does not expose or accidentally replace them.
* Provider spreading, block-account cap initialization, credential-test races, indexer quota/RPM enforcement, proxy failure behavior, and request timeout handling were corrected.
* Warden, Watchtower, Search Profiles, Arr monitoring, rclone, SAB path containment, maintenance operations, and container startup received settings-related correctness and safety fixes.
* Frontend session keys persist across restarts, operational environment values receive stricter validation, disabling authentication produces a visible warning, release versions are consistent, and database backups use a consistent SQLite snapshot of the main database.

### Settings organization and cleanup

* Usenet connection budgets, streaming priority, article buffering, and decoded segment-cache controls moved from WebDAV to a clearer, full-width **Usenet → Advanced performance** disclosure. Concurrency, streaming/cache, provider-routing, and pipelining subsections have restrained visual grouping without the previous vertical accent line.
* The prep connection limit no longer has a separate enable toggle: blank directly means Automatic. Decoded segment caching is documented as recommended for Nuvio-style streaming when backed by fast local SSD/NVMe storage.
* Public Base URL moved out of the SAB page and now sits with Search Profiles, which generate and display most public adapter and playback URLs. The stored `general.base-url` key and inferred-value behavior are unchanged.
* The unreachable Library settings route was removed while retaining the underlying `media.library-dir` control where it is still used by Repairs and Maintenance.
* The duplicate global Retrieve User-Agent control was removed from SAB. Indexers is now the canonical UI for the separate global Search and Retrieve identities and their per-indexer overrides.
* The legacy blob-migration panel is now status-only and appears only while legacy database blobs remain. Automatic migration and its progress channel remain available for restored legacy databases.
* Provider data usage is presented as a compact inline status strip instead of a large panel.

### Configuration migrations and stable identity

* Legacy `play.exclude-patterns` is migrated to `search.exclude-patterns` only when the current key is absent, then removed. An explicitly stored empty current value remains authoritative.
* Legacy per-indexer `UserAgent` values are migrated into missing Search and Retrieve User-Agent fields without overwriting explicit values; the old alias is removed while unknown indexer fields are preserved.
* Providers and indexers now receive persistent UUIDs. Provider usage, caps, failover metrics, connection snapshots, and frontend identity use provider IDs, while quota/RPM state, Search Profile references, cached candidates, and Watchtower pointers use indexer IDs.
* Existing provider host metrics remain readable during transition, and database migrations convert name-based indexer hits and Search Profile references to stable IDs.
* The one-time `UPGRADE=0.6.0` acknowledgement remains supported for irreversible upgrades from legacy databases; it is documented as migration safety rather than a normal tuning setting.

### Watchtower active-set behavior

* Watchtower's active-set cap now enforces a real least-recently-used warm/cold set. Lowering the cap cools the least recently accessed ready entries, and new resolutions make room using the same policy.
* Cold entries retain their verified winner and promote immediately when accessed, avoiding a needless re-resolution while keeping the configured warm-set bound meaningful.
* Wanted items now persist their last-access time and expose the cold state needed for deterministic rotation and UI status.

### Verification

* Added regression coverage for settings parsing and validation, provider routing, indexer quotas, Warden behavior, Watchtower active-set rotation, path safety, proxy handling, and coalesced NZB fetches.
* The current local change set passes all 140 backend tests, frontend type generation and TypeScript checks, the frontend production build, container entrypoint syntax checking, workflow YAML parsing, and `git diff --check`.

## [1.2.0](https://github.com/qooode/nzbdavex/compare/v1.1.0...v1.2.0) (2026-06-17)

Adds **Watchtower** — a time-shifted watchdog that keeps the titles on your lists pre-resolved and verified so the first read lands on a known-good release with no search at request time — alongside a major **Warden** expansion (GitHub backup, remote sources, import/export), per-indexer search/retrieve user agents with authenticated proxies, smarter search-profile fallback and identity matching, and a round of read-path and connection-pool reliability work.


### Highlights

1. **Watchtower: keep your lists ready before you ask.** A new background service pre-resolves the titles on your lists to a healthy release and re-verifies them over time, so a request lands on a known-good release with no search on the hot path — the same watchdog ranker and STAT health check, run *ahead* of time instead of on demand. It is pointer-only by design (it stores the winning NZB's segment map and a small verified shortlist, never the file itself) and `resolve-only` by default, so nothing downloads until a title is actually requested. Sources are agnostic: a manual IMDb id, any Stremio catalog URL (Trakt, MDBList, Letterboxd, and anything wrapped as a catalog addon), a URL/JSON list, or a whole series expanded into episodes via TVmaze with finished seasons warmed as a single season bundle. A dedicated Watchtower page shows the wanted-set live, and a Settings tab exposes the full `watchtower.*` tuning surface. ([abd53d5](https://github.com/qooode/nzbdavex/commit/abd53d5), [c5bdc0f](https://github.com/qooode/nzbdavex/commit/c5bdc0f), [75b16e5](https://github.com/qooode/nzbdavex/commit/75b16e5), [100eee3](https://github.com/qooode/nzbdavex/commit/100eee3), [b258aca](https://github.com/qooode/nzbdavex/commit/b258aca))
2. **Warden, grown into a portable, multi-source filter list.** The dead-release filter gains scheduled backup and restore to a GitHub repo, remote fingerprint sources with per-source trust levels (full/corroborate/observe) that refresh on their own cadence, bulk import/export of those sources as a shareable JSON bundle, gzip-compressed fingerprint import/export, drag-and-drop import, and backbone-scoped filtering that only applies a source when its provider matches one of yours. ([6b7d3c2](https://github.com/qooode/nzbdavex/commit/6b7d3c2), [3eff1cd](https://github.com/qooode/nzbdavex/commit/3eff1cd), [567ed2d](https://github.com/qooode/nzbdavex/commit/567ed2d), [1bc001d](https://github.com/qooode/nzbdavex/commit/1bc001d), [125e975](https://github.com/qooode/nzbdavex/commit/125e975), [631b755](https://github.com/qooode/nzbdavex/commit/631b755))
3. **Per-indexer user agents and authenticated proxies.** Each indexer can now carry its own search and retrieve user agent (blank falls back to the global default), proxy URLs may embed `user:pass@` credentials, and the HTTP pool was rebuilt on `SocketsHttpHandler` with connection-lifetime and keep-alive tuning plus a one-shot retry on transient failures, so flaky indexers and authenticated proxies behave. ([be18f22](https://github.com/qooode/nzbdavex/commit/be18f22), [c23fec1](https://github.com/qooode/nzbdavex/commit/c23fec1), [43548ff](https://github.com/qooode/nzbdavex/commit/43548ff), [3920eaf](https://github.com/qooode/nzbdavex/commit/3920eaf))
4. **Smarter search profiles.** Movies and TV now get independent fallback modes and thresholds, series results are filtered against the canonical title (`FilenameMatcher` identity verification) to cut cross-show false positives, and Watchtower's already-verified pick is boosted to the top of the candidate list when present. ([16fecb2](https://github.com/qooode/nzbdavex/commit/16fecb2), [c1c8619](https://github.com/qooode/nzbdavex/commit/c1c8619), [b258aca](https://github.com/qooode/nzbdavex/commit/b258aca), [b460138](https://github.com/qooode/nzbdavex/commit/b460138))


### Features

* **Watchtower engine:** source-agnostic wanted-set built from a three-loop background service (sync, resolve, keep-fresh) over new `ListSource`/`WantedItem` models, warming `PreflightCache` with the verified winner so `ProfilePlayController` hits it instantly; bounded by an active warm-set cap, daily resolve budget, and per-resolve grab cap, and reusing `IndexerHitTracker`/`NewznabRateLimiter` for the same per-indexer safety as the watchdog. ([abd53d5](https://github.com/qooode/nzbdavex/commit/abd53d5), [75b16e5](https://github.com/qooode/nzbdavex/commit/75b16e5), [165dc16](https://github.com/qooode/nzbdavex/commit/165dc16), [10b8339](https://github.com/qooode/nzbdavex/commit/10b8339))
* **Watchtower sources & series:** manual IMDb add, Stremio catalog discovery (`WatchtowerDiscoverController` parses addon manifests), URL/JSON lists, and whole-series expansion via TVmaze with `series-scope` bounding, finished seasons warmed as a single season bundle, and a `season-bundle-fallback` to per-episode warming. ([c5bdc0f](https://github.com/qooode/nzbdavex/commit/c5bdc0f), [3b89593](https://github.com/qooode/nzbdavex/commit/3b89593), [100eee3](https://github.com/qooode/nzbdavex/commit/100eee3), [9d755f2](https://github.com/qooode/nzbdavex/commit/9d755f2))
* **Warden backup:** `WardenBackupService` pushes a compressed `.ndjson.gz` of your list (local or merged scope) to a GitHub repo on a 1–30 day schedule with content-hash dedup, plus manual back-up-now and restore from the Warden settings. ([3eff1cd](https://github.com/qooode/nzbdavex/commit/3eff1cd), [b4f443c](https://github.com/qooode/nzbdavex/commit/b4f443c), [b97ab5b](https://github.com/qooode/nzbdavex/commit/b97ab5b))
* **Warden remote sources:** `WardenRemoteSourceService` tracks multiple remote fingerprint URLs, each with its own trust level, enable toggle, ETag-cached refresh on a 1–720 hour cadence, and manual refresh; bulk import/export of the source set as a JSON bundle. ([567ed2d](https://github.com/qooode/nzbdavex/commit/567ed2d), [0733c63](https://github.com/qooode/nzbdavex/commit/0733c63), [1bc001d](https://github.com/qooode/nzbdavex/commit/1bc001d))
* **Warden import/export & scope:** import `.ndjson`/`.ndjson.gz` merged or as separate sources, export local or merged-and-deduped, gzip on the wire, drag-and-drop in the UI, and a `warden.backbone-scope` toggle that applies remote/imported sources only when their backbone provider (matched by `RootDomain`) is one of yours. ([125e975](https://github.com/qooode/nzbdavex/commit/125e975), [7b159a5](https://github.com/qooode/nzbdavex/commit/7b159a5), [631b755](https://github.com/qooode/nzbdavex/commit/631b755), [799c38e](https://github.com/qooode/nzbdavex/commit/799c38e))
* **Indexer user agents & proxy:** separate `SearchUserAgent`/`RetrieveUserAgent` per indexer with global fallback, `AddUrlRequest` honoring per-indexer agent and proxy, and clearer settings guidance. ([be18f22](https://github.com/qooode/nzbdavex/commit/be18f22), [c23fec1](https://github.com/qooode/nzbdavex/commit/c23fec1), [43548ff](https://github.com/qooode/nzbdavex/commit/43548ff), [98d20fb](https://github.com/qooode/nzbdavex/commit/98d20fb))
* **Search profile fallback:** independent movie/TV fallback modes with their own minimum-result thresholds (legacy single-threshold config auto-migrated), with per-indexer query estimates surfaced in the fallback help text. ([16fecb2](https://github.com/qooode/nzbdavex/commit/16fecb2), [c1c8619](https://github.com/qooode/nzbdavex/commit/c1c8619), [12985ad](https://github.com/qooode/nzbdavex/commit/12985ad))


### Performance & reliability

* Fast seek in `NzbFileStream`: estimate the target segment from file size and probe yEnc headers instead of scanning, so in-file seeks land quickly. ([9d72a08](https://github.com/qooode/nzbdavex/commit/9d72a08))
* `DavMultipartFileStream` resolves trailing RAR volumes in the background so the first read returns on byte 0 immediately, with on-demand resolution covering later seeks and pre-warm errors never breaking a read. ([fa9f8a8](https://github.com/qooode/nzbdavex/commit/fa9f8a8))
* Separate on-demand-read and queue connection semaphores in `DownloadingNntpClient`/`MultiConnectionNntpClient`, so queue work no longer competes with on-demand reads against the provider connection limit. ([02976ae](https://github.com/qooode/nzbdavex/commit/02976ae))
* `NzbFetchCoalescer` deduplicates concurrent NZB downloads by URL and `PlayResolutionCoalescer` collapses concurrent resolves of the same content, cutting redundant indexer fetches and duplicate work during request bursts. ([0b7436e](https://github.com/qooode/nzbdavex/commit/0b7436e), [963e92b](https://github.com/qooode/nzbdavex/commit/963e92b))
* `NewznabClient` retries once on transient network errors, and the `SocketsHttpHandler` pool adds keep-alive probing to detect dead peers faster. ([3920eaf](https://github.com/qooode/nzbdavex/commit/3920eaf))
* Disable SQLite connection pooling in `DavDatabaseContext` to avoid concurrency stalls under load. ([e2ba41c](https://github.com/qooode/nzbdavex/commit/e2ba41c))
* Stremio catalog fetching paginates (deduping by `type:id`, with a page ceiling and item cap) so large lists enumerate fully. ([1cd7da4](https://github.com/qooode/nzbdavex/commit/1cd7da4))
* Tighter read verification with per-segment STAT timeouts that release a stalled provider connection. ([8a9ec1a](https://github.com/qooode/nzbdavex/commit/8a9ec1a), [2c4c9b9](https://github.com/qooode/nzbdavex/commit/2c4c9b9))


### Documentation

* Add `docs/watchtower.md` documenting the design, sources, safety model, and full `watchtower.*` configuration. ([48c0b72](https://github.com/qooode/nzbdavex/commit/48c0b72))
* Document IPv6 support in the README and setup guide, and extend the Docker CI workflow to mirror images to Docker Hub. ([1879dd1](https://github.com/qooode/nzbdavex/commit/1879dd1))

## [1.1.0](https://github.com/qooode/nzbdavex/compare/v1.0.0...v1.1.0) (2026-06-01)

Builds on the 1.0.0 foundation with an on-disk segment cache, a live failover-savings dashboard, smarter title resolution (TMDB and anime), and paginated queue/history views, plus a round of streaming reliability and performance work.


### Highlights

1. **On-disk segment cache.** davex can now persist decoded Usenet segments to disk and serve repeat reads from the cache instead of refetching them from your provider, cutting provider load and speeding up re-reads. Enable it from the WebDAV settings, with a configurable cache path and maximum size. ([876f226](https://github.com/qooode/nzbdavex/commit/876f226), [fc6a2b1](https://github.com/qooode/nzbdavex/commit/fc6a2b1), [0ad8e54](https://github.com/qooode/nzbdavex/commit/0ad8e54))
2. **Failover-savings dashboard.** A new Overview panel quantifies how often provider failover rescued a read: articles recovered, reads saved, a per-provider rescuer ranking, peak-rescue stats, and a cumulative saves trend over time, all backed by new metrics that persist across restarts. ([d4fe020](https://github.com/qooode/nzbdavex/commit/d4fe020), [925bffd](https://github.com/qooode/nzbdavex/commit/925bffd), [2614782](https://github.com/qooode/nzbdavex/commit/2614782))
3. **Smarter title resolution: TMDB and anime.** Search profiles now resolve TMDB IDs (rewritten to IMDb/TVDB queries) and fall back to community anime-list mappings when Kitsu's data is incomplete, with an added Wikidata fallback and a TVDB name-search last resort, so more requests find a match. ([7402f76](https://github.com/qooode/nzbdavex/commit/7402f76), [06e8c22](https://github.com/qooode/nzbdavex/commit/06e8c22), [b7ae972](https://github.com/qooode/nzbdavex/commit/b7ae972))
4. **Paginated queue and history.** The Queue and History tables now paginate with navigation controls instead of loading everything at once, with clearer live-update status. ([3a1cfba](https://github.com/qooode/nzbdavex/commit/3a1cfba), [7ffc6ad](https://github.com/qooode/nzbdavex/commit/7ffc6ad))


### Features

* **Segment cache:** disk-backed cache of decoded segments in `UsenetStreamingClient`, with cache status/path/size wired through `ConfigManager` and new WebDAV settings (enable, path, max size) including validation. ([876f226](https://github.com/qooode/nzbdavex/commit/876f226))
* **Failover metrics & dashboard:** `FailoverSaves` tracking on read sessions and provider rollups, a `FailoverBlock` in the overview stats response, miss/edge tracking with new database migrations, and the FailoverSaves panel with rescuer ranking, peak-rescue, and a saves trend. ([d4fe020](https://github.com/qooode/nzbdavex/commit/d4fe020), [925bffd](https://github.com/qooode/nzbdavex/commit/925bffd), [bf13a97](https://github.com/qooode/nzbdavex/commit/bf13a97), [ac5dbac](https://github.com/qooode/nzbdavex/commit/ac5dbac), [c65f493](https://github.com/qooode/nzbdavex/commit/c65f493), [db47f36](https://github.com/qooode/nzbdavex/commit/db47f36), [2614782](https://github.com/qooode/nzbdavex/commit/2614782))
* **TMDB resolver:** `TmdbIdResolver` singleton, `tmdb` id-prefix support in the profile manifest, and TMDB→IMDb/TVDB query building in `SearchProfileService`. ([7402f76](https://github.com/qooode/nzbdavex/commit/7402f76))
* **Anime list mapping:** `AnimeListMappingResolver` plus an `ExternalIdResolver` title fallback that consults community datasets when Kitsu's mapping is incomplete, used as a last-resort search query. ([06e8c22](https://github.com/qooode/nzbdavex/commit/06e8c22))
* **Resolver fallbacks & caching:** Wikidata fallback in `ImdbTitleResolver`, a TVDB name-search last resort, multiple fallback query variants, and in-memory caching of resolved IDs with a reduced 6s HTTP timeout. ([b7ae972](https://github.com/qooode/nzbdavex/commit/b7ae972), [2d2a0c7](https://github.com/qooode/nzbdavex/commit/2d2a0c7))


### Performance & reliability

* Drain each segment into memory in `MultiSegmentStream` so the connection returns to the pool immediately and following segments prefetch. ([e52166b](https://github.com/qooode/nzbdavex/commit/e52166b))
* Fail fast on a definitively missing first segment, and retry transient segment failures (up to two attempts) before zero-filling, so the player surfaces a real error instead of silently corrupt playback. ([88d8e16](https://github.com/qooode/nzbdavex/commit/88d8e16), [871843c](https://github.com/qooode/nzbdavex/commit/871843c))
* Resolve lazy RAR files in `DatabaseStoreMultipartFile` when metadata still shows pending parts. ([b1a0c61](https://github.com/qooode/nzbdavex/commit/b1a0c61))
* Use hourly rollup data for long Overview windows (7 days and up) and hide widgets that would otherwise show misleading data. ([7dd8cd4](https://github.com/qooode/nzbdavex/commit/7dd8cd4))
* Rework the activity heatmap to aggregate by local days and weeks correctly and build its grid without the extra lookup map. ([ebb587f](https://github.com/qooode/nzbdavex/commit/ebb587f))


### Documentation

* Rename NzbDav to NzbDavEx across the README, setup guide, and issue templates, and switch the documented Docker image tag from `:latest` to `:stable`. ([17ee7fc](https://github.com/qooode/nzbdavex/commit/17ee7fc))

## [1.0.0](https://github.com/qooode/nzbdavex/releases/tag/v1.0.0) (2026-05-28)

**First official release of davex.** davex is an independent fork of [nzbdav](https://github.com/nzbdav-dev/nzbdav), rebuilt into a fast on-demand usenet client with a token-scoped search API, self-healing content resolution, and a live observability dashboard. This release captures everything the fork added since it diverged from nzbdav 0.6.4.


### Highlights

1. **Search Profiles: a token-scoped, multi-adapter search API.** Each profile exposes its own Newznab adapter, JSON search, and NZB proxy endpoints behind a scoped token, so *arr apps and other external clients can search and fetch straight from davex. This is the backbone of the fork. ([93e61fc](https://github.com/qooode/nzbdavex/commit/93e61fc), [81e6e20](https://github.com/qooode/nzbdavex/commit/81e6e20))
2. **Watchdog: self-healing resolution with a live dashboard.** When a client requests an item, davex works down the ranked candidates across your indexers and providers and automatically falls through dead, incomplete, or excluded releases until one resolves, so a single bad release no longer breaks the request. The Watchdog page shows it happening live: every click grouped into its attempts with indexer, provider, size, outcome, fail reason, and timing, plus the winning release and how fast it resolved. Filter by live, resolved, failed, or excluded, and the whole history persists across restarts. ([476484a](https://github.com/qooode/nzbdavex/commit/476484a), [874681b](https://github.com/qooode/nzbdavex/commit/874681b), [6bb315f](https://github.com/qooode/nzbdavex/commit/6bb315f), [6f805ad](https://github.com/qooode/nzbdavex/commit/6f805ad))
3. **Live Overview dashboard.** A real-time control center built from scratch: throughput chart, error donut, activity heatmap, provider and indexer scoreboards, lifetime totals, and an active-reads panel showing exactly what is being served right now, all pushed live over WebSocket. ([93e89b7](https://github.com/qooode/nzbdavex/commit/93e89b7), [3130a89](https://github.com/qooode/nzbdavex/commit/3130a89), [051112f](https://github.com/qooode/nzbdavex/commit/051112f))
4. **Preflight verification.** davex can pre-verify the top search candidates and cache their NZB bytes before a client ever connects, so the first read lands on a known-good release. Mode, candidate count, TTL, and max indexer wait are all configurable. ([e4ae7cc](https://github.com/qooode/nzbdavex/commit/e4ae7cc))
5. **Size-aware content variants.** When the same title shows up as several releases, the variant resolver keeps the best one by size (with configurable tolerance and eviction) instead of re-downloading duplicates. ([2e6951c](https://github.com/qooode/nzbdavex/commit/2e6951c))
6. **Faster RAR access.** Lazy RAR mounting parses only the first volume at import and defers the rest to first read, dropping click-to-mount from roughly 5 to 15 seconds down to 1 to 2 seconds on typical releases. Trailing volumes then pre-warm in the background so the first read no longer stutters. Toggle via `api.lazy-rar-parsing`; unsupported archives fall back to the eager processor automatically. ([18f6a67](https://github.com/qooode/nzbdavex/commit/18f6a67), [88f6a14](https://github.com/qooode/nzbdavex/commit/88f6a14))
7. **Provider usage tracking and data caps.** Track each Usenet provider's usage against a configurable data cap, toggle providers on and off, and give them nicknames, all surfaced in the client and on the scoreboards. ([1855512](https://github.com/qooode/nzbdavex/commit/1855512), [989673a](https://github.com/qooode/nzbdavex/commit/989673a), [3f53915](https://github.com/qooode/nzbdavex/commit/3f53915), [17ce0b9](https://github.com/qooode/nzbdavex/commit/17ce0b9))


### Features

**Search and indexers**

* ID resolvers for TVDB, IMDb titles, and external IDs, with a configurable query fallback when the first search returns too few results. ([60896c6](https://github.com/qooode/nzbdavex/commit/60896c6), [6d7397f](https://github.com/qooode/nzbdavex/commit/6d7397f), [f0b54b3](https://github.com/qooode/nzbdavex/commit/f0b54b3))
* Strict matching to cut false positives, plus per-indexer category overrides (extra movie/TV categories and an ignore-filter toggle). ([4fa8222](https://github.com/qooode/nzbdavex/commit/4fa8222), [d31404f](https://github.com/qooode/nzbdavex/commit/d31404f))
* Result filtering by age and grab count, with richer result metadata. ([174e7cb](https://github.com/qooode/nzbdavex/commit/174e7cb), [0d393fc](https://github.com/qooode/nzbdavex/commit/0d393fc))
* Per-indexer request timeouts, custom User-Agent, and proxy support, plus a Newznab rate limiter and an NZB resolution cache. ([2335550](https://github.com/qooode/nzbdavex/commit/2335550), [e9e2d4a](https://github.com/qooode/nzbdavex/commit/e9e2d4a), [9c1bb8f](https://github.com/qooode/nzbdavex/commit/9c1bb8f), [c451079](https://github.com/qooode/nzbdavex/commit/c451079))
* Indexer API-usage tracking: hits against each indexer's configured limits, shown on the Overview and Indexer settings. ([0f17473](https://github.com/qooode/nzbdavex/commit/0f17473))

**Content delivery**

* New profile delivery pipeline with active read sessions and a session registry. ([051112f](https://github.com/qooode/nzbdavex/commit/051112f), [6212172](https://github.com/qooode/nzbdavex/commit/6212172))
* Suffix-length range requests (`bytes=-N`), video-resolution caching, extended content-type mappings, and Cache-Control headers. ([f06f896](https://github.com/qooode/nzbdavex/commit/f06f896), [3f8173e](https://github.com/qooode/nzbdavex/commit/3f8173e), [ba0749f](https://github.com/qooode/nzbdavex/commit/ba0749f))

**Observability**

* Live log viewer that pushes backend logs over WebSocket in real time, with download. ([ef0111b](https://github.com/qooode/nzbdavex/commit/ef0111b))
* Transfer metrics (bytes served and throughput) feeding the live dashboard. ([67f1016](https://github.com/qooode/nzbdavex/commit/67f1016), [ade1815](https://github.com/qooode/nzbdavex/commit/ade1815))

**UI and settings**

* Rebrand from nzbdav to davex with refreshed branding and app icons. ([17db1a4](https://github.com/qooode/nzbdavex/commit/17db1a4), [7596bc4](https://github.com/qooode/nzbdavex/commit/7596bc4), [b7b2dd4](https://github.com/qooode/nzbdavex/commit/b7b2dd4))
* Reworked Indexers settings (card layout and modal), an advanced settings tab with accordion, a cleaner SABnzbd settings page, and an explore route with a toolbar, multi-select, and delete. ([c808876](https://github.com/qooode/nzbdavex/commit/c808876), [166bf94](https://github.com/qooode/nzbdavex/commit/166bf94), [7b2002e](https://github.com/qooode/nzbdavex/commit/7b2002e), [9faedbb](https://github.com/qooode/nzbdavex/commit/9faedbb))
* Custom checkboxes and toggle switches across the app. ([49213ab](https://github.com/qooode/nzbdavex/commit/49213ab))

**Packaging**

* Multi-arch Docker images (`linux/amd64` and `linux/arm64`) published to GHCR with `edge` and semver tags, and removal of the legacy workflows and Dependabot config. ([6317dcc](https://github.com/qooode/nzbdavex/commit/6317dcc), [b5c49c1](https://github.com/qooode/nzbdavex/commit/b5c49c1))


### Bug Fixes

* forward `/adapters/*` to the backend so addon manifest and Newznab URLs stop 404ing. ([e42f9d8](https://github.com/qooode/nzbdavex/commit/e42f9d8))
* harden `MultiSegmentStream` and handle NNTP read timeouts more gracefully. ([51c6c1b](https://github.com/qooode/nzbdavex/commit/51c6c1b), [817cccd](https://github.com/qooode/nzbdavex/commit/817cccd))
* lazy RAR resolver: skip SharpCompress seek-past-data stalls and serialize concurrent persistence. ([f04113a](https://github.com/qooode/nzbdavex/commit/f04113a), [7f4c57e](https://github.com/qooode/nzbdavex/commit/7f4c57e))
* negative-cache failed WebDAV item lookups and return structured request errors. ([8e8a2e8](https://github.com/qooode/nzbdavex/commit/8e8a2e8), [d875049](https://github.com/qooode/nzbdavex/commit/d875049))
* drop static `XElement` references in WebDAV property managers to avoid cross-request races. ([14fbaaf](https://github.com/qooode/nzbdavex/commit/14fbaaf))
* cap search results at 100, normalize titles, and raise the regex timeout for exclude-pattern matching. ([ff6e10c](https://github.com/qooode/nzbdavex/commit/ff6e10c), [77c8176](https://github.com/qooode/nzbdavex/commit/77c8176), [d52aea7](https://github.com/qooode/nzbdavex/commit/d52aea7))
* cancel in-flight verify tasks more reliably, with consistent route paths and NWebDav path-segment exclusions. ([216b5d5](https://github.com/qooode/nzbdavex/commit/216b5d5), [7467446](https://github.com/qooode/nzbdavex/commit/7467446), [ce6f177](https://github.com/qooode/nzbdavex/commit/ce6f177))

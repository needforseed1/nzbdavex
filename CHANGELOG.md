# Changelog

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

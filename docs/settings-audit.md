# Settings audit

Date: 2026-07-12

Scope: every database-backed setting loaded by the Settings page, the nested Usenet/Indexer/Profile/Arr models, Warden's immediate-save source and backup settings, and hidden database-backed configuration switches. Operational environment-only switches are inventoried separately below. Prep and health-check performance algorithms were not re-optimised, but their settings were checked for validation, persistence, and runtime wiring.

Verdicts used below:

- **Working** — the control is persisted and its runtime consumer matches the UI description.
- **Fixed** — a confirmed defect was corrected in this review.
- **Decision** — behavior is real but a product or migration choice is needed before changing it safely.
- **Retire / move** — redundant, legacy, dead, or in the wrong section.

## Cross-cutting settings infrastructure

| Area | Verdict | Review result |
|---|---|---|
| Settings load/defaults | **Fixed** | The loader no longer mutates its module-global defaults. Stored environment fallbacks for categories, mount path, WebDAV user, and user agents are now represented in the UI. Empty completed-download and mount paths use the same defaults as the backend. |
| Shared Save | **Fixed** | Network errors, non-2xx responses, and backend `status:false` no longer look successful or leave the button stuck at “Saving”. Edits made during an in-flight save are preserved and a retryable error is shown. |
| Validation | **Fixed** | Every shared-save section now participates in validation, including cross-tab references (Profiles→Indexers, Watchtower→Profiles, Repairs→Arr, Maintenance→Library, Usenet limits→provider capacity, and Public Base URL→SAB/Profile consumers). Backend validation was added for regexes and the four embedded JSON models before persistence. |
| Scalar config parsing | **Fixed** | Invalid booleans, integers, longs, enums, and JSON now fall back/clamp without crashing runtime consumers. The backend registry supplies canonical defaults, types, ranges/choices and effective values; filesystem existence and writability remain runtime concerns. |
| Embedded JSON | **Fixed** | Malformed Usenet, Indexer, Profile, or Arr JSON is locked in the UI rather than silently displayed as an empty configuration. Unknown fields and custom Arr rules are preserved during normal edits. |
| Secrets | **Partly fixed / Decision** | WebDAV and rclone passwords are masked and blank means “keep existing”. Provider and indexer UUIDs now provide stable merge identity, but Usenet credentials and Indexer/Arr API keys remain embedded in returned JSON. Secret-aware merge/masking and explicit clear actions are still missing. |
| Effective values | **Fixed** | The backend metadata endpoint exposes default, effective value, source (stored/environment/default), type/range/choices, secret flag, and restart requirement. Settings loads this contract and no longer owns a duplicate default table. |
| Apply behavior | **Decision** | Saving provider JSON replaces and disposes the old NNTP client graph immediately, which can disrupt active streams. Provider changes should be validated, built, then drained/applied safely. |

## Usenet

| Setting(s) | Verdict | Review result |
|---|---|---|
| `usenet.providers`: Host, Port, User, Pass, SSL, MaxConnections, Type/role, PrepOnly | **Fixed** | Structural validation now covers host, port 1–65535, credentials, enum role, and connection count 1–1024. Test/benchmark responses can no longer approve credentials edited while the request was in flight. Unknown JSON fields survive edits. |
| Provider `Nickname`, `Priority`, drag ordering, enable/disable | **Fixed** | Persistent UUIDs now anchor React/DnD identity, live connection snapshots, usage joins and reordering independently of host or array position. |
| Provider `PipeliningDepth`, `HealthPipeliningDepth` | **Fixed** | Optional overrides are restricted to 1–64 in UI and backend payload validation. |
| Provider `PrepSpreadEnabled` | **Fixed / missing UI** | The stored flag was ignored by provider selection; it now controls first-choice prep spreading and is preserved by edits. It should be exposed under provider advanced settings or deliberately removed after migration. |
| Provider `ByteLimit`, `BytesUsedOffset`, `BytesUsedResetAt` | **Fixed with limitation** | UUID-keyed accounting isolates multiple accounts on one host. Legacy host history remains readable, and usage is seeded before pools become available. Existing-account offset adjustment is still missing from the UI. |
| `usenet.cascade.enabled` | **Working** | Ordered vs balanced routing is wired. Low-priority prep spreading intentionally has separate selection behavior. |
| `usenet.pipelining.playback.enabled`, `usenet.pipelining.depth` | **Working / validated** | Runtime and per-provider fallback are wired; depth is 1–64. |
| `usenet.pipelining.health.enabled`, `.health.depth`, `.health.lanes` | **Working / validated** | Health depth is 1–64; lanes are 1–1024 and then bounded by available eligible connections, now stated in the UI. |
| `usenet.max-download-connections` | **Working** | Controls the playback-side concurrency budget. Actual provider capacity remains the physical ceiling. |
| `usenet.max-queue-connections` | **Fixed** | Blank directly means Automatic and follows pooled capacity; there is no redundant enable toggle. An explicit limit is validated against current pooled capacity instead of being silently clamped. |
| `usenet.streaming-priority` | **Working / validated** | Integer 0–100 controls playback-vs-prep semaphore odds. |
| `usenet.article-buffer-size` | **Working** | Positive article look-ahead value is consumed by stream buffering. A very high value is allowed; a memory-based upper-bound policy may be worth adding. |
| `usenet.segment-cache.enabled`, `.path`, `.max-gb` | **Working / fixed** | Blank paths and size overflow are safe; the UI correctly marks restart-required behavior. Enable on fast local SSD/NVMe for Nuvio-style streaming so probes, seeks, retries, and reopened streams can reuse decoded articles; disable for one-pass workloads or slow/network cache storage. Filesystem writability/capacity cannot be proved until runtime. |

## Indexers

| Setting(s) | Verdict | Review result |
|---|---|---|
| Global proxy, request timeout, result limit | **Fixed** | Proxy must be absolute HTTP(S); invalid configured proxies now fail closed instead of silently going direct. Timeout is 1–3600s and applies to search and every in-app NZB retrieval path. Result gathering is bounded to 1–5000. |
| `api.search-user-agent`, `api.user-agent` | **Fixed / consolidated** | Search and retrieve agents remain separate. The duplicate retrieve-agent control was removed from SAB; Indexers is canonical. Line breaks are rejected server-side. |
| `search.exclude-patterns` | **Fixed** | Browser JavaScript regex validation was removed because runtime uses .NET. The server validates .NET regex before persistence, including .NET-only constructs, with a match timeout. |
| `play.exclude-patterns` | **Migrated / removed** | A data migration copies it only when `search.exclude-patterns` is absent, preserves an explicitly empty current value, deletes the old key, and removes frontend/backend fallbacks. |
| Name, URL, API key, Enabled | **Fixed** | Names are non-empty and case-insensitively unique; URLs are HTTP(S). Disabled/deleted references are surfaced in Profiles. API keys are still returned inside JSON (see secret design above). |
| Per-indexer Search/Retrieve UA, proxy, timeout, result limit | **Fixed** | Per-indexer overrides are preserved and consistently applied to searches and grabs. |
| Legacy per-indexer `UserAgent` | **Migrated / removed** | A data migration fills only blank/missing Search/Retrieve fields, preserves specific and unknown fields, and removes the alias. Runtime/UI fallbacks are gone. |
| Max requests/minute | **Working / validated** | Applied per indexer across search and retrieval paths. Values are 0/unlimited or 1–10000. |
| API hit limit, download limit, UTC reset hour | **Fixed** | Checks are now atomically reserved within the process before outbound requests, persisted, applied to paging, profile play, JSON/Newznab proxy, Preflight, Watchtower, and SAB AddUrl, and no longer double-count coalesced/cache hits. Reset hour is 0–23 or rolling 24h. |
| Strict matching | **Fixed** | Canonical expected-title matching now applies even when an indexer returns only one result. Consensus fallback still correctly requires at least two. |
| Extra movie/TV categories, ignore-category-filter | **Working / validated** | Numeric category lists and the explicit “send no cat parameter” escape hatch are wired. |
| Result filter: enabled, password, minimum grabs, grace, zero-grab age, rank by grabs | **Working / validated** | Rules are applied per indexer before merge. Unknown filter fields survive edits. |

Remaining limitations:

- SAB AddUrl identifies an indexer by URL host because SAB does not provide the configured indexer name. Multiple logical indexers on one host can therefore charge the wrong bucket. A signed/internal source identifier or API-key-aware match would solve this.
- Quotas, RPM gates, cached candidate retrieval, Watchtower pointers and Profile references are now UUID-keyed, so renaming an indexer preserves identity and accounting.

## Search Profiles

| Setting(s) | Verdict | Review result |
|---|---|---|
| Token and Name | **Working / validated** | Tokens and names are required and tokens unique. A first-class rotate-token action is still missing. |
| `IndexerNames` | **Fixed** | Empty means all enabled indexers. Disabled/deleted/renamed selections remain visible, can be removed, and block an invalid cross-tab save rather than silently selecting zero indexers. Backend matching is case-insensitive. |
| `EnabledAdapters` (JSON, Newznab, Addon) | **Fixed** | Missing/null retains legacy “all enabled”; explicit `[]` now means none. Unknown adapters are rejected. |
| Movie/TV fallback mode and minimum results | **Working / validated** | Movie supports Off/Title; TV supports Off/Title/Broad; thresholds are 1–5000 and legacy threshold data is migrated in memory. |
| Generated/copy URLs | **Fixed** | URLs honor configured `general.base-url`; blank uses the browser/request origin. |

## Watchdog and variants

| Setting(s) | Verdict | Review result |
|---|---|---|
| Watchdog enabled, total budget, hedge delay, max candidates, max attempts | **Working / validated** | Documented ranges are enforced by shared Save and safe backend parsing. |
| Verify mode | **Working** | none/stat/body routing is wired. |
| `play.verify-sample-count` | **Fixed / added to UI** | The active backend setting was hidden; it is now shown when verification is relevant and validated 1–10. |
| Candidate negative-cache minutes | **Working / validated** | TTL is wired and bounded. Provider changes now clear the health service’s stale cache because the listener watches `usenet.providers`, not the obsolete single-host key. |
| Stall failover enabled, window, ceiling | **Working / validated** | Ceiling must be at least the window; documented ranges are enforced. |
| Variant mode, tolerance, max copies, replay, fallback, eviction strategy | **Working / validated** | Off/smart/collect-all and eviction modes are wired. `never` intentionally allows the cap to be exceeded. |
| Variant eviction grace | **Description fixed / Decision** | Code protects recently created/played copies, not a continuously open stream. It is now labelled “recent-use grace”. True active-read protection needs HistoryItem↔active-read identity integration. |

## Preflight

| Setting(s) | Verdict | Review result |
|---|---|---|
| Mode, max attempts, verify samples, cache TTL, max indexer wait | **Fixed / validated** | All ranges and enums are enforced. Duplicate indexer names can no longer crash dictionary construction. Quotas, RPM, enabled state, and per-indexer timeout are applied inside the coalesced external fetch. |
| Warden interaction | **Fixed** | The Warden master switch is respected. Known-dead candidates are hidden only when an alive alternative exists, preserving the documented last-resort behavior. |

## Watchtower

| Setting(s) | Verdict | Review result |
|---|---|---|
| Enabled, Search Profile, ranking | **Fixed / validated** | An explicitly stale profile token no longer silently switches to the first profile. Blank still means first configured profile. Cross-tab deletion is blocked while enabled. |
| Series scope, recent count, season bundles, bundle fallback/scope/count/cap, per-series cap/keep | **Fixed** | Turning scope off now removes prior expanded children; disabling episode fallback unparks bundles and reconciles fallback children. Relevant changes immediately nudge expanders instead of waiting up to a sync interval. |
| Size floor/ceiling, minimum grabs | **Fixed / validated** | Floor must not exceed a nonzero ceiling. Default floor is now exactly 0.5 GiB, matching display/help. |
| Active warm-set cap | **Fixed / LRU** | The cap is enforced when lowered and before new resolutions. Least-recently-accessed Ready entries become Cold; verified winners are retained and promote immediately when accessed. |
| Auto throughput, daily resolve budget | **Working with disclosed limitation** | Indexer headroom/caps govern auto mode. The daily budget is in-memory, UTC-based, and resets on restart; UI now says so. Persist it if restart-resistant pacing is required. |
| Shortlist depth, grab cap, verify samples, verify timeout | **Working / validated** | Limits are enforced. Backup-pointer promotion can consume one indexer download hit; help now distinguishes this from ordinary Usenet-only rechecks. |
| Recheck base/max, unavailable retry, sync interval | **Fixed / validated** | `max < base` can no longer crash `Math.Clamp`; UI blocks it and backend normalizes it. |
| Verbose logging | **Working** | Changes take effect immediately and control per-item/heartbeat activity logs. |
| `watchtower.resolve-concurrency` | **Missing UI / Decision** | Backend setting works (1–16, default 3). Expose it under Advanced throughput, or derive it automatically and remove the hidden knob. |
| Warden interaction | **Fixed** | The master switch and last-resort semantics are now consistent with search, Preflight, and playback. |

## Warden

| Setting(s) | Verdict | Review result |
|---|---|---|
| Hide dead, quorum, backbone scope | **Fixed / validated** | Quorum is 1–20. Hide-dead behavior is consistent across all candidate paths and never removes the last available set solely because every candidate is known dead. |
| Source name, trust, enabled, refresh interval | **Fixed** | Trust and interval are normalized; interval is 1–720h. Disabled remote sources no longer refresh in the background. Invalid immediate-save values are rejected/reset in UI/backend. |
| Remote URL and bundle import | **Working / validated** | Only HTTP(S) remote URLs are accepted. Duplicate/invalid bundle entries are reported. |
| File import separate/merge | **Working** | Separate remains reversible; merge intentionally cannot be unmerged. |
| Export local/merged/source selection/deduplicate | **Working** | Manual exports retain current timestamps. |
| GitHub repo/token/path/branch/scope/interval/automatic | **Fixed with missing action** | Enabled backup requires repo and token; token is write-only. Scheduled backups are deterministic (fixed header, sorted canonical records), so unchanged content no longer produces a new commit. Push/restore failure status is accurate. A “remove stored token” action is still needed. |
| `warden.max-source-entries` | **Hidden safety setting** | Works as a bounded import cap (default 2,000,000; backend max 10,000,000). Keep hidden or place under diagnostic/safety Advanced; it is not a normal tuning control. |

Warden persistence is now explicitly described in the UI: global filter controls use shared Save; source and backup operations apply immediately.

## SAB-compatible API and imports

| Setting(s) | Verdict | Review result |
|---|---|---|
| API key | **Working** | Display and rotate flow are wired. Rotation breaks clients using the old key, as expected. |
| Categories and manual category | **Fixed** | Defaults are explained, duplicates are removed case-insensitively, and a category must be a safe single segment. Backend containment checks prevent category/path traversal into NZB backup or STRM output paths. |
| Import strategy, rclone mount dir, completed-download dir, base URL | **Fixed / validated** | Symlink and STRM dependencies are explicit; blank backend paths fall back safely. STRM requires an absolute public HTTP(S) base URL and output containment is enforced. |
| Ignored-file globs | **Fixed** | Spaces are no longer treated as tag delimiters, so globs containing spaces can be represented. |
| Duplicate NZB behavior | **Working / normalized** | increment and mark-failed are the only accepted effective modes. |
| Retrieve User-Agent | **Retire duplicate UI** | The duplicate SAB control was removed; the same global key is edited under Indexers. Per-indexer override remains available there. |
| Fail no-video NZBs | **Working** | Importability check is wired. |
| Health-check categories | **Fixed** | Master/category selections serialize correctly; removed/unknown selected categories remain visible rather than active but hidden. |
| Full History | **Working** | Controls SAB history limit behavior. |
| NZB backup enabled/location | **Working with Decision** | Location is required and category containment is safe. A backup failure currently rejects the incoming NZB; consider a strict-vs-best-effort policy setting. |

## WebDAV

| Setting(s) | Verdict | Review result |
|---|---|---|
| User/password | **Fixed with missing action** | Stored password hashes are never returned or re-hashed by the UI. Blank preserves the current password; environment password hashing is cached. There is no explicit clear-password action, and a cached Basic-auth cookie may remain valid for up to one hour after rotation. |
| Show hidden, enforce read-only, PAR2 preview | **Working** | Each flag is wired to its WebDAV/DAV Explorer consumer. |

Usenet concurrency, scheduling, buffering, and cache controls were moved to Usenet Advanced. WebDAV now owns only DAV authentication and presentation controls.

## Radarr / Sonarr

| Setting(s) | Verdict | Review result |
|---|---|---|
| Instance host/API key | **Fixed / validated** | HTTP(S), key, and duplicate-host validation are enforced. Invalid JSON no longer crashes the settings page. |
| Queue action presets | **Fixed with limitation** | Unknown/custom rules survive preset edits; matching is now case-insensitive. Runtime checks delete HTTP status before reporting success and failed GET/POST status before JSON parsing. English substring matching remains brittle across Arr versions/locales. |
| Monitoring loop | **Fixed / missing settings** | Instances run in parallel with cancellation and a 30s timeout, so one bad host no longer blocks all others or shutdown. Per-instance Enabled, poll interval, and request timeout are still valuable additions. |
| Test Connection | **Working with limitation** | Proves the endpoint/API key, but does not verify that a row labelled Radarr is Radarr (or Sonarr is Sonarr). A system-status identity check would improve it. |

## Repairs

| Setting(s) | Verdict | Review result |
|---|---|---|
| Enable background repairs, library directory | **Fixed / validated** | Enabled requires a library path and at least one valid Arr instance. If prerequisites disappear, stored Enabled remains visibly on and can be turned off instead of silently reappearing later. Filesystem existence/writability remains a runtime concern. |

## Rclone

| Setting(s) | Verdict | Review result |
|---|---|---|
| RC notifications and host | **Fixed / validated** | Enabling requires an absolute HTTP(S) RC URL. |
| User/password | **Fixed with missing action** | Authentication remains optional; stored password is masked, blank preserves it, and Test Connection reuses it only when testing the same stored endpoint/user. Explicit clear-password is missing. |
| Request timeout | **Missing setting** | The shared client currently uses HttpClient's default timeout. Add a modest configurable timeout if RC outages are operationally significant. |

## Maintenance

| Setting(s) | Verdict | Review result |
|---|---|---|
| Startup VACUUM | **Working** | The container migration/startup path consumes it. It remains opt-in because VACUUM can be expensive. |
| Daily orphan cleanup enabled/time | **Fixed / validated** | Enabled requires a library directory and valid local minutes 0–1439; an invalid stored time is not silently scheduled. |
| Remove Orphans, STRM→symlink, blob migration actions | **Working / fixed visibility** | Text/navigation typos were corrected. Blob migration remains automatic; its status accordion is now shown only while legacy rows remain and disappears on completion. Destructive actions should consistently require confirmation. |

## Operational environment and container settings

These settings are intentionally outside the database/UI because most affect process startup, authentication boundaries, or storage layout.

| Variable(s) | Verdict | Review result |
|---|---|---|
| `PUID`, `PGID` | **Working** | The combined image creates/reuses the requested user/group and repairs config ownership. The official rclone sidecar does not consume these variables; its redundant example entries were removed because `--uid`/`--gid` already control mounted-file ownership. |
| `TZ` | **Fixed documentation** | Local-time maintenance uses the container timezone. The main davex Compose example now actually passes `TZ`, matching its instructions. |
| `CONFIG_PATH` | **Fixed** | Controls the main/metrics/Warden databases, blob store, data-protection keys, and persisted frontend session key. Entrypoint now requires a non-root absolute path, creates it, and fails clearly if creation is impossible. |
| `BACKEND_URL` | **Fixed** | Entrypoint strips trailing slashes and checks HTTP(S); frontend runtime config independently requires an HTTP(S) origin with no path/query/fragment. This prevents `//api`, broken proxy, websocket, and health URLs. |
| `FRONTEND_BACKEND_API_KEY` | **Fixed / internal** | Blank/whitespace values now fail instead of becoming an accepted empty internal secret. The combined image generates a per-start key when omitted. Keep this internal and never expose it as a user-facing API credential. |
| `SESSION_KEY` | **Fixed with deployment caveat** | The combined image now generates a mode-0600 key once under `CONFIG_PATH`, so one-year login cookies survive restarts. Standalone frontend/replicated deployments must explicitly provide the same strong key; otherwise a visible production warning is emitted and a process-local random key is used. |
| `SECURE_COOKIES` | **Fixed / important** | Case-insensitive `y`/`yes`/`true` parsing now matches backend flags. Enable whenever the browser-facing URL is HTTPS. |
| `DISABLE_FRONTEND_AUTH`, `DISABLE_WEBDAV_AUTH` | **Fixed warning / Decision** | Parsing is consistent and disabling either boundary now emits an unavoidable warning at startup/default log levels. Keep environment-only and use solely behind trusted external auth or on a private network; never put these switches in the Settings UI. |
| `PORT` | **Fixed** | Frontend startup now requires an integer 1–65535 instead of accepting partial/invalid `parseInt` values. |
| `MAX_REQUEST_BODY_SIZE` | **Fixed** | Defaults to 100 MiB and now rejects zero, negative, or non-integer values before Kestrel starts. No arbitrary upper cap is imposed because large manual NZBs may be intentional. |
| `MAX_BACKEND_HEALTH_RETRIES`, `MAX_BACKEND_HEALTH_RETRY_DELAY` | **Fixed** | Combined-image startup now requires positive integer count/delay values, preventing shell comparison errors or a zero-delay retry spin. |
| `LOG_LEVEL`, `LOG_BUFFER_SIZE` | **Working with limitation** | Buffer size is clamped 100–50,000. Invalid log levels currently fall back to Information; a centralized environment schema should warn rather than silently change verbosity. |
| `ALLOW_HTTPS_TO_HTTP_REDIRECTS` | **Fixed / Decision** | Off by default. The manual downgrade loop now resolves each relative hop against the current URI and disposes intermediate responses. Keep global opt-in for compatibility, or move it to a per-indexer advanced override to reduce downgrade scope. |
| `DANGEROUS_ENABLE_DATABASE_DOWNLOAD_ENDPOINT` | **Fixed / dangerous** | Still API-authenticated and off by default. It now uses SQLite's backup API so WAL transactions are included in a consistent main-database snapshot. It is explicitly not a full backup: metrics, Warden, blobs, and data-protection/session keys are separate. Prefer a dedicated full-`CONFIG_PATH` backup workflow. |
| `NZBDAV_VERSION` | **Fixed / build metadata** | Release builds now derive a strict SemVer value from the tag (and a valid `-edge.g<sha>` prerelease for manual branch builds); the fallback file was updated to 1.3.0. The footer, Addon manifest, and default User-Agents therefore no longer report 1.2.0 or receive `v1.3.0`/`main`. `NODE_ENV`, `TARGETARCH`, and `REPO_URL` remain internal build/runtime metadata. |
| `UPGRADE=0.6.0` | **Retire after support window** | One-time acknowledgement for a legacy direct-upgrade migration. Remove it when pre-0.6 upgrades are no longer supported. |
| Compatibility fallbacks: `MOUNT_DIR`, `CATEGORIES`, `WEBDAV_USER`, `WEBDAV_PASSWORD`, `NZB_GRAB_USER_AGENT`, `NZB_SEARCH_USER_AGENT` | **Working with limitation** | Precedence is database → environment → default, and non-secret effective values are now visible in Settings. Environment values still bypass Settings-API validation; validate them centrally. There is no action to clear a stored password back to the `WEBDAV_PASSWORD` fallback. |

## Backend-only and internal keys

| Key | Verdict | Recommendation |
|---|---|---|
| `api.lazy-rar-parsing` | **Working, hidden** | Useful compatibility/performance escape hatch. Expose under Advanced with a warning, or hard-enable after the eager fallback is no longer supported. |
| `api.strm-key` | **Internal** | Keep hidden. Rotation invalidates every existing STRM URL and needs a dedicated destructive workflow if ever exposed. |
| `watchtower.resolve-concurrency` | **Working, hidden** | See Watchtower decision above. |
| `warden.max-source-entries` | **Working, hidden safety cap** | Keep hidden or diagnostic-only. |
| Provider `PrepSpreadEnabled` | **Working, hidden nested field** | Expose under provider advanced or retire deliberately. |

## Unnecessary, duplicate, or misplaced UI

1. The dead Library component was removed; the underlying Repairs library setting remains.
2. The duplicate Retrieve User-Agent control was removed from SAB; Indexers is canonical.
3. `play.exclude-patterns` and legacy per-indexer `UserAgent` are migrated and removed.
4. Usenet performance/cache controls now live under Usenet → Advanced performance.
5. `general.base-url` now lives with Search Profiles while retaining SAB/Profile cross-validation; the one-control General tab was removed.
6. Blob migration status now appears only while legacy rows remain.
7. `UPGRADE=0.6.0` remains until the old direct-upgrade support window closes.

## Additions to evaluate

Recommended order:

1. **Safe provider apply** — build and validate a replacement NNTP graph, then drain the old graph without interrupting active streams.
2. **Arr controls** — per-instance Enabled, request timeout, poll interval, and identity-aware connection test. Longer term, prefer stable Arr status codes/state over localized English substrings.
3. **Credential lifecycle actions** — clear stored WebDAV/rclone passwords, remove Warden GitHub token, rotate Search Profile tokens, and define immediate cookie/session invalidation on WebDAV credential rotation.
4. **Watchtower persistence controls** — persist the daily resolve counter if “per day” must survive restarts; expose or derive resolve concurrency.
5. **Full configuration backup/restore** — snapshot all databases plus blobs and data-protection/session keys, with secret-aware encryption and restore validation. The opt-in main-DB endpoint is not this workflow.
6. **Rclone timeout** and optional retry/backoff policy.
7. **NZB backup failure policy** — strict intake failure versus best-effort backup with alerting.

## Verification

- Frontend: `npm run typecheck`
- Frontend production bundle: `npm run build`
- Backend: full `dotnet test backend.Tests/NzbWebDAV.Tests.csproj --no-restore`
- Container entrypoint: `sh -n entrypoint.sh`
- Added regression coverage for safe config parsing, password masking/update semantics, provider prep routing, profile adapter semantics, strict matching, Warden disabled refresh and deterministic backups, regex/payload validation, path-safe categories, Arr message matching, and fail-closed proxy handling.

Build/test output still reports known dependency advisories for Microsoft.OpenApi and SQLitePCLRaw (high) and SharpCompress (moderate). The container's npm install also reports 13 findings (2 low, 4 moderate, 7 high). Those are dependency-maintenance findings rather than settings behavior, but should be scheduled separately.

Latest verification result: 105/105 backend tests passed; frontend type-check and production build passed.

Deployment verification (2026-07-12): rebuilt `nzbdavex:main-local`, ran database maintenance, and redeployed current image `sha256:168d626720734e5bc395c0b9546344279bc4a73a97adf82b52d2b1bac21f9e2b`. The nzbdavex and rclone containers are healthy with zero restarts; the public route returns the expected authentication redirect. Live structural validation found 102 metadata entries and valid unique provider UUIDs/Profile references. An entrypoint export-order bug found during deployment was fixed before the final healthy rollout.

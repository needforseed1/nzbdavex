# Settings review summary

Date: 2026-07-12

The full settings audit is available in [settings-audit.md](settings-audit.md).

## Outcome

The review covered UI/database settings, nested provider and indexer models, hidden switches, and operational environment variables.

Key fixes include:

- Settings loading, shared-save failures, cross-tab validation, malformed JSON handling, secret masking, and inconsistent defaults.
- Provider spreading, block-account cap initialization, credential-test races, indexer quotas and RPM enforcement, fail-open proxy behavior, and timeout inconsistencies.
- Warden, Watchtower, Search Profile, Arr, rclone, SAB path containment, maintenance, and container-startup defects.
- Persistent frontend session keys, strict operational environment validation, visible authentication-disable warnings, correct release versions, and consistent SQLite main-database snapshots.
- Additional regression tests for settings parsing, validation, routing, quota behavior, Warden behavior, path safety, proxy handling, and shared NZB fetches.

## Verification

- Backend: 162/162 tests passing.
- Frontend settings-state tests: passing.
- Frontend TypeScript/type generation: passing.
- Frontend production build: passing.
- Container entrypoint shell syntax: passing.
- Workflow YAML parsing: passing.
- `git diff --check`: passing.

The approved legacy-setting migrations and UI relocations described below have been implemented in the working tree and included in the local container rebuild.

The container build also reports 13 npm audit findings (2 low, 4 moderate, 7 high), in addition to the .NET dependency advisories listed later in this document. Dependency upgrades remain separate follow-up work.

## Deployment

Rebuilt and redeployed locally on 2026-07-12 as `nzbdavex:main-local` (current image `sha256:168d626720734e5bc395c0b9546344279bc4a73a97adf82b52d2b1bac21f9e2b`). Database maintenance completed successfully; the application container and its rclone companion are healthy with zero restarts, and the public route responds through authentication as expected.

The final UI refinement removed the prep-limit toggle (blank now directly means Automatic), placed Public Base URL with Search Profiles instead of a one-control General tab, and documents decoded segment cache as recommended for Nuvio-style streaming when backed by fast local SSD/NVMe.

Provider data usage is now a compact inline status strip rather than a large boxed panel. The Usenet Advanced disclosure has a clearer full-width summary, tuning icon, contents preview, hover state, and chevron while remaining collapsed by default.
Its Concurrency & scheduling, Streaming & cache, Provider routing, and Pipelining groups now use restrained subsection emphasis so their boundaries and headings are easier to scan.

The first deployment also exposed and fixed an entrypoint regression: the default `BACKEND_URL` was validated in a child process before being exported. The corrected image exports it before validation and starts cleanly.

## Highest-value remaining decisions

1. Drain and safely apply provider graph changes without interrupting active streams.
2. Add credential removal/rotation actions and a complete encrypted `CONFIG_PATH` backup workflow.
3. Add missing Arr, rclone timeout, and NZB backup-policy controls.

Stable provider/indexer UUIDs, the centralized settings defaults and validation catalog, and Watchtower LRU enforcement are now implemented.

## Implemented architecture work

- Provider UUIDs now own usage caps, byte metrics, failover edges, connection snapshots, and UI identity. Legacy host metrics remain readable during the transition.
- Indexer UUIDs now own quota/RPM state, Search Profile references, cached candidate retrieval, and Watchtower pointers. Existing name-based hits and references are migrated.
- The backend settings registry supplies UI defaults and basic scalar validation. The settings endpoint returns only the effective key/value pairs the frontend consumes, so the duplicate frontend default table and unused presentation metadata are gone.
- Watchtower now enforces the active-set cap with LRU warm/cold rotation. Verified cold entries retain their winner and promote immediately when accessed.

## Implemented retirement and relocation work

### Dead `frontend/app/routes/settings/library/`

The unreachable component, stylesheet, and CSS type file were deleted. The underlying `media.library-dir` setting remains in Repairs because Repairs and Maintenance still use it.

### Legacy `play.exclude-patterns`

The legacy key is now migrated to `search.exclude-patterns` only when the current key is absent, then deleted. An explicitly stored empty current value still wins. Frontend and runtime fallback code has been removed, and migration behavior has regression coverage.

### Legacy per-indexer `UserAgent`

Legacy `UserAgent` values are now copied into blank/missing `SearchUserAgent` and `RetrieveUserAgent` fields, without overwriting either specific value, and the alias is removed from stored JSON. Runtime and frontend fallbacks are gone; editing also defensively strips stale aliases. Unknown indexer fields are preserved.

### Usenet performance controls moved out of WebDAV

The following controls now live under `Usenet → Advanced performance`:

- `usenet.max-download-connections` — playback connection budget.
- `usenet.max-queue-connections` — prep/queue connection budget.
- `usenet.streaming-priority` — playback-versus-queue semaphore weighting.
- `usenet.article-buffer-size` — per-stream article look-ahead.
- `usenet.segment-cache.enabled`, `.path`, and `.max-gb` — decoded segment disk cache.

They are grouped into “Concurrency & scheduling” and “Streaming & cache.” Their stored keys are unchanged, and their update detection, validation, and provider-capacity dependency moved with the controls. WebDAV now contains only DAV authentication and presentation behavior.

### `general.base-url` moved out of SAB

`general.base-url` is broader than the SAB-compatible API. It is used for:

- URLs written into generated `.strm` files.
- Search Profile JSON, Newznab, and Addon URLs.
- Public playback and sharing links.

It now lives with Search Profiles, which generate and display most public adapter URLs, rather than occupying a one-control General tab. The stored key is unchanged, blank/inferred values remain supported, and cross-tab validation still requires an absolute HTTP(S) URL for STRM imports and validates Profile URL generation.

### Status-only blob migration UI

The “Migrate Large Database Blobs to Blobstore” accordion does not start, stop, or configure anything. `UsenetFileToBlobstoreMigrationService` runs automatically as a hosted background service, and the UI only subscribes to websocket topic `uftbmp` to display progress.

The backend now reports the number of legacy database blobs remaining. Maintenance shows the status accordion only while work remains and removes it when migration reports completion. The automatic migration service and websocket topic remain available for restored legacy databases.

### Legacy `UPGRADE=0.6.0`

`UPGRADE=0.6.0` is not a normal tuning setting. When the old incompatible migration is pending, startup refuses to continue until the exact value is supplied. It serves as acknowledgement that the schema change is irreversible and that the entire `/config` directory should be backed up first.

Recommended action:

- Keep it while direct upgrades from pre-0.6 installations are supported.
- Document it as a one-time migration acknowledgement, not a persistent environment setting.
- Remove the environment check and associated startup branch when the pre-0.6 upgrade path leaves the support window.

Removing it too early would allow an irreversible legacy upgrade without explicit acknowledgement; retaining it forever adds dead startup complexity.

### Duplicate Retrieve User-Agent control removed

The global retrieve User-Agent, `api.user-agent`, was previously editable under both SAB and Indexers. These were two controls for the same stored key, not independent settings. Editing either changed the other through shared form state, which made ownership unclear and suggested SAB downloads might use a different value.

The SAB copy was removed. Indexers is now the canonical location because it can present the global Search and Retrieve User-Agents beside their per-indexer overrides.

No backend behavior or stored key was removed:

- `api.search-user-agent` remains the global search identity.
- `api.user-agent` remains the global NZB retrieval identity.
- Per-indexer overrides remain available for both.

The two global identities should remain separate; only the duplicate UI control was unnecessary.

### Remaining legacy cleanup

Retire the blob migration service/topic and `UPGRADE=0.6.0` only when their respective legacy database and pre-0.6 direct-upgrade support windows close.

## Separate maintenance findings

Dependency checks still report:

- High-severity advisories for Microsoft.OpenApi and SQLitePCLRaw.
- A moderate-severity advisory for SharpCompress.

These should be addressed in a separate dependency-upgrade pass.

## Repository note

The unrelated untracked `h -h github.com -s delete:packages,read:packages` file was left untouched.

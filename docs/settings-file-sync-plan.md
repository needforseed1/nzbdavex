# Settings File Synchronization Plan

## Status

Implemented. The product and safety decisions listed at the end of this
document are confirmed. Bidirectional synchronization remains opt-in through
`SETTINGS_FILE_SYNC_ENABLED=true`.

## Objective

Add a human-readable `/config/settings.yaml` that:

- contains every persistent, user-editable setting;
- follows the same section, subsection, and field order as the settings GUI;
- updates when settings are saved in the GUI;
- can be edited while nzbdavex is running;
- applies valid file edits to SQLite and the running application;
- refreshes the GUI when settings change outside the browser; and
- round-trips explicitly configured passwords, API keys, and tokens, accepting
  that these values will be present in cleartext in the file.

SQLite remains the operational source of truth. The YAML file is a synchronized,
editable projection of the configuration, not a replacement for the database.

## Current State

- The normal settings screen is bootstrapped from 101 keys in `SettingsRegistry`.
- Values are stored as strings in SQLite `ConfigItems`.
- Providers, indexers, Search Profiles, and Radarr/Sonarr instances are stored as
  JSON blobs inside individual `ConfigItems` rows.
- A GUI save validates the changed values, writes SQLite, updates the in-memory
  `ConfigManager`, and raises `OnConfigChanged`.
- The browser receives a settings snapshot when the route loads. It is not
  notified when another browser or process changes settings.
- GUI order is currently expressed by the React route and individual settings
  components. There is no single ordered schema shared by the frontend,
  validator, defaults registry, and a potential config-file writer.
- Warden remote sources and Warden GitHub backup options are stored separately
  from `ConfigItems`.
- Some saved values are operational state or actions rather than settings and
  must not be exported.

## Scope

### Include

- All 101 registered settings.
- Usenet provider definitions, options, usernames, and passwords.
- Indexer definitions, options, and API keys.
- Search Profile definitions, options, and access tokens.
- Radarr/Sonarr instance definitions and queue rules.
- Warden behavioral settings.
- Warden remote-source definitions: name, URL, enabled state, trust, and refresh
  interval.
- Warden backup settings: enabled state, repository, path, branch, scope,
  schedule, and GitHub token.
- Automatic/blank states, such as blank Usenet prep connections meaning
  automatic pool sizing.
- Provider cap configuration, manual usage offset, and counter-reset timestamp.

### Exclude

- Warden fingerprint data and imported Warden datasets.
- Queue, history, Watchdog, Watchtower, provider metrics, and benchmark results.
- Live provider byte counters; only the user-configured cap/offset/reset values
  belong in configuration.
- Database migration status and blob-migration state.
- Buttons and one-off actions such as tests, benchmarks, imports, exports,
  backups, restores, cleanup, conversions, and counter resets.
- Generated runtime status such as `last run`, `last error`, health, active
  connections, or whether a secret is configured.
- Internal/runtime environment values such as `FRONTEND_BACKEND_API_KEY`,
  `SESSION_KEY`, `BACKEND_URL`, process ports, logging controls, image metadata,
  UID/GID, and authentication-disable switches.

## Canonical Display Order

The schema and YAML writer must preserve this top-level order:

1. Usenet
   1. Providers
   2. Advanced Usenet settings
      1. Concurrency & scheduling
      2. Streaming & cache
      3. Provider routing
      4. Pipelining
2. Indexers
3. Search Profiles
4. Watchdog
5. Preflight
6. Watchtower
7. Warden
8. Advanced
   1. WebDAV
   2. SABnzbd
   3. Radarr / Sonarr
   4. Repairs
   5. Rclone Server
   6. Maintenance

Field order inside each subsection must also come from the central schema. The
YAML writer must not alphabetize mappings.

## Proposed File Shape

Use typed, nested YAML rather than exposing internal dotted keys or JSON strings.
Compound values should be readable arrays and objects.

```yaml
version: 1

usenet:
  providers:
    - id: aaa3cc10-c54e-35d4-2079-c173b6d71879
      name: Eweka
      role: pooled
      host: news.eweka.nl
      port: 563
      ssl: true
      username: example-user
      password: "<cleartext password>"
      max_connections: 50
      priority: 0
      prep_only: false
      prep_spread: true
      playback_pipeline_depth: 8
      health_pipeline_depth: 32
      data_cap_bytes: null
      usage_offset_bytes: 0
      usage_reset_at: null

  advanced:
    concurrency_and_scheduling:
      playback_connections: 15
      prep_connections: null # null means automatic
      streaming_priority: 80

    streaming_and_cache:
      article_buffer_mib: 40
      decoded_segment_cache: false
      cache_path: /config/segment-cache
      cache_size_gib: 10
```

The example is illustrative. Final names must be reviewed against every GUI
field before the version 1 schema is frozen.

The generated file contains reusable credentials and must be treated like a
password vault even though it is human-readable configuration.

## Central Settings Schema

Create one backend-owned, versioned schema containing, for every setting:

- stable internal key;
- YAML path;
- GUI section and subsection;
- display order;
- data type;
- default value;
- whether blank/null means automatic, inherited, disabled, or empty;
- allowed choices and numeric range;
- credential/sensitive-value classification;
- environment fallback name and source-precedence policy, where applicable;
- validation dependencies;
- runtime application policy: immediate, component reload, or restart required;
- serialization adapter for compound settings; and
- deprecation/migration aliases.

The frontend metadata endpoint should expose the relevant non-secret parts of
this schema. At minimum, automated tests must prove that GUI order and YAML order
match the schema. Longer term, shared metadata can reduce duplicated frontend
labels, defaults, and validation.

The current `SettingsRegistry`, `settings-state.ts`, backend validator, and file
writer must not become four independent lists.

## Credential Handling

### Accepted Tradeoff

The synchronized file may contain reusable credentials in cleartext. This avoids
a secret-store migration and lets a complete configuration round-trip without
special references or merge rules. It does not make the installation secure
against disclosure of the file.

The following may be serialized when they are explicitly configured in
nzbdavex:

- Usenet provider passwords.
- Indexer API keys.
- Radarr and Sonarr API keys.
- SABnzbd-compatible API key.
- Rclone password.
- Search Profile access tokens.
- Warden GitHub token.
- Proxy credentials and credentials embedded in configured URLs.

The WebDAV password is the exception. nzbdavex stores only its one-way hash and
cannot export the original password. YAML must therefore support
`password_hash`. If a user writes a plaintext `password`, import it once, hash it,
and canonically rewrite the entry as `password_hash` without logging either
value.

### File Protection

- Create `settings.yaml` with mode `0600` and refuse symbolic-link targets.
- Create temporary files in the same protected directory with `0600` before
  writing any content, then replace atomically.
- Do not relax permissions during rewrites, ownership repair, or container
  startup.
- Enforce mode `0600`. If the file has broader permissions, correct them
  automatically when possible before reading or writing it.
- If restrictive permissions cannot be established, disable file
  synchronization, retain SQLite's last-known-good settings, and show a clear
  error. Do not stop the rest of the application.
- Add the filename to repository `.gitignore` patterns and documentation.
- Never include the file in diagnostics or support bundles by default.
- Never log serialized configuration, parse excerpts containing values, request
  bodies, or diffs that could expose credentials.
- Document that `/config` backups, snapshots, editor swap files, and copied YAML
  contain account access.

No database credential migration is required for this feature. Existing storage
continues to work as it does today; this plan changes only the synchronized file
and mutation path.

## Environment and `.env` Compatibility

Yes, several GUI settings already have environment-variable fallbacks. A Docker
Compose `.env` file is not read directly by nzbdavex: Compose uses it for
substitution, and only values actually passed into the container become process
environment variables.

### Existing Setting Fallbacks

| YAML / internal setting | Environment fallback | Current precedence |
| --- | --- | --- |
| `rclone.mount-dir` | `MOUNT_DIR` | stored setting -> environment -> default |
| `api.categories` | `CATEGORIES` | stored setting -> environment -> default |
| `webdav.user` | `WEBDAV_USER` | stored setting -> environment -> default |
| `webdav.pass` | `WEBDAV_PASSWORD` | stored hash -> environment password -> unset |
| `api.user-agent` | `NZB_GRAB_USER_AGENT` | stored setting -> environment -> generated default |
| `api.search-user-agent` | `NZB_SEARCH_USER_AGENT` | stored setting -> environment -> generated default |
| `api.key` | `FRONTEND_BACKEND_API_KEY` | stored setting -> internal environment key |

`FRONTEND_BACKEND_API_KEY` is primarily the private frontend/backend transport
credential. It must remain environment-only and must never be exported merely
because `api.key` falls back to it.

### Infrastructure-Only Environment Values

The following remain outside YAML and the GUI settings model:

- `CONFIG_PATH`, `BACKEND_URL`, `PORT`, `PUID`, `PGID`, and `TZ`.
- `FRONTEND_BACKEND_API_KEY`, `SESSION_KEY`, and `SECURE_COOKIES`.
- `DISABLE_FRONTEND_AUTH` and `DISABLE_WEBDAV_AUTH`.
- `LOG_LEVEL`, `LOG_BUFFER_SIZE`, and `MAX_REQUEST_BODY_SIZE`.
- `NZBDAV_VERSION`, `NODE_ENV`, build metadata, and the legacy `UPGRADE` guard.
- The new opt-in file-sync path/enable environment variable.

### Precedence Rules

Preserve existing compatibility in version 1:

1. An explicit YAML/SQLite value wins.
2. If the explicit value is absent/inherited, use the environment fallback.
3. Otherwise use the application default.

YAML and SQLite form one synchronized explicit-settings layer; neither has
independent precedence over the other after a change is accepted.

The exporter must serialize configured values, not blindly serialize effective
values. If a value currently comes from the environment, YAML must represent it
as `null`/inherited and may add a non-sensitive source comment. It must not copy
an environment value into YAML, especially an environment-supplied credential.
Otherwise the generated file would silently freeze the old environment value
and shadow later `.env` changes.

Setting an eligible YAML field to `null` resets it to inherited behavior. The
coordinator must delete/reset the corresponding stored override rather than
writing a value that accidentally blocks fallback. This also adds the currently
missing way to return from a stored WebDAV password hash to `WEBDAV_PASSWORD`.

Settings metadata should report `source: yaml/sqlite`, `environment`, or
`default`, plus the relevant environment-variable name. The GUI should show when
an environment fallback is active and when an explicit setting is shadowing a
present environment value.

Environment changes are not live: a process cannot rewrite its own Docker
environment or `.env`. Changing an environment fallback requires recreating the
container. YAML edits remain live within the rules of each setting.

## Bidirectional Synchronization

All mutations must go through a single `SettingsCoordinator`; controllers and a
file watcher must not implement separate save paths.

### GUI/API to File

1. Receive the proposed changes and the browser's base revision.
2. Acquire the settings mutation lock.
3. Merge the changes with the current full configuration and environment-source
   metadata.
4. Preserve masked GUI credentials that were not explicitly replaced; require
   credential fields in complete YAML objects unless that field is inherited
   from an environment fallback.
5. Validate the complete resulting configuration.
6. Persist normal settings and any out-of-band Warden settings transactionally
   where possible.
7. Update `ConfigManager` and notify runtime subscribers.
8. Increment the settings revision.
9. Render canonical YAML in schema order.
10. Write a sibling temporary file, flush it, then atomically replace
    `settings.yaml`.
11. Record the exported content hash and publish a settings-changed event.

If the final file replacement fails after SQLite succeeds, mark the projection
dirty, report the failure, and retry/reconcile. Do not silently claim that both
sides are synchronized.

### File to GUI/API

1. Watch the containing directory, not only the file, because editors commonly
   replace files atomically.
2. Debounce duplicate events and retry briefly while an editor is writing.
3. Ignore content whose hash matches the app's last canonical export.
4. Parse the complete file into the versioned typed model.
5. Reject unknown fields, duplicate IDs, invalid types, invalid references, and
   unsupported schema versions.
6. Resolve explicit values, inherited environment fallbacks, and defaults.
7. Validate the complete effective configuration using the same validator as a
   GUI save.
8. Under the mutation lock, calculate the delta and commit it through the same
   coordinator.
9. Increment the revision and rewrite the accepted configuration canonically.
10. Notify runtime subscribers and connected settings pages.

An invalid edit must not partially apply. Keep the last-known-good database and
runtime configuration, log an actionable error with its YAML location, and show
the error in the GUI.

## Revision and Conflict Handling

- Maintain a monotonically increasing settings revision in SQLite.
- Return the revision with settings metadata.
- Require GUI saves to include the revision they were based on.
- Return HTTP 409 if a stale browser attempts to overwrite newer settings.
- Publish `settings-changed` events over the existing WebSocket infrastructure,
  or a dedicated SSE stream.
- If a settings page has no unsaved edits, reload it automatically.
- If it has unsaved edits, show an external-change banner with Reload and Review
  options. Never silently overwrite the form.
- Store the last canonical file hash so the app can distinguish its own write
  from a manual edit.

## Startup and Recovery Rules

- The feature is permanently opt-in through an environment variable, with
  `/config/settings.yaml` as the default path. It must never become enabled
  implicitly because enabling it creates a human-readable credential file.
- If the file does not exist, create it from the current database.
- If the file exists and differs from the last exported hash, validate and
  import it before dependent services begin work.
- If the file is invalid, start with the last-known-good database configuration,
  mark file synchronization unhealthy, and surface the error prominently.
- Never overwrite an invalid user-edited file automatically. Preserve it for
  correction and expose a separate action to rewrite it from the database.
- A successful import rewrites canonical formatting and records a new revision
  and hash.

## Runtime Application Policy

File synchronization can be live even when a particular setting cannot take
effect without rebuilding a component or restarting the process.

The schema must label each setting as:

- **Immediate:** consumers read the current `ConfigManager` value per operation.
- **Component reload:** safely rebuild and drain the affected client/service.
- **Restart required:** persist immediately but clearly show that activation is
  deferred.

Known cases requiring attention:

- `db.is-startup-vacuum-enabled` is intentionally restart-only.
- Segment-cache enable/path/size are currently read when the singleton Usenet
  client is constructed. Saving only those fields does not rebuild the cache
  wrapper today.
- Provider changes already rebuild through a draining Usenet-client swap.
- Rclone mount behavior partly belongs to the external rclone container and
  cannot necessarily be changed by the nzbdavex process alone.

Do not claim universal hot reload until every registered setting has an audited
application policy and tests.

## Validation and Formatting

- Use one full-snapshot validator for GUI and file changes.
- Preserve current scalar ranges, choices, regex validation, provider/indexer
  UUID checks, and cross-object reference checks.
- Add validation for missing required credentials before enabling dependent
  objects.
- Treat `null`, blank, automatic, inherited, and zero as distinct where the GUI
  does so.
- Emit booleans and numbers as YAML primitives, not strings.
- Emit byte quantities in an unambiguous representation. Prefer exact integer
  bytes in the persisted model, with comments or GUI formatting for GB/TB.
- Emit timestamps as ISO 8601 UTC rather than raw Unix milliseconds where a
  human is expected to edit them; adapters can retain existing internal values.
- Canonical app rewrites preserve schema order and generated descriptions but
  deliberately discard arbitrary user comments. A comment-preserving YAML
  syntax tree is out of scope.

## Migration Plan

1. Introduce the ordered schema without changing persistence.
2. Add source metadata so explicit, environment-derived, and default values are
   distinguishable without changing their current precedence.
3. Add reset-to-inherited behavior, including removal of a stored WebDAV hash
   when returning to `WEBDAV_PASSWORD`.
4. Add the typed YAML model and deterministic exporter.
5. Add file import through the shared coordinator.
6. Add revision checks and live browser notifications.
7. Enable synchronization only for installations that explicitly opt in and
   collect upgrade feedback. Keep it opt-in permanently.

No credential-store migration or plaintext-field scrub is part of this feature.
Create a database and `settings.yaml` backup before first enabling imports so the
explicit configuration layer can be restored independently of `.env` fallbacks.

## Implementation Phases

### Phase 1: Inventory and Schema

- Map every GUI field to its internal key, type, section, order, and apply policy.
- Inventory Warden settings stored outside `ConfigItems`.
- Classify every credential and environment-backed setting so serialization,
  redaction in logs, and source precedence are explicit.
- Add coverage tests proving no editable GUI control is missing from the schema.

### Phase 2: Canonical Export

- Add typed YAML DTOs and internal-key adapters.
- Generate the complete file, including explicitly stored credentials, in GUI
  order.
- Add deterministic snapshot tests and restrictive file permissions.
- Confirm environment-derived values are emitted as inherited/null rather than
  copied into the file.
- Add manual Export/Rebuild action for recovery.

### Phase 3: Import and Runtime Sync

- Add the mutation coordinator, directory watcher, debounce, hashing, and atomic
  writes.
- Route GUI saves through the coordinator.
- Add full validation, last-known-good behavior, and actionable diagnostics.
- Add explicit reset-to-environment behavior and collision/source warnings.
- Audit and implement component reloads where safe.

### Phase 4: Live GUI and Conflicts

- Add settings revisions and optimistic concurrency.
- Broadcast external changes.
- Auto-refresh clean forms and protect dirty forms with a conflict banner.
- Show file path, synchronization health, last accepted revision, and last file
  error in Maintenance.

### Phase 5: Rollout

- Ship as a permanently opt-in feature. Never create the credential-bearing
  file without an explicit enablement decision.
- Exercise upgrades, downgrades, backup restores, invalid files, permission
  failures, editor atomic replacements, concurrent browsers, and environment
  fallback collisions.
- Document credential exposure, backup handling, and `.env` precedence.

## Test Plan

### Schema and Round Trips

- Every editable GUI field exists exactly once in the ordered schema.
- YAML order matches the GUI order.
- Database -> YAML -> database preserves all explicitly configured settings and
  credentials.
- Compound providers/indexers/profiles/Arr settings round-trip without escaped
  JSON or lost stable IDs.
- Automatic blank values remain automatic.
- Environment-derived values remain inherited and are not converted into stored
  overrides.
- Canonical output is deterministic across repeated exports.

### Credentials and Environment

- Explicit provider, indexer, Arr, rclone, profile, SABnzbd, proxy, and Warden
  credentials round-trip exactly.
- Environment-supplied credentials are never copied into YAML automatically.
- `FRONTEND_BACKEND_API_KEY` is never exported as the SABnzbd-compatible API key
  when it is only acting as a fallback.
- WebDAV plaintext import produces a valid hash and canonical export never
  claims to know the original password.
- Setting an environment-backed field to null removes its stored override.
- An explicit YAML value predictably shadows a present environment fallback and
  produces visible source metadata.
- Credentials do not appear in logs, exceptions, diffs, settings-change events,
  support bundles, or world/group-readable temporary files.
- File mode and ownership remain restricted after create, rewrite, import, and
  container restart.

### Synchronization

- GUI saves update SQLite, runtime state, YAML, revision, and connected browsers.
- Valid file edits update all corresponding destinations once.
- Self-generated writes do not loop through the watcher.
- Partial/invalid YAML never partially applies.
- Rapid editor saves collapse into one accepted update.
- Atomic rename, in-place write, deletion, recreation, and permission errors are
  handled predictably.
- A dirty browser cannot overwrite a newer file or browser update silently.
- A failed YAML write is reported and later reconciled.

### Runtime Behavior

- Immediate settings affect the next applicable operation.
- Component-reload settings drain old clients without interrupting active reads.
- Restart-required settings are labelled correctly in GUI and file diagnostics.
- Provider, indexer, profile, Watchdog, Preflight, Watchtower, Warden, WebDAV,
  SABnzbd, Arr, repair, rclone, and maintenance settings each have representative
  end-to-end tests.

## Acceptance Criteria

- `/config/settings.yaml` is complete, readable, deterministic, and ordered like
  the GUI.
- The file round-trips explicitly configured credentials and is always treated
  as sensitive data.
- Environment-derived values are represented as inherited and never silently
  frozen into the explicit configuration layer.
- Existing credentials survive unrelated GUI and file edits.
- Valid changes propagate in both directions without restarting when the setting
  supports live application.
- Invalid edits leave the last-known-good configuration active and produce a
  useful error.
- Concurrent edits cannot silently overwrite one another.
- The GUI and YAML are both backed by the same schema, validator, coordinator,
  and revision.
- Existing installations adopt synchronization without losing connectivity,
  credentials, or established environment fallback behavior.

## Confirmed Decisions

1. **Canonical comments:** Do not preserve custom comments. Canonically rewrite
   YAML in GUI order and include generated descriptions where useful.
2. **Permissions:** Enforce `0600`. Correct permissions automatically when
   possible. Otherwise disable synchronization, retain SQLite's last-known-good
   settings, and show a clear error without stopping the application.
3. **Warden remote sources:** Include source name, URL, trust, enabled state, and
   refresh interval. Exclude fingerprint data, import history, and runtime
   status.
4. **Precedence:** Preserve explicit YAML/SQLite -> environment -> default.
   Setting an eligible YAML field to `null` removes the explicit override and
   returns to the environment fallback.
5. **Enablement:** Keep bidirectional synchronization permanently opt-in because
   enabling it creates a human-readable credential file.

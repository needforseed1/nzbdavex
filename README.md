# davex

**Stream Usenet releases on demand, with provider-aware verification and enough evidence to understand every failure.**

davex turns NZBs into seekable files without downloading the complete release first. It can act as a SABnzbd-compatible download client for Radarr and Sonarr, expose the resulting files through WebDAV, or search and play releases directly through token-scoped JSON, Newznab, and addon adapters.

This repository is an independent fork of [qooode/nzbdavex](https://github.com/qooode/nzbdavex), built around reliable multi-provider operation, automatic candidate fallback, proactive verification, and playback diagnostics.

> [!IMPORTANT]
> davex does not include a Usenet provider, indexer, or media catalog. You supply and configure the services you are entitled to use.

## Why this fork exists

Mounting an NZB is easy when every article is healthy. The difficult part is deciding quickly whether a release is actually usable, combining coverage from several providers, recovering safely during playback, and explaining what happened when something fails.

This fork focuses on that complete path:

- **Qualify before committing:** preparation inspects only useful media and recovery data, then samples article availability before starting an expensive full health check.
- **Combine provider coverage:** a release does not need to be complete on one provider. Primary and backup providers can collectively supply it, while providers that fail qualification stay out of normal bulk work.
- **Fail with evidence:** troublesome NZBs receive bounded checks instead of holding the queue indefinitely. Watchdog records the release, provider contribution, failure reason, and time spent in preparation, probing, and health verification.
- **Recover during playback:** stalled, truncated, or silent article bodies are retried on healthy connections and eligible providers. Playback capacity can be reserved so queue and health work cannot consume every connection.
- **Keep the next play ready:** Watchtower can resolve and periodically reverify wanted titles before they are requested. Warden remembers known-dead release fingerprints and can share trusted remote lists.
- **Show viewer impact, not internal noise:** Activity groups range requests into plays and separates successful recovery from source delays, damaged output, timeouts, and failures.

## Two ways to use davex

The modes can be used separately or together.

### Automation library

```text
Radarr / Sonarr
      │  SABnzbd API
      ▼
    davex ── prepare and verify NZB ──► WebDAV
                                          │
                                          ▼
                                    rclone mount
                                          │
                              symlink import / playback
                                          │
                                          ▼
                                   Plex / Jellyfin
```

Radarr or Sonarr sends davex an NZB as if it were a normal SABnzbd download. davex prepares and verifies the release, creates the virtual files, and reports the import path. Rclone presents those files to the host, while media bytes are fetched from Usenet only when a client reads them.

If an NZB fails, davex reports the failure through the SAB-compatible workflow. Your configured Radarr/Sonarr queue rules decide whether to remove it, blocklist it, and search again, preserving the Arr application's release scoring and retry behavior.

### On-demand search and playback

```text
Compatible client
      │  Search Profile adapter
      ▼
Indexer search ──► ranked candidates ──► verify / fall through ──► playable URL
                                                 │
                                                 ▼
                                       Watchdog evidence
```

A Search Profile selects indexers, matching rules, fallback queries, and output adapters. On a play request, davex tries candidates in ranked order, skips known-dead results, verifies the release, and falls through to the next candidate when necessary.

Each profile can expose:

| Adapter | Purpose |
|---|---|
| **Addon** | Manifest and stream endpoints for compatible media clients |
| **Newznab** | A profile-backed meta-indexer for Prowlarr, Radarr, or Sonarr |
| **JSON** | A vendor-neutral search and play API for custom clients |

## What is included

| Area | Capabilities |
|---|---|
| **Virtual filesystem** | WebDAV browsing, range reads, seeking, multipart media, RAR and 7z mapping, and SAB-compatible queue/history endpoints |
| **Usenet routing** | Multiple providers, Primary / Backup + health / Backup roles, persistent connection pools, playback reservations, usage caps, and per-provider accounting |
| **Verification** | Coverage probing, pipelined `STAT` health checks, collective provider coverage, bounded fallback, and confirmed-missing versus temporarily-unverifiable verdicts |
| **Playback** | MiB-based read-ahead, optional NNTP body pipelining, decoded segment cache, incomplete-body detection, connection replacement, and provider failover |
| **Search** | Multiple Newznab-compatible indexers, strict title matching, filtering, quotas, fallback queries, deduplication, and token-scoped Search Profiles |
| **Automation** | Radarr/Sonarr monitoring, configurable queue actions, symlink or STRM workflows, background repair, and Plex attribution |
| **Observability** | Live throughput and connections, queue progress, Watchdog attempt history, Activity playback history, provider shares, health history, and in-app logs |
| **Proactive reliability** | Preflight candidate verification, Watchtower wanted-list warming, and Warden local/remote dead-release fingerprints |

## Reliability model

For an imported or on-demand NZB, davex normally moves through these stages:

1. **Preparation** reads the minimum useful metadata needed to expose the media. Samples, artwork, checksums, unnecessary recovery volumes, and other irrelevant files do not hold up the hot path.
2. **Probe** samples article availability across eligible health providers. Coverage is evaluated collectively; one provider does not need to return every sample.
3. **Health check** verifies the full release using qualified provider capacity. Unanswered work can move to another provider without discarding completed checks.
4. **Playback** reads only the requested ranges. Silent or partial bodies are not accepted as successful articles, and recovery can rotate connections or providers.

When the evidence is insufficient, davex distinguishes a confirmed missing article from a provider outage or other temporarily unverifiable result. This avoids poisoning the missing-article cache or rejecting a release merely because one provider was unavailable.

## Pages that matter

- **Overview** — live throughput, provider performance, active reads, errors, and historical trends.
- **Queue** — preparation and verification progress for SAB imports.
- **Watchdog** — candidate-by-candidate resolution history, including failure reasons, provider coverage, and time to success or failure.
- **Activity** — active and completed plays, source delays, bytes served, provider contribution, recovery behavior, and Plex context.
- **Watchtower** — titles davex is resolving and keeping verified ahead of demand.
- **Files** — browse the WebDAV-backed virtual filesystem.
- **Health** — background verification and repair state.
- **Search** — query configured indexers from the UI.

## Quick start

Release images are published for `linux/amd64` and `linux/arm64` at `ghcr.io/needforseed1/nzbdavex`.

```yaml
services:
  davex:
    image: ghcr.io/needforseed1/nzbdavex:stable
    container_name: davex
    restart: unless-stopped
    ports:
      - "3000:3000"
    environment:
      PUID: 1000
      PGID: 1000
      TZ: Europe/Oslo
    volumes:
      - ./config:/config
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:3000/health"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 10s
```

Start it and open `http://<server>:3000`:

```bash
docker compose up -d
```

On first launch:

1. Create the administrator account.
2. Add and test at least one provider under **Settings → Usenet**.
3. Set WebDAV credentials under **Settings → WebDAV** if you will use rclone.
4. Add indexers and a Search Profile if you want on-demand search or profile adapters.
5. Configure the SAB API and Radarr/Sonarr integration if you want automated library imports.

The `/config` volume contains databases, credentials, encryption keys, session state, and other persistent application data. Back up the complete directory, not only `db.sqlite`.

### Image tags

- `:stable` and `:latest` — newest stable release.
- `:1.4` — newest release in a minor line.
- `:1.4.0` — an exact release.

Pin an exact version when you prefer controlled upgrades.

## Integrations

### Radarr and Sonarr

Add davex as a **SABnzbd** download client using port `3000` and the API key shown under **Settings → SABnzbd**. Add your Arr instances under **Settings → Radarr/Sonarr** to enable queue monitoring and configurable remove, blocklist, and search actions.

Library imports normally use the `completed-symlinks` directory exposed through an rclone WebDAV mount. The same container path must be meaningful to davex and the Arr application.

### Rclone and media servers

Rclone is optional for direct Search Profile playback, but is normally required for Radarr/Sonarr symlink imports and filesystem-based Plex or Jellyfin libraries. Use WebDAV with `--links`, cookies, and VFS full-cache mode so symlinks, authentication, range reads, and seeks behave correctly.

See the [setup guide](docs/setup-guide.md) for the full compose stack, rclone flags, Arr paths, repairs, and adapter examples.

### Search Profiles

The Settings page generates the exact URL for each enabled adapter. Treat profile tokens as credentials: anyone with the URL can use that profile's exposed capabilities.

For AIOStreams, configure its NzbDav service with `streaming:<NZBDAV API key>`. Add the `streaming:` prefix only in AIOStreams; keep the API key stored in davex unchanged. This lets davex classify those submissions as on-demand activity rather than normal Arr imports.

## Performance controls

The defaults are intended to be safe, but installations differ substantially in provider count, latency, bandwidth, and connection limits. The built-in provider benchmarks are a better starting point than copying another installation's connection counts.

The most important controls are under **Settings → Usenet → Advanced performance**:

- **Read-ahead per stream (MiB)** targets a predictable amount of decoded data rather than a fixed article count. davex adapts the number of buffered articles to each NZB's article size.
- **Playback-reserved connections** protect viewing capacity while preparation and health checks are active.
- **Ready connections** reduce cold-start authentication cost without opening every configured connection permanently.
- **Health pipeline depth and lanes** control `STAT` concurrency independently of playback.
- **Playback pipelining** can remove per-article round trips, but should be validated against your providers before relying on it.
- **Segment cache** can accelerate seeks and repeat reads when placed on fast local storage.

More detail is available in [NNTP pipelining](docs/nntp-pipelining.md) and the [settings audit](docs/settings-audit.md).

## Build from source

```bash
git clone https://github.com/needforseed1/nzbdavex.git
cd nzbdavex
docker build -t nzbdavex:local .
docker run --rm -it \
  -p 3000:3000 \
  -v "$(pwd)/config:/config" \
  -e PUID="$(id -u)" \
  -e PGID="$(id -g)" \
  nzbdavex:local
```

Backend development requires .NET 10; the frontend uses Node.js and React Router. See [CONTRIBUTING.md](CONTRIBUTING.md) for the split frontend/backend workflow.

## Upgrading

Back up `/config`, pull the desired image, and recreate the application container:

```bash
docker compose pull davex
docker compose up -d --force-recreate davex
```

Read the [changelog](CHANGELOG.md) before upgrading across release lines. Database migrations run automatically when the container starts.

## Security

- Put public deployments behind HTTPS and an authenticating reverse proxy.
- Do not publish WebDAV, Search Profile, SAB, or administrative tokens.
- Give the container only the mounts it needs; `/config` contains secrets.
- Use provider and indexer credentials dedicated to davex where possible.

## Project status

This fork evolves quickly and changes the central NNTP, verification, playback, and observability paths. Stable releases are validated with the backend test suite, frontend tests and type checking, and production frontend/container builds, but real Usenet servers differ. Review release notes, retain backups, and pin versions when operating a critical library.

Issues and release history are available in the [GitHub repository](https://github.com/needforseed1/nzbdavex).

## License

See [LICENSE](LICENSE).

## Disclaimer

davex is a general-purpose WebDAV server and file-mounting utility. It is provided **as-is**, without warranty of any kind. It does not host, distribute, or index content; it connects only to services configured by the operator.

You are responsible for complying with applicable laws and the terms of every provider, indexer, and third-party service you use. The authors and contributors accept no liability for how this software is used.

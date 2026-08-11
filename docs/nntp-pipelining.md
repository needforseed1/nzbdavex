# NNTP Pipelining

Pipelining sends multiple NNTP commands per connection without waiting for each
response, then reads the responses in order. It removes the per-article
round-trip stall that otherwise dominates STAT checks and can improve
per-segment streaming on high-latency providers.

Health STAT pipelining is enabled by default. Playback pipelining is optional.

## What it speeds up

| Path | Before | With pipelining |
|------|--------|-----------------|
| Health check (100→200%) | one `STAT` per article | `STAT`s pipelined, falling back to per-segment failover on a miss |
| Streaming playback | article concurrency derived from the per-stream MiB read-ahead target | one connection streaming consecutive segments with no round-trip gaps |

Queue first-segment preparation deliberately uses bounded parallel `ARTICLE`
requests. The former single-connection queue pipeline serialized large imports
and was removed.

## Enabling it

Settings → Usenet → **NNTP Pipelining (Experimental)**:

- **Enable health-check STAT pipelining** — toggles
  `usenet.pipelining.health.enabled`.
- **Health-check pipeline depth/lanes** — control requests per connection and
  parallel STAT connections.
- **Enable playback pipelining** — toggles
  `usenet.pipelining.playback.enabled`.
- **Default pipeline depth** — `usenet.pipelining.depth`, used by playback when
  no provider override is configured.

(Config keys can also be set directly via the SAB-compatible config API.)

## How it's built

The pipelining engine lives in **UsenetSharp** (`UsenetClient.PipelinedAsync.cs`):
a windowed FIFO pipeline (`StatPipelinedAsync` / `BodyPipelinedAsync` /
`ArticlePipelinedAsync`) that writes up to *depth* commands ahead and reads
responses strictly in order. The existing single-command methods are unchanged.

nzbdav consumes it through the existing client chain. Each layer is handled:
- `BaseNntpClient` — real pipelining + yEnc decode
- `MultiConnectionNntpClient` — leases one connection per batch; **destroys it if
  the batch is abandoned early** so a half-read socket never returns to the pool
- `DownloadingNntpClient` — priority permit · `MultiProviderNntpClient` — provider
  selection + byte counting · `WrappingNntpClient` — delegation
- The abstract base provides a **non-pipelined fallback** for every batch method,
  so any path that isn't pipelined still works correctly.

## Build / release workflow

The UsenetSharp protocol implementation used by nzbdavex is maintained in
`backend/UsenetSharp` and compiled directly into `NzbWebDAV.csproj`. Local,
Docker, and CI builds therefore use the same source and do not depend on a
separately published UsenetSharp package or sibling checkout.

Changes to pipelining and body handling ship with the normal nzbdavex release.

## Testing

The backend test suite covers in-order delivery, mixed found/missing handling,
partial and stalled batches, fallback, body completeness, and connection reuse.
Real-provider validation remains useful because server implementations differ in
their response timing and handling of deep command pipelines.

Because pipelining touches the core I/O path, validate with the switch **on**
against your providers before relying on it.

## Characteristics / limitations

- **Streaming uses one connection per stream** (pipelined, gap-free). This frees
  the connection pool dramatically versus one connection per buffered segment and
  is sufficient for typical bitrates.
- Multi-provider pipelining preserves batches across failover. Missing or
  unreturned segments are retried on the next eligible provider without reducing
  the entire batch to sequential requests.
- The segment/article cache is bypassed on the pipelined path.

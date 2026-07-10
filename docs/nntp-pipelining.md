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
| Streaming playback | up to `article-buffer-size` connections, one segment each | one connection streaming consecutive segments with no round-trip gaps |

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

The pipelining engine is a change to the **UsenetSharp** library
(`github.com/nzbdav-dev/UsenetSharp`). `NzbWebDAV.csproj` references it
conditionally:

- **Local dev** — if a sibling checkout exists at `../../UsenetSharp` (overridable
  via the `UsenetSharpProject` MSBuild property), it's used as a `ProjectReference`
  so you can build both together.
- **Docker / CI** — when no sibling exists, it falls back to
  `PackageReference UsenetSharp 1.0.7`.

So to release:
1. Merge the UsenetSharp pipelining branch and **publish UsenetSharp 1.0.7** to
   NuGet.
2. The conditional reference then resolves to the package automatically — the
   Dockerfile needs no changes.

## Testing

UsenetSharp ships integration tests that run against a real provider. Fill in
`UsenetSharpTest/Credentials.cs` and valid segment ids, then run the
`UsenetClientPipelinedAsyncTests` suite — it verifies in-order delivery,
byte-for-byte equality with sequential fetches, mixed found/missing handling, and
that the connection stays reusable after a batch.

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

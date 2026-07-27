# Playback stalls: mid-body progress handling

Investigation of user reports of playback buffering (Eweka primary, no backup
provider attempted). Written 2026-07-27.

## Symptom

```
playback-session ... stage=stall kind=upstream-read
  file="Psych.S04E08...mkv" offset=5928602436 waitMs=8112
  bufferedSegments=50 inFlightSegments=0
```

Repeated 8–19 second `upstream-read` stalls at ~15–20 second intervals during
sequential playback. Session recovers on its own each time; no provider
failover, no connection-permit or provider-pool wait warnings.

`bufferedSegments=50` with `inFlightSegments=0` is the signature of head-of-line
blocking: the buffer channel is full, so the producer is parked in
`WaitToWriteAsync` (`backend/Streams/MultiSegmentStream.cs`), while the consumer
waits on one segment that is already past its `222 body follows` response.

Note the snapshot is taken when the warning is logged — after the slow read
completed — so these counters describe the recovery moment, not the stall
moment. They do not by themselves prove which download path was in use.

## Findings

### 1. No whole-body deadline after `222 body follows`

`MultiProviderNntpClient.PreserveCallerCancellationForStreamingResult`
(`backend/Clients/Usenet/MultiProviderNntpClient.cs:998`) disarms the provider
attempt deadline once a body response is returned:

```csharp
case UsenetDecodedBodyResponse body when body.Stream is not null:
    attemptCts.CancelAfter(Timeout.InfiniteTimeSpan);
```

This is deliberate — a slow lane must not abort a body that already started —
but it leaves the byte transfer with no budget of its own.

Lower layers are not timeout-free: `CreateCtsWithTimeout`
(`backend/UsenetSharp/Clients/UsenetClient.Helpers.cs:60`) applies a 10 second
timeout to every `ReadLineAsync` and to the periodic `writer.FlushAsync` in
`ReadBodyToPipeAsync`. What is missing is a whole-body deadline and a minimum
throughput floor, so a socket that keeps trickling one line at a time can run
arbitrarily long without ever tripping the per-line timeout.

Observed `waitMs` values fit both mechanisms: 8112 / 9403 ms sit just under the
10 s per-line timeout (trickle), while 13575 / 18647 ms are consistent with a
per-line timeout plus retry backoff and refetch.

### 2. Background body failures are swallowed (the real defect)

`UsenetClient.ReadBodyToPipeAsync`
(`backend/UsenetSharp/Clients/UsenetClient.BodyAsync.cs:95`) records failures on
the connection but completes the pipe cleanly:

```csharp
catch (Exception e)
{
    lock (this) { _backgroundException = ExceptionDispatchInfo.Capture(e); }
}
finally
{
    onFinally.Invoke(completed ? ArticleBodyResult.Retrieved : ArticleBodyResult.NotRetrieved);
}
```

and the callback runs `pipe.Writer.Complete()` with no exception argument. The
reader therefore sees an ordinary EOF. Same shape in
`UsenetClient.ArticleAsync.cs`.

Three ways to reach a short body that looks successful:

- per-line timeout (`TimeoutException`) mid-body,
- server closes the socket mid-body (`ReadLineAsync` returns `null`),
- cancellation of the operation token mid-body.

Consequences:

- `MultiSegmentStream.MaxBodyRetries` never engages, because nothing throws.
- Nothing downstream validates length. `YencStream` performs no CRC32 check and
  no part-size check, and `DrainSegmentAsync` returns whatever arrived without
  comparing against `ExpectedSegmentSize`. `ZeroFillSegment` only pads on
  *thrown* failures.

So a truncated segment silently shortens the stream and every subsequent byte
of the file shifts. That is container corruption, not a dropped frame — a more
plausible driver of user-visible stutter and player recovery loops than raw
slowness alone.

Socket hygiene is already correct and needs no work:
`MultiConnectionNntpClient.HandleConnectionReadyAgain`
(`backend/Clients/Usenet/MultiConnectionNntpClient.cs:422`) replaces the
connection on a background `NotRetrieved`. Only the data path lies.

### 3. Pipelined playback is a separate path

`UsenetClient.RunPipelinedAsync` reads each body fully into memory via
`ReadBodyToBufferAsync` (`backend/UsenetSharp/Clients/UsenetClient.PipelinedAsync.cs:280`)
before yielding the result. A watchdog attached to a transferred stream cannot
protect that path. It does propagate timeouts (they throw inside the
enumeration), so it does not truncate silently — it stalls the whole lane
instead.

Playback pipelining defaults to off (`usenet.pipelining.playback.enabled`,
`backend/Config/ConfigManager.cs:288`); the deployed setting still needs
confirming for these reports.

### 4. Not the cause here: connection reservation

`usenet.max-download-connections` ("playback connections") is a ceiling
enforced by a `PrioritizedSemaphore` in `DownloadingNntpClient`, not a reserve —
background prep/health work shares the provider pools and playback only gets
priority odds (`usenet.streaming-priority`, default 80). That is a genuine
product gap, but starvation emits `stage=connection-permit-wait` and
`stage=provider-pool-wait` warnings, and none appear in the reported logs.
Prewarming likewise creates idle authenticated sockets; it does not consume
sustained bandwidth.

`backups="none"` in the session summary means no `BackupOnly` provider was
*attempted* (`PlaybackDiagnosticContext.BackupSummary`), not that none is
configured.

## Plan

1. **Propagate background BODY/ARTICLE failures through the pipe.** Complete the
   pipe writer with the captured exception, and synthesize one when the body
   ends without its terminating `.` line. Restores `MaxBodyRetries`.
2. **Validate yEnc completeness in `YencStream`.** Throw when the stream ends
   without `=yend`, or when decoded bytes do not match the declared part size.
   Covers every consumer (streaming drain, pipelined materialize, seek probe)
   in one place, so no per-call-site size assert is needed.
3. **Body-progress watchdog for playback.** Arm an inactivity deadline on the
   transferred body stream; on expiry replace the socket and let the existing
   retry loop refetch the segment.
4. **Cover pipelined playback separately**, since its body is buffered before
   the result is yielded.
5. **Optional: hedge a delayed segment** on a second connection/provider and
   take the first successful result.
6. **Hard playback reserve**, tracked independently of this incident.

Steps 1–3 are implemented. Steps 4–6 are not started.

## Diagnostics fixes (2026-07-27, after first live session)

Two flaws surfaced while diagnosing a real 4G session with these logs.

**Stall counters were sampled at log time.** The producer refills the buffer
while the consumer is stuck, so `bufferedSegments`/`inFlightSegments` described
the recovery rather than the stall — the exact trap that produced an
over-confident reading of the original Psych logs. `PlaybackTransferPump` now
captures the depth before each blocking step and passes it to `RecordTransfer`,
which hands it to the stall log.

**`downstream-write` stalls logged at Debug while `upstream-read` logged at
Warning.** The downstream line is the evidence that clears the server: a client
that stops reading while segments sit buffered is backpressure, not a provider
fault. Hiding it below the upstream warning left an operator at default level
seeing only "upstream stalled" for what was entirely a slow client. Both kinds
now warn. The completion summary still treats downstream stalls alone as
non-actionable (`CompletionLogLevel`), which is the right split: a warning per
stall aids diagnosis, but a request that only ever waited on its client is not
a problem to report.

Still open from that review: stall lines carry no recent throughput, so
"client or server" needs arithmetic across two lines; and long-lived requests
emit nothing between stalls, so silence is ambiguous.

## Playback page and live view (2026-07-27)

`TotalUpstreamStallMs`/`TotalDownstreamStallMs` now run from the diagnostics
through to the page (metrics migration `20260727000000_Add-Playback-Stall-Totals`),
because a count and a worst case cannot say how much of a play was spent
waiting. The card's single "Avg rate" became **To client** and **From usenet**:
a source rate far above the client rate means prefetch ran ahead and the player
set the pace, which is the question the logs made us compute by hand.

Stalls are now recorded into `PlaybackSessionStats` the moment they happen
(`RecordStall`) instead of being folded in when a request completes, and
`PlaybackRequestDelta` no longer carries them. Sequential playback is a single
long HTTP request, so the old path left a live view reading zero for the entire
time a viewer sat buffering.

The dashboard's `LiveReads` (websocket topic `ar`, 1 s tick) now carries a
per-tick byte rate and the live stall totals. It previously rendered
`buffering…` whenever a read's provider list was empty — which only means no
articles have been attributed yet, i.e. how every read begins. That false
signal is now `starting…`.

Deliberately not done: merging in-flight sessions into the playback page. That
would mean synthesising plays with no `ReadSession` row, de-duplicating when
the real row lands after the 15 s idle prune, and bending grouping logic that
assumes terminal rows. The page is the forensic view; "what is happening right
now" belongs on the dashboard, which already holds the socket.

## Notes from implementing step 3

The deadline belongs on the byte source, not on the decoded read. One
`LifetimeYencStream.ReadAsync` fills the caller's whole buffer — 80 KB under
`Stream.CopyToAsync` — which pulls many socket reads, so a deadline at that
level measures how long a buffer takes to fill rather than how long the
transfer has been silent. A healthy segment on a merely slow link would have
tripped it. `YencStream.ArmReadInactivityWatchdog` therefore applies the window
around each inner-stream read, resetting on every chunk that arrives:
slowness is tolerated, silence is not.

The watchdog is armed only when a playback diagnostics context is present.
Queue downloads keep the old behaviour, where finishing a slow body still beats
refetching it.

On expiry the reader throws `BodyProgressStalledException` and the attempt CTS
is cancelled, which ends the background body pump; that reports the body as not
retrieved, and the existing `HandleConnectionReadyAgain` path replaces the
socket. `MultiSegmentStream.MaxBodyRetries` then refetches the segment on a
fresh connection.

Timeout is a hardcoded 5 seconds (`DefaultBodyProgressInactivityTimeout`),
constructor-injectable for tests. No user-facing setting until the value is
validated against real traffic.

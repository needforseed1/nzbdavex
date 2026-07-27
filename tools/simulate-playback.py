#!/usr/bin/env python3
"""Drive a file the way a video player does, to produce real playback diagnostics.

A player is not `cat`. It reads at roughly the file's bitrate, stops when its
buffer is full, and jumps when someone scrubs. Reading flat-out instead measures
how fast the source *can* go, which is the one number that never explains a
stall — so this paces itself and seeks on purpose.

Reads through the rclone mount by default, so the whole chain (rclone → WebDAV →
nzbdavex → usenet) is exercised exactly as it is during playback.

    tools/simulate-playback.py FILE --mbps 40 --seconds 90
    tools/simulate-playback.py FILE --seek 0.15 --seek 0.6   # scrub twice

Watch what it produces:

    docker logs -f nzbdavex 2>&1 | grep -E 'stage=(stall|first-byte|request-end)'
"""

import argparse
import os
import sys
import time

CHUNK = 1 << 20  # 1 MiB, close to what a player asks for


def human(n):
    for unit in ("B", "KB", "MB", "GB"):
        if abs(n) < 1024 or unit == "GB":
            return f"{n:.1f} {unit}" if unit != "B" else f"{int(n)} B"
        n /= 1024


def read_span(fh, start, seconds, byte_rate, label):
    """Read from `start` for `seconds`, paced to `byte_rate`. Returns stats."""
    fh.seek(start)
    began = time.monotonic()
    first_byte = None
    served = 0
    # Waits longer than this are what a viewer would notice as a pause.
    worst_gap = 0.0
    deadline = began + seconds

    while time.monotonic() < deadline:
        chunk_began = time.monotonic()
        data = fh.read(CHUNK)
        gap = time.monotonic() - chunk_began
        if not data:
            break
        if first_byte is None:
            first_byte = gap
        worst_gap = max(worst_gap, gap)
        served += len(data)

        # Pace: a player that has enough buffered simply stops asking. Sleeping
        # here is what produces downstream backpressure instead of a flat-out
        # drain, and it is the difference between measuring playback and
        # measuring a speed test.
        target = began + served / byte_rate
        behind = target - time.monotonic()
        if behind > 0:
            time.sleep(behind)

    elapsed = time.monotonic() - began
    rate = served / elapsed if elapsed else 0
    kept_up = rate >= byte_rate * 0.98
    print(
        f"  {label:22} first byte {(first_byte or 0) * 1000:7.0f} ms | "
        f"read {human(served):>9} in {elapsed:5.1f}s | "
        f"{rate * 8 / 1e6:6.1f} Mbps | worst wait {worst_gap * 1000:6.0f} ms | "
        f"{'kept up' if kept_up else 'FELL BEHIND'}"
    )
    return {"served": served, "first_byte": first_byte or 0, "worst_gap": worst_gap,
            "kept_up": kept_up}


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("path", help="file to play, usually under the rclone mount")
    p.add_argument("--mbps", type=float, default=0,
                   help="playback bitrate to sustain; default derives it from "
                        "file size assuming a 2 h runtime")
    p.add_argument("--seconds", type=float, default=60,
                   help="how long to read at each position (default 60)")
    p.add_argument("--seek", type=float, action="append", default=[],
                   help="fraction of the file to jump to, repeatable "
                        "(e.g. --seek 0.15 --seek 0.6)")
    p.add_argument("--runtime-min", type=float, default=120,
                   help="assumed runtime when deriving bitrate (default 120)")
    args = p.parse_args()

    size = os.path.getsize(args.path)
    mbps = args.mbps or (size * 8 / (args.runtime_min * 60) / 1e6)
    byte_rate = mbps * 1e6 / 8

    print(f"file      {args.path}")
    print(f"size      {human(size)}")
    print(f"bitrate   {mbps:.1f} Mbps {'(given)' if args.mbps else '(derived)'} "
          f"— the rate playback must sustain")
    print(f"positions start{''.join(f' + {s:.0%}' for s in args.seek)}, "
          f"{args.seconds:.0f}s each\n")

    results = []
    # O_DIRECT is deliberately not used: the page cache is part of how playback
    # really behaves through the mount.
    with open(args.path, "rb") as fh:
        results.append(read_span(fh, 0, args.seconds, byte_rate, "start"))
        for fraction in args.seek:
            offset = int(size * fraction) & ~(CHUNK - 1)
            results.append(
                read_span(fh, offset, args.seconds, byte_rate, f"seek to {fraction:.0%}"))

    behind = [r for r in results if not r["kept_up"]]
    worst = max(r["worst_gap"] for r in results)
    print()
    if behind:
        print(f"VERDICT: {len(behind)} of {len(results)} positions could not sustain "
              f"{mbps:.1f} Mbps — this would buffer.")
    else:
        print(f"VERDICT: every position sustained {mbps:.1f} Mbps.")
    print(f"Worst single wait for data: {worst * 1000:.0f} ms")
    print("\nCause breakdown is in the playback page, or:")
    print("  docker logs nzbdavex --since 5m 2>&1 | grep 'stage=stall'")
    return 1 if behind else 0


if __name__ == "__main__":
    sys.exit(main())

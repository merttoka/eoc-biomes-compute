#!/usr/bin/env python3
"""OSC /index tester for eoc-biomes-compute neuron firing.

Sends the OSC frame index that NeuronFiringSource scrubs (OSCMapping listens on
/index, default port 9000). The firing blob is 131 neurons x 180000 frames, so
valid indices are 0..179999. Firing decays to quiet ~0.5s after the last message,
so use --stream for sustained firing and --sweep to scrub discrete frames.

Setup (venv per repo convention):
    python3 -m venv tools/.venv
    tools/.venv/bin/pip install -r tools/requirements.txt

Examples:
    # no args = default installation loop: full-range 60fps stream, looping, with a
    # /sim_resetSimsOnly at the start of each pass, then 5x /sim_resetTermites +
    # 10x /sim_resetPhysarum spread evenly through the pass
    tools/.venv/bin/python tools/osc_index_tester.py

    # one frame
    tools/.venv/bin/python tools/osc_index_tester.py 90000

    # scrub 20 frames across the full range, holding each 1s
    tools/.venv/bin/python tools/osc_index_tester.py --sweep --steps 20 --hold 1.0

    # sustained firing: stream a range at 60fps (rings stay lit)
    tools/.venv/bin/python tools/osc_index_tester.py --stream 60000 66000 --fps 60

    # stream + loop forever (Ctrl+C to stop)
    tools/.venv/bin/python tools/osc_index_tester.py --stream 0 180000 --fps 30 --loop

    # random frames
    tools/.venv/bin/python tools/osc_index_tester.py --random --count 10 --hold 0.5

    # different host/port/address
    tools/.venv/bin/python tools/osc_index_tester.py 1234 --host 10.0.0.5 --port 9001 --addr /index
"""
import argparse
import random
import sys
import time

from pythonosc.udp_client import SimpleUDPClient

MAX_FRAME = 179999  # 180000 frames, 0-based


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


def main():
    p = argparse.ArgumentParser(
        description="Send OSC /index frames to drive neuron firing.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    p.add_argument("index", nargs="?", type=int, help="single frame index to send (0..%d)" % MAX_FRAME)
    p.add_argument("--host", default="127.0.0.1", help="target host (default 127.0.0.1)")
    p.add_argument("--port", type=int, default=1234, help="target port (default 1234, = OSCMapping m_Port)")
    p.add_argument("--addr", default="/index", help="OSC address (default /index)")
    p.add_argument("--max", type=int, default=MAX_FRAME, dest="max_frame",
                   help="max frame index, for clamping (default %d)" % MAX_FRAME)

    p.add_argument("--sweep", nargs="*", type=int, metavar="START END",
                   help="scrub discrete frames START..END (default 0..max); use --steps and --hold")
    p.add_argument("--steps", type=int, default=10, help="number of frames in --sweep (default 10)")
    p.add_argument("--hold", type=float, default=1.0, help="seconds to hold each frame in --sweep/--random (default 1.0)")

    p.add_argument("--stream", nargs="*", type=int, metavar="START END",
                   help="stream every frame START..END at --fps (default 0..max); sustained firing")
    p.add_argument("--fps", type=float, default=None,
                   help="frames/sec for --stream (default 60; ignored by --sweep/--random, use --hold)")
    p.add_argument("--loop", action="store_true", help="loop --stream/--sweep forever (Ctrl+C to stop)")

    p.add_argument("--resets", type=int, default=0, metavar="N",
                   help="send N reset commands evenly spaced through --stream (interior points)")
    p.add_argument("--reset-addr", default=None, dest="reset_addr",
                   help="OSC address for --resets (default /sim_resetSimsOnly)")
    p.add_argument("--reset-start", default=None, dest="reset_start", metavar="ADDR",
                   help="OSC address sent once at the start of each --stream pass (e.g. /sim_resetSimsOnly)")

    p.add_argument("--random", action="store_true", help="send random frames")
    p.add_argument("--count", type=int, default=10, help="number of frames for --random (default 10)")

    args = p.parse_args()

    # Reset schedules: list of (osc_addr, count). Each count is spread evenly through the
    # --stream span (interior points). Multiple schedules run concurrently at their own cadence.
    reset_specs = []

    # Default composite mode: no positional index and no mode flag -> the canonical
    # installation loop. Full-range 30fps stream, /sim_resetSimsOnly at the start of each
    # pass, then 5x /sim_resetTermites and 10x /sim_resetPhysarum spaced through it, looping.
    no_mode = (args.index is None and args.stream is None
               and args.sweep is None and not args.random)
    if no_mode:
        args.stream = [0, 180000]
        args.loop = True
        if args.fps is None:
            args.fps = 60.0
        if args.reset_start is None:
            args.reset_start = "/sim_resetSimsOnly"
        reset_specs = [("/sim_resetTermites", 5), ("/sim_resetPhysarum", 10)]
        print("default mode: full-range 30fps loop + /sim_resetSimsOnly@start + 5x resetTermites + 10x resetPhysarum")

    # Explicit single schedule via --resets/--reset-addr (when a mode is chosen manually).
    if args.resets > 0:
        reset_specs.append((args.reset_addr or "/sim_resetSimsOnly", args.resets))

    if args.fps is not None and args.stream is None:
        print("WARNING: --fps only applies to --stream; --sweep/--random pace with --hold", file=sys.stderr)
    client = SimpleUDPClient(args.host, args.port)
    hi = args.max_frame

    def send(frame):
        frame = clamp(int(frame), 0, hi)
        client.send_message(args.addr, frame)
        print("  %s %d" % (args.addr, frame))
        return frame

    print("OSC -> %s:%d  addr=%s  range=0..%d" % (args.host, args.port, args.addr, hi))

    try:
        if args.stream is not None:
            start = args.stream[0] if len(args.stream) >= 1 else 0
            end = args.stream[1] if len(args.stream) >= 2 else hi
            fps = args.fps if args.fps is not None else 60.0
            dt = 1.0 / fps if fps > 0 else 0.0
            step = 1 if end >= start else -1
            # Evenly spaced interior reset points per schedule (N resets split the span into
            # N+1 parts). Schedules merge into one frame -> [addr, ...] map; if two land on
            # the same frame, both fire.
            span = end - start
            frame_resets = {}
            for addr, count in reset_specs:
                if count <= 0:
                    continue
                for i in range(1, count + 1):
                    fr = round(start + span * i / (count + 1))
                    frame_resets.setdefault(fr, []).append(addr)

            def send_reset(addr):
                client.send_message(addr, 1)
                print("  %s (reset)" % addr)

            print("stream %d..%d @ %.0ffps%s" % (start, end, fps, "  (loop)" if args.loop else ""))
            if args.reset_start:
                print("reset-start @ frame %d: %s" % (start, args.reset_start))
            for addr, count in reset_specs:
                print("resets x%d -> %s" % (count, addr))
            # Absolute-deadline pacing: sleep until next_t, not sleep(dt) after each
            # send — otherwise send/print overhead accumulates and the actual rate
            # undershoots the requested fps (~18% low at 30fps).
            next_t = time.monotonic()
            rate_t0, rate_n = next_t, 0
            while True:
                if args.reset_start:
                    send_reset(args.reset_start)
                for f in range(start, end + step, step):
                    send(f)
                    if f in frame_resets:
                        for addr in frame_resets[f]:
                            send_reset(addr)
                    rate_n += 1
                    now = time.monotonic()
                    if now - rate_t0 >= 2.0:
                        print("  [rate: %.1f msg/s]" % (rate_n / (now - rate_t0)))
                        rate_t0, rate_n = now, 0
                    if dt:
                        next_t += dt
                        delay = next_t - now
                        if delay > 0:
                            time.sleep(delay)
                        else:
                            next_t = now  # fell behind (fps > achievable); resync
                if not args.loop:
                    break

        elif args.sweep is not None:
            start = args.sweep[0] if len(args.sweep) >= 1 else 0
            end = args.sweep[1] if len(args.sweep) >= 2 else hi
            n = max(1, args.steps)
            frames = [round(start + (end - start) * i / max(1, n - 1)) for i in range(n)]
            print("sweep %d..%d in %d steps, hold %.2fs%s" % (start, end, n, args.hold, "  (loop)" if args.loop else ""))
            while True:
                for f in frames:
                    send(f)
                    time.sleep(args.hold)
                if not args.loop:
                    break

        elif args.random:
            print("random x%d, hold %.2fs" % (args.count, args.hold))
            for _ in range(args.count):
                send(random.randint(0, hi))
                time.sleep(args.hold)

        elif args.index is not None:
            send(args.index)

        else:
            p.print_help()
            return 1
    except KeyboardInterrupt:
        print("\nstopped")
    return 0


if __name__ == "__main__":
    sys.exit(main())

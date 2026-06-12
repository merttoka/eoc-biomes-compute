#!/usr/bin/env python3
"""Example OSC senders for BiomeInjector Dispersal sources (EoC biomes 11.0).

Run from a venv (see tools/README.md):
    tools/.venv/bin/python tools/osc_dispersal_example.py            # animated demo
    tools/.venv/bin/python tools/osc_dispersal_example.py --once     # single audio hit

In Unity: select the BiomeInjector, click "Add Example Dispersal Sources" (creates
arm1/arm2/arm3 + audio targeting the Dispersal channel), then enter Play. OSCMapping
listens on port 9000 and registers, per source name:

    /inject/<name>          <value 0..1>                       intensity at the source's uv
    /inject/<name>/pos      <u> <v>                            move the emitter (0..1 biome UV)
    /inject/<name>/shape    <radius> <falloff>                 resize only
    /inject/<name>/stamp    <u> <v> <radius> <falloff> <val>   full hit (audio places sized hits)

u,v are normalized biome coordinates: (0,0)=one corner, (0.5,0.5)=center, (1,1)=opposite.
radius ~0.02..0.5, falloff ~0.25..6 (higher = sharper edge). Adapt the arm/audio logic
below to your real kinetic-arm pose stream and audio onsets.
"""
import argparse
import math
import time

from pythonosc.udp_client import SimpleUDPClient

ARMS = ["arm1", "arm2", "arm3"]


def send_audio_hit(client, u=0.5, v=0.5, radius=0.18, falloff=1.0, value=1.0):
    """One sized Dispersal hit from the audio channel (e.g. on a beat/onset)."""
    client.send_message(
        "/inject/audio/stamp",
        [float(u), float(v), float(radius), float(falloff), float(value)],
    )


def run_demo(client, label="", hz=30.0):
    """Three kinetic arms sweep + pulse; audio throws a hit every ~2 s."""
    dt = 1.0 / hz
    t = 0.0
    print(f"[demo] sending to {label} at {hz:.0f} Hz (Ctrl-C to stop)")
    next_audio = 2.0
    while True:
        t += dt
        for i, arm in enumerate(ARMS):
            phase = i * (2.0 * math.pi / len(ARMS))
            # Arm pose → normalized biome UV. Replace with your real arm mapping.
            u = 0.5 + 0.35 * math.sin(t * 0.6 + phase)
            v = 0.5 + 0.15 * math.sin(t * 0.9 + phase)
            client.send_message(f"/inject/{arm}/pos", [float(u), float(v)])
            # Arm "activity" 0..1 → dispersal intensity (cubed for punchy strikes).
            intensity = max(0.0, math.sin(t * 1.5 + phase)) ** 3
            client.send_message(f"/inject/{arm}", [float(intensity)])

        if t >= next_audio:
            send_audio_hit(client, u=0.5 + 0.25 * math.sin(t), v=0.5, radius=0.18, value=1.0)
            next_audio += 2.0

        time.sleep(dt)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ip", default="127.0.0.1", help="Unity host (default 127.0.0.1)")
    ap.add_argument("--port", type=int, default=9000, help="OSC port (default 9000)")
    ap.add_argument("--once", action="store_true", help="send a single audio dispersal hit and exit")
    ap.add_argument("--hz", type=float, default=30.0, help="demo send rate")
    args = ap.parse_args()

    client = SimpleUDPClient(args.ip, args.port)
    if args.once:
        send_audio_hit(client)
        print("[once] sent /inject/audio/stamp 0.5 0.5 0.18 1.0 1.0")
        return
    try:
        run_demo(client, label=f"{args.ip}:{args.port}", hz=args.hz)
    except KeyboardInterrupt:
        print("\n[demo] stopped")


if __name__ == "__main__":
    main()

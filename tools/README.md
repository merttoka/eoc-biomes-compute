# tools

Offline helpers for `eoc-biomes-compute`. Python, run from a local venv.

## Setup

```bash
python3 -m venv tools/.venv
tools/.venv/bin/pip install -r tools/requirements.txt
```

`tools/.venv/` is gitignored. `requirements.txt` covers both scripts (`python-osc`, `numpy`).

---

## `osc_index_tester.py` — drive neuron firing over OSC

Sends the OSC frame index that `NeuronFiringSource` scrubs. `OSCMapping` listens on
`/index` (default port `9000`). The firing blob is **131 neurons × 180000 frames**, so
valid indices are `0..179999`. Firing **decays to quiet ~0.5 s** after the last message —
so `--stream` for sustained firing, `--sweep` to scrub discrete frames.

```bash
# one frame
tools/.venv/bin/python tools/osc_index_tester.py 90000

# scrub 20 frames across the full range, holding each 1 s
tools/.venv/bin/python tools/osc_index_tester.py --sweep --steps 20 --hold 1.0

# sustained firing: stream a range at 60fps (rings stay lit)
tools/.venv/bin/python tools/osc_index_tester.py --stream 60000 66000 --fps 60

# stream + loop forever (Ctrl+C to stop)
tools/.venv/bin/python tools/osc_index_tester.py --stream 0 180000 --fps 30 --loop

# stream full range with 5 resetSimsOnly commands evenly spaced through it
tools/.venv/bin/python tools/osc_index_tester.py --stream 0 179999 --resets 5

# random frames
tools/.venv/bin/python tools/osc_index_tester.py --random --count 10 --hold 0.5

# different host / port / address / max
tools/.venv/bin/python tools/osc_index_tester.py 1234 --host 10.0.0.5 --port 9001 --addr /index
```

| Mode | What it does | Use for |
|------|--------------|---------|
| `index` (positional) | send one frame | quick check |
| `--sweep [START END] --steps N --hold S` | N discrete frames, hold each S s | inspecting *which* neurons a frame fires (each blips then decays) |
| `--stream [START END] --fps F [--loop]` | every frame at F fps | sustained firing (intensity stays ~1) — tuning the ring overlay / visual balance |
| `--stream … --resets N` | fire `/sim_resetSimsOnly` at N evenly-spaced interior frames during the stream | scripted resets mid-playback (e.g. clear sims partway through a run) |
| `--random --count N --hold S` | N random frames | stress / variety |

`--resets N` only applies to `--stream`. It splits the streamed span into `N+1` parts and
sends a reset at each interior boundary — so `--stream 0 179999 --resets 5` resets at frames
`30000, 60000, 90000, 119999, 149999`. Override the address with `--reset-addr` (default
`/sim_resetSimsOnly` → `SimulationManager.ResetSimsOnly()`; the argument is ignored, any value
triggers it). Reset commands are marshalled to Unity's main thread, so they take effect on the
next frame.

Defaults: `--host 127.0.0.1`, `--port 1234` (= `OSCMapping.m_Port`), `--addr /index`,
`--reset-addr /sim_resetSimsOnly`, `--max 179999` (indices are clamped). `--sweep`/`--stream`
ranges default to `0..max`.

---

## `osc_dispersal_example.py` — drive Dispersal injection over OSC

Example senders for the `BiomeInjector` Dispersal channel. In Unity, click **"Add Example
Dispersal Sources"** on the BiomeInjector (creates `arm1`/`arm2`/`arm3` + `audio`), then Play.

```bash
# animated demo: 3 kinetic arms sweep + pulse, audio throws a hit every ~2s
tools/.venv/bin/python tools/osc_dispersal_example.py

# single sized audio hit and exit
tools/.venv/bin/python tools/osc_dispersal_example.py --once --ip 10.0.0.5 --port 9000
```

Per-source OSC addresses (`<name>` = arm1/arm2/arm3/audio):
`/inject/<name> <0..1>` (intensity) · `/inject/<name>/pos <u> <v>` (move) ·
`/inject/<name>/shape <radius> <falloff>` (resize) ·
`/inject/<name>/stamp <u> <v> <radius> <falloff> <value>` (full hit). `u,v` are normalized
biome coords (0..1). Adapt the arm-pose / audio-onset logic to your real installation data.

---

## `firing_csv_to_f16.py` — preprocess the firing blob

One-time offline conversion of the source neuron recording into the compact binary blob
`NeuronFiringSource` loads at runtime. Streams line-by-line, so it never loads the ~729 MB
source CSV into memory.

```bash
tools/.venv/bin/python tools/firing_csv_to_f16.py <src.csv> <dst.f16>

# canonical: source CSV -> the blob the runtime reads
tools/.venv/bin/python tools/firing_csv_to_f16.py \
    normalized_neuron_data.csv \
    "Assets/StreamingAssets/biomes11/termite_firing.f16"
```

**Input** `normalized_neuron_data.csv` — `frames × (3·neurons)` columns: each neuron is an
`(x, y, z)` triple. Only the **z** column of each triple is kept (`z = col[n*3 + 2]`) as the
per-neuron firing value. Header / ragged / malformed rows are skipped automatically.

**Output** `termite_firing.f16` — little-endian:

```
magic   : 4 bytes  "TFR1"
uint32  : neuronCount   (= columns / 3, e.g. 131)
uint32  : frameCount
data    : frameCount × neuronCount  float16   (row-major: frame, then neuron)
```

`NeuronFiringSource.firingBlobFile` points at this file under `StreamingAssets/` (default
`biomes11/termite_firing.f16`), and the **neuron positions** come from the matching
`labels_positions.csv` (assigned on the sims + `NeuronFiringSource`) — row *k* of the CSV is
neuron *k* of the blob. The committed `.f16` is `131 × 180000`; re-run this only if the source
recording changes. The blob is large (~47 MB, tracked via Git LFS).

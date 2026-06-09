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
| `--random --count N --hold S` | N random frames | stress / variety |

Defaults: `--host 127.0.0.1`, `--port 9000` (= `OSCMapping.m_Port`), `--addr /index`,
`--max 179999` (indices are clamped). `--sweep`/`--stream` ranges default to `0..max`.

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

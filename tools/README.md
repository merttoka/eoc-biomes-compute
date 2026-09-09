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

**Run with no arguments** for the canonical installation loop: full-range `0..180000` stream at
60fps, looping forever, with `/sim_resetSimsOnly` at the start of each pass and `/sim_resetPhysarum`
fired 5× evenly through it.

```bash
# default: the installation loop (equivalent to the --stream line below)
tools/.venv/bin/python tools/osc_index_tester.py
#   == --stream 0 180000 --fps 60 --loop --resets 5 \
#      --reset-addr /sim_resetPhysarum --reset-start /sim_resetSimsOnly

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

# per-type resets: resetSimsOnly at the start of each pass, 5x resetPhysarum through it
tools/.venv/bin/python tools/osc_index_tester.py --stream 0 180000 --loop \
    --reset-start /sim_resetSimsOnly --resets 5 --reset-addr /sim_resetPhysarum

# random frames
tools/.venv/bin/python tools/osc_index_tester.py --random --count 10 --hold 0.5

# different host / port / address / max
tools/.venv/bin/python tools/osc_index_tester.py 1234 --host 10.0.0.5 --port 9001 --addr /index
```

### Armed mode — start the stream from Unity

`--wait [PORT]` (default 9101) starts the tester **idle**: it listens for OSC and only begins the
mode given by the other flags when Unity sends `/stream/start`. `/stream/stop` halts the run and
re-arms; `/stream/quit` exits; `--once` exits after the first run. `/stream/start START END FPS`
overrides the range/fps for that run (`OscStreamTrigger.sendRange`), no args = the CLI settings.

```bash
# arm with your usual settings, then press Play in Unity
tools/.venv/bin/python tools/osc_index_tester.py --wait --stream 125000 131000 --fps 50
```

Unity side: `OscStreamTrigger` (network/) sends the messages — tick `startOnPlayMode`, or assign it
to `SimTimeline.streamTrigger` so timeline Play/Stop drive it. **Realtime only:** under a capture
clock (FigureExporter `captureFramerate`, Unity Recorder) Unity runs slower than wall time and the
external stream drifts ahead of the recording — for recorded takes assign `NeuronFiringPlayback`
to `SimTimeline.firingPlayback` instead (same range, advances on Unity's clock, frame-locked).

| Mode | What it does | Use for |
|------|--------------|---------|
| *(no args)* | full-range 60fps loop + `resetSimsOnly` at each pass start + 5× `resetPhysarum` | the installation default |
| `index` (positional) | send one frame | quick check |
| `--sweep [START END] --steps N --hold S` | N discrete frames, hold each S s | inspecting *which* neurons a frame fires (each blips then decays) |
| `--stream [START END] --fps F [--loop]` | every frame at F fps | sustained firing (intensity stays ~1) — tuning the ring overlay / visual balance |
| `--stream … --resets N [--reset-addr A]` | fire reset `A` at N evenly-spaced interior frames during the stream | scripted resets mid-playback (e.g. clear one sim family partway through a run) |
| `--stream … --reset-start A` | fire reset `A` once at the start of each stream pass | reset state before each loop (e.g. `resetSimsOnly`) |
| `--wait [PORT] [--once]` | idle until `/stream/start` from Unity, then run the chosen mode; re-arms after | one-press starts with a live Unity take |
| `--random --count N --hold S` | N random frames | stress / variety |

`--resets N` / `--reset-start` only apply to `--stream`. `--resets` splits the streamed span
into `N+1` parts and sends `--reset-addr` at each interior boundary — so `--stream 0 179999
--resets 5` resets at frames `30000, 60000, 90000, 119999, 149999`. `--reset-start` fires its
address once at the top of every pass (before frame 0). The reset argument is ignored (any value
triggers it); commands are marshalled to Unity's main thread, so they take effect on the next
frame. Available reset addresses (see `OSCMapping`):

| Address | Effect |
| ------- | ------ |
| `/sim_reset` | full reset — sims + biome + external input |
| `/sim_resetSimsOnly` | respawn all sims, preserve biome |
| `/sim_resetPhysarum` · `/sim_resetBoids` · `/sim_resetTermites` | respawn only that sim family |

Defaults: `--host 127.0.0.1`, `--port 1234` (= `OSCMapping.m_Port`), `--addr /index`,
`--reset-addr /sim_resetSimsOnly` (interior resets) with no `--reset-start`, `--max 179999`
(indices are clamped). `--sweep`/`--stream` ranges default to `0..max`. With **no mode flag at
all**, the tool runs the installation default described at the top.

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

## `channel_wiring.py` — channel-wiring boards for `DAC_params` sets

Reads one `DAC_params/<NN_Name>/` folder (BiomeFieldConfig + the three UmweltMapping assets +
`Scene_DAC_*.unity`) and emits Paper-ready HTML for a wiring board: who writes into which biome
channel (seeder routes, injector firing dispersal and sources, species deposits, metabolic heat /
O2, termite mound building), each channel's config (diffuse / decay / relax / init / kernel /
advection), the PDE couplings from the config globals, and each species' reads (effect + weight,
habitat band). Rows are bright when a species or input touches them, mid when only the PDE does,
dim when nothing does. Death is not drawn — no kernel reads it. No third-party deps.

```bash
tools/.venv/bin/python tools/channel_wiring.py "Assets/Workspace/11.2 SIGGRAPH Scene/assets/DAC_params/01_Trace" --out /tmp/wiring/01_Trace
tools/.venv/bin/python tools/channel_wiring.py --all "Assets/Workspace/11.2 SIGGRAPH Scene/assets/DAC_params" --out /tmp/wiring
```

Per set: `preview.html` (standalone, open in a browser), plus `header.html`, `body_shell.html`,
`lane_*.html` (writers · gutter_writes · channels · gutter_pde · gutter_reads · readers) and
`legend.html`. To put a board in Paper: create an artboard (width printed by the script, dark
`#0F0F0F`, 72px padding, column flex, 56px gap), `write_html` the header, the body shell, the six
lanes into the shell in that order, then the legend. Retune the assets → rerun → replace the lanes.
Boards live in the Paper file *SIGGRAPH DAC Shanghai*, page *EoC Interaction Map*.

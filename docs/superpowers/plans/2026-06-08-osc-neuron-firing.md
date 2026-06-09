# OSC-Driven Shared Neuron Firing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the termite firing system's internal sequential playhead with an external OSC frame index, and promote neuron firing into a shared signal that excites all three sims (termite, physarum, boid) at the neuron each agent sits on.

**Architecture:** A singleton `NeuronFiringSource` (component on the manager, like `BiomeInjector`) owns the `.f16` blob, the OSC-set frame index, and a decay envelope; each step it decodes the current row × intensity into a shared 131-float `ComputeBuffer`. `SimulationManager` broadcasts that buffer to every sim. Neuron-position seeding and firing consumption are hoisted into `SimulationBase`, so all three sims read `firing[agent % neuronCount]` in-shader via a shared HLSL include and apply `firingSpeedMul` / `firingDepositAmount`.

**Tech Stack:** Unity 2022+, C# (HDRP), HLSL compute shaders, `jp.keijiro.osc-jack` (OscJack), `EasyButtons`. Namespace: `Biomes`.

**Reference spec:** `docs/superpowers/specs/2026-06-08-osc-neuron-firing-design.md`

---

## Testing approach (read first)

This is a Unity GPU-compute visual project with **no unit-test harness**. "Verification" each task = **(a)** Unity compiles with **zero errors** in the Console (Unity auto-recompiles on focus; watch the Console), and **(b)** a specific **play-mode observation**. Several tasks send OSC for verification — set that up once:

```bash
python3 -m venv /tmp/oscvenv && /tmp/oscvenv/bin/pip install python-osc
# send a frame index (match the port to OSCMapping.m_Port, default 9000):
/tmp/oscvenv/bin/python -c "from pythonosc.udp_client import SimpleUDPClient as C; C('127.0.0.1',9000).send_message('/index', 90000)"
```

> The blob has **131 neurons × 180000 frames**. Valid `/index` range is `0..179999`.

Commit after every task (frequent commits). Work on branch `feat/osc-neuron-firing` (already created).

---

## File structure

| File | Responsibility | Tasks |
|---|---|---|
| `src/components/network/NeuronFiringSource.cs` *(new)* | Own blob + OSC frame + decay → shared 131-float buffer | 1 |
| `src/computes/includes/neuron_firing.hlsl` *(new)* | Shared firing buffer decl + `NeuronFireValue`/`IsFiring` helpers | 2 |
| `src/components/core/SimulationBase.cs` | Firing-consume fields/helpers (T2) + hoisted neuron seeding (T6) | 2, 6 |
| `src/components/core/SimulationManager.cs` | Own + init + per-step `UpdateFiring` + broadcast | 3 |
| `src/components/network/OSCMapping.cs` | `/index <int>` → `SetFrame` | 4 |
| `src/components/Sim/TermiteSim.cs` + `computes/TermiteSim.compute` | Drop private blob/playback; read shared buffer (T5); drop local seeding (T6) | 5, 6 |
| `src/components/Sim/PhysarumSim.cs` + `computes/PhysarumSim.compute` + `params/PhysarumParams.cs` + `computes/includes/physarum_type_params.hlsl` | Drop local seeding (T6); add firing params + effect (T8) | 6, 8 |
| `src/components/Sim/BoidSim.cs` + `computes/BoidSim.compute` + `params/BoidParams.cs` + `computes/includes/boid_type_params.hlsl` | Add neuron seeding + firing params + effect | 7 |

---

## Task 1: `NeuronFiringSource` component (producer)

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/network/NeuronFiringSource.cs`

Self-contained: its own blob loader (a copy of termite's `LoadFiringBlob`; termite's copy is removed in Task 5 — the transient duplication keeps every build green). Not a `SimulationBase`, so it manages its own `ComputeBuffer` with explicit `Release()`.

- [ ] **Step 1: Create the file**

```csharp
using System;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Single owner of the neuron-firing blob. The OSC frame index (set via SetFrame,
    /// thread-safe) selects which row of the blob is shown; a decay envelope fades
    /// firing to quiet when no new index arrives. Produces a shared float buffer
    /// (one value per neuron, already scaled by the envelope) that SimulationManager
    /// broadcasts to every sim each step.
    /// </summary>
    public class NeuronFiringSource : MonoBehaviour
    {
        [Tooltip("Path under Assets/StreamingAssets, produced by tools/firing_csv_to_f16.py")]
        public string firingBlobFile = "biomes11/termite_firing.f16";

        [Tooltip("Seconds for firing intensity to fade to zero when no /index arrives")]
        public float firingDecaySeconds = 0.5f;

        [Tooltip("Log frame changes to the Console (main thread)")]
        public bool debugLog = false;

        // Blob (loaded once)
        private ushort[] _firingHalf;   // flat float16 bits: frame*_neuronCount + neuron
        private int _neuronCount;
        private int _frameCount;

        // OSC-driven (written on the receive thread)
        private volatile int _targetFrame;
        private volatile bool _dirty;

        // Runtime state (main thread)
        private int _currentFrame = -1;
        private float _intensity;
        private float _lastTime;
        private float[] _row;       // decoded current frame
        private float[] _scaled;    // _row * _intensity, uploaded each step
        private ComputeBuffer _buffer;

        public ComputeBuffer Buffer => _buffer;
        public int NeuronCount => _neuronCount;
        public int FrameCount => _frameCount;
        public int CurrentFrame => _currentFrame;
        public float Intensity => _intensity;

        /// <summary>Thread-safe: called from the OSC receive thread.</summary>
        public void SetFrame(int frame)
        {
            _targetFrame = frame;
            _dirty = true;
        }

        public void Initialize()
        {
            LoadBlob();
            int n = Mathf.Max(1, _neuronCount);
            _row = new float[n];
            _scaled = new float[n];
            ReleaseBuffer();
            _buffer = new ComputeBuffer(n, sizeof(float));
            _buffer.SetData(new float[n]);
            _currentFrame = -1;
            _intensity = 0f;
            _lastTime = Time.unscaledTime;
            _dirty = false;
        }

        /// <summary>Called once per sim step by SimulationManager (main thread).</summary>
        public void UpdateFiring()
        {
            if (_firingHalf == null || _neuronCount <= 0 || _buffer == null) return;

            if (_dirty)
            {
                _dirty = false;
                _currentFrame = Mathf.Clamp(_targetFrame, 0, _frameCount - 1);
                int baseIdx = _currentFrame * _neuronCount;
                for (int i = 0; i < _neuronCount; i++)
                    _row[i] = Mathf.HalfToFloat(_firingHalf[baseIdx + i]);
                _intensity = 1f;
                if (debugLog) Debug.Log($"NeuronFiringSource: frame={_currentFrame}");
            }

            // Decay by real wall-clock time (advances once per rendered frame even if
            // Step() runs multiple times per frame: Time.unscaledTime is constant within a frame).
            float now = Time.unscaledTime;
            float dt = Mathf.Max(0f, now - _lastTime);
            _lastTime = now;
            if (_intensity > 0f && firingDecaySeconds > 0f)
                _intensity = Mathf.Max(0f, _intensity - dt / firingDecaySeconds);

            for (int i = 0; i < _neuronCount; i++)
                _scaled[i] = _row[i] * _intensity;
            _buffer.SetData(_scaled);
        }

        private void LoadBlob()
        {
            _firingHalf = null; _frameCount = 0; _neuronCount = 0;
            if (string.IsNullOrEmpty(firingBlobFile)) return;

            string path = System.IO.Path.Combine(Application.streamingAssetsPath, firingBlobFile);
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"NeuronFiringSource: blob not found at {path} (run tools/firing_csv_to_f16.py)");
                return;
            }

            using var br = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
            var magic = br.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != (byte)'T' || magic[1] != (byte)'F'
                || magic[2] != (byte)'R' || magic[3] != (byte)'1')
            {
                Debug.LogWarning("NeuronFiringSource: blob has bad magic; ignoring");
                return;
            }
            _neuronCount = (int)br.ReadUInt32();
            _frameCount  = (int)br.ReadUInt32();
            long count = (long)_frameCount * _neuronCount;
            if (count <= 0 || count > int.MaxValue / 2)
            {
                Debug.LogWarning($"NeuronFiringSource: blob size out of range ({_frameCount}x{_neuronCount})");
                _frameCount = 0; _neuronCount = 0;
                return;
            }
            var bytes = br.ReadBytes((int)(count * 2));
            _firingHalf = new ushort[count];
            System.Buffer.BlockCopy(bytes, 0, _firingHalf, 0, bytes.Length);
        }

        private void ReleaseBuffer() { _buffer?.Release(); _buffer = null; }
        void OnDisable() => ReleaseBuffer();
        void OnDestroy() => ReleaseBuffer();

        [Button]
        public void TestLoadAndLog()
        {
            Initialize();
            Debug.Log($"NeuronFiringSource: neurons={_neuronCount} frames={_frameCount}");
        }
    }
}
```

- [ ] **Step 2: Verify it compiles & loads**

In Unity: wait for recompile, confirm **no Console errors**. Add the component to the `SimulationManager`'s GameObject (Inspector → Add Component → `Neuron Firing Source`), then click its **TestLoadAndLog** button.
Expected Console: `NeuronFiringSource: neurons=131 frames=180000`

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/NeuronFiringSource.cs"*
git commit -m "feat(neurons): NeuronFiringSource - blob owner, OSC frame + decay -> shared buffer"
```

---

## Task 2: Shared firing include + `SimulationBase` consumption

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/computes/includes/neuron_firing.hlsl`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs`

- [ ] **Step 1: Create the shared HLSL include**

`Assets/Workspace/11.0 Biomes/src/computes/includes/neuron_firing.hlsl`:

```hlsl
// Shared neuron-firing signal. Produced once per step by NeuronFiringSource
// (one float per neuron, already scaled by the global decay envelope) and bound
// per-sim by SimulationBase.BindNeuronFiring(). seedNeuronCount = the same neuron
// count used to seed agent positions, so a firing neuron excites the agents on it.
#ifndef NEURON_FIRING_INCLUDED
#define NEURON_FIRING_INCLUDED

StructuredBuffer<float> neuronFiring;   // length = neuronFiringCount
int   neuronFiringCount;                // 0 when no source wired => no firing
float firingThreshold;                  // per-sim, default 0.1

float NeuronFireValue(uint agentId, uint seedNeuronCount)
{
    if (neuronFiringCount <= 0) return 0.0;
    uint nIdx = (seedNeuronCount > 0) ? (agentId % seedNeuronCount) : agentId;
    return neuronFiring[nIdx % (uint)neuronFiringCount];
}

bool IsFiring(uint agentId, uint seedNeuronCount)
{
    return NeuronFireValue(agentId, seedNeuronCount) >= firingThreshold;
}

#endif
```

- [ ] **Step 2: Add firing statics to `SimulationBase.cs`**

In the statics block (after line 57, `s_PerceptionTexID`), add:

```csharp
        protected static readonly int s_NeuronFiringID = Shader.PropertyToID("neuronFiring");
        protected static readonly int s_NeuronFiringCountID = Shader.PropertyToID("neuronFiringCount");
        protected static readonly int s_FiringThresholdID = Shader.PropertyToID("firingThreshold");
```

- [ ] **Step 3: Add firing fields to `SimulationBase.cs`**

After the `externalInfluenceTex` field (line 27), add:

```csharp

        // Shared neuron firing (assigned by SimulationManager from NeuronFiringSource)
        [NonSerialized] public ComputeBuffer neuronFiring;
        [NonSerialized] public int neuronFiringCount;
        [Header("Neuron Firing")]
        [Range(0f, 1f)] public float firingThreshold = 0.1f;
        private ComputeBuffer dummyNeuronFiringBuffer;
```

- [ ] **Step 4: Add the `BindNeuronFiring` helper to `SimulationBase.cs`**

Right after the existing `BindPerceptionTex` method (ends line 194), add:

```csharp

        // Bind the shared neuron-firing buffer + count + threshold to the given kernels.
        // Falls back to a 1-element dummy (count 0 => no firing) when no source is wired.
        protected void BindNeuronFiring(params int[] kernels)
        {
            ComputeBuffer buf = neuronFiring;
            int count = neuronFiringCount;
            if (buf == null)
            {
                if (dummyNeuronFiringBuffer == null)
                {
                    dummyNeuronFiringBuffer = gpu.CreateBuffer(1, sizeof(float));
                    dummyNeuronFiringBuffer.SetData(new float[1] { 0f });
                }
                buf = dummyNeuronFiringBuffer;
                count = 0;
            }
            foreach (int k in kernels)
                cs.SetBuffer(k, s_NeuronFiringID, buf);
            cs.SetInt(s_NeuronFiringCountID, count);
            cs.SetFloat(s_FiringThresholdID, firingThreshold);
        }
```

> `dummyNeuronFiringBuffer` is allocated via `gpu`, so `gpu.ReleaseAll()` in `Release()` frees it; it lazily re-creates after the next `Reset()`.

- [ ] **Step 5: Verify it compiles**

Unity recompiles. Expected: **no Console errors**. Nothing reads the buffer yet, so behavior is unchanged.

- [ ] **Step 6: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/computes/includes/neuron_firing.hlsl"* "Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs"
git commit -m "feat(neurons): SimulationBase shared firing buffer + BindNeuronFiring + include"
```

---

## Task 3: `SimulationManager` owns + drives + broadcasts the source

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs`

- [ ] **Step 1: Add the serialized reference**

After the External Input field (line 28, `private ExternalTextureReceiver externalInput;`), add:

```csharp

        [Header("Neuron Firing")]
        [SerializeField] private NeuronFiringSource neuronFiring;
```

- [ ] **Step 2: Initialize it in `Reset()`**

In `Reset()`, right after the external-input init block:

```csharp
            // Initialize external input
            if (externalInput != null)
                externalInput.Initialize();
```

change to:

```csharp
            // Initialize external input
            if (externalInput != null)
                externalInput.Initialize();

            // Initialize neuron firing source
            if (neuronFiring != null)
                neuronFiring.Initialize();
```

- [ ] **Step 3: Drive + broadcast in `Step()`**

In `Step()`, the current influence-broadcast block reads:

```csharp
            // Assign influence texture to sims
            Texture influenceTex = externalInput != null ? externalInput.OutputTexture : null;
            foreach (var sim in simulations)
            {
                if (sim != null)
                    sim.externalInfluenceTex = influenceTex;
            }
```

Replace it with:

```csharp
            // Assign influence texture to sims
            Texture influenceTex = externalInput != null ? externalInput.OutputTexture : null;
            foreach (var sim in simulations)
            {
                if (sim != null)
                    sim.externalInfluenceTex = influenceTex;
            }

            // 0b. Update neuron firing source (OSC frame + decay) and broadcast its buffer
            neuronFiring?.UpdateFiring();
            ComputeBuffer firingBuf = neuronFiring != null ? neuronFiring.Buffer : null;
            int firingCount = neuronFiring != null ? neuronFiring.NeuronCount : 0;
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.neuronFiring = firingBuf;
                sim.neuronFiringCount = firingCount;
            }
```

- [ ] **Step 4: Wire it in the scene**

In the active scene, select the `SimulationManager` GameObject. Its `NeuronFiringSource` component (added in Task 1) should already be on it — drag that component into the manager's new **Neuron Firing** field. Leave `firingBlobFile` at its default.

- [ ] **Step 5: Verify it compiles & runs**

Enter Play mode. Expected: **no Console errors**; sims behave exactly as before (no sim reads firing yet; termite still uses its own private firing path until Task 5).

- [ ] **Step 6: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs"
git commit -m "feat(neurons): manager owns NeuronFiringSource, drives + broadcasts per step"
```

---

## Task 4: OSC `/index` handler

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/network/OSCMapping.cs`

- [ ] **Step 1: Add the serialized reference**

After the `m_BiomeInjector` field (line 14), add:

```csharp
        [SerializeField] public NeuronFiringSource m_NeuronFiringSource;
```

- [ ] **Step 2: Register `/index` in `Start()`**

In `Start()`, after `m_OscServer = new OscServer(m_Port);` and before/among the existing `AddCallback` registrations, add:

```csharp
            // Neuron firing: external frame index (0..frameCount-1) scrubs the blob.
            m_OscServer.MessageDispatcher.AddCallback(
                "/index",
                (string address, OscDataHandle data) => {
                    if (m_NeuronFiringSource == null) return;
                    int frame;
                    try { frame = data.GetElementAsInt(0); }
                    catch { frame = Mathf.RoundToInt(data.GetElementAsFloat(0)); }
                    m_NeuronFiringSource.SetFrame(frame);
                }
            );
```

> If `GetElementAsInt` is not present in this osc-jack version (compile error on that line), delete the `try/catch` and use `int frame = Mathf.RoundToInt(data.GetElementAsFloat(0));`. All existing handlers use `GetElementAsFloat`, so the float path is guaranteed to work.

- [ ] **Step 3: Wire it in the scene**

Select the GameObject holding `OSCMapping`. Drag the `NeuronFiringSource` component into its new **M Neuron Firing Source** field. Note the `M Port` value (default 9000).

- [ ] **Step 4: Verify end-to-end OSC path**

On the `NeuronFiringSource`, tick **debugLog**. Enter Play mode. Run (match the port):

```bash
/tmp/oscvenv/bin/python -c "from pythonosc.udp_client import SimpleUDPClient as C; C('127.0.0.1',9000).send_message('/index', 90000)"
```

Expected Console: `NeuronFiringSource: frame=90000`. Send a few different indices and confirm each logs. Untick **debugLog** after.

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/OSCMapping.cs"
git commit -m "feat(neurons): OSC /index <int> -> NeuronFiringSource.SetFrame"
```

---

## Task 5: Termite reads the shared buffer; remove private blob/playback

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/TermiteSim.compute`

This is the one behavior-touching refactor. Effect params and the `0.1` threshold are preserved; only the *source* of firing changes (now external `/index`, no auto-advance).

- [ ] **Step 1: Compute — include the shared helper**

In `TermiteSim.compute`, the firing declaration block (lines 32-34) reads:

```hlsl
// Optional firing state (per agent, 0/1). firingEnabled gates it.
StructuredBuffer<uint> firing;
uint firingEnabled;
```

Delete those three lines. Near the top of the file, after the existing `#include` lines, add:

```hlsl
#include "includes/neuron_firing.hlsl"
```

- [ ] **Step 2: Compute — swap the speed-boost firing test**

Line 162 reads:

```hlsl
    float fireMul = (firingEnabled != 0 && firing[id.x] != 0) ? p.firingSpeedMul : 1.0;
```

Replace with:

```hlsl
    float fireMul = IsFiring(id.x, neuronCount) ? p.firingSpeedMul : 1.0;
```

- [ ] **Step 3: Compute — swap the trail-deposit firing test**

Line 189 reads:

```hlsl
    bool isFiring = (firingEnabled != 0) && (firing[id.x] != 0);
```

Replace with:

```hlsl
    bool isFiring = IsFiring(id.x, neuronCount);
```

- [ ] **Step 4: C# — bind the shared buffer in `GPUStep()`**

In `TermiteSim.cs` `GPUStep()` (lines 217-239), the head reads:

```csharp
        protected override void GPUStep()
        {
            UploadTypeParams();
            BindPerceptionTex(moveAgentsKernel);
            UploadFiring();
```

Replace those three setup lines with:

```csharp
        protected override void GPUStep()
        {
            UploadTypeParams();
            BindPerceptionTex(moveAgentsKernel);
            BindNeuronFiring(moveAgentsKernel, writeTrailsKernel);
```

- [ ] **Step 5: C# — remove the private firing machinery**

Make these deletions in `TermiteSim.cs`:

1. The firing field block (lines 38-53) — delete from the `[Header("Firing ...")]` line through `dummyFiringBuffer`:

```csharp
        [Header("Firing (optional, float16 blob in StreamingAssets)")]
        public bool enableFiring = false;
        [Tooltip("Path under Assets/StreamingAssets, produced by tools/firing_csv_to_f16.py")]
        public string firingBlobFile = "biomes11/termite_firing.f16";
        [Range(0f, 1f)] public float firingThreshold = 0.1f;
        public bool loopFiring = true;
        private ushort[] _firingHalf;               // flat float16 bits: frame*_neuronZCount + neuron
        private int _frameCount;
        private int _neuronZCount;
        private int _currentFrame;
        private float[] _frameScratch;              // decoded current-frame z values
        private uint[] _firingScratch;              // agentsCount, uploaded per step
        private ComputeBuffer firingBuffer;
        private ComputeBuffer dummyFiringBuffer;
```

> Keep the neuron-position fields directly above (`labelsPositionsCsv`, `csvCoordinatesAreNormalized`, `spawnScale`, `neuronPositionsBuffer`, `dummyNeuronBuffer`) — they are hoisted in Task 6, not here. `firingThreshold` now lives in `SimulationBase`.

2. The two firing `PropertyToID` statics (lines 57-58):

```csharp
        private static readonly int s_FiringID = Shader.PropertyToID("firing");
        private static readonly int s_FiringEnabledID = Shader.PropertyToID("firingEnabled");
```

3. The `LoadFiringBlob();` call at **line 85**.

4. In `InitBuffers()`, the firing-allocation block (after the `dummyNeuronBuffer` block):

```csharp
            dummyFiringBuffer = gpu.CreateBuffer(1, sizeof(uint));
            dummyFiringBuffer.SetData(new uint[1] { 0u });

            bool firingActive = enableFiring && _firingHalf != null && _frameCount > 0;
            if (firingActive)
            {
                firingBuffer = gpu.CreateBuffer(agentsCount, sizeof(uint));
                _firingScratch = new uint[agentsCount];
            }
```

5. The entire `UploadFiring()` method (lines 187-215).

6. The entire `LoadFiringBlob()` method (lines 314-347).

- [ ] **Step 6: Verify — termite fires from OSC, decays to quiet**

Enter Play mode (the `NeuronFiringSource` is wired from Tasks 3-4). Termites should be visible but **not firing** (no `/index` sent yet → buffer is quiet). Then sweep frames:

```bash
for f in 1000 40000 90000 140000; do \
  /tmp/oscvenv/bin/python -c "from pythonosc.udp_client import SimpleUDPClient as C; C('127.0.0.1',9000).send_message('/index', $f)"; sleep 1; done
```

Expected: at each index a subset of termites moves ~2× faster and lays bright/white trails (same look as before, now externally timed). **Stop sending** → within ~0.5 s firing fades out (decay to quiet), no termites stuck firing. Confirm **no Console errors** and termite spawn/positions unchanged.

- [ ] **Step 7: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs" "Assets/Workspace/11.0 Biomes/src/computes/TermiteSim.compute"
git commit -m "feat(neurons): termite reads shared firing buffer, drop private blob/playback"
```

---

## Task 6: Hoist neuron-position seeding into `SimulationBase`; termite + physarum adopt

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/Sim/PhysarumSim.cs`

Removes the duplicated CSV-seeding code (currently copy-pasted in termite **and** physarum) and makes it a base capability boid will reuse in Task 7.

- [ ] **Step 1: Add seeding statics + fields + helper to `SimulationBase.cs`**

In the statics block (after the firing statics from Task 2), add:

```csharp
        protected static readonly int s_NeuronPositionsID = Shader.PropertyToID("neuronPositions");
        protected static readonly int s_NeuronCountID = Shader.PropertyToID("neuronCount");
        protected static readonly int s_NeuronScaleID = Shader.PropertyToID("neuronScale");
```

In the fields region (near the `firingThreshold` field), add:

```csharp

        [Header("Neuron Positions (optional CSV seeding)")]
        public TextAsset labelsPositionsCsv;
        public bool csvCoordinatesAreNormalized = false;
        [Tooltip("How much of the canvas agents fill (0-1). (1,1)=full canvas")]
        public Vector2 spawnScale = new Vector2(0.8f, 0.9f);
        protected ComputeBuffer neuronPositionsBuffer;
        protected ComputeBuffer dummyNeuronBuffer;
```

Add these methods (place after `BindNeuronFiring`). The body mirrors termite's existing seeding exactly:

```csharp

        // Parse labelsPositionsCsv, upload neuron positions, bind to the given reset
        // kernel, and set neuronCount/neuronScale globals. Returns the neuron count
        // (0 => random scatter). Mirrors the previous per-sim seeding.
        protected int BuildNeuronPositions(int resetKernel)
        {
            if (dummyNeuronBuffer == null)
            {
                dummyNeuronBuffer = gpu.CreateBuffer(1, sizeof(float) * 2);
                dummyNeuronBuffer.SetData(new Vector2[1] { Vector2.zero });
            }

            int neuronCount = 0;
            if (labelsPositionsCsv != null && !string.IsNullOrEmpty(labelsPositionsCsv.text))
            {
                var positions = ParseCsvFloat2(labelsPositionsCsv.text);
                if (csvCoordinatesAreNormalized || LooksNormalized01(positions))
                    for (int i = 0; i < positions.Count; i++)
                        positions[i] = new Vector2(positions[i].x * rezX, positions[i].y * rezY);

                neuronCount = positions.Count;
                if (neuronCount > 0)
                {
                    neuronPositionsBuffer = gpu.CreateBuffer(neuronCount, sizeof(float) * 2);
                    neuronPositionsBuffer.SetData(positions);
                    cs.SetBuffer(resetKernel, s_NeuronPositionsID, neuronPositionsBuffer);
                }
                else cs.SetBuffer(resetKernel, s_NeuronPositionsID, dummyNeuronBuffer);
            }
            else cs.SetBuffer(resetKernel, s_NeuronPositionsID, dummyNeuronBuffer);

            cs.SetInt(s_NeuronCountID, neuronCount);
            cs.SetVector(s_NeuronScaleID, new Vector4(spawnScale.x, spawnScale.y, 0, 0));
            return neuronCount;
        }

        protected static List<Vector2> ParseCsvFloat2(string csv)
        {
            var list = new List<Vector2>();
            var lines = csv.Split('\n');
            var inv = CultureInfo.InvariantCulture;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (float.TryParse(parts[1], NumberStyles.Float, inv, out float x) &&
                    float.TryParse(parts[2], NumberStyles.Float, inv, out float y))
                    list.Add(new Vector2(x, (1 - y)));
            }
            return list;
        }

        protected static bool LooksNormalized01(List<Vector2> points)
        {
            if (points == null || points.Count == 0) return false;
            float maxX = float.MinValue, maxY = float.MinValue;
            float minX = float.MaxValue, minY = float.MaxValue;
            int sampleCount = Mathf.Min(points.Count, 2048);
            for (int i = 0; i < sampleCount; i++)
            {
                var p = points[i];
                if (float.IsNaN(p.x) || float.IsNaN(p.y)) continue;
                maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
                minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y);
            }
            return (minX >= -0.01f && maxX <= 1.01f && minY >= -0.01f && maxY <= 1.01f);
        }
```

> Add `using System.Collections.Generic;` and `using System.Globalization;` to the top of `SimulationBase.cs` (for `List<Vector2>` / `CultureInfo` / `NumberStyles`). Then **delete** the now-identical `ParseCsvFloat2` and `LooksNormalized01` from **both** `TermiteSim.cs` **and** `PhysarumSim.cs` (they were copy-pasted in each).

- [ ] **Step 2: Termite — use the inherited seeding**

In `TermiteSim.cs`:
- Delete the now-duplicated fields `labelsPositionsCsv`, `csvCoordinatesAreNormalized`, `spawnScale`, `neuronPositionsBuffer`, `dummyNeuronBuffer`, and the statics `s_NeuronPositionsID`, `s_NeuronCountID`, `s_NeuronScaleID` (all now in the base).
- Delete the local `ParseCsvFloat2` / `LooksNormalized01` methods.
- In `GPUReset()` (starts line 110), find the seeding block (the code that parses the CSV, creates `neuronPositionsBuffer`, binds `neuronPositions`, sets `neuronCount`/`neuronScale`, before `Dispatch(resetAgentsKernel, ...)`). Replace that whole block with a single call:

```csharp
            BuildNeuronPositions(resetAgentsKernel);
```

> Keep the `dummyNeuronBuffer` creation out of termite's `InitBuffers` (the base creates it lazily). Delete termite's `dummyNeuronBuffer = gpu.CreateBuffer(...)` lines in `InitBuffers`.

- [ ] **Step 3: Physarum — use the inherited seeding**

In `PhysarumSim.cs`, do the same:
- Delete its `labelsPositionsCsv` / `csvCoordinatesAreNormalized` / `spawnScale` / neuron-buffer fields and `s_Neuron*` statics (now in base).
- Delete its local `ParseCsvFloat2` / `LooksNormalized01`.
- In its reset (the method that parses the CSV ~lines 94-125 and dispatches `resetAgentsKernel`), replace the seeding block with:

```csharp
            BuildNeuronPositions(resetAgentsKernel);
```

> Physarum's `.compute` already declares `neuronPositions`/`neuronCount`/`neuronScale` and seeds in its `ResetAgentsKernel` — no shader change here.

- [ ] **Step 4: Verify — seeding unchanged for termite & physarum**

Assign the same `labels_positions.csv` to **both** termite's and physarum's new inherited `Labels Positions Csv` field (Inspector → each sim → Neuron Positions). Enter Play, click manager `Reset`. Expected: termite and physarum agents spawn at the neuron cluster positions exactly as before; **no Console errors**.

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs" "Assets/Workspace/11.0 Biomes/src/components/Sim/TermiteSim.cs" "Assets/Workspace/11.0 Biomes/src/components/Sim/PhysarumSim.cs"
git commit -m "refactor(neurons): hoist neuron-position seeding to SimulationBase (DRY termite/physarum)"
```

---

## Task 7: Boid — neuron seeding + firing effect + params

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/params/BoidParams.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/includes/boid_type_params.hlsl`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/Sim/BoidSim.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/BoidSim.compute`

- [ ] **Step 1: Add firing params to `BoidAgentType`**

In `BoidParams.cs`, the `BoidAgentType` class ends with `saturation`. After `public float saturation = 0.5f;` add:

```csharp
        public float firingSpeedMul = 2f;
        public float firingDepositAmount = 0.3f;
```

- [ ] **Step 2: Add ranges (MIDI/OSC tunable)**

In `BoidParams.cs`, the `ranges` list — add two entries (before the closing `};`):

```csharp
            new("firingSpeedMul",     1f,  5f),
            new("firingDepositAmount", 0f, 1f),
```

- [ ] **Step 3: Add fields to the HLSL type-params struct**

In `boid_type_params.hlsl`, the struct currently ends:

```hlsl
    float diffuseRate;
    float hue;
    float saturation;
};  // 52 bytes (13 floats)
```

Replace with:

```hlsl
    float diffuseRate;
    float hue;
    float saturation;
    float firingSpeedMul;
    float firingDepositAmount;
};  // 60 bytes (15 floats)
```

- [ ] **Step 4: Add fields to the C# GPU struct + packing**

In `BoidSim.cs`, the `BoidTypeParamsGPU` struct — after `public float saturation;` add:

```csharp
            public float firingSpeedMul;
            public float firingDepositAmount;
```

In `UploadTypeParams()`, the initializer ends `saturation = t.saturation,`. After it add:

```csharp
                    firingSpeedMul = t.firingSpeedMul,
                    firingDepositAmount = t.firingDepositAmount,
```

- [ ] **Step 5: Compute — declarations (seeding globals + firing include)**

In `BoidSim.compute`, near the top after the existing `#include` lines, add:

```hlsl
#include "includes/neuron_firing.hlsl"

// Neuron-position seeding (bound by SimulationBase.BuildNeuronPositions)
StructuredBuffer<float2> neuronPositions;
uint neuronCount;
float2 neuronScale;
```

- [ ] **Step 6: Compute — seed `ResetAgentsKernel` from neurons**

`ResetAgentsKernel` (lines 82-92) currently reads:

```hlsl
[numthreads(1024, 1, 1)]
void ResetAgentsKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= agentsCount) return;

    Agent a;
    float2 c = Random2(id.x * .0001 + time * .001);
    a.position = float2(c.x * (float)rezX, c.y * (float)rezY);
    a.velocity = RandomDirection2(id.xy * .001 + sin(time));
    a.typeId = (id.x * typeCount) / agentsCount;
    agentsOut[id.x] = a;
}
```

Replace with (neuron seeding mirrors termite's `ResetAgentsKernel`, keeping boid's velocity init):

```hlsl
[numthreads(1024, 1, 1)]
void ResetAgentsKernel(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= agentsCount) return;

    Agent a;
    if (neuronCount > 0) {
        uint idx = id.x % neuronCount;
        float2 p = neuronPositions[idx] * neuronScale + float2(rezX * (1.0 - neuronScale.x) * 0.5, rezY * (1.0 - neuronScale.y) * 0.5);
        p.x = clamp(p.x, 0.0, (float)rezX - 1.0);
        p.y = clamp(p.y, 0.0, (float)rezY - 1.0);
        a.position = p;
    } else {
        float2 c = Random2(id.x * .0001 + time * .001);
        a.position = float2(c.x * (float)rezX, c.y * (float)rezY);
    }
    a.velocity = RandomDirection2(id.xy * .001 + sin(time));
    a.typeId = (id.x * typeCount) / agentsCount;
    agentsOut[id.x] = a;
}
```

- [ ] **Step 7: Compute — firing speed burst**

In `MoveAgentsKernel`, the velocity-limit block (lines 273-278) reads:

```hlsl
    b.velocity += acceleration;

    // Apply permeability speed penalty from biome
    float speedMult = perception.g;
    float effectiveMaxSpeed = p.maxSpeed * speedMult;
    b.velocity = Limit(b.velocity, effectiveMaxSpeed);
```

Replace with:

```hlsl
    b.velocity += acceleration;

    // Apply permeability speed penalty from biome + neuron-firing burst
    float speedMult = perception.g;
    float fireMul = IsFiring(id.x, neuronCount) ? p.firingSpeedMul : 1.0;
    float effectiveMaxSpeed = p.maxSpeed * speedMult * fireMul;
    b.velocity = Limit(b.velocity, effectiveMaxSpeed);
```

- [ ] **Step 8: Compute — firing brighter deposit**

In `WriteTrailsKernel` (lines 295-311), the own-layer deposit reads:

```hlsl
    float own = trailWrite[uint3(pos, b.typeId)];
    trailWrite[uint3(pos, b.typeId)] = clamp(own + p.depositAmount, 0, 1);
```

Replace with:

```hlsl
    float dep = IsFiring(id.x, neuronCount) ? p.firingDepositAmount : p.depositAmount;
    float own = trailWrite[uint3(pos, b.typeId)];
    trailWrite[uint3(pos, b.typeId)] = clamp(own + dep, 0, 1);
```

- [ ] **Step 9: C# — seed at reset + bind firing each step**

In `BoidSim.cs` `GPUReset()` (lines 106-118), the dispatch block reads:

```csharp
            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetBuffer(resetAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            Dispatch(resetAgentsKernel, agentsCount, 1, 1);
```

Replace with:

```csharp
            cs.SetInt(s_AgentsCountID, agentsCount);
            BuildNeuronPositions(resetAgentsKernel);
            cs.SetBuffer(resetAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            Dispatch(resetAgentsKernel, agentsCount, 1, 1);
```

In `BoidSim.cs` `GPUStep()`, find the per-step setup (after `UploadTypeParams();` and the `BindPerceptionTex(...)` call) and add right after them:

```csharp
            BindNeuronFiring(moveAgentsKernel, writeTrailsKernel);
```

- [ ] **Step 10: Verify — boids seed at neurons + fire**

Assign `labels_positions.csv` to boid's inherited `Labels Positions Csv` field. Enter Play, `Reset`. Expected: boids now spawn from the neuron positions (not uniform scatter). Send a frame sweep:

```bash
for f in 1000 40000 90000 140000; do \
  /tmp/oscvenv/bin/python -c "from pythonosc.udp_client import SimpleUDPClient as C; C('127.0.0.1',9000).send_message('/index', $f)"; sleep 1; done
```

Expected: firing boids burst to higher speed and lay brighter trails; stop → fades to quiet. **No Console errors**, no GPU struct/stride warnings.

- [ ] **Step 11: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/params/BoidParams.cs" "Assets/Workspace/11.0 Biomes/src/computes/includes/boid_type_params.hlsl" "Assets/Workspace/11.0 Biomes/src/components/Sim/BoidSim.cs" "Assets/Workspace/11.0 Biomes/src/computes/BoidSim.compute"
git commit -m "feat(neurons): boid neuron-position seeding + firing speed/deposit"
```

---

## Task 8: Physarum — firing effect + params

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/params/PhysarumParams.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/includes/physarum_type_params.hlsl`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/Sim/PhysarumSim.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/computes/PhysarumSim.compute`

Seeding is already inherited (Task 6); this adds only the firing effect.

- [ ] **Step 1: Add firing params to `PhysarumAgentType`**

In `PhysarumParams.cs`, after `public float saturation = 0.5f;` add:

```csharp
        public float firingSpeedMul = 2f;
        public float firingDepositAmount = 0.3f;
```

- [ ] **Step 2: Add ranges**

In the `ranges` list, before the closing `};` add:

```csharp
            new("firingSpeedMul",     1f,  5f),
            new("firingDepositAmount", 0f, 1f),
```

- [ ] **Step 3: Add HLSL struct fields**

In `physarum_type_params.hlsl`, the struct ends:

```hlsl
    float hue;
    float saturation;
};  // 36 bytes (9 floats)
```

Replace with:

```hlsl
    float hue;
    float saturation;
    float firingSpeedMul;
    float firingDepositAmount;
};  // 44 bytes (11 floats)
```

- [ ] **Step 4: Add C# GPU struct fields + packing**

In `PhysarumSim.cs`, the `PhysarumTypeParamsGPU` struct reads:

```csharp
        struct PhysarumTypeParamsGPU
        {
            public float senseAngle, senseDistance, turnAngle, moveSpeed;
            public float depositAmount, eatAmount;
            public float diffuseRate, hue, saturation;
        }
```

Replace with:

```csharp
        struct PhysarumTypeParamsGPU
        {
            public float senseAngle, senseDistance, turnAngle, moveSpeed;
            public float depositAmount, eatAmount;
            public float diffuseRate, hue, saturation;
            public float firingSpeedMul, firingDepositAmount;
        }
```

In `UploadTypeParams()`, the initializer ends `saturation = t.saturation,`. After it add:

```csharp
                    firingSpeedMul = t.firingSpeedMul,
                    firingDepositAmount = t.firingDepositAmount,
```

- [ ] **Step 5: Compute — include + firing speed**

In `PhysarumSim.compute`, near the top after the existing `#include` lines add:

```hlsl
#include "includes/neuron_firing.hlsl"
```

In `MoveAgentsKernel` (lines 144-170), the speed block reads:

```hlsl
    float2 uv = a.position / float2((float)rezX, (float)rezY);
    float speedMult = perceptionTex.SampleLevel(sampler_perceptionTex, uv, 0).g;
    float effectiveSpeed = p.moveSpeed * speedMult;
```

Replace with:

```hlsl
    float2 uv = a.position / float2((float)rezX, (float)rezY);
    float speedMult = perceptionTex.SampleLevel(sampler_perceptionTex, uv, 0).g;
    float fireMul = IsFiring(id.x, neuronCount) ? p.firingSpeedMul : 1.0;
    float effectiveSpeed = p.moveSpeed * speedMult * fireMul;
```

- [ ] **Step 6: Compute — firing brighter deposit**

In `WriteTrailsKernel` (lines 176-194), the own-layer deposit reads:

```hlsl
    // Deposit into own layer
    float own = trailWrite[uint3(pos, a.typeId)];
    trailWrite[uint3(pos, a.typeId)] = clamp(own + p.depositAmount, 0, 1);
```

Replace with:

```hlsl
    // Deposit into own layer (brighter when the agent's neuron is firing)
    float dep = IsFiring(id.x, neuronCount) ? p.firingDepositAmount : p.depositAmount;
    float own = trailWrite[uint3(pos, a.typeId)];
    trailWrite[uint3(pos, a.typeId)] = clamp(own + dep, 0, 1);
```

- [ ] **Step 7: C# — bind firing each step**

In `PhysarumSim.cs` `GPUStep()` (lines 161-185), after:

```csharp
            UploadTypeParams();
            BindPerceptionTex(moveAgentsKernel);
```

add:

```csharp
            BindNeuronFiring(moveAgentsKernel, writeTrailsKernel);
```

- [ ] **Step 8: Verify — physarum fires**

Enter Play, `Reset`. Sweep frames:

```bash
for f in 1000 40000 90000 140000; do \
  /tmp/oscvenv/bin/python -c "from pythonosc.udp_client import SimpleUDPClient as C; C('127.0.0.1',9000).send_message('/index', $f)"; sleep 1; done
```

Expected: physarum agents on firing neurons move faster and deposit brighter trails; stop → fades to quiet. **No Console errors**.

- [ ] **Step 9: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/params/PhysarumParams.cs" "Assets/Workspace/11.0 Biomes/src/computes/includes/physarum_type_params.hlsl" "Assets/Workspace/11.0 Biomes/src/components/Sim/PhysarumSim.cs" "Assets/Workspace/11.0 Biomes/src/computes/PhysarumSim.compute"
git commit -m "feat(neurons): physarum firing speed/deposit effect + params"
```

---

## Task 9: End-to-end wiring, defaults & coherence check

**Files:**
- Modify: scene + preset assets (inspector). Possibly `params` preset `.asset` files.

- [ ] **Step 1: Confirm scene wiring**

On the `SimulationManager` GameObject: `NeuronFiringSource` present, blob path = `biomes11/termite_firing.f16`, manager `Neuron Firing` field → that component, `OSCMapping` `M Neuron Firing Source` → that component. All three sims have `labels_positions.csv` assigned and `firingThreshold ≈ 0.1`. Physarum & boid presets have sensible `firingSpeedMul` (≈2) / `firingDepositAmount` (≈0.3).

- [ ] **Step 2: Coherence run**

Enter Play, `Reset`. Send a slow sweep across the full range and confirm all three biomes light up the **same neuron sites** in lockstep:

```bash
for f in $(seq 0 6000 179999); do \
  /tmp/oscvenv/bin/python -c "from pythonosc.udp_client import SimpleUDPClient as C; C('127.0.0.1',9000).send_message('/index', $f)"; sleep 0.4; done
```

Expected: at each index, termite + physarum + boid agents seeded at the firing neurons burst/brighten together; the firing pattern visibly follows the external index; on stopping, all three fade to quiet within ~0.5 s. **No Console errors.**

- [ ] **Step 3: Stream test (smooth playback)**

```bash
/tmp/oscvenv/bin/python -c "
import time
from pythonosc.udp_client import SimpleUDPClient
c = SimpleUDPClient('127.0.0.1', 9000)
for f in range(0, 12000):
    c.send_message('/index', f)
    time.sleep(1/60)
"
```

Expected: continuous, smooth firing animation (intensity stays ≈1 because indices arrive each frame). Stop → fades to quiet.

- [ ] **Step 4: Commit any scene/preset changes**

```bash
git add -A "Assets/Workspace/11.0 Biomes"
git commit -m "chore(neurons): scene + preset wiring for OSC-driven shared firing"
```

---

## Documentation (after all tasks)

Per repo conventions (`docs/ARCHITECTURE.md` §7): update `docs/ARCHITECTURE.md` — §3.4 (firing now external/shared, all three sims), §3.7 (new `/index` OSC), and the repo layout note. Add a session log under `docs/sessions/` via the `eoc-docs` skill. Update `README.md` roadmap/changes. Then open a PR from `feat/osc-neuron-firing`.

---

## Notes for the implementer

- **GPU struct stride:** the C# `[StructLayout(Sequential)]` struct and the HLSL struct must match field-for-field. Tasks 7 & 8 add the same two floats to both — keep order identical (`firingSpeedMul` then `firingDepositAmount`, after `saturation`). A mismatch shows as garbled colors/behavior, not a hard error.
- **`neuronCount` global:** each sim's `.compute` already (termite/physarum) or now (boid) declares `uint neuronCount;`, set by `BuildNeuronPositions`. `IsFiring(id.x, neuronCount)` reuses it. It persists across dispatches since `BuildNeuronPositions` runs at reset.
- **Graceful absence:** with no `NeuronFiringSource` wired, `neuronFiring` is null → `BindNeuronFiring` binds a dummy with count 0 → `NeuronFireValue` returns 0 → no firing, no errors. Sims run normally.
- **Decay vs steps-per-frame:** `UpdateFiring` decays by `Time.unscaledTime` delta, which is constant within a frame, so decay advances once per rendered frame regardless of `stepsPerFrame`.
```

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Routes external installation drivers (plants, robotics, neuron firing, …) into
    /// biome channels as soft spatial "stamps." Each <see cref="Source"/> maps a physical
    /// location to a biome UV + channel; its live value (constant in the inspector, or
    /// pushed via <see cref="SetValue"/> from an OSC/sensor callback) becomes a Gaussian
    /// deposit. SimulationManager calls <see cref="Inject"/> once per step — after sim
    /// write-back and before Biome.Step() — so deposits ride the field ping-pong with no
    /// clobber risk (see Biome.InjectSources).
    ///
    /// Examples: a plant → Oxygen source at its mapped floor location; a robot arm →
    /// Temperature source driven by a proximity sensor; neuron firing → Pheromone/alarm.
    ///
    /// A source may instead be <see cref="Drive.Procedural"/> — self-animating from a
    /// phase clock rather than OSC. The diurnal sun is one: a warm MaxToward stamp that sweeps
    /// Temperature L→R, phased off the neuron playhead so one blob playthrough is one day
    /// (<see cref="AddDiurnalSunSource"/>).
    /// </summary>
    public class BiomeInjector : MonoBehaviour
    {
        public enum BlendMode { Additive = 0, MaxToward = 1, SetToward = 2 }

        // Source value origin. External = inspector/OSC (default). Procedural = the source
        // animates its own position + value from a phase clock (e.g. the diurnal sun), ignoring OSC.
        public enum Drive { External = 0, Procedural = 1 }

        // Phase clock for a Procedural source. FiringIndex = the neuron playhead
        // (NeuronFiringSource.CurrentFrame / FrameCount): one full firing-blob playthrough
        // (index 0 → last) is one "day", so the biome's diurnal rhythm rides the organoid
        // playback and the index=0 loop reset lands on sunrise. SimStep = free-running
        // SimStepCount with periodSteps (fallback when no OSC index, e.g. a tempo breath).
        public enum PhaseSource { FiringIndex = 0, SimStep = 1 }

        [Serializable]
        public class Source
        {
            public string name = "source";
            public bool enabled = true;

            [Tooltip("Optional OSC address override. Blank = listen on /inject/<name>. Set this " +
                     "to decouple the wire protocol from the display name (rename freely without " +
                     "breaking the sensor mapping).")]
            public string oscAddress = "";

            [Tooltip("NORMALIZED biome coordinate, 0..1 (NOT world space). (0,0)=one corner, " +
                     "(0.5,0.5)=center, (1,1)=opposite corner. This is the manual physical→biome map.")]
            public Vector2 fieldUV = new Vector2(0.5f, 0.5f);
            [Range(0.001f, 0.5f)] public float radius = 0.06f;
            [Range(0.25f, 6f)]    public float falloff = 1.5f;

            [Tooltip("Target biome channel.")]
            [BiomeChannelField] public int channel = BiomeChannel.Oxygen;

            [Tooltip("Multiplies the (calibrated, smoothed) value. Additive mode: small per-step " +
                     "increment (~0.005-0.05). Max/Set modes: target-level scale (~1).")]
            public float gain = 1f;

            [Tooltip("Persistent sources: prefer MaxToward (builds a stable gradient) over Additive (saturates to a flat blob).")]
            public BlendMode mode = BlendMode.MaxToward;

            [Header("Raw input → 0..1 calibration")]
            [Tooltip("Raw sensor range that maps to 0..1. Most sensors don't send 0..1 — set the " +
                     "min/max you actually see (e.g. a CO₂ ppm or distance range) and the injector " +
                     "remaps + clamps for you. Leave 0..1 for an already-normalized feed.")]
            public float inputMin = 0f;
            public float inputMax = 1f;
            [Tooltip("Temporal smoothing (EMA) of the calibrated value: 0 = none (snappy), " +
                     "0.9 = heavy (slow, denoised). Tames jittery sensors without TD-side work.")]
            [Range(0f, 0.99f)] public float smoothing = 0f;

            [Tooltip("Live RAW value (pre-calibration). Edit here to test, or push via SetValue(name, v) from OSC/sensor.")]
            public float value = 1f;

            [Tooltip("Seconds before an un-refreshed value decays to 0 (sensor-dropout guard). 0 = never.")]
            public float valueTimeout = 0f;

            [Header("Procedural drive (self-animating; ignores OSC when Procedural)")]
            [Tooltip("External = value from inspector/OSC (default). Procedural = the source animates " +
                     "its own fieldUV + value from a phase clock — the diurnal sun. Reuses radius/falloff/" +
                     "channel/gain/mode below; with MaxToward the sun only ever RAISES Temperature, so it " +
                     "warms by day and simply stops at night (field relaxes to its 0.5 baseline).")]
            public Drive drive = Drive.External;
            [Tooltip("Procedural phase clock. FiringIndex = the neuron playhead (one blob playthrough = one day). " +
                     "SimStep = free-running SimStepCount over periodSteps.")]
            public PhaseSource phaseSource = PhaseSource.FiringIndex;
            [Tooltip("SimStep mode only: sim steps per full day. 7200 = 2 min @ 60 Hz. Ignored in FiringIndex mode " +
                     "(period = the blob's frame range, e.g. 0..180000).")]
            public int periodSteps = 7200;
            [Tooltip("Fraction of the cycle that is daylight; the remainder is a dark, cool night (no stamp, " +
                     "field relaxes to baseline). 0.7 = 70% day.")]
            [Range(0.1f, 1f)] public float dayFraction = 0.7f;
            [Tooltip("Vertical position of the sun (0..1). It sweeps horizontally L→R across daylight; gain is the " +
                     "noon warmth target (MaxToward), tapering to 0 at sunrise/sunset via a sine envelope.")]
            [Range(0f, 1f)] public float sweepHeight = 0.5f;

            [NonSerialized] public float lastSetTime = -1f;
            [NonSerialized] public volatile bool valueDirty; // set off-thread by SetValue, consumed in Inject
            // Monitoring (main-thread, for the editor readout): post-calibration smoothed value
            // actually stamped, and wall-clock of the last received message.
            [NonSerialized] public float monCalibrated;
            [NonSerialized] public float monLastMsgTime = -1f;
            [NonSerialized] public bool  monStale;
        }

        public List<Source> sources = new();

        [Header("Firing-driven dispersal")]
        [Tooltip("Neuron firing source; each firing neuron injects a Dispersal pulse at its location.")]
        public NeuronFiringSource firingSource;
        public bool firingDispersalEnabled = true;
        [BiomeChannelField] public int dispersalChannel = BiomeChannel.Dispersal;
        [Tooltip("Match the sims' spawnScale so pulses land on agent clusters.")]
        public Vector2 firingSpawnScale = new Vector2(0.8f, 0.9f);
        [Range(0.001f, 0.5f)] public float dispersalRadius = 0.05f;
        [Tooltip("Radius grows by this fraction as the firing intensity fades.")]
        [Range(0f, 4f)] public float dispersalExpandGain = 1.5f;
        [Range(0.25f, 6f)] public float dispersalFalloff = 1.5f;
        [Tooltip("Min stamp amount for a barely-firing neuron.")]
        [Range(0f, 1f)] public float dispersalBaseline = 0.05f;
        [Tooltip("Max stamp amount for a full-intensity firing neuron.")]
        [Range(0f, 1f)] public float dispersalAmount = 0.6f;
        [Range(0f, 1f)] public float dispersalFireThreshold = 0.1f;

        public enum FiringDispersalSource { NeuronPositions, AgentPositions }
        [Tooltip("NeuronPositions = fixed CSV neuron coords (no readback). AgentPositions = live agent positions of the sim below (pulses erupt from where the swarm actually is). With useAsyncReadback on (default) the readback is non-blocking; positions lag 1-2 frames (invisible for a fading pulse).")]
        public FiringDispersalSource firingDispersalSource = FiringDispersalSource.NeuronPositions;
        [Tooltip("Sim whose live agent positions drive AgentPositions mode (e.g. the TermiteSim). Agent i uses firing neuron (i % neuronCount).")]
        public SimulationBase firingAgentSim;
        [Tooltip("Max agents read back / stamped per frame in AgentPositions mode (caps cost at high agent counts).")]
        [Range(1, 4096)] public int firingAgentStampCap = 256;
        [Tooltip("AgentPositions readback mode. ON (default) = AsyncGPUReadback: non-blocking, no CPU↔GPU stall, positions lag 1-2 frames. Large win on BOTH discrete GPUs (D3D12) and Apple Silicon (Metal) — the cost removed is the sync barrier, not just the copy. OFF = synchronous GetData (stalls the CPU every frame) — fallback only.")]
        public bool useAsyncReadback = true;

        [Header("Gizmo")]
        public bool drawGizmos = true;
        public Color gizmoColor = new Color(0.3f, 0.9f, 1f, 0.6f);

        // Must match HLSL `struct InjectStamp` in Biome.compute (32 bytes, tightly packed).
        [StructLayout(LayoutKind.Sequential)]
        private struct Stamp
        {
            public Vector2 uv;
            public float radius;
            public float falloff;
            public int channel;
            public float amount;
            public int mode;
            public float pad;
        }
        private const int StampStride = sizeof(float) * 8; // 32

        // Matches the GPU Agent struct (float2 position, float2 direction, uint typeId = 20 bytes)
        // for reading back live sim agent positions in AgentPositions mode.
        [StructLayout(LayoutKind.Sequential)]
        private struct AgentLayout { public Vector2 position; public Vector2 direction; public uint typeId; }
        private AgentLayout[] _agentScratch;            // sync fallback path only

        // Async readback (default): two NativeArrays ping-pong so we stamp from the last
        // COMPLETED result (_rbResult) while the next non-blocking request fills the other
        // buffer (_rbPending). No CPU↔GPU stall. Swapped in OnAgentReadback on completion.
        private NativeArray<AgentLayout> _rbResult, _rbPending;
        private int _rbResultCount, _rbPendingCount;
        private bool _rbValid, _rbInFlight;
        private AsyncGPUReadbackRequest _rbReq;

        private ComputeBuffer _buffer;
        private Stamp[] _scratch;

        /// <summary>Push a live value (0..1) to a named source — e.g. from an OSC/sensor
        /// callback. Thread-safe: only writes plain fields (no Unity API), so it is safe to
        /// call from an OSC receive thread. The valueTimeout clock is stamped on the main
        /// thread in Inject.</summary>
        public void SetValue(string sourceName, float v)
        {
            var list = sources;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && s.name == sourceName) { s.value = v; s.valueDirty = true; }
            }
        }

        /// <summary>Remap raw input to 0..1 by [inputMin,inputMax], clamped. Identity for the
        /// default 0..1 range. Degenerate range (min≈max) → 0.</summary>
        private static float Calibrate(float raw, float lo, float hi)
        {
            float span = hi - lo;
            if (Mathf.Abs(span) < 1e-6f) return 0f;
            return Mathf.Clamp01((raw - lo) / span);
        }

        /// <summary>Move a named source to a new normalized biome UV (0..1) — e.g. a robot
        /// pose driving the stamp location. Thread-safe; clamped to [0,1].</summary>
        public void SetPosition(string sourceName, float u, float v)
        {
            var list = sources;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s != null && s.name == sourceName)
                    s.fieldUV = new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
            }
        }

        /// <summary>Drive a named source's FULL stamp from one message: position (u,v),
        /// radius, falloff, and value — e.g. an audio engine placing a sized Dispersal hit
        /// anywhere. Thread-safe (plain-field writes, same pattern as SetValue/SetPosition);
        /// radius/falloff clamped to their inspector ranges.</summary>
        public void SetStamp(string sourceName, float u, float v, float radius, float falloff, float value)
        {
            var list = sources;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null || s.name != sourceName) continue;
                s.fieldUV = new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
                s.radius  = Mathf.Clamp(radius, 0.001f, 0.5f);
                s.falloff = Mathf.Clamp(falloff, 0.25f, 6f);
                s.value   = value;
                s.valueDirty = true;
            }
        }

        /// <summary>Resize a named source's stamp (radius, falloff) without changing its value
        /// or position. Thread-safe; clamped to the inspector ranges.</summary>
        public void SetShape(string sourceName, float radius, float falloff)
        {
            var list = sources;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null || s.name != sourceName) continue;
                s.radius  = Mathf.Clamp(radius, 0.001f, 0.5f);
                s.falloff = Mathf.Clamp(falloff, 0.25f, 6f);
            }
        }

        /// <summary>Pack the active sources and dispatch injection into the biome. Call once
        /// per step, AFTER sim write-back and BEFORE biome.Step(). simStep is the canonical
        /// SimStepCount, used as the fallback phase clock for Procedural sources.</summary>
        public void Inject(Biome biome, int simStep)
        {
            if (biome == null || sources == null) return;

            int n = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s != null && s.enabled && s.radius > 0f) n++;
            }
            if (n == 0 && !(firingDispersalEnabled && firingSource != null)) return;
            if (_scratch == null || _scratch.Length < n) _scratch = new Stamp[Mathf.Max(n, 8)];

            float now = Time.time;
            int k = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s == null || !s.enabled || s.radius <= 0f) continue;

                Vector2 uv;
                float value;   // 0..1, pre-gain

                if (s.drive == Drive.Procedural)
                {
                    // Self-driving source (diurnal sun): sweep position + sine warmth from a phase
                    // clock. Night (phase past dayFraction) emits no stamp — MaxToward never cools,
                    // so the field just relaxes to baseline until the next sunrise.
                    float phase = ProceduralPhase(s, simStep);
                    float day = Mathf.Clamp(s.dayFraction, 0.05f, 1f);
                    if (phase >= day) { s.monCalibrated = 0f; continue; }
                    float dayPhase = phase / day;                 // 0..1 across daylight
                    uv = new Vector2(dayPhase, Mathf.Clamp01(s.sweepHeight));
                    value = Mathf.Sin(Mathf.PI * dayPhase);       // 0 sunrise → 1 noon → 0 sunset
                    s.monCalibrated = value;                      // inspector readout
                }
                else
                {
                    if (s.valueDirty) { s.lastSetTime = now; s.monLastMsgTime = now; s.valueDirty = false; } // stamp set-time on main thread

                    // Calibrate raw → 0..1, guard sensor dropout, then EMA-smooth.
                    float cal = Calibrate(s.value, s.inputMin, s.inputMax);
                    s.monStale = (s.valueTimeout > 0f && s.lastSetTime >= 0f && now - s.lastSetTime > s.valueTimeout);
                    if (s.monStale) cal = 0f; // stale value decays to nothing
                    s.monCalibrated = Mathf.Lerp(cal, s.monCalibrated, Mathf.Clamp(s.smoothing, 0f, 0.99f));
                    value = s.monCalibrated;
                    uv = new Vector2(Mathf.Clamp01(s.fieldUV.x), Mathf.Clamp01(s.fieldUV.y));
                }

                _scratch[k++] = new Stamp
                {
                    uv = uv,
                    radius = s.radius,
                    falloff = s.falloff,
                    channel = Mathf.Clamp(s.channel, 0, BiomeChannel.Count - 1),
                    amount = s.gain * value,
                    mode = (int)s.mode,
                    pad = 0f,
                };
            }

            // Firing-driven dispersal pulses: one stamp per firing neuron (or per live agent),
            // strength scaled by firing intensity, radius expanding as it fades.
            if (firingDispersalEnabled && firingSource != null)
            {
                var scaled = firingSource.ScaledValues;
                int neuronCount = scaled != null ? scaled.Length : 0;
                if (neuronCount > 0)
                {
                    if (firingDispersalSource == FiringDispersalSource.AgentPositions && firingAgentSim != null)
                        k = AppendAgentPositionStamps(scaled, neuronCount, k);
                    else
                        k = AppendNeuronPositionStamps(scaled, neuronCount, k);
                }
            }

            if (k == 0) return;
            if (_buffer == null || _buffer.count < k)
            {
                _buffer?.Release();
                _buffer = new ComputeBuffer(Mathf.Max(k, 4), StampStride);
            }
            _buffer.SetData(_scratch, 0, 0, k);
            biome.InjectSources(_buffer, k);
        }

        // Phase 0..1 for a Procedural source. FiringIndex rides the neuron playhead so one
        // full blob playthrough (index 0→last) is one day; CurrentFrame<0 (pre-playback) or a
        // missing/unloaded firing source falls back to the free-running SimStep clock.
        private float ProceduralPhase(Source s, int simStep)
        {
            if (s.phaseSource == PhaseSource.FiringIndex && firingSource != null && firingSource.FrameCount > 1)
            {
                int f = firingSource.CurrentFrame;
                if (f >= 0) return Mathf.Clamp01((float)f / (firingSource.FrameCount - 1));
                // else: no index received yet → fall through to the free-running clock (sunrise-ish)
            }
            int period = Mathf.Max(1, s.periodSteps);
            return (simStep % period) / (float)period;
        }

        /// <summary>Append a ready-tuned diurnal-sun source (Procedural, Temperature, MaxToward,
        /// FiringIndex-phased). Edit gain/radius/dayFraction after adding to taste.</summary>
        [Button]
        public void AddDiurnalSunSource()
        {
            sources ??= new List<Source>();
            sources.Add(new Source
            {
                name = "DiurnalSun",
                drive = Drive.Procedural,
                phaseSource = PhaseSource.FiringIndex,
                channel = BiomeChannel.Temperature,
                mode = BlendMode.MaxToward,
                gain = 0.8f,          // noon Temperature target (baseline is 0.5)
                radius = 0.22f,
                falloff = 1.5f,
                dayFraction = 0.7f,
                sweepHeight = 0.5f,
                periodSteps = 7200,   // SimStep-mode fallback (2 min @ 60 Hz)
            });
        }

        // Grow the stamp scratch array, preserving existing entries.
        private void GrowScratch(int needed)
        {
            int newLen = Mathf.Max(needed, _scratch != null ? _scratch.Length * 2 : 8);
            var grown = new Stamp[newLen];
            if (_scratch != null) System.Array.Copy(_scratch, grown, _scratch.Length);
            _scratch = grown;
        }

        // Stamp dispersal at fixed neuron CSV positions (normalized * spawnScale, centered).
        private int AppendNeuronPositionStamps(float[] scaled, int neuronCount, int k)
        {
            var posCPU = firingSource.PositionsCPU;
            int cap = posCPU != null ? Mathf.Min(neuronCount, posCPU.Count) : 0;
            for (int i = 0; i < cap; i++)
            {
                float f = scaled[i];
                if (f < dispersalFireThreshold) continue;
                Vector2 np = posCPU[i];
                Vector2 uv = new Vector2(
                    np.x * firingSpawnScale.x + (1f - firingSpawnScale.x) * 0.5f,
                    np.y * firingSpawnScale.y + (1f - firingSpawnScale.y) * 0.5f);
                k = AddDispersalStamp(uv, Mathf.Clamp01(f), k);
            }
            return k;
        }

        // Stamp dispersal at the live agent positions of firingAgentSim. Agent i uses firing
        // neuron (i % neuronCount); duplicate copies (i / neuronCount) fire progressively
        // weaker (identity when agentCount == neuronCount). Readback is async by default
        // (no CPU stall, positions lag 1-2 frames); useAsyncReadback off = synchronous fallback.
        private int AppendAgentPositionStamps(float[] scaled, int neuronCount, int k)
        {
            var buf = firingAgentSim.GetAgentPositionBuffer();
            int agentCount = firingAgentSim.GetAgentCount();
            if (buf == null || agentCount <= 0) return k;

            int readCount = Mathf.Min(agentCount, Mathf.Min(buf.count, firingAgentStampCap));
            if (readCount <= 0) return k;

            float rezX = Mathf.Max(1, firingAgentSim.rezX);
            float rezY = Mathf.Max(1, firingAgentSim.rezY);

            if (!useAsyncReadback)
            {
                // Synchronous fallback: flushes the GPU and stalls the CPU every frame. The sync
                // barrier costs on EVERY GPU (unified memory only saves the copy, not the wait) —
                // kept only as a safety net.
                if (_agentScratch == null || _agentScratch.Length < readCount)
                    _agentScratch = new AgentLayout[Mathf.Max(readCount, _agentScratch != null ? _agentScratch.Length * 2 : 8)];
                buf.GetData(_agentScratch, 0, 0, readCount);
                for (int i = 0; i < readCount; i++)
                {
                    int copy = i / neuronCount;                       // 0 for the first neuronCount agents, then 1, 2, ...
                    float f = scaled[i % neuronCount] / (copy + 1);   // duplicate copies fire progressively weaker (discrete per copy)
                    if (f < dispersalFireThreshold) continue;
                    Vector2 p = _agentScratch[i].position;
                    Vector2 uv = new Vector2(Mathf.Clamp01(p.x / rezX), Mathf.Clamp01(p.y / rezY));
                    k = AddDispersalStamp(uv, Mathf.Clamp01(f), k);
                }
                return k;
            }

            // Async path: kick one non-blocking request (only when none is in flight, so the
            // grow/realloc below can never touch a buffer the GPU is still writing).
            if (!_rbInFlight)
            {
                if (!_rbPending.IsCreated || _rbPending.Length < readCount)
                {
                    if (_rbPending.IsCreated) _rbPending.Dispose();
                    _rbPending = new NativeArray<AgentLayout>(
                        Mathf.Max(readCount, _rbPending.IsCreated ? _rbPending.Length * 2 : 8),
                        Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }
                _rbPendingCount = readCount;
                _rbReq = AsyncGPUReadback.RequestIntoNativeArray(ref _rbPending, buf, readCount * buf.stride, 0, OnAgentReadback);
                _rbInFlight = true;
            }

            if (!_rbValid) return k; // warmup: no completed result yet (first 1-2 frames)
            int count = Mathf.Min(_rbResultCount, _rbResult.Length);
            for (int i = 0; i < count; i++)
            {
                int copy = i / neuronCount;
                float f = scaled[i % neuronCount] / (copy + 1);
                if (f < dispersalFireThreshold) continue;
                Vector2 p = _rbResult[i].position;
                Vector2 uv = new Vector2(Mathf.Clamp01(p.x / rezX), Mathf.Clamp01(p.y / rezY));
                k = AddDispersalStamp(uv, Mathf.Clamp01(f), k);
            }
            return k;
        }

        // Completion of an async agent-position readback. The just-filled _rbPending becomes the
        // readable result; the previous result buffer is recycled for the next request (ping-pong),
        // so stamping never reads a buffer the GPU is mid-write on.
        private void OnAgentReadback(AsyncGPUReadbackRequest req)
        {
            _rbInFlight = false;
            if (req.hasError) return; // keep last good result
            (_rbResult, _rbPending) = (_rbPending, _rbResult);
            _rbResultCount = _rbPendingCount;
            _rbValid = true;
        }

        // Append one dispersal stamp at uv with intensity fc (0..1), growing scratch as needed.
        private int AddDispersalStamp(Vector2 uv, float fc, int k)
        {
            if (k >= _scratch.Length) GrowScratch(k + 1);
            _scratch[k++] = new Stamp
            {
                uv = uv,
                radius = dispersalRadius * (1f + dispersalExpandGain * (1f - fc)),
                falloff = dispersalFalloff,
                channel = Mathf.Clamp(dispersalChannel, 0, BiomeChannel.Count - 1),
                amount = dispersalBaseline + (dispersalAmount - dispersalBaseline) * fc,
                mode = (int)BlendMode.MaxToward,
                pad = 0f,
            };
            return k;
        }

        /// <summary>The OSC address a source listens on: its explicit override, or
        /// /inject/&lt;name&gt; by default. Used by OSCMapping to register callbacks.</summary>
        public static string OscAddressFor(Source s)
        {
            if (s == null) return null;
            if (!string.IsNullOrEmpty(s.oscAddress)) return s.oscAddress;
            if (string.IsNullOrEmpty(s.name)) return null;
            return $"/inject/{s.name}";
        }

        /// <summary>Bring-up aid: dump each source's wiring + live state to the Console so you
        /// can see at a glance which sensors are actually arriving. Run in Play mode.</summary>
        [Button("Log Live Source Values")]
        public void LogLiveValues()
        {
            if (sources == null || sources.Count == 0) { Debug.Log("[BiomeInjector] no sources"); return; }
            float now = Application.isPlaying ? Time.time : -1f;
            var sb = new StringBuilder("[BiomeInjector] live sources:\n");
            foreach (var s in sources)
            {
                if (s == null) continue;
                string age = (now >= 0f && s.monLastMsgTime >= 0f)
                    ? $"{now - s.monLastMsgTime:0.0}s ago" : "never";
                sb.Append($"  • {s.name} → {BiomeChannel.Names[Mathf.Clamp(s.channel, 0, BiomeChannel.Count - 1)]}")
                  .Append($" @uv({s.fieldUV.x:0.00},{s.fieldUV.y:0.00})")
                  .Append($"  raw={s.value:0.000} → cal={s.monCalibrated:0.000}")
                  .Append($"  osc={OscAddressFor(s)}  lastMsg={age}{(s.monStale ? " [STALE]" : "")}\n");
            }
            Debug.Log(sb.ToString());
        }

        // Append ready-to-drive example Dispersal sources: 3 kinetic arms + 1 audio emitter.
        // OSC drivers are registered at OSCMapping.Start() from this list, so add these in
        // EDIT mode then enter Play. Each is named, so it gets these addresses automatically:
        //   /inject/<name>                 <0..1>                          intensity at current uv
        //   /inject/<name>/pos             <u> <v>                         move the emitter (arms)
        //   /inject/<name>/shape           <radius> <falloff>              resize only
        //   /inject/<name>/stamp           <u> <v> <radius> <falloff> <v>  full hit (audio)
        // Names: arm1, arm2, arm3, audio.  All target the Dispersal channel (scatter).
        [Button("Add Example Dispersal Sources")]
        public void AddExampleDispersalSources()
        {
            if (sources == null) sources = new List<Source>();

            // Three kinetic arms: movable emitters. Drive /inject/armN/pos with the arm's
            // mapped position and /inject/armN with its activity (0..1). MaxToward holds a
            // stable level under the arm; the channel's fast decay trails it as the arm moves.
            AddDispersalArm("arm1", new Vector2(0.25f, 0.5f));
            AddDispersalArm("arm2", new Vector2(0.50f, 0.5f));
            AddDispersalArm("arm3", new Vector2(0.75f, 0.5f));

            // Audio: full-stamp control so the audio engine can throw sized hits anywhere via
            //   /inject/audio/stamp <u> <v> <radius> <falloff> <value>.
            sources.Add(new Source
            {
                name = "audio", channel = BiomeChannel.Dispersal, mode = BlendMode.MaxToward,
                fieldUV = new Vector2(0.5f, 0.5f), radius = 0.12f, falloff = 1.5f, gain = 1f,
                inputMin = 0f, inputMax = 1f, smoothing = 0f, value = 0f, valueTimeout = 0.2f,
            });

            Debug.Log("[BiomeInjector] Added example Dispersal sources: arm1, arm2, arm3, audio. " +
                      "OSC: /inject/<name> <v> · /pos <u> <v> · /shape <r> <f> · /stamp <u> <v> <r> <f> <v>. " +
                      "Re-enter Play so OSCMapping registers them.");
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        private void AddDispersalArm(string name, Vector2 uv)
        {
            sources.Add(new Source
            {
                name = name, channel = BiomeChannel.Dispersal, mode = BlendMode.MaxToward,
                fieldUV = uv, radius = 0.08f, falloff = 1.5f, gain = 1f,
                inputMin = 0f, inputMax = 1f, smoothing = 0f, value = 0f, valueTimeout = 0.5f,
            });
        }

        // Append one near-global warmth Source per media-agent entity (termite/physarum/boid),
        // each stamping the Temperature channel. Driven by /sn/<entity>/warmth via OSCMapping's
        // media-agent bridge (SetValue → this source by name). MaxToward builds a stable warm
        // tint rather than saturating; the large radius makes warmth a near-global valence per
        // entity (NOT a per-swarm localized stamp). Set each name below as the matching
        // EntityBinding.warmthSourceName in OSCMapping. Add in EDIT mode, then enter Play.
        [Button("Add Example Warmth Sources")]
        public void AddExampleWarmthSources()
        {
            if (sources == null) sources = new List<Source>();

            AddWarmthSource("warmth-termite",  new Vector2(0.5f, 0.5f));
            AddWarmthSource("warmth-physarum", new Vector2(0.5f, 0.5f));
            AddWarmthSource("warmth-boid",     new Vector2(0.5f, 0.5f));

            Debug.Log("[BiomeInjector] Added example Warmth sources: warmth-termite, warmth-physarum, warmth-boid " +
                      "(Temperature, MaxToward, near-global radius). Set these names as EntityBinding.warmthSourceName " +
                      "in OSCMapping. OSC: /sn/<entity>/warmth <0..1>. Re-enter Play so OSCMapping registers them.");
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        private void AddWarmthSource(string name, Vector2 uv)
        {
            sources.Add(new Source
            {
                name = name, channel = BiomeChannel.Temperature, mode = BlendMode.MaxToward,
                fieldUV = uv, radius = 0.5f, falloff = 1.5f, gain = 1f,
                inputMin = 0f, inputMax = 1f, smoothing = 0f, value = 0f, valueTimeout = 0.5f,
            });
        }

        void OnDestroy()
        {
            // Force any in-flight readback to finish before freeing the buffers it writes into.
            if (_rbInFlight) _rbReq.WaitForCompletion();
            if (_rbPending.IsCreated) _rbPending.Dispose();
            if (_rbResult.IsCreated) _rbResult.Dispose();
            _buffer?.Release();
            _buffer = null;
        }

        // Keep authored values in range so a stamp can never land off the field.
        void OnValidate()
        {
            if (sources == null) return;
            foreach (var s in sources)
            {
                if (s == null) continue;
                s.fieldUV = new Vector2(Mathf.Clamp01(s.fieldUV.x), Mathf.Clamp01(s.fieldUV.y));
                s.channel = Mathf.Clamp(s.channel, 0, BiomeChannel.Count - 1);
            }
        }

        void OnDrawGizmosSelected()
        {
            if (!drawGizmos || sources == null) return;
            // The biome is a normalized [0,1] field. Draw it as a unit square on this
            // object's local XY plane — purely a placement aid; fieldUV is the real map.
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 1f, 1f, 0.25f);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(1f, 1f, 0f)); // [0,1]² centered at origin
            foreach (var s in sources)
            {
                if (s == null) continue;
                Gizmos.color = s.enabled ? gizmoColor : new Color(0.5f, 0.5f, 0.5f, 0.4f);
                Gizmos.DrawWireSphere(new Vector3(s.fieldUV.x - 0.5f, s.fieldUV.y - 0.5f, 0f), s.radius);
            }
            Gizmos.matrix = Matrix4x4.identity;
#if UNITY_EDITOR
            foreach (var s in sources)
            {
                if (s == null) continue;
                Vector3 wc = transform.TransformPoint(new Vector3(s.fieldUV.x - 0.5f, s.fieldUV.y - 0.5f, 0f));
                UnityEditor.Handles.Label(wc, $"{s.name} → ch{s.channel}");
            }
#endif
        }
    }
}

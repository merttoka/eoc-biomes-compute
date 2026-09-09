using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using EasyButtons;

namespace Biomes
{
    /// <summary>Lifecycle of one sim under SimulationManager's per-tick loop. Stopped sims
    /// are skipped by every manager loop and composited as black; Fading sims run
    /// FadeStep() (trail decay + render, no agent motion, no biome write-back) until the
    /// fade window expires and they drop to Stopped.</summary>
    public enum SimRunState { Stopped, Running, Fading }

    /// <summary>Where agents (re)spawn on Reset. Firing coupling is by agent INDEX
    /// (see neuron_firing.hlsl), so neuron-driven behavior is identical in every mode —
    /// only the birth geometry changes.</summary>
    public enum SpawnMode { NeuronPositions = 0, BottomEdge = 1, Scatter = 2, TopEdge = 3 }

    public abstract class SimulationBase : MonoBehaviour
    {
        [Header("Setup")]
        public ComputeShader cs;
        public Material outputMat;

        [Tooltip("Weight of this sim's output in the final additive composite (1 = full). Lower a dense sim (e.g. physarum) so it stops saturating the canvas and drowning the others.")]
        [Range(0f, 4f)] public float compositeWeight = 1f;

        [Tooltip("Per-frame retention of the rendered output (was hardcoded 0.9). Raising toward 0.95-0.98 makes trails linger and fill the canvas — the main lever to keep the dense look with fewer agents.")]
        [Range(0.5f, 0.995f)] public float renderPersistence = 0.9f;

        [Header("Run Control")]
        [Tooltip("Start this sim when the manager resets (play-mode entry / global Reset). " +
                 "Off = the sim stays unallocated and off the canvas until a Start button/API call.")]
        public bool startOnPlay = true;
        [Tooltip("Seconds a Stop takes to fade the sim off the canvas. Agent sims keep their " +
                 "trail diffusion running so trails dissolve organically (diffuseRate < 1 " +
                 "decays them each step) while agents freeze and biome deposits stop; the " +
                 "composite weight eases out over the final quarter as a floor for decay-less " +
                 "presets (diffuseRate 1 conserves trail mass and would never vanish). Field " +
                 "sims fade by composite weight over the whole window. 0 = instant cut.")]
        [Range(0f, 30f)] public float fadeOutSeconds = 6f;

        /// <summary>Run gate, owned by SimulationManager (Start/Stop buttons + Reset). Not
        /// serialized: every play-mode entry starts from Stopped and startOnPlay decides.</summary>
        [NonSerialized] public SimRunState runState = SimRunState.Stopped;
        /// <summary>Sim-time seconds left in the Fading state (counted down by the manager).</summary>
        [NonSerialized] public float fadeRemaining;

        [Header("Spawn")]
        [Tooltip("NeuronPositions = the organoid layout (default; random scatter when no CSV is " +
                 "wired; spawns landing inside a keep-out rect are evicted to its nearest open " +
                 "edge). BottomEdge/TopEdge = a band along that canvas edge with headings coned " +
                 "toward the canvas interior — for thin/wide strips where the organoid layout " +
                 "makes no spatial sense. Scatter = uniform random. Firing is index-mapped, so " +
                 "neuron coupling behaves identically in every mode. Takes effect on Reset.")]
        public SpawnMode spawnMode = SpawnMode.NeuronPositions;
        [Tooltip("BottomEdge/TopEdge only: spawn band height as a fraction of canvas height.")]
        [Range(0.01f, 0.5f)] public float spawnBandFraction = 0.08f;

        // Keep-out rects (normalized xMin,yMin,xMax,yMax; up to 4 used), pushed by
        // SimulationManager via ConfigureAndReset — single owner, like neuronSpawnScale.
        // Spawns resample to land clear of them (see spawn.hlsl).
        [NonSerialized] public Vector4[] keepOutRects;
        [NonSerialized] public int keepOutCount;
        [NonSerialized] public float keepOutFeather;
        private static readonly Vector4[] s_NoKeepOut = new Vector4[4];

        // Dispersal speed response — shared by all sims (consumes perception.a = SpeedBoost).
        public enum DispersalSpeedMode { Multiplier = 0, Constant = 1 }
        [Header("Dispersal speed response")]
        [Tooltip("Constant = snap toward a fixed flee speed (fast reaction even at low base speed). Multiplier = scale current speed up with local dispersal.")]
        public DispersalSpeedMode dispersalSpeedMode = DispersalSpeedMode.Constant;
        [Tooltip("Multiplier mode gain: speed *= 1 + dispersal*mult.")]
        [Range(0f, 20f)] public float dispersalSpeedMult = 4f;
        [Tooltip("Constant mode target flee speed (agents snap toward this as dispersal→1).")]
        [Range(0f, 50f)] public float dispersalConstantSpeed = 6f;

        // Scale of the perception texture relative to sim resolution (set by
        // SimulationManager before Reset). Perception is built from the low-res biome
        // field and sampled by UV, so it can be much smaller than the sim canvas.
        [NonSerialized] public float perceptionResScale = 1f;

        [HideInInspector] public int rezX = 1024;
        [HideInInspector] public int rezY = 1024;
        // Resolution-independence (pushed by SimulationManager). Pixel-unit params (speed,
        // ranges, sensor distance) and trail density (deposit/eat) are authored at
        // referenceHeight and rescaled by rezY/referenceHeight on Reset, so motion + density
        // read the same across output resolutions. See ResolutionScale + ScaleSpatial/ScaleDensity.
        [HideInInspector] public float referenceHeight = 2160f;
        [HideInInspector] public bool scaleSpatialToResolution = true;
        [HideInInspector] public bool scaleDensityToResolution = true;

        [Header("Biome Integration")]
        public UmweltMapping umwelt;

        // Perception texture: biome fields filtered through Umwelt (built by Biome each frame)
        // R=chemotaxis, G=speed multiplier, B=avoidance, A=speed boost (Dispersal)
        [NonSerialized] public RenderTexture perceptionTex;

        // External influence texture (assigned by SimulationManager from ExternalTextureReceiver)
        [NonSerialized] public Texture externalInfluenceTex;

        // Shared neuron firing (assigned by SimulationManager from NeuronFiringSource)
        [NonSerialized] public ComputeBuffer neuronFiring;
        [NonSerialized] public int neuronFiringCount;
        // CPU-side aggregate of the same source, broadcast alongside the buffer. Field sims
        // detect a rising edge on this to auto-trigger a burst; reading it CPU-side avoids a
        // GPU readback and its sync point.
        [NonSerialized] public float neuronIntensity;
        // Current playback frame from NeuronFiringSource; -1 = none yet. Field sims can
        // treat a frame ADVANCE as a burst trigger (see burstOnFrameAdvance) because a
        // dense /index stream pins the intensity high and starves the rising edge.
        [NonSerialized] public int neuronFrame = -1;
        // Aggregate strength of the current playback frame (0..1): the frame's mean firing
        // value, normalized to the loaded recording's own peak frame mean (see
        // NeuronFiringSource.FrameActivity). Gates frame-advance bursts via
        // burstFiringThreshold so a dense stream only ignites on strong/synchronous frames.
        [NonSerialized] public float neuronFrameActivity;

        [Header("Trail Diffusion (> 0 = GPU cost, fps warning)")]
        [Tooltip("Coherence-enhancing trail diffusion: elongate each cell's blur along the " +
                 "local trail ridge (from the trail's own structure tensor), so a moving agent " +
                 "leaves a crisp comet tail instead of a round bloom — at 1, trail width " +
                 "roughly halves. Round blobs and trail crossings stay isotropic by " +
                 "construction. 0 = legacy isotropic box blur, and FREE — the tensor is " +
                 "skipped entirely. Any value > 0 pays a 25-tap tensor per pixel per sim " +
                 "step; at high sim rez that can blow the 60 Hz budget and cascade to " +
                 "single-digit fps via fixed-timestep catch-up.")]
        [Range(0f, 1f)] public float trailAnisotropy = 0f;

        [Header("Neuron Firing")]
        [Range(0f, 1f)] public float firingThreshold = 0.1f;
        private ComputeBuffer dummyNeuronFiringBuffer;

        // Neuron seed positions, normalized 0..1, pushed by SimulationManager from
        // NeuronFiringSource; sims convert to their own pixel space (see
        // BuildNeuronPositions). Not serialized and not authored per sim — NeuronFiringSource
        // is the single CSV owner (see the 2026-08-12 single-owner refactor).
        [NonSerialized] public IReadOnlyList<Vector2> neuronPositionsNorm;
        // Neuron layout scale, pushed by SimulationManager from NeuronFiringSource.spawnScale
        // (the single authored copy). Not serialized and not authored per sim: three
        // independent copies of this value silently desynced in 11.2 and 11.3.
        // Falls back to the old default when no NeuronFiringSource is wired.
        [NonSerialized] public Vector2 neuronSpawnScale = NeuronLayout.DefaultScale;
        protected ComputeBuffer neuronPositionsBuffer;
        protected ComputeBuffer dummyNeuronBuffer;

        // Trail texture array: layers 0..typeCount-1 = per-type, layer typeCount = total
        protected RenderTexture trailReadArray;
        protected RenderTexture trailWriteArray;
        protected RenderTexture outTex;

        protected GPUResourceManager gpu;

        // Clear-in-place: allocation signature. Reset() reuses the existing GPU resources
        // and only re-clears/respawns; it reallocates — which changes the outTex instance
        // and so forces a Syphon server teardown downstream — ONLY when one of these
        // changes (resolution, perception scale, type count, or agent count).
        int _allocRezX = -1, _allocRezY = -1, _allocTypeCount = -1, _allocAgentCount = -1;
        float _allocPerceptionScale = float.NaN;

        // Neuron seed positions depend on rez + the pushed neuronPositionsNorm list, so they
        // are uploaded once per allocation (or after InvalidateNeuronPositions — e.g. a
        // runtime CSV swap on NeuronFiringSource) and merely rebound on each clear-in-place
        // reset. Previously BuildNeuronPositions re-parsed the CSV and leaked a fresh buffer
        // every reset (tracked, freed only at the next full Release).
        bool _neuronPositionsBuilt;
        int _neuronPositionsCount;
        bool _warnedNoNeuronPositions;

        // Common kernel handles
        protected int resetTexKernel;
        protected int resetAgentsKernel;
        protected int moveAgentsKernel;
        protected int writeTrailsKernel;
        protected int diffuseTextureKernel;
        protected int renderKernel;

        protected abstract int TypeCount { get; }

        #region Shader Property IDs
        protected static readonly int s_RezXID = Shader.PropertyToID("rezX");
        protected static readonly int s_RezYID = Shader.PropertyToID("rezY");
        protected static readonly int s_TimeID = Shader.PropertyToID("time");
        protected static readonly int s_TrailReadID = Shader.PropertyToID("trailRead");
        protected static readonly int s_TrailWriteID = Shader.PropertyToID("trailWrite");
        protected static readonly int s_OutTexID = Shader.PropertyToID("outTex");
        protected static readonly int s_AgentsCountID = Shader.PropertyToID("agentsCount");
        protected static readonly int s_AgentsInID = Shader.PropertyToID("agentsIn");
        protected static readonly int s_AgentsOutID = Shader.PropertyToID("agentsOut");
        protected static readonly int s_TypeParamsID = Shader.PropertyToID("typeParams");
        protected static readonly int s_TypeCountID = Shader.PropertyToID("typeCount");
        protected static readonly int s_PerceptionTexID = Shader.PropertyToID("perceptionTex");
        protected static readonly int s_NeuronFiringID = Shader.PropertyToID("neuronFiring");
        protected static readonly int s_NeuronFiringCountID = Shader.PropertyToID("neuronFiringCount");
        protected static readonly int s_FiringThresholdID = Shader.PropertyToID("firingThreshold");
        protected static readonly int s_NeuronPositionsID = Shader.PropertyToID("neuronPositions");
        protected static readonly int s_NeuronCountID = Shader.PropertyToID("neuronCount");
        protected static readonly int s_NeuronScaleID = Shader.PropertyToID("neuronScale");
        protected static readonly int s_PersistenceID = Shader.PropertyToID("persistence");
        protected static readonly int s_SpawnModeID = Shader.PropertyToID("spawnMode");
        protected static readonly int s_SpawnBandFractionID = Shader.PropertyToID("spawnBandFraction");
        protected static readonly int s_KeepOutCountID = Shader.PropertyToID("keepOutCount");
        protected static readonly int s_KeepOutRectsID = Shader.PropertyToID("keepOutRects");
        protected static readonly int s_KeepOutFeatherID = Shader.PropertyToID("keepOutFeather");
        protected static readonly int s_TrailAnisoID = Shader.PropertyToID("trailAnisotropy");
        protected static readonly int s_TrailTensorStrideID = Shader.PropertyToID("trailTensorStride");
        #endregion

        public abstract string SimName { get; }

        // IControllableSim interface
        public abstract IReadOnlyList<string> ModulatableParams { get; }
        public abstract void SetParameter(string paramName, int index, float value);
        public abstract void SetParameterDelta(string paramName, int index, float delta);
        public abstract float GetParameter(string paramName, int index);

        /// <summary>Live runtime params (agentParams) exposed for interpolation.</summary>
        public abstract IParamSet LiveParamSet { get; }

        /// <summary>The assigned preset asset (paramsSO) that LiveParamSet was cloned from.</summary>
        public abstract ScriptableObject PresetParamSet { get; }

        /// <summary>Editor-only: copy the current live params back into the assigned preset
        /// asset in place (overwrites it; no new snapshot file). Returns true if written.
        /// Caller batches AssetDatabase.SaveAssets() after looping sims.</summary>
        public bool SaveLiveParamsToPreset()
        {
#if UNITY_EDITOR
            var live = LiveParamSet as ScriptableObject;
            var preset = PresetParamSet;
            if (live == null || preset == null) return false;
            string presetName = preset.name;                        // CopySerialized would stamp "(Clone)";
            UnityEditor.EditorUtility.CopySerialized(live, preset);  // copy all tuned fields into the asset
            preset.name = presetName;                               // restore the asset's name
            UnityEditor.EditorUtility.SetDirty(preset);
            return true;
#else
            return false;
#endif
        }

        // ── Media-agent behavior multipliers (topology-B OSC bridge) ──────────────
        // Non-destructive per-sim global multipliers driven by the media-agent's
        // /sn/<entity>/behavior/{speed,trail,sensor,cohesion} leaves. Applied ONLY in each
        // sim's UploadTypeParams into the transient type-params cache each frame — never
        // written back into serialized agentParams (so authored presets are never clobbered
        // and values never compound). Neutral bias (behaviorMulNeutral) → multiplier 1.0.
        [Header("Media-agent behavior multipliers")]
        [Tooltip("bias 0 → this multiplier (agent slows / thins its trail / narrows its cone).")]
        [Range(0.01f, 1f)] public float behaviorMulMin = 0.25f;
        [Tooltip("bias 1 → this multiplier (agent speeds up / thickens its trail / widens its cone).")]
        [Range(1f, 8f)] public float behaviorMulMax = 4f;
        [Tooltip("bias value that maps to multiplier 1.0 (identity — no change).")]
        [Range(0f, 1f)] public float behaviorMulNeutral = 0.5f;

        // Live multipliers, written off-thread by SetBehaviorMultiplier, read on the main
        // thread in each sim's UploadTypeParams. volatile so the socket-thread write is
        // visible promptly on the main thread.
        protected volatile float behSpeedMul = 1f, behTrailMul = 1f, behSensorMul = 1f, behCohesionMul = 1f;

        public enum BehaviorLeaf { Speed, Trail, Sensor, Cohesion }

        /// <summary>Set a non-destructive global behavior multiplier from a 0..1 bias (a
        /// media-agent /sn/&lt;entity&gt;/behavior/* leaf). Neutral bias (behaviorMulNeutral,
        /// default 0.5) → 1.0; below neutral lerps toward behaviorMulMin, above toward
        /// behaviorMulMax. Thread-safe: writes only a volatile float (no Unity API), so it is
        /// safe to call from the OSC socket thread — same contract as BiomeInjector.SetValue.
        /// Consumed on the main thread in UploadTypeParams; never written into agentParams.</summary>
        public void SetBehaviorMultiplier(BehaviorLeaf leaf, float bias01)
        {
            float mul = BiasToMultiplier(bias01);
            switch (leaf)
            {
                case BehaviorLeaf.Speed:    behSpeedMul    = mul; break;
                case BehaviorLeaf.Trail:    behTrailMul    = mul; break;
                case BehaviorLeaf.Sensor:   behSensorMul   = mul; break;
                case BehaviorLeaf.Cohesion: behCohesionMul = mul; break;
            }
        }

        // Map a 0..1 bias to a multiplier around neutral: bias==neutral → 1.0, bias→0 →
        // behaviorMulMin, bias→1 → behaviorMulMax (two-sided lerp so neutral is exactly identity).
        float BiasToMultiplier(float bias01)
        {
            float b = Mathf.Clamp01(bias01);
            float n = Mathf.Clamp01(behaviorMulNeutral);
            if (b <= n)
                return n <= 0f ? 1f : Mathf.Lerp(behaviorMulMin, 1f, b / n);
            return n >= 1f ? 1f : Mathf.Lerp(1f, behaviorMulMax, (b - n) / (1f - n));
        }

        protected abstract void InitBuffers();
        protected abstract void GPUReset();
        protected abstract void GPUStep();
        protected abstract void Render();
        protected virtual void InitSimKernels() { }

        public RenderTexture GetOutputTexture() => outTex;

        /// <summary>Returns the agent position buffer for biome write-back.</summary>
        public abstract ComputeBuffer GetAgentPositionBuffer();
        public abstract int GetAgentCount();

        public void SetResolution(int x, int y)
        {
            rezX = x;
            rezY = y;
        }

        // Wrapped sim-step counter fed to shaders as `time`. Keeps (float)time small so
        // RNG seeds (e.g. time*0.001 + id*0.0001, sin(time)) keep per-agent precision over
        // long installation runs — a raw monotonic counter degrades them within hours.
        // Sourced from the sim step, NOT Time.frameCount: consecutive Step()s in one
        // render frame (catch-up on slow HW, or stepsPerTick>1) must get distinct seeds
        // so the sim advances identically regardless of frame pacing. Wraps every 65536
        // steps (~18 min @60Hz); the one-step discontinuity at wrap is imperceptible.
        protected const int TimeWrap = 65536;
        private int _simStep;
        protected int WrappedStep => _simStep % TimeWrap;

        // True when the live resolution/scale/counts differ from what GPU resources were
        // last allocated for (or nothing is allocated yet). Drives clear-in-place: a normal
        // reset keeps the same instances; only a genuine size change reallocates.
        // Virtual so a subclass with extra allocation keys (FieldSimulationBase's cell
        // resolution) can widen the test without duplicating the agent-side signature.
        protected virtual bool NeedsAllocation() =>
            gpu == null
            || rezX != _allocRezX || rezY != _allocRezY
            || TypeCount != _allocTypeCount
            || GetAgentCount() != _allocAgentCount
            || !Mathf.Approximately(perceptionResScale, _allocPerceptionScale);

        // Agent count the GPU agent buffers are currently sized for. A live edit to a sim's
        // agent count flags NeedsAllocation, so it only takes effect on the next Reset();
        // until then per-step dispatches MUST clamp to this, not the live field, or they index
        // past the allocated buffers (out-of-bounds GPU access — the boid live-slider bug).
        // -1 before the first Allocate(), but Step() only runs post-Reset so it's always set.
        protected int AllocatedAgentCount => _allocAgentCount;

        // rezY / referenceHeight — 1 at the reference height, <1 shorter, >1 taller. Pixel-unit
        // params are multiplied by this on Reset (LiveParamSet.ScaleSpatial/ScaleDensity) so
        // authored values ground at referenceHeight and read the same across output resolutions.
        protected float ResolutionScale =>
            (referenceHeight > 0f && rezY > 0) ? rezY / referenceHeight : 1f;

        [Button]
        public virtual void Reset()
        {
            _simStep = 0;

            // Resolution-independence: rescale the freshly-cloned pixel-unit params. LiveParamSet
            // is the runtime clone the concrete Reset set (via Instantiate) before calling base —
            // re-cloned from the pristine asset each Reset, so scaling never compounds and never
            // touches the on-disk asset. Applied before GPUReset→UploadTypeParams reads them.
            if (LiveParamSet != null)
            {
                float k = ResolutionScale;
                if (scaleSpatialToResolution) LiveParamSet.ScaleSpatial(k);
                if (scaleDensityToResolution) LiveParamSet.ScaleDensity(k);
            }

            if (NeedsAllocation())
                Allocate();
            GPUReset();                                 // clear trails + outTex, respawn agents
            cs.SetFloat(s_PersistenceID, renderPersistence);
            Render();
        }

        // Allocate (or reallocate) all GPU resources for the current resolution. Called by
        // Reset() only when the allocation signature changes; otherwise resources persist
        // across resets so the streamed/displayed outTex keeps the same instance.
        protected virtual void Allocate()
        {
            Release();
            gpu = new GPUResourceManager();

            int layers = TypeCount + 1;
            trailReadArray = CreateTrailArray(layers, SimName + "_trailRead");
            trailWriteArray = CreateTrailArray(layers, SimName + "_trailWrite");
            // ARGBHalf (8 B/px) instead of ARGBFloat (16 B/px): output color is saturated
            // 0..1 so half precision is ample, and it halves bandwidth on the per-pixel
            // render/composite/Syphon path — the dominant memory traffic at 2×FHD.
            outTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Trilinear,
                RenderTextureFormat.ARGBHalf, SimName + "_out");

            // Perception texture (populated by Biome). RGBA carry chemotaxis/speed/avoidance/speed-boost,
            // all in 0..1 — half precision is plenty and halves the per-frame read cost in
            // every sim's MoveAgents kernel (the hottest sampler in the project).
            // Built from the low-res biome field and read by UV everywhere, so it can be
            // smaller than the sim canvas (perceptionResScale, set by SimulationManager).
            int pw = Mathf.Max(8, Mathf.RoundToInt(rezX * Mathf.Clamp(perceptionResScale, 0.05f, 1f)));
            int ph = Mathf.Max(8, Mathf.RoundToInt(rezY * Mathf.Clamp(perceptionResScale, 0.05f, 1f)));
            perceptionTex = gpu.CreateTexture2D(pw, ph, FilterMode.Bilinear,
                RenderTextureFormat.ARGBHalf, SimName + "_perception");

            resetTexKernel = cs.FindKernel("ResetTextureKernel");
            resetAgentsKernel = cs.FindKernel("ResetAgentsKernel");
            moveAgentsKernel = cs.FindKernel("MoveAgentsKernel");
            writeTrailsKernel = cs.FindKernel("WriteTrailsKernel");
            diffuseTextureKernel = cs.FindKernel("DiffuseTextureKernel");
            renderKernel = cs.FindKernel("RenderKernel");

            InitSimKernels();
            InitBuffers();

            MarkAllocated();
        }

        /// <summary>
        /// Stamp the clear-in-place allocation signature, recording what the GPU resources
        /// were just built for. MUST be called at the end of every Allocate() override —
        /// a subclass that allocates without stamping leaves NeedsAllocation() permanently
        /// true, so every Reset() reallocates, the outTex instance changes, and downstream
        /// Syphon servers tear down and re-announce on each reset (the failure ADR-0008
        /// exists to prevent). The fields themselves stay private so the signature can only
        /// be written here, in one place.
        /// </summary>
        protected void MarkAllocated()
        {
            _allocRezX = rezX; _allocRezY = rezY;
            _allocTypeCount = TypeCount; _allocAgentCount = GetAgentCount();
            _allocPerceptionScale = perceptionResScale;
        }

        public virtual void Step()
        {
            _simStep++;
            cs.SetInt(s_TimeID, WrappedStep);
            cs.SetFloat(s_PersistenceID, renderPersistence);
            GPUStep();
            SwapTrailArrays();
            Render();
        }

        /// <summary>
        /// One fade tick (runState == Fading): the diffuse kernel keeps running so trails
        /// decay toward black exactly as they do behind a live agent (blur × diffuseRate per
        /// step), and Render keeps draining outTex via persistence — but agents do not move
        /// and nothing deposits, so the picture dissolves instead of cutting. Field sims
        /// override to a no-op: no trail arrays, their frozen output fades by composite
        /// weight alone (see FadeWeight).
        /// </summary>
        public virtual void FadeStep()
        {
            _simStep++;
            cs.SetInt(s_TimeID, WrappedStep);
            cs.SetFloat(s_PersistenceID, renderPersistence);
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            BindFadeParams();
            BindTrailAnisotropy();
            cs.SetTexture(diffuseTextureKernel, s_TrailReadID, trailReadArray);
            cs.SetTexture(diffuseTextureKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(diffuseTextureKernel, rezX, rezY, 1);
            SwapTrailArrays();
            Render();
        }

        /// <summary>Re-bind what the diffuse/render kernels read beyond the base uniforms —
        /// each agent sim's typeParams buffer (diffuseRate lives there). Called every
        /// FadeStep, mirroring the per-step UploadTypeParams the normal GPUStep does.</summary>
        protected virtual void BindFadeParams() { }

        /// <summary>Composite-weight multiplier while Fading (fade01 runs 1 → 0). Agent sims
        /// hold full weight for the first three quarters — the visible fade is the trail
        /// decay — then ease to 0 as a floor for decay-less presets. Field sims override to
        /// a full-window smooth fade.</summary>
        public virtual float FadeWeight(float fade01) => Mathf.Min(1f, fade01 * 4f);

        protected RenderTexture CreateTrailArray(int layers, string name)
        {
            return gpu.CreateTextureArray(rezX, rezY, layers, FilterMode.Point,
                RenderTextureFormat.RHalf, name);
        }

        protected void Dispatch(int kernel, int x, int y, int z)
        {
            cs.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out uint wz);
            cs.Dispatch(kernel,
                Mathf.CeilToInt((float)x / wx),
                Mathf.CeilToInt((float)y / wy),
                Mathf.CeilToInt((float)z / wz));
        }

        protected void SwapTrailArrays()
        {
            (trailReadArray, trailWriteArray) = (trailWriteArray, trailReadArray);
        }

        protected void ResetTrailArrays()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetTexture(resetTexKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(resetTexKernel, rezX, rezY, 1);
            cs.SetTexture(resetTexKernel, s_TrailWriteID, trailReadArray);
            Dispatch(resetTexKernel, rezX, rezY, 1);
            var prev = RenderTexture.active;
            RenderTexture.active = outTex;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        /// <summary>Push the trail-anisotropy knob (orientation is derived in-kernel from
        /// the trail's own structure tensor, so there is nothing to bind — just uniforms).
        /// Call each GPUStep, alongside the other per-step binds.</summary>
        protected void BindTrailAnisotropy()
        {
            cs.SetFloat(s_TrailAnisoID, trailAnisotropy);
            // Texel spacing of the structure-tensor sample window, scaled like every
            // other spatial param (rezY/referenceHeight): 3 texels at the 2160
            // reference, never below 1. A fixed 5-texel window reads a wide trail's
            // flat core as "no orientation" (coherence ~0 -> isotropic), which muted
            // the whole effect on boid-scale trails.
            cs.SetInt(s_TrailTensorStrideID, Mathf.Max(1, Mathf.RoundToInt(3f * ResolutionScale)));
        }

        protected void BindPerceptionTex(params int[] kernels)
        {
            Texture tex = perceptionTex;
            foreach (int k in kernels)
                cs.SetTexture(k, s_PerceptionTexID, tex);
        }

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

        // Bind the shared dispersal speed-response params (consumed via includes/dispersal_speed_response.hlsl).
        protected void BindDispersalSpeedParams()
        {
            cs.SetInt("dispersalSpeedMode", (int)dispersalSpeedMode);
            cs.SetFloat("dispersalSpeedMult", dispersalSpeedMult);
            cs.SetFloat("dispersalConstantSpeed", dispersalConstantSpeed);
        }

        /// <summary>Force the next BuildNeuronPositions to re-upload from
        /// neuronPositionsNorm (SimulationManager calls this after pushing a different
        /// list — e.g. a runtime CSV swap on NeuronFiringSource). Does not touch GPU
        /// resources itself; the next reset kernel bind reallocates/refills as needed.</summary>
        public void InvalidateNeuronPositions() => _neuronPositionsBuilt = false;

        // Consume neuronPositionsNorm (pushed by SimulationManager from NeuronFiringSource),
        // upload into this sim's pixel space, bind to the given reset kernel, and set
        // neuronCount/neuronScale globals. Returns the neuron count (0 => the reset kernel
        // should random-scatter).
        protected int BuildNeuronPositions(int resetKernel)
        {
            if (dummyNeuronBuffer == null)
            {
                dummyNeuronBuffer = gpu.CreateBuffer(1, sizeof(float) * 2);
                dummyNeuronBuffer.SetData(new Vector2[1] { Vector2.zero });
            }

            // Upload once per allocation (or after InvalidateNeuronPositions). On a
            // clear-in-place reset the buffer already exists, so we skip straight to the
            // rebind below. Release() clears _neuronPositionsBuilt.
            if (!_neuronPositionsBuilt)
            {
                _neuronPositionsCount = 0;
                if (neuronPositionsNorm != null && neuronPositionsNorm.Count > 0)
                {
                    var positions = new Vector2[neuronPositionsNorm.Count];
                    for (int i = 0; i < positions.Length; i++)
                        positions[i] = new Vector2(
                            neuronPositionsNorm[i].x * rezX, neuronPositionsNorm[i].y * rezY);

                    _neuronPositionsCount = positions.Length;
                    neuronPositionsBuffer = gpu.CreateBuffer(_neuronPositionsCount, sizeof(float) * 2);
                    neuronPositionsBuffer.SetData(positions);
                }
                else if (!_warnedNoNeuronPositions)
                {
                    Debug.LogWarning($"{SimName}: no neuron positions — no NeuronFiringSource wired to " +
                                      "SimulationManager (or its labelsPositionsCsv is unset); random-scattering agents");
                    _warnedNoNeuronPositions = true;
                }
                _neuronPositionsBuilt = true;
            }

            cs.SetBuffer(resetKernel, s_NeuronPositionsID,
                _neuronPositionsCount > 0 ? neuronPositionsBuffer : dummyNeuronBuffer);
            cs.SetInt(s_NeuronCountID, _neuronPositionsCount);
            cs.SetVector(s_NeuronScaleID, new Vector4(neuronSpawnScale.x, neuronSpawnScale.y, 0, 0));

            // Spawn mode + keep-out exclusion for the reset kernel (spawn.hlsl /
            // keepout.hlsl). Bound here because this is the one bind point every sim's
            // reset already funnels through. No-ops on shaders without the uniforms
            // (the field sims bind this to their rule kernel instead).
            cs.SetInt(s_SpawnModeID, (int)spawnMode);
            cs.SetFloat(s_SpawnBandFractionID, spawnBandFraction);
            cs.SetInt(s_KeepOutCountID, keepOutCount);
            cs.SetVectorArray(s_KeepOutRectsID, keepOutRects ?? s_NoKeepOut);
            cs.SetFloat(s_KeepOutFeatherID, keepOutFeather);
            return _neuronPositionsCount;
        }

        public virtual void Release()
        {
            gpu?.ReleaseAll();
            gpu = null;
            trailReadArray = null;
            trailWriteArray = null;
            perceptionTex = null;
            dummyNeuronFiringBuffer = null;
            neuronPositionsBuffer = null;
            dummyNeuronBuffer = null;
            _neuronPositionsBuilt = false;
            // Force the next Reset() to reallocate (gpu == null already does, but keep the
            // signature honest so a stale memo can never skip a needed allocation).
            _allocRezX = _allocRezY = _allocTypeCount = _allocAgentCount = -1;
            _allocPerceptionScale = float.NaN;
        }

        void OnDisable() => Release();
        void OnDestroy() => Release();

        [Button("Export as PNG")]
        public void ExportPNG()
        {
            if (outTex == null) return;
            string path = System.IO.Path.Combine(PngExport.Dir("Exports/Figures"),
                $"{SimName}-{DateTime.Now:yyyyMMdd_HHmmss}.png");
            PngExport.Save(outTex, path);   // sRGB-encoded, matches the screen
            Debug.Log($"[{SimName}] Exported → {path}");
        }

        protected float MapAndClamp(float value, float minValue, float maxValue, float min = 0, float max = 1)
        {
            float mapped = (value - min) / (max - min) * (maxValue - minValue) + minValue;
            return Mathf.Clamp(mapped, minValue, maxValue);
        }

        protected float ClampDelta(float field, float delta, float minValue, float maxValue)
        {
            float value = field + delta * (maxValue - minValue);
            return Mathf.Clamp(value, minValue, maxValue);
        }
    }
}

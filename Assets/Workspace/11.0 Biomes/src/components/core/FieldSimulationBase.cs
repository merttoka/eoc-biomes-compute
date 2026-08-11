using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Base for <b>field-native</b> sims — grid processes with no agents, whose entire state
    /// is one integer per cell. Cellular automata are the first family; reaction-diffusion is
    /// the obvious next one.
    ///
    /// <para><b>Why it derives from SimulationBase rather than sitting beside it.</b>
    /// <see cref="SimulationManager"/> holds a <c>List&lt;SimulationBase&gt;</c> and already
    /// null-guards the agent path in both places it matters — the write-back loop skips a sim
    /// whose position buffer is null, and the perception build skips a sim with no umwelt or
    /// perception texture. So a zero-agent sim needs no orchestrator special-casing at all: it
    /// answers <c>null</c>/<c>0</c> to the agent contract and the existing guards route around
    /// it. A parallel base plus an interface for the manager to iterate would have bought
    /// nothing and cost a manager rewrite.</para>
    ///
    /// <para><b>What it replaces from the agent base.</b> Trail texture arrays, agent buffers
    /// and the Move/WriteTrails/Diffuse kernels are all skipped: <see cref="Allocate"/> is
    /// overridden wholesale, so a field sim's compute shader is never asked for kernels it
    /// does not have. What it keeps is everything that is genuinely shared — composite weight
    /// and the additive 8-layer composite, IControllableSim string params (so MIDI/OSC binding
    /// is free), neuron-firing binding, and clear-in-place reset discipline.</para>
    ///
    /// <para><b>Double buffering is mandated here, not left to subclasses.</b> The ported CCA
    /// already ping-ponged, but the ported CA2D bound a single texture as both read and write
    /// — an in-place update where threads read neighbours other threads have already
    /// overwritten. That is a data race and not a synchronous CA. Owning
    /// <see cref="stateRead"/>/<see cref="stateWrite"/> at this level makes the correct
    /// structure the only structure a subclass can express.</para>
    /// </summary>
    public abstract class FieldSimulationBase : SimulationBase
    {
        [Header("Cellular field")]
        [Tooltip("State-grid HEIGHT in cells, absolute. Width follows the master's aspect. " +
                 "Absolute rather than a fraction of master so the automaton's on-screen cell " +
                 "size does not move when output resolution does — 1080p and 4K give the same " +
                 "picture, upscaled differently by the composite. On very wide canvases width " +
                 "scales with the aspect, so lower this there. Takes effect on Reset.")]
        [Range(64, 2048)] public int cellRezHeight = 540;

        [Tooltip("Run the rule only every Nth sim step. CA patterns read as slow structure; " +
                 "decimating the rule decouples its pace from the 60 Hz agent clock without " +
                 "touching sim rate. 1 = every step.")]
        [Range(1, 16)] public int stepEvery = 1;

        [Header("Biome channel publish (CA as substrate)")]
        [Tooltip("Optional. When set, the normalized state is published into a biome channel " +
                 "each step, so agent sims perceive the CA through UmweltMapping with no change " +
                 "to their shaders. Leave null to run as a pure composite layer.")]
        public Biome publishTarget;
        [BiomeChannelField]
        [Tooltip("Channel the state is published into. Give the CA its own channel and leave the " +
                 "PDE off it (diffuseRate 0, relaxRate 0) unless you want the pattern to bleed.")]
        public int publishChannel = BiomeChannel.Excitability;
        [Tooltip("Scales the normalized state before blending into the channel.")]
        [Range(0f, 4f)] public float publishGain = 1f;
        [Tooltip("SetToward overwrites the channel with the CA state (CA owns the channel). " +
                 "MaxToward lets other writers only raise it. Additive accumulates.")]
        public BiomeInjector.BlendMode publishMode = BiomeInjector.BlendMode.SetToward;

        [Header("Seed from a biome channel")]
        [Tooltip("Seed the grid from a biome channel instead of the rule's own figure, so the " +
                 "automaton grows out of what the ecosystem is actually doing. Uses the same " +
                 "Biome as publishTarget. Off = the rule's built-in seeding.")]
        public bool seedFromChannel = false;
        [BiomeChannelField]
        [Tooltip("Channel sampled at reset. Read by UV, so the biome and the CA grid need not " +
                 "share dimensions.")]
        public int seedChannel = BiomeChannel.Pheromone0;
        [Tooltip("Cells at or above this normalized channel value are seeded live. Keep the seed " +
                 "SPARSE — a lookup CA forces the all-quiescent neighbourhood to stay quiescent, " +
                 "so scattered seeds grow fronts while a dense seed just boils on step one.")]
        [Range(0f, 1f)] public float seedThreshold = 0.5f;

        [Header("CA-CA coupling")]
        [Tooltip("Optional. Another field sim whose state gates this one — this rule only " +
                 "advances where the other CA is 'alive'. Pointing the cyclic CA at the lookup " +
                 "CA confines excitation waves inside the standing lattice, so structure and " +
                 "flow become one coupled system rather than two stacked layers.")]
        public FieldSimulationBase couplingSource;
        [Tooltip("0 = ignore the coupling source entirely, 1 = fully gated by it. Interpolatable, " +
                 "so the coupling can be brought in over a show arc.")]
        [Range(0f, 1f)] public float couplingStrength = 1f;
        [Tooltip("Coupling source cells at or above this normalized value count as 'alive'.")]
        [Range(0f, 1f)] public float couplingThreshold = 0.5f;
        [Tooltip("Invert the gate: advance only where the coupling source is DEAD (the CA flows " +
                 "around the lattice instead of through it).")]
        public bool couplingInvert = false;

        [Header("Neuron ignition")]
        [Tooltip("A firing neuron perturbs the state at its canvas position, tying the CA to the " +
                 "same playback clock as the diurnal sun and dispersal pulses. 0 = off.")]
        [Range(0f, 1f)] public float firingIgnitionStrength = 0f;
        [Tooltip("Radius of a firing neuron's ignition disc, in cells.")]
        [Range(0f, 64f)] public float firingIgnitionRadius = 8f;

        [Header("Burst (event-driven)")]
        [Tooltip("Off = the legacy continuous layer: the rule runs every step and the output " +
                 "is always visible. On = the sim is idle until triggered, then seeds, runs, " +
                 "fades and goes idle again, dispatching nothing in between.")]
        public bool burstEnabled = true;
        [Tooltip("How long a burst holds at full output, in SIM steps (not rule steps — " +
                 "stepEvery decimates only the rule). Measured from the trigger, so the " +
                 "release begins at fadeInSteps + burstSustainSteps.")]
        public int burstSustainSteps = 240;
        [Tooltip("Sim steps to ramp the output up after a trigger.")]
        public int fadeInSteps = 30;
        [Tooltip("Sim steps to ramp the output back down once the sustain ends.")]
        public int fadeOutSteps = 90;
        [Tooltip("A rising edge of the neuron firing level past this starts a burst. A trigger " +
                 "during a live burst extends it (resets the clock, keeps the lattice).")]
        [Range(0f, 1f)] public float burstFiringThreshold = 0.35f;

        [Header("Centre keep-out (physical screen cutout)")]
        [Tooltip("Normalized width of a centre band where this layer's contribution to the " +
                 "composite is attenuated. Applied to the RENDER only — the rule keeps evolving " +
                 "across the whole grid, because a gap in the rule would leave a seam the waves " +
                 "never cross. 0 = off. For installs whose display has a hole in the middle.")]
        [Range(0f, 0.6f)] public float centreKeepOut = 0f;
        [Tooltip("How much is removed at dead centre. Not 1 by default: the same master may also " +
                 "play on a screen with no cutout, where a hard-edged empty band reads as a defect.")]
        [Range(0f, 1f)] public float centreKeepOutDepth = 0.8f;

        // ── State ────────────────────────────────────────────────────────────────
        // RFloat + point filter + repeat wrap (GPUResourceManager sets the latter two).
        // The rule's authoritative state lives HERE and is rounded on read — never run the
        // rule off the published biome layer, whose float channel accumulates PDE error.
        protected RenderTexture stateRead;
        protected RenderTexture stateWrite;

        protected int cellRezX = 8, cellRezY = 8;

        // Rule kernels. Named so a field sim's compute shader has a fixed, small contract.
        protected int resetStateKernel = -1;
        protected int stepRuleKernel = -1;
        protected int renderStateKernel = -1;

        private int _allocCellRezX = -1, _allocCellRezY = -1;
        private int _ruleStep;

        private BurstEnvelope _burst;
        private RisingEdge _firingEdge;
        private bool _outputDirty;   // outTex holds a frame that must be cleared on going idle

        /// <summary>Output multiplier for the render kernel. Always 1 when bursts are off.</summary>
        protected float OutputEnvelope => burstEnabled ? _burst.Value : 1f;

        #region Shader property IDs
        protected static readonly int s_StateReadID = Shader.PropertyToID("stateRead");
        protected static readonly int s_StateWriteID = Shader.PropertyToID("stateWrite");
        protected static readonly int s_CellRezXID = Shader.PropertyToID("cellRezX");
        protected static readonly int s_CellRezYID = Shader.PropertyToID("cellRezY");
        protected static readonly int s_NStatesID = Shader.PropertyToID("nstates");
        protected static readonly int s_CouplingTexID = Shader.PropertyToID("couplingState");
        protected static readonly int s_CouplingStrengthID = Shader.PropertyToID("couplingStrength");
        protected static readonly int s_CouplingThresholdID = Shader.PropertyToID("couplingThreshold");
        protected static readonly int s_CouplingNStatesID = Shader.PropertyToID("couplingNStates");
        protected static readonly int s_CouplingInvertID = Shader.PropertyToID("couplingInvert");
        protected static readonly int s_CouplingEnabledID = Shader.PropertyToID("couplingEnabled");
        protected static readonly int s_IgnitionStrengthID = Shader.PropertyToID("ignitionStrength");
        protected static readonly int s_IgnitionRadiusID = Shader.PropertyToID("ignitionRadius");
        protected static readonly int s_CentreKeepOutID = Shader.PropertyToID("centreKeepOut");
        protected static readonly int s_CentreKeepOutDepthID = Shader.PropertyToID("centreKeepOutDepth");
        protected static readonly int s_OutputEnvelopeID = Shader.PropertyToID("outputEnvelope");
        protected static readonly int s_SeedFieldID = Shader.PropertyToID("seedField");
        protected static readonly int s_SeedChannelID = Shader.PropertyToID("seedChannelIndex");
        protected static readonly int s_SeedThresholdID = Shader.PropertyToID("seedThreshold");
        protected static readonly int s_SeedFromChannelID = Shader.PropertyToID("seedFromChannel");
        #endregion

        // ── Agent contract: answered, not implemented ────────────────────────────
        // Sealed because a field sim having agents is a category error, not a variation.
        // These three answers are exactly what SimulationManager's null-guards look for.
        public sealed override ComputeBuffer GetAgentPositionBuffer() => null;
        public sealed override int GetAgentCount() => 0;
        protected sealed override int TypeCount => 1;

        /// <summary>Number of discrete states the rule cycles through. Used to normalize
        /// state into 0..1 for the render and the channel publish.</summary>
        public abstract int StateCount { get; }

        /// <summary>The live state grid, for another field sim to read as a coupling mask.</summary>
        public RenderTexture StateTexture => stateRead;

        protected int CellRezX => CellGrid.Width(cellRezHeight, rezX, rezY);
        protected int CellRezY => CellGrid.Height(cellRezHeight);

        // Cell resolution depends on cellRezHeight, which the base signature does not track.
        // Width also uses rezX/rezY (already tracked by base), but cellRezHeight is the new lever.
        protected override bool NeedsAllocation() =>
            base.NeedsAllocation() || CellRezX != _allocCellRezX || CellRezY != _allocCellRezY;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        /// <summary>
        /// Field-sim allocation: state pair + output, and the three rule kernels. Deliberately
        /// does NOT chain to base.Allocate() — that one builds trail arrays and looks up
        /// MoveAgents/WriteTrails/Diffuse, none of which a CA shader declares.
        /// </summary>
        protected override void Allocate()
        {
            if (!IsConfigured) return;   // guarded at Reset() too; this is the direct-call path

            Release();
            gpu = new GPUResourceManager();

            cellRezX = CellRezX;
            cellRezY = CellRezY;

            stateRead = gpu.CreateTexture2D(cellRezX, cellRezY, FilterMode.Point,
                RenderTextureFormat.RFloat, SimName + "_stateRead");
            stateWrite = gpu.CreateTexture2D(cellRezX, cellRezY, FilterMode.Point,
                RenderTextureFormat.RFloat, SimName + "_stateWrite");

            // Output lives at cell resolution: the composite UV-samples every layer through a
            // linear-clamp sampler, so a coarse CA upscales without a resample pass here.
            // outTex = gpu.CreateTexture2D(cellRezX, cellRezY, FilterMode.Trilinear,
                // RenderTextureFormat.ARGBHalf, SimName + "_out");
            outTex = gpu.CreateTexture2D(cellRezX, cellRezY, FilterMode.Point,
                RenderTextureFormat.ARGBHalf, SimName + "_out");

            resetStateKernel = cs.FindKernel("ResetStateKernel");
            stepRuleKernel = cs.FindKernel("StepRuleKernel");
            renderStateKernel = cs.FindKernel("RenderStateKernel");

            InitSimKernels();
            InitBuffers();

            _allocCellRezX = cellRezX;
            _allocCellRezY = cellRezY;
            MarkAllocated();   // see SimulationBase.MarkAllocated — skipping this reallocates forever
        }

        /// <summary>
        /// Whether the sim has enough wiring to touch the GPU at all.
        ///
        /// <para><b>Why this exists.</b> <c>Reset()</c> is this project's public "re-seed the
        /// sim" call — the manager, the MIDI bindings and the Inspector button all use it. It
        /// is also, by name, the message Unity fires the instant a component is added in the
        /// Editor. So the very first <c>Reset()</c> a freshly added CA sim receives arrives
        /// before <c>cs</c> has been assigned, and <see cref="Allocate"/> would dereference it
        /// at <c>FindKernel</c>. Guarding beats renaming: the name is load-bearing across the
        /// manager, the bindings and the agent sims.</para>
        ///
        /// <para>Not an error state — <c>SimulationManager.ConfigureAndReset</c> resets every
        /// sim again once the scene is wired, so an unconfigured sim simply sits inert until
        /// then.</para>
        /// </summary>
        protected bool IsConfigured => cs != null;

        /// <summary>
        /// Clear-in-place reset. Mirrors SimulationBase.Reset() minus the agent/trail work:
        /// re-clone params, rescale, allocate only on a genuine size change, seed, render.
        /// </summary>
        // No [Button] here: SimulationBase.Reset already carries one and EasyButtons would
        // draw a second. Same reason the agent sims override it bare.
        public override void Reset()
        {
            if (!IsConfigured) return;

            _ruleStep = 0;

            // Honour the IParamSet resolution-independence contract even though CA params are
            // mostly unitless (counts and state indices). `range` is the one cell-unit param and
            // is deliberately NOT rez-scaled — it is a rule parameter, not a physical distance —
            // so the concrete param sets implement these as near no-ops.
            if (LiveParamSet != null)
            {
                float k = ResolutionScale;
                if (scaleSpatialToResolution) LiveParamSet.ScaleSpatial(k);
                if (scaleDensityToResolution) LiveParamSet.ScaleDensity(k);
            }

            if (NeedsAllocation())
                Allocate();

            GPUReset();
            Render();
        }

        /// <summary>
        /// Start a burst, or extend the one already running. Only a trigger arriving from idle
        /// re-seeds the grid — see <see cref="BurstEnvelope.Trigger"/>. Inert while
        /// <see cref="burstEnabled"/> is off, so the button cannot wipe a running legacy
        /// continuous-mode lattice or leave a phantom in-flight burst for later.
        /// </summary>
        [Button("Trigger burst")]
        public void TriggerBurst()
        {
            if (!IsConfigured || !burstEnabled) return;

            if (_burst.Trigger())
            {
                if (NeedsAllocation()) Allocate();
                GPUReset();
                _ruleStep = 0;
            }
            _outputDirty = true;
        }

        /// <summary>Blank the composited output so an idle sim leaves no stale frame behind.</summary>
        private void ClearOutput()
        {
            if (outTex == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = outTex;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        /// <summary>Seed the grid, then render. Subclasses bind their own seeding params via
        /// <see cref="BindRuleParams"/>; the dispatch and the swap are owned here.</summary>
        protected override void GPUReset()
        {
            BindCommon(resetStateKernel);
            BindRuleParams(resetStateKernel);

            // Channel seeding. Falls back to the rule's own figure whenever the wiring is
            // incomplete, so a half-configured sim seeds rather than throwing.
            bool canSeedFromChannel =
                seedFromChannel && publishTarget != null && publishTarget.FieldReadArray != null;
            cs.SetInt(s_SeedFromChannelID, canSeedFromChannel ? 1 : 0);
            cs.SetInt(s_SeedChannelID, Mathf.Clamp(seedChannel, 0, BiomeChannel.Count - 1));
            cs.SetFloat(s_SeedThresholdID, seedThreshold);
            cs.SetTexture(resetStateKernel, s_SeedFieldID,
                canSeedFromChannel ? publishTarget.FieldReadArray : Texture2D.blackTexture);

            // Seed into stateWrite, then swap so stateRead holds the seed — the same
            // write-then-swap discipline every step uses, so there is only one convention.
            cs.SetTexture(resetStateKernel, s_StateWriteID, stateWrite);
            cs.SetTexture(resetStateKernel, s_StateReadID, stateRead);
            Dispatch(resetStateKernel, cellRezX, cellRezY, 1);
            SwapState();

            var prev = RenderTexture.active;
            RenderTexture.active = outTex;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        public override void Step()
        {
            if (!IsConfigured) return;

            if (burstEnabled)
            {
                if (_firingEdge.Update(neuronIntensity, burstFiringThreshold))
                    TriggerBurst();

                _burst.Advance(fadeInSteps, burstSustainSteps, fadeOutSteps);

                if (_burst.IsIdle)
                {
                    // Nothing dispatches while idle — not the rule, not the render, not the
                    // publish. Publishing stops here deliberately: the last lattice stays in
                    // the biome channel and the PDE erodes it from now on.
                    if (_outputDirty) { ClearOutput(); _outputDirty = false; }
                    return;
                }
                _outputDirty = true;
            }

            // Rule decimation. Render still runs every step so the composite never stutters.
            _ruleStep++;
            if (_ruleStep % Mathf.Max(1, stepEvery) == 0)
            {
                GPUStep();
                SwapState();
            }
            Render();
            PublishToChannel();
        }

        protected override void GPUStep()
        {
            BindCommon(stepRuleKernel);
            BindRuleParams(stepRuleKernel);
            BindCoupling(stepRuleKernel);

            // Neuron ignition rides the same firing buffer every other sim reads, so a firing
            // organoid neuron lights a wavefront on the same clock as dispersal and the sun.
            BindNeuronFiring(stepRuleKernel);
            cs.SetFloat(s_IgnitionStrengthID, firingIgnitionStrength);
            cs.SetFloat(s_IgnitionRadiusID, firingIgnitionRadius);
            cs.SetVector(s_NeuronScaleID,
                new Vector4(neuronSpawnScale.x, neuronSpawnScale.y, 0, 0));
            // Field sims have no reset-agents kernel, so the inherited CSV parse/upload is
            // pointed at the rule kernel instead. It parses once per allocation and rebinds
            // thereafter, uploading positions pre-multiplied by the SIM resolution — the rule
            // rescales those into cell space via neuronSrcRez (see cellular_common.hlsl).
            BuildNeuronPositions(stepRuleKernel);

            cs.SetTexture(stepRuleKernel, s_StateReadID, stateRead);
            cs.SetTexture(stepRuleKernel, s_StateWriteID, stateWrite);
            Dispatch(stepRuleKernel, cellRezX, cellRezY, 1);
        }

        protected override void Render()
        {
            BindCommon(renderStateKernel);
            BindRuleParams(renderStateKernel);
            cs.SetTexture(renderStateKernel, s_StateReadID, stateRead);
            cs.SetTexture(renderStateKernel, s_OutTexID, outTex);
            Dispatch(renderStateKernel, cellRezX, cellRezY, 1);

            if (outputMat != null)
                outputMat.SetTexture("_UnlitColorMap", outTex);
        }

        /// <summary>
        /// Publish the normalized state into a biome channel so agent sims perceive the CA
        /// through UmweltMapping — no change to any sim's shader, only its mapping asset.
        /// This is wiring "B1" from the CA design: the CA owns the channel and the PDE is
        /// expected to leave it alone, which keeps the rule's authoritative state in this
        /// sim's own RFloat texture rather than in a diffused float layer.
        /// </summary>
        protected virtual void PublishToChannel()
        {
            if (publishTarget == null || stateRead == null) return;
            // 1/StateCount folds the integer state into 0..1 in the shader, so the channel
            // carries a normalized value and quantization stays a render-time concern.
            publishTarget.SeedChannelFromTexture(publishChannel, stateRead,
                publishGain / Mathf.Max(1, StateCount), publishMode);
        }

        // ── Shared binding helpers ───────────────────────────────────────────────

        /// <summary>Uniforms every field-sim kernel needs. `time` is the wrapped SIM step,
        /// never Time.frameCount — catch-up steps within one rendered frame must get distinct
        /// seeds or a multi-step frame is not deterministic under Recorder's driven clock.</summary>
        protected void BindCommon(int kernel)
        {
            cs.SetInt(s_CellRezXID, cellRezX);
            cs.SetInt(s_CellRezYID, cellRezY);
            cs.SetInt(s_RezXID, cellRezX);
            cs.SetInt(s_RezYID, cellRezY);
            cs.SetInt(s_TimeID, WrappedStep);
            cs.SetInt(s_NStatesID, StateCount);
            cs.SetFloat(s_CentreKeepOutID, centreKeepOut);
            cs.SetFloat(s_CentreKeepOutDepthID, centreKeepOutDepth);
            cs.SetFloat(s_OutputEnvelopeID, OutputEnvelope);
        }

        /// <summary>Bind the coupling CA's state as a gate on this rule. Reads the other sim's
        /// <c>stateRead</c>, i.e. the PREVIOUS step's state — both CAs therefore see a
        /// consistent snapshot regardless of which the manager steps first.</summary>
        protected void BindCoupling(int kernel)
        {
            bool enabled = couplingSource != null
                        && couplingSource != this
                        && couplingSource.StateTexture != null
                        && couplingStrength > 0f;

            cs.SetInt(s_CouplingEnabledID, enabled ? 1 : 0);
            cs.SetFloat(s_CouplingStrengthID, enabled ? couplingStrength : 0f);
            cs.SetFloat(s_CouplingThresholdID, couplingThreshold);
            cs.SetInt(s_CouplingInvertID, couplingInvert ? 1 : 0);
            cs.SetInt(s_CouplingNStatesID, enabled ? couplingSource.StateCount : 1);
            // Always bind something: an unbound Texture2D is undefined behaviour on some
            // backends even when the branch that reads it is dead.
            cs.SetTexture(kernel, s_CouplingTexID,
                enabled ? couplingSource.StateTexture : stateRead);
        }

        protected void SwapState() => (stateRead, stateWrite) = (stateWrite, stateRead);

        /// <summary>Bind the rule-specific uniforms/buffers for one kernel (neighbourhood,
        /// thresholds, transition table, palette). Called for reset, step and render, so a
        /// subclass can bind everything in one place without tracking which kernel needs what.</summary>
        protected abstract void BindRuleParams(int kernel);

        public override void Release()
        {
            base.Release();
            stateRead = null;
            stateWrite = null;
            _allocCellRezX = _allocCellRezY = -1;
        }
    }
}

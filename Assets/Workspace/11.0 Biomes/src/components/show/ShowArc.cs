using System;
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// The 90-second Scene_DAC programme.
    ///
    /// <pre>
    /// 0s ─────── 20s ─────── 55s ─────── 75s ─────── 90s → loop
    /// empty      many        converging   ONE BODY    melt
    ///            8 hues      spread       8 hues      back to
    ///            spread      closing      unified     open / empty
    ///            white-ish                red
    ///
    /// perm       1975 ──────── epochs crossfade ──────── 2030  → ResetTermites
    /// CA         ·             seeding      GLITCH PEAK         melts it open
    /// firing     sparse ────────────────── dense ── peak ─────── sparse
    /// </pre>
    ///
    /// <para><b>Clocked on sim steps, not wall time.</b> Progress comes from
    /// <see cref="SimulationManager.SimStepCount"/>, so the arc is identical whether the
    /// project runs at 60 fps live or at single-digit wall-clock fps under Unity Recorder.
    /// At the shipping 60 Hz sim rate and 60 fps output the ratio is 1:1 and 90 s is exactly
    /// 5400 steps, which makes the loop seam reproducible across re-renders — a non-integer
    /// ratio would not be.</para>
    ///
    /// <para><b>Why the hue spread is computed here rather than driven through
    /// ParameterInterpolator.</b> The design calls for a waypoint program, and the
    /// interpolator is the shipped, exhibition-exercised path — but it morphs whole param
    /// ASSETS between two authored poses. Expressing "8 hues spread wide becoming 8 hues
    /// unified" through it means authoring and maintaining two 8-type PhysarumParams assets
    /// whose only meaningful difference is one scalar. The spread is a one-parameter family
    /// (see <see cref="ApplyHueSpread"/>), so it is computed closed-form and pushed through
    /// SetParameter — the same shipped path MIDI and OSC already drive. Fewer moving parts,
    /// re-tunable live, and no asset to fall out of sync. An interpolator can still be driven
    /// alongside for anything genuinely multi-parameter (see <see cref="interpolators"/>).</para>
    /// </summary>
    // Ordering matters: ShowArc (-110) writes epochPhase/closureStrength, then
    // ShanghaiTransect (-100) seeds with those values, then SimulationManager (0) steps.
    // Running the arc second would seed with the PREVIOUS step's values every frame.
    [DefaultExecutionOrder(-110)]
    public class ShowArc : MonoBehaviour
    {
        [Header("Wiring")]
        public SimulationManager simManager;
        public ShanghaiTransect transect;
        [Tooltip("Physarum sims whose per-type hue spread carries the colour register. " +
                 "Normally one sim with 8 types.")]
        public List<PhysarumSim> physarumSims = new();
        [Tooltip("Field sims (the CAs) whose composite weight carries the 55-75s glitch.")]
        public List<FieldSimulationBase> cellularSims = new();
        public BiomeInjector injector;
        [Tooltip("Optional but required for an offline render. The firing playhead is normally " +
                 "scrubbed by an external patch over OSC /index; a pre-rendered show has no OSC, " +
                 "so nothing advances it and no neuron ever fires. Assign this and the arc drives " +
                 "the playhead itself.")]
        public NeuronFiringSource firingSource;
        [Tooltip("Optional. Driven alongside the arc for genuinely multi-parameter moves; " +
                 "the hue spread does not need it.")]
        public List<ParameterInterpolator> interpolators = new();

        [Header("Timing (seconds)")]
        [Tooltip("Total loop length. 90 s at a 60 Hz sim is exactly 5400 steps.")]
        public float loopSeconds = 90f;
        [Tooltip("Ramp-from-nothing ends here: the field is populated and the 8 hues are at " +
                 "their widest spread. This ramp IS the arc, not a prologue.")]
        public float manySeconds = 20f;
        [Tooltip("Convergence begins: spread starts closing and the CA glitch starts rising.")]
        public float convergeSeconds = 55f;
        [Tooltip("ONE BODY: spread closed, hues unified red, glitch at peak. ResetTermites " +
                 "fires here — the built form dissolving is the loop point, and it masks the " +
                 "permeability reset from 2030 back to 1975.")]
        public float oneBodySeconds = 75f;

        [Header("Colour register")]
        [Tooltip("Hue every type converges on. 0 = red.")]
        [Range(0f, 1f)] public float convergedHue = 0.0f;
        [Tooltip("Hue spread across the types at the open pole. 1 = the full colour wheel, " +
                 "which reads as several separate organisms sharing a space.")]
        [Range(0f, 1f)] public float spreadOpen = 1f;
        [Tooltip("Spread at the closed pole. 0 = one continuous body.")]
        [Range(0f, 0.2f)] public float spreadClosed = 0f;
        [Tooltip("Saturation at the open pole. Low reads as white-ish / many pale organisms.")]
        [Range(0f, 1f)] public float saturationOpen = 0.15f;
        [Tooltip("Saturation at the closed pole — the unified red.")]
        [Range(0f, 1f)] public float saturationClosed = 0.85f;

        [Header("CA glitch (composite layer, never the field)")]
        [Tooltip("Composite weight the CA layers reach at the 55-75s peak. The tear rides the " +
                 "existing additive composite; the channel-publish path is not load-bearing here.")]
        [Range(0f, 4f)] public float glitchPeakWeight = 1.6f;
        [Range(0f, 4f)] public float glitchBaseWeight = 0f;

        [Header("Screen 1.2 centre cutout")]
        [Tooltip("Normalized width of the centre band biased AGAINST activity, so the composition " +
                 "survives 1.2's central cutout. Biases density; it must never leave a hard-edged " +
                 "empty band, because 2.6 has no cutout and shows the same master.")]
        [Range(0f, 0.6f)] public float centreKeepOut = 0.33f;

        [Header("Firing playhead (offline render)")]
        [Tooltip("Advance the organoid blob's playhead from the arc clock. Off = the playhead " +
                 "stays external (OSC /index), which is correct for a live show and silent for " +
                 "a pre-rendered one.")]
        public bool driveFiringPlayhead = true;
        [Tooltip("First blob frame of the loop. The blob holds 180000 frames; the loop reads a " +
                 "window of it and returns to this frame at the seam, so firing loops with picture.")]
        [Min(0)] public int firingFrameStart = 0;
        [Tooltip("How many blob frames one 90 s loop traverses. At the default 5400 the blob " +
                 "advances 1:1 with rendered frames, which keeps firing reproducible across " +
                 "re-renders — the same property the 1:1 sim-to-frame ratio buys everywhere else.")]
        [Min(1)] public int firingFrameSpan = 5400;

        [Header("Playback")]
        public bool autoRun = true;
        [Tooltip("Loop forever. Off = hold at the end (useful when scrubbing).")]
        public bool loop = true;

        // Sim-step clock. Captured on the first tick so the arc is measured from when it
        // started rather than from scene load.
        private int _startStep = -1;
        private float _t;             // 0..1 through the loop
        private int _lastLoopIndex = -1;
        private bool _firedOneBody;

        private readonly List<CueRecord> _cues = new();

        /// <summary>Seconds elapsed in the current loop.</summary>
        public float ArcSeconds => _t * loopSeconds;
        /// <summary>Normalized 0..1 position through the loop.</summary>
        public float ArcPhase => _t;
        public IReadOnlyList<CueRecord> Cues => _cues;

        [Serializable]
        public struct CueRecord
        {
            public string name;
            public float seconds;
            public int simStep;
            public int frame;      // at the render frame rate the export assumes
        }

        private float StepsPerSecond =>
            simManager != null ? Mathf.Max(1f, simManager.simRate) : 60f;

        void OnEnable()
        {
            _startStep = -1;
            _firedOneBody = false;
            _lastLoopIndex = -1;
            _cues.Clear();
        }

        void FixedUpdate()
        {
            if (!autoRun) return;
            Tick();
        }

        /// <summary>Advance the arc one sim step and push every driven value.</summary>
        public void Tick()
        {
            if (simManager == null) return;

            int step = simManager.SimStepCount;
            if (_startStep < 0) _startStep = step;

            float totalSteps = Mathf.Max(1f, loopSeconds * StepsPerSecond);
            float progress = (step - _startStep) / totalSteps;

            int loopIndex = Mathf.FloorToInt(progress);
            if (!loop && loopIndex > 0)
            {
                _t = 1f;
            }
            else
            {
                _t = Mathf.Repeat(progress, 1f);
                if (loopIndex != _lastLoopIndex)
                {
                    _lastLoopIndex = loopIndex;
                    _firedOneBody = false;         // re-arm the once-per-loop cue
                }
            }

            Apply(_t * loopSeconds);
        }

        /// <summary>
        /// Push every driven value for a given time in the loop. Pure function of `seconds`
        /// apart from the one-shot ResetTermites cue, so it is safe to call while scrubbing.
        /// </summary>
        public void Apply(float seconds)
        {
            float ramp = Mathf.InverseLerp(0f, Mathf.Max(0.01f, manySeconds), seconds);
            float converge = Mathf.InverseLerp(manySeconds, Mathf.Max(manySeconds + 0.01f, oneBodySeconds), seconds);
            float glitch = GlitchEnvelope(seconds);
            float melt = Mathf.InverseLerp(oneBodySeconds, Mathf.Max(oneBodySeconds + 0.01f, loopSeconds), seconds);

            // ── Space register: the city grows and closes, then releases ──────────────
            if (transect != null)
            {
                // Epochs crossfade 1975 -> 2030 across everything up to ONE BODY, then the melt
                // releases the closure rather than snapping the phase back — the loop seam is
                // hidden by closureStrength falling to 0, not by the epoch index resetting.
                transect.epochPhase = Mathf.InverseLerp(0f, Mathf.Max(0.01f, oneBodySeconds), seconds);
                transect.closureStrength = ramp * (1f - Mathf.SmoothStep(0f, 1f, melt));
            }

            // ── Colour register: spread closes, hues unify on red ─────────────────────
            float spread = Mathf.Lerp(spreadOpen, spreadClosed, Mathf.SmoothStep(0f, 1f, converge));
            float sat = Mathf.Lerp(saturationOpen, saturationClosed, Mathf.SmoothStep(0f, 1f, converge));
            foreach (var sim in physarumSims)
                ApplyHueSpread(sim, spread, sat);

            // ── CA glitch: composite weight only. The field is never touched. ─────────
            foreach (var ca in cellularSims)
            {
                if (ca == null) continue;
                ca.compositeWeight = Mathf.Lerp(glitchBaseWeight, glitchPeakWeight, glitch);
                ca.centreKeepOut = centreKeepOut;
            }

            // ── Firing playhead ───────────────────────────────────────────────────────
            // Without this the organoid blob never advances in an offline render and no neuron
            // ever fires, so the arc's firing row drives nothing — measured over 10800 steps,
            // peak firing was exactly 0. Wrapped on the loop so firing returns to its starting
            // frame at the seam and repeats with picture.
            if (driveFiringPlayhead && firingSource != null && firingSource.FrameCount > 0)
            {
                float phase = loopSeconds > 0f ? Mathf.Clamp01(seconds / loopSeconds) : 0f;
                int span = Mathf.Max(1, firingFrameSpan);
                int frame = firingFrameStart + Mathf.FloorToInt(phase * span);
                firingSource.SetFrame(frame % firingSource.FrameCount);
            }

            // ── Firing density: sparse -> dense -> peak -> sparse ─────────────────────
            if (injector != null)
            {
                float density = Mathf.Lerp(0.15f, 1f, Mathf.SmoothStep(0f, 1f, converge))
                                * (1f - 0.85f * Mathf.SmoothStep(0f, 1f, melt));
                injector.dispersalAmount = Mathf.Clamp01(density);
                injector.centreKeepOut = centreKeepOut;
            }

            // ── Loop point: the built form dissolves ──────────────────────────────────
            // Uses the shipped ResetTermites, which already clears permeability, melts mounds
            // and frees confined species. Firing it here makes the dissolve the loop point AND
            // masks the permeability reset from 2030 back to 1975. Null-tolerant because Apply
            // is also reached via ScrubTo, which — unlike Tick — does not require a simManager.
            if (!_firedOneBody && seconds >= oneBodySeconds)
            {
                _firedOneBody = true;
                simManager?.ResetTermites();
                RecordCue("ResetTermites", seconds);
            }
        }

        /// <summary>
        /// Distribute the types' hues symmetrically around <see cref="convergedHue"/> and
        /// close that spread toward zero.
        ///
        /// <para>Spread, not mean, is the variable — that is the whole mechanism. A global
        /// colour grade shifts the mean and can never produce the "separate things merging"
        /// read; several settlements becoming one continuous built mass IS several organisms
        /// becoming one organism, and only a closing spread says that.</para>
        /// </summary>
        private void ApplyHueSpread(PhysarumSim sim, float spread, float saturation)
        {
            if (sim == null || sim.agentParams == null) return;
            int n = sim.agentParams.types.Count;
            if (n <= 0) return;

            for (int i = 0; i < n; i++)
            {
                // Centred offset in -0.5..0.5, scaled by the spread. At spread 1 the types sit
                // evenly around the whole wheel; at 0 they collapse onto convergedHue exactly.
                // A single type lands on exactly 0, so it sits on convergedHue at any spread.
                float offset = (i / (float)n) - 0.5f + (0.5f / n);
                float hue = Mathf.Repeat(convergedHue + offset * spread, 1f);

                sim.agentParams.types[i].hue = hue;
                sim.agentParams.types[i].saturation = saturation;
            }
        }

        /// <summary>
        /// Glitch envelope: nothing until convergence begins, rising to a plateau across
        /// 55-75 s, then resolving through the melt. Smoothstepped at both ends so the tear
        /// arrives and leaves rather than cutting.
        /// </summary>
        private float GlitchEnvelope(float seconds)
        {
            if (seconds < convergeSeconds) return 0f;
            if (seconds <= oneBodySeconds)
                return Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(convergeSeconds, oneBodySeconds, seconds));
            return 1f - Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(oneBodySeconds, loopSeconds, seconds));
        }

        private void RecordCue(string name, float seconds)
        {
            _cues.Add(new CueRecord
            {
                name = name,
                seconds = seconds,
                simStep = simManager != null ? simManager.SimStepCount : 0,
                frame = Mathf.RoundToInt(seconds * StepsPerSecond),
            });
        }

        [Button("Restart arc")]
        public void Restart()
        {
            _startStep = -1;
            _firedOneBody = false;
            _lastLoopIndex = -1;
            _cues.Clear();
        }

        /// <summary>Scrub to a time without running — for checking a frame in the Editor.</summary>
        public void ScrubTo(float seconds)
        {
            _t = loopSeconds > 0f ? Mathf.Clamp01(seconds / loopSeconds) : 0f;
            Apply(seconds);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Multi-state lookup-table cellular automaton as a composited biome layer — a crisp
    /// standing lattice of gliders and blooms. Counterpart to <see cref="CyclicCASim"/>'s
    /// smooth waves.
    ///
    /// <para>Point a CyclicCASim's <c>couplingSource</c> at this sim and its excitation waves
    /// only propagate where this lattice is alive, so the two stop being stacked layers and
    /// become one coupled system — the same structure/flow relationship termites already have
    /// with permeability.</para>
    ///
    /// <para>Assign <see cref="publishTarget"/> and agents perceive the lattice through
    /// UmweltMapping: map Substrate as SpeedPenalty or Avoidance and boids stall or steer
    /// around it, with no edit to the boid shader.</para>
    /// </summary>
    public class LookupCASim : FieldSimulationBase
    {
        public override string SimName => "LookupCA";

        private static readonly IReadOnlyList<string> s_ModulatableParams = new[]
            { "nstates", "lambda", "seed", "initSize",
              "hue", "hueSpread", "saturation", "brightness" };
        public override IReadOnlyList<string> ModulatableParams => s_ModulatableParams;

        [Header("Parameters (assign preset, runtime clone appears on Play)")]
        public LookupCAParams paramsSO;
        [Header("Runtime Parameters (live tweaking)")]
        public LookupCAParams caParams;

        public override IParamSet LiveParamSet => caParams;
        public override ScriptableObject PresetParamSet => paramsSO;

        public override int StateCount => caParams != null ? Mathf.Clamp(caParams.nstates, 2, MaxStates) : 3;

        // nstates is capped at 6 by the param set, so the table never exceeds 6^5 entries.
        private const int MaxStates = 6;
        private const int MaxTableSize = MaxStates * MaxStates * MaxStates * MaxStates * MaxStates; // 7776

        private ComputeBuffer transitionTableBuffer;
        private int _uploadedTableSize;
        // Signature of the table currently on the GPU. Regenerating is cheap but not free,
        // and it must happen when — and only when — one of the three inputs actually moves.
        private int _tableSeed = int.MinValue;
        private float _tableLambda = float.NaN;
        private int _tableStates = -1;

        #region Shader property IDs
        private static readonly int s_TransitionTableID = Shader.PropertyToID("transitionTable");
        private static readonly int s_TableSizeID = Shader.PropertyToID("tableSize");
        private static readonly int s_InitModeID = Shader.PropertyToID("initMode");
        private static readonly int s_InitSizeID = Shader.PropertyToID("initSize");
        private static readonly int s_RuleSeedID = Shader.PropertyToID("ruleSeed");
        private static readonly int s_HueID = Shader.PropertyToID("caHue");
        private static readonly int s_HueSpreadID = Shader.PropertyToID("caHueSpread");
        private static readonly int s_SaturationID = Shader.PropertyToID("caSaturation");
        private static readonly int s_BrightnessID = Shader.PropertyToID("caBrightness");
        private static readonly int s_NeuronSrcRezID = Shader.PropertyToID("neuronSrcRez");
        #endregion

        public override void Reset()
        {
            caParams = paramsSO != null
                ? Instantiate(paramsSO)
                : ScriptableObject.CreateInstance<LookupCAParams>();
            // Force a rebuild: the clone is a fresh object and the cached signature refers to
            // whatever the previous clone held.
            _tableStates = -1;
            base.Reset();
        }

        /// <summary>
        /// Allocate the table at its MAXIMUM size once, rather than at nstates^5.
        ///
        /// <para>Sizing it to the current nstates would make nstates an allocation key: editing
        /// it live would force a reallocation, which changes the outTex instance and tears down
        /// any downstream Syphon server (the failure ADR-0008 exists to prevent). At 6^5 entries
        /// the worst case is 31 KB, so the fixed allocation costs nothing and buys the ability
        /// to retune the rule mid-show without a visible hitch.</para>
        /// </summary>
        protected override void InitBuffers()
        {
            transitionTableBuffer = gpu.CreateBuffer(MaxTableSize, sizeof(uint));
            _uploadedTableSize = 0;
            _tableStates = -1;   // buffer contents are undefined until the first upload
        }

        protected override void BindRuleParams(int kernel)
        {
            if (caParams == null) return;

            EnsureTable();

            cs.SetBuffer(kernel, s_TransitionTableID, transitionTableBuffer);
            cs.SetInt(s_TableSizeID, _uploadedTableSize);
            cs.SetInt(s_InitModeID, (int)caParams.initMode);
            cs.SetFloat(s_InitSizeID, caParams.initSize);
            cs.SetInt(s_RuleSeedID, caParams.seed);

            cs.SetFloat(s_HueID, caParams.hue);
            cs.SetFloat(s_HueSpreadID, caParams.hueSpread);
            cs.SetFloat(s_SaturationID, caParams.saturation);
            cs.SetFloat(s_BrightnessID, caParams.brightness);

            cs.SetVector(s_NeuronSrcRezID, new Vector4(rezX, rezY, 0, 0));
        }

        /// <summary>
        /// Regenerate and upload the transition table if (seed, lambda, nstates) has moved
        /// since the last upload. Cheap to call every bind; only the comparison runs in the
        /// common case.
        /// </summary>
        private void EnsureTable()
        {
            if (transitionTableBuffer == null || caParams == null) return;

            int n = Mathf.Clamp(caParams.nstates, 2, MaxStates);
            if (n == _tableStates
                && caParams.seed == _tableSeed
                && Mathf.Approximately(caParams.lambda, _tableLambda))
                return;

            var table = caParams.BuildTransitionTable();
            transitionTableBuffer.SetData(table, 0, 0, table.Length);

            _uploadedTableSize = table.Length;
            _tableStates = n;
            _tableSeed = caParams.seed;
            _tableLambda = caParams.lambda;
        }

        /// <summary>Draw a new rule. The single most useful control here — most seeds produce
        /// nothing, so finding a good automaton is a search, not a tuning exercise.</summary>
        [Button("Randomize rule (new seed)")]
        public void RandomizeSeed()
        {
            if (caParams == null) return;
            caParams.seed = UnityEngine.Random.Range(0, 65536);
            EnsureTable();
        }

        [Button("Regenerate table")]
        public void RegenerateTable()
        {
            _tableStates = -1;   // invalidate the signature so EnsureTable rebuilds
            EnsureTable();
        }

        public override void Release()
        {
            base.Release();
            transitionTableBuffer = null;   // owned and freed by the GPU resource manager
            _uploadedTableSize = 0;
            _tableStates = -1;
        }

        #region Parameter Control
        private float R(string p, float v) { var (mn, mx) = caParams.GetRange(p); return MapAndClamp(v, mn, mx); }
        private float D(string p, float f, float d) { var (mn, mx) = caParams.GetRange(p); return ClampDelta(f, d, mn, mx); }

        public override void SetParameter(string paramName, int index, float value)
        {
            if (caParams == null) return;
            caParams.SetValue(paramName, 0, R(paramName, value));
        }

        public override void SetParameterDelta(string paramName, int index, float delta)
        {
            if (caParams == null) return;
            float current = caParams.GetValue(paramName, 0);
            caParams.SetValue(paramName, 0, D(paramName, current, delta));
        }

        public override float GetParameter(string paramName, int index)
            => caParams != null ? caParams.GetValue(paramName, 0) : 0f;
        #endregion

        [Button] public void RandomizeParams() => caParams?.RandomizeParams();
        [Button] public void RandomizeColors() => caParams?.RandomizeColors();
    }
}

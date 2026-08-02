using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Cyclic (Griffeath) cellular automaton as a composited biome layer — an excitable
    /// medium of smooth spiral and demon waves. The aesthetic and behavioural counterpart to
    /// <see cref="LookupCASim"/>'s crisp lattice, which is why the two are designed to run
    /// together rather than as alternatives: one is a medium to follow, the other a structure
    /// to avoid.
    ///
    /// <para>Assign <see cref="publishTarget"/> and an agent species can perceive the wave
    /// field through its UmweltMapping — map Excitability as Chemotaxis and physarum tracks
    /// the spiral arms, with no edit to the physarum shader.</para>
    /// </summary>
    public class CyclicCASim : FieldSimulationBase
    {
        public override string SimName => "CyclicCA";

        private static readonly IReadOnlyList<string> s_ModulatableParams = new[]
            { "range", "threshold", "nstates", "noiseAmount",
              "hue", "hueSpread", "saturation", "brightness" };
        public override IReadOnlyList<string> ModulatableParams => s_ModulatableParams;

        [Header("Parameters (assign preset, runtime clone appears on Play)")]
        public CyclicCAParams paramsSO;
        [Header("Runtime Parameters (live tweaking)")]
        public CyclicCAParams caParams;

        public override IParamSet LiveParamSet => caParams;
        public override ScriptableObject PresetParamSet => paramsSO;

        // Falls back to the asset default when called before the runtime clone exists —
        // Allocate() binds nstates and can run ahead of the first param clone on a cold start.
        public override int StateCount => caParams != null ? Mathf.Max(2, caParams.nstates) : 8;

        private int secondaryNoiseKernel = -1;

        #region Shader property IDs
        private static readonly int s_RangeID = Shader.PropertyToID("ccaRange");
        private static readonly int s_ThresholdID = Shader.PropertyToID("ccaThreshold");
        private static readonly int s_MooreID = Shader.PropertyToID("ccaMoore");
        private static readonly int s_RuleSeedID = Shader.PropertyToID("ruleSeed");
        private static readonly int s_NoiseAmountID = Shader.PropertyToID("noiseAmount");
        private static readonly int s_HueID = Shader.PropertyToID("ccaHue");
        private static readonly int s_HueSpreadID = Shader.PropertyToID("ccaHueSpread");
        private static readonly int s_SaturationID = Shader.PropertyToID("ccaSaturation");
        private static readonly int s_BrightnessID = Shader.PropertyToID("ccaBrightness");
        private static readonly int s_NeuronSrcRezID = Shader.PropertyToID("neuronSrcRez");
        #endregion

        [Tooltip("Seed for the initial random state field. Changing it and resetting gives a " +
                 "different spiral arrangement from the same rule.")]
        public int resetSeed = 12345;

        public override void Reset()
        {
            // Re-clone from the pristine asset every reset, exactly as the agent sims do, so
            // live tweaks and resolution scaling never compound into the on-disk preset.
            caParams = paramsSO != null
                ? Instantiate(paramsSO)
                : ScriptableObject.CreateInstance<CyclicCAParams>();
            base.Reset();
        }

        protected override void InitSimKernels()
        {
            secondaryNoiseKernel = cs.HasKernel("SecondaryNoiseKernel")
                ? cs.FindKernel("SecondaryNoiseKernel") : -1;
        }

        // No rule buffers: the cyclic rule is arithmetic, not a table.
        protected override void InitBuffers() { }

        protected override void BindRuleParams(int kernel)
        {
            if (caParams == null) return;

            cs.SetInt(s_RangeID, Mathf.Max(1, caParams.range));
            cs.SetInt(s_ThresholdID, Mathf.Max(1, caParams.threshold));
            cs.SetInt(s_MooreID, caParams.moore ? 1 : 0);
            cs.SetInt(s_RuleSeedID, resetSeed);
            cs.SetFloat(s_NoiseAmountID, caParams.noiseAmount);

            cs.SetFloat(s_HueID, caParams.hue);
            cs.SetFloat(s_HueSpreadID, caParams.hueSpread);
            cs.SetFloat(s_SaturationID, caParams.saturation);
            cs.SetFloat(s_BrightnessID, caParams.brightness);

            // The neuron CSV is un-normalized against the SIM resolution, but this rule
            // indexes the CELL grid; the shader needs the source space to convert between them.
            cs.SetVector(s_NeuronSrcRezID, new Vector4(rezX, rezY, 0, 0));
        }

        /// <summary>
        /// Re-randomise a fraction of cells in place. Kicks a field that has locked into
        /// stable spiral cores back into transient motion WITHOUT a full Reset — a reset would
        /// clear outTex and flash the composite, which is unusable mid-show.
        /// </summary>
        [Button("Perturb (re-seed some cells)")]
        public void Perturb()
        {
            if (secondaryNoiseKernel < 0 || stateRead == null || caParams == null) return;

            BindCommon(secondaryNoiseKernel);
            BindRuleParams(secondaryNoiseKernel);
            cs.SetTexture(secondaryNoiseKernel, s_StateReadID, stateRead);
            cs.SetTexture(secondaryNoiseKernel, s_StateWriteID, stateWrite);
            Dispatch(secondaryNoiseKernel, cellRezX, cellRezY, 1);
            SwapState();   // the kernel writes the perturbed field into stateWrite
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

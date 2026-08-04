using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Parameters for the cyclic (Griffeath) cellular automaton.
    ///
    /// <para>Unlike the agent param sets this has no per-type list — a CA has exactly one
    /// rule, so <see cref="TypeCount"/> is 1 and the type index is ignored throughout. The
    /// IParamSet surface is still implemented in full because it is what buys MIDI/OSC
    /// binding, ParameterInterpolator waypoints and the shared params inspector.</para>
    /// </summary>
    [CreateAssetMenu(fileName = "CyclicCAParams", menuName = "Biomes/CyclicCAParams")]
    public class CyclicCAParams : ScriptableObject, IParamSet
    {
        [Header("Rule")]
        [Tooltip("Neighbourhood radius in CELLS. Cost is O((2r+1)^2) per cell per step under " +
                 "Moore, so this is the single most expensive knob here — raise cellResolutionScale " +
                 "down or stepEvery up before raising this.")]
        [Range(1, 10)] public int range = 1;

        [Tooltip("How many neighbours must already hold the next state before a cell advances. " +
                 "With range and nstates this is the edge-of-chaos control: too low and the field " +
                 "boils uniformly, too high and it freezes into static blocks.")]
        [Range(1, 30)] public int threshold = 3;

        [Tooltip("Length of the state cycle. Low values give coarse blocky fronts; higher values " +
                 "give the smooth many-banded spirals the rule is known for.")]
        [Range(2, 32)] public int nstates = 8;

        [Tooltip("Moore = square neighbourhood (all cells within the radius box). Off = von Neumann " +
                 "diamond, which is cheaper and gives more angular, crystalline fronts.")]
        public bool moore = true;

        [Tooltip("Fraction of cells re-randomised by the Perturb button / OSC trigger. Kicks a " +
                 "locked spiral field back into motion without a full reset.")]
        [Range(0f, 1f)] public float noiseAmount = 0.15f;

        [Header("Look")]
        [Range(0f, 1f)] public float hue = 0.55f;
        [Tooltip("How much of the colour wheel the state cycle spans. 1 = a full rainbow across " +
                 "the cycle; small values give a tight monochrome ramp.")]
        [Range(0f, 1f)] public float hueSpread = 0.25f;
        [Range(0f, 1f)] public float saturation = 0.6f;
        [Range(0f, 1f)] public float brightness = 0.7f;

        [Header("MIDI/OSC Ranges (min/max for 0-1 mapping)")]
        public List<ParamRange> ranges = new()
        {
            new("range",       1f, 10f),
            new("threshold",   1f, 30f),
            new("nstates",     2f, 32f),
            new("noiseAmount", 0f, 1f),
            new("hue",         0f, 1f),
            new("hueSpread",   0f, 1f),
            new("saturation",  0f, 1f),
            new("brightness",  0f, 1f),
        };

        public (float min, float max) GetRange(string paramName)
            => ParamRangeUtil.GetRange(ranges, paramName);

        // One rule, no per-type variation. typeIndex is accepted and ignored so the
        // interpolator and MIDI paths need no special case for field sims.
        public int TypeCount => 1;

        public float GetValue(string name, int typeIndex) => name switch
        {
            "range"       => range,
            "threshold"   => threshold,
            "nstates"     => nstates,
            "noiseAmount" => noiseAmount,
            "hue"         => hue,
            "hueSpread"   => hueSpread,
            "saturation"  => saturation,
            "brightness"  => brightness,
            _ => 0f,
        };

        public void SetValue(string name, int typeIndex, float raw)
        {
            switch (name)
            {
                // Rounded, not truncated: these are integer rule parameters arriving from a
                // continuous MIDI/interpolator path, and truncation would make the top of
                // every knob's travel unreachable.
                case "range":       range       = Mathf.Clamp(Mathf.RoundToInt(raw), 1, 10); break;
                case "threshold":   threshold   = Mathf.Clamp(Mathf.RoundToInt(raw), 1, 30); break;
                case "nstates":     nstates     = Mathf.Clamp(Mathf.RoundToInt(raw), 2, 32); break;
                case "noiseAmount": noiseAmount = Mathf.Clamp01(raw); break;
                case "hue":         hue         = Mathf.Clamp01(raw); break;
                case "hueSpread":   hueSpread   = Mathf.Clamp01(raw); break;
                case "saturation":  saturation  = Mathf.Clamp01(raw); break;
                case "brightness":  brightness  = Mathf.Clamp01(raw); break;
            }
        }

        public void SyncTypesList() { }   // no per-type list to sync

        public void ResetToDefaults()
        {
            range = 1; threshold = 3; nstates = 8; moore = true; noiseAmount = 0.15f;
            hue = 0.55f; hueSpread = 0.25f; saturation = 0.6f; brightness = 0.7f;
        }

        // Deliberately no-ops. CA parameters are unitless — counts, state indices and a
        // neighbourhood radius measured in CELLS. `range` is the only cell-unit value and it
        // is a RULE parameter, not a physical distance: scaling it with output resolution
        // would silently change the automaton being simulated, not merely its scale.
        public void ScaleSpatial(float k) { }
        public void ScaleDensity(float k) { }

        public void RandomizeParams()
        {
            range = UnityEngine.Random.Range(1, 4);
            nstates = UnityEngine.Random.Range(4, 17);
            // Threshold is only meaningful relative to the neighbourhood size, so it is drawn
            // as a fraction of the available neighbours rather than from a flat range — a
            // flat draw mostly lands outside the band where the rule does anything at all.
            int neighbours = moore ? (2 * range + 1) * (2 * range + 1) - 1 : 2 * range * (range + 1);
            threshold = Mathf.Clamp(
                Mathf.RoundToInt(neighbours * UnityEngine.Random.Range(0.08f, 0.35f)), 1, 30);
        }

        public void RandomizeColors()
        {
            var palette = ColorPalette.GenerateHS(1);
            if (palette.Count > 0)
            {
                hue = palette[0].hue;
                saturation = palette[0].saturation;
            }
        }
    }
}

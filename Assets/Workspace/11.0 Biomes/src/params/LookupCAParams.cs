using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Parameters for the lookup-table cellular automaton.
    ///
    /// <para><b>The rule is a buffer, not a number.</b> The actual transition rule is a
    /// table of <c>nstates^5</c> entries generated from <see cref="seed"/>,
    /// <see cref="lambda"/> and <see cref="nstates"/>. ParameterInterpolator can only morph
    /// scalars, so those three ARE the interpolatable surface and changing any of them
    /// regenerates the table. Evolution across a show arc is therefore a sequence of
    /// discrete regenerations, not a continuous morph — worth knowing when authoring
    /// waypoints, because a lambda ramp will step rather than glide.</para>
    /// </summary>
    [CreateAssetMenu(fileName = "LookupCAParams", menuName = "Biomes/LookupCAParams")]
    public class LookupCAParams : ScriptableObject, IParamSet
    {
        [Header("Rule (changing any of these regenerates the table)")]
        [Tooltip("States per cell. The table is nstates^5 entries, so this grows fast: " +
                 "2 -> 32, 4 -> 1024, 6 -> 7776. 2 gives binary Life-like behaviour.")]
        [Range(2, 6)] public int nstates = 3;

        [Tooltip("Table generator seed. Every distinct seed is a different automaton, so this " +
                 "is the 'which rule' dial rather than a tuning knob — scrub it to hunt.")]
        public int seed = 1337;

        [Tooltip("Langton's lambda: the fraction of table entries that are NON-quiescent. This " +
                 "is the edge-of-chaos control. Near 0 every pattern dies out; near 1 the field " +
                 "boils into noise; the gliders and standing structures live in a narrow band " +
                 "around 0.2-0.4. Expect most values to be uninteresting — that is the point.")]
        [Range(0f, 1f)] public float lambda = 0.28f;

        [Header("Seeding")]
        [Tooltip("Shape the grid is seeded with on Reset. Line and circle give a single growth " +
                 "front; random gives immediate full-field activity.")]
        public InitMode initMode = InitMode.Circle;
        [Tooltip("Normalized extent of the seed figure (or, in Random mode, the fill fraction).")]
        [Range(0.001f, 1f)] public float initSize = 0.15f;

        [Header("Look")]
        [Range(0f, 1f)] public float hue = 0.08f;
        [Range(0f, 1f)] public float hueSpread = 0.15f;
        [Range(0f, 1f)] public float saturation = 0.35f;
        [Range(0f, 1f)] public float brightness = 0.85f;

        public enum InitMode { Line = 0, Rect = 1, Circle = 2, Random = 3 }

        [Header("MIDI/OSC Ranges (min/max for 0-1 mapping)")]
        public List<ParamRange> ranges = new()
        {
            new("nstates",    2f, 6f),
            new("lambda",     0f, 1f),
            new("seed",       0f, 65535f),
            new("initSize",   0.001f, 1f),
            new("hue",        0f, 1f),
            new("hueSpread",  0f, 1f),
            new("saturation", 0f, 1f),
            new("brightness", 0f, 1f),
        };

        public (float min, float max) GetRange(string paramName)
            => ParamRangeUtil.GetRange(ranges, paramName);

        public int TypeCount => 1;

        public float GetValue(string name, int typeIndex) => name switch
        {
            "nstates"    => nstates,
            "lambda"     => lambda,
            "seed"       => seed,
            "initSize"   => initSize,
            "hue"        => hue,
            "hueSpread"  => hueSpread,
            "saturation" => saturation,
            "brightness" => brightness,
            _ => 0f,
        };

        public void SetValue(string name, int typeIndex, float raw)
        {
            switch (name)
            {
                case "nstates":    nstates    = Mathf.Clamp(Mathf.RoundToInt(raw), 2, 6); break;
                case "lambda":     lambda     = Mathf.Clamp01(raw); break;
                case "seed":       seed       = Mathf.RoundToInt(raw); break;
                case "initSize":   initSize   = Mathf.Clamp(raw, 0.001f, 1f); break;
                case "hue":        hue        = Mathf.Clamp01(raw); break;
                case "hueSpread":  hueSpread  = Mathf.Clamp01(raw); break;
                case "saturation": saturation = Mathf.Clamp01(raw); break;
                case "brightness": brightness = Mathf.Clamp01(raw); break;
            }
        }

        public void SyncTypesList() { }

        public void ResetToDefaults()
        {
            nstates = 3; seed = 1337; lambda = 0.28f;
            initMode = InitMode.Circle; initSize = 0.15f;
            hue = 0.08f; hueSpread = 0.15f; saturation = 0.35f; brightness = 0.85f;
        }

        // No-ops for the same reason as the cyclic CA: every parameter here is a rule
        // parameter or a normalized figure size. Nothing is measured in pixels.
        public void ScaleSpatial(float k) { }
        public void ScaleDensity(float k) { }

        public void RandomizeParams()
        {
            seed = UnityEngine.Random.Range(0, 65536);
            // Biased to the interesting band rather than the full 0..1 range — a uniform
            // lambda draw lands on "dies immediately" or "boils" the large majority of the time.
            lambda = UnityEngine.Random.Range(0.15f, 0.45f);
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

        /// <summary>
        /// Build the transition table for the current (seed, lambda, nstates).
        ///
        /// <para>Deterministic and self-contained: it uses its own hash chain rather than
        /// UnityEngine.Random, so regenerating mid-show cannot disturb any other consumer of
        /// the global RNG, and the same three values always produce the same automaton across
        /// machines and across re-renders. That reproducibility is a hard requirement for an
        /// offline render that must be re-runnable.</para>
        ///
        /// <para>Entry 0 (all-quiescent neighbourhood) is forced quiescent so empty space
        /// stays empty — without it the whole field ignites on step one regardless of lambda,
        /// and the seed figure never means anything.</para>
        /// </summary>
        public uint[] BuildTransitionTable()
        {
            int n = Mathf.Clamp(nstates, 2, 6);
            int size = n * n * n * n * n;
            var table = new uint[size];

            uint h = (uint)seed * 2654435761u + 1u;
            for (int i = 0; i < size; i++)
            {
                // xorshift-ish step, then two independent draws from the mixed value.
                h ^= h << 13; h ^= h >> 17; h ^= h << 5;
                float draw = (h & 0x00FFFFFFu) / 16777216f;
                float pick = ((h >> 8) & 0x00FFFFFFu) / 16777216f;

                table[i] = draw < lambda
                    ? (uint)Mathf.Min(1 + Mathf.FloorToInt(pick * (n - 1)), n - 1)
                    : 0u;
            }

            table[0] = 0u;   // quiescent neighbourhood stays quiescent
            return table;
        }
    }
}

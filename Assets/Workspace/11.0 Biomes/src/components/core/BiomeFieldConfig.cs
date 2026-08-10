using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    // Channel indices into the biome field texture array
    public static class BiomeChannel
    {
        public const int Nutrient   = 0;
        public const int Pheromone0 = 1;  // sim 0 species scent
        public const int Pheromone1 = 2;  // sim 1 species scent
        public const int Pheromone2 = 3;  // sim 2 species scent
        public const int Oxygen     = 4;
        public const int Temperature = 5;
        public const int Waste      = 6;
        public const int Permeability = 7;
        public const int FlowX      = 8;
        public const int FlowY      = 9;
        public const int Dispersal  = 10;  // transient agitation: scatters all sims
        public const int Humidity   = 11;  // renewable moisture: agent-consumed, evaporated by Temperature
        public const int HumidityGrad = 12;  // |∇Humidity| magnitude: precomputed moisture-edge (termite build cue)
        // CA-published substrate channels. Written by a FieldSimulationBase each step and read
        // by agent sims through UmweltMapping, so a species responds to a cellular automaton
        // with no change to its shader — only its mapping asset. Both are CA-OWNED: leave
        // diffuseRate/relaxRate at 0 unless you deliberately want the pattern to bleed or
        // advect through the flow field.
        public const int Excitability = 13;  // cyclic CA state: smooth spiral/demon waves (a medium to follow)
        public const int Substrate    = 14;  // lookup CA state: crisp lattice (a structure to avoid)
        public const int Count      = 15;

        /// <summary>Display names, index-aligned with the constants above. Single source of
        /// truth for the inspector channel dropdown — keep in sync if channels change.</summary>
        public static readonly string[] Names =
        {
            "Nutrient", "Pheromone_0", "Pheromone_1", "Pheromone_2", "Oxygen",
            "Temperature", "Waste", "Permeability", "Flow_X", "Flow_Y", "Dispersal", "Humidity",
            "Humidity_Grad", "Excitability", "Substrate",
        };
    }

    /// <summary>Field flag: renders an int channel index as a named dropdown in the
    /// inspector (drawer in src/Editor/BiomeChannelFieldDrawer.cs). Stored value stays the
    /// channel index, so it's GPU- and serialization-compatible with a plain int.</summary>
    public class BiomeChannelFieldAttribute : PropertyAttribute { }

    /// <summary>Base stencil for a channel's 3×3 diffusion neighbourhood. Box is the legacy
    /// uniform blur; Gaussian (1,2,1 ⊗ 1,2,1) has less diagonal bias, i.e. is closer to
    /// rotationally symmetric. Packed as a float so the shader can morph between them.</summary>
    public enum DiffusionKernelShape { Box = 0, Gaussian = 1 }

    [Serializable]
    public class FieldChannelSettings
    {
        public string name;
        [Range(0f, 1f)] public float diffuseRate = 0.95f;
        [Tooltip("Sink toward 0 each step (evaporation/breakdown). Use for deposit channels (Waste, Pheromones).")]
        [Range(0f, 1f)] public float decayRate = 0f;
        public bool advectedByFlow = false;

        // Initial value (uniform fill on reset). Also the relaxation baseline (see relaxRate).
        [Range(0f, 1f)] public float initialValue = 0f;

        [Tooltip("Homeostatic relaxation toward the baseline (= initialValue) each step. Use for " +
                 "channels that must hold an ambient level instead of ramping (Oxygen 0.8, Temperature " +
                 "0.5). For Permeability it relaxes toward the recomputed noise terrain (heals digs). " +
                 "0 = off (one-way decay/accumulate, the old behaviour).")]
        [Range(0f, 1f)] public float relaxRate = 0f;

        // ── Non-uniform diffusion (defaults reproduce the legacy uniform box blur) ──
        [Tooltip("Base 3×3 stencil. Box = legacy uniform blur; Gaussian weights the center/axes " +
                 "more, reducing the box's diagonal spreading bias.")]
        public DiffusionKernelShape kernelShape = DiffusionKernelShape.Box;

        [Tooltip("Elongate this channel's diffusion along the LOCAL flow vector, scaled by flow " +
                 "speed (turbulent eddy diffusivity: scent plumes stretch along the wind — " +
                 "symmetrically up/downwind; the one-way drift is advection's job, see " +
                 "advectedByFlow). 0 = isotropic (legacy). Needs a live flow field " +
                 "(temperatureToFlowStrength and/or ambientWind) to have any effect.")]
        [Range(0f, 1f)] public float flowAnisotropy = 0f;

        [Tooltip("Gate diffusion by interface permeability min(here, neighbour) — porous media. " +
                 "1 = termite walls fully block this chemical (mounds become scent containers: " +
                 "chambers pool it, corridors duct it); 0 = scent ignores walls (legacy).")]
        [Range(0f, 1f)] public float permeabilityInfluence = 0f;
    }

    [CreateAssetMenu(fileName = "BiomeFieldConfig", menuName = "Biomes/BiomeFieldConfig")]
    public class BiomeFieldConfig : ScriptableObject
    {
        public List<FieldChannelSettings> channels = new()
        {
            new() { name = "Nutrient",      diffuseRate = 0.995f, decayRate = 0.0005f, advectedByFlow = true,  initialValue = 0.3f, relaxRate = 0f },
            new() { name = "Pheromone_0",    diffuseRate = 0.98f,  decayRate = 0.002f, advectedByFlow = true,  initialValue = 0f,   relaxRate = 0f },
            new() { name = "Pheromone_1",    diffuseRate = 0.98f,  decayRate = 0.002f, advectedByFlow = true,  initialValue = 0f,   relaxRate = 0f },
            new() { name = "Pheromone_2",    diffuseRate = 0.98f,  decayRate = 0.002f, advectedByFlow = true,  initialValue = 0f,   relaxRate = 0f },
            new() { name = "Oxygen",         diffuseRate = 0.995f, decayRate = 0f,     advectedByFlow = true,  initialValue = 0.8f, relaxRate = 0.01f },
            new() { name = "Temperature",    diffuseRate = 0.997f, decayRate = 0.0005f, advectedByFlow = false, initialValue = 0.5f, relaxRate = 0.02f },
            new() { name = "Waste",          diffuseRate = 0.99f,  decayRate = 0.001f, advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
            new() { name = "Permeability",   diffuseRate = 0f,     decayRate = 0f,     advectedByFlow = false, initialValue = 0.7f, relaxRate = 0.05f },
            new() { name = "Flow_X",         diffuseRate = 0.92f,  decayRate = 0.02f,  advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
            new() { name = "Flow_Y",         diffuseRate = 0.92f,  decayRate = 0.02f,  advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
            new() { name = "Dispersal",      diffuseRate = 0.9f,   decayRate = 0.12f,  advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
            new() { name = "Humidity",       diffuseRate = 0.97f,  decayRate = 0.001f, advectedByFlow = true,  initialValue = 0.5f, relaxRate = 0.01f },
            new() { name = "Humidity_Grad",  diffuseRate = 0f,     decayRate = 0f,     advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
            // CA-owned: the automaton rewrites these every step, so the PDE must not touch
            // them. Any diffusion here would blur the rule's own output back over itself.
            new() { name = "Excitability",   diffuseRate = 0f,     decayRate = 0f,     advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
            new() { name = "Substrate",      diffuseRate = 0f,     decayRate = 0f,     advectedByFlow = false, initialValue = 0f,   relaxRate = 0f },
        };

        // Cross-field interaction rates
        [Header("Cross-Field Interactions")]
        [Range(0f, 0.1f)] public float wasteToNutrientRate = 0.005f;       // decomposition (base rate at temp=0.5)
        [Tooltip("Q10 temperature exponent span for decomposition: rate = base·2.74^((temp-0.5)·span). " +
                 "0 ≈ flat; ~4 gives near-frozen-when-cold / explosive-when-hot (travelling fertility fronts).")]
        [Range(0f, 8f)]   public float decompositionTempSpan = 4f;
        [Range(0f, 1f)]   public float temperatureToFlowStrength = 0.5f;   // convection
        [Tooltip("Ambient wind added into the flow field every step (prevailing drift for advected " +
                 "channels + the axis flow-anisotropic diffusion elongates along). Flow equilibrium " +
                 "is ≈10× this per-axis value (flow's diffuse-leak/decay), clamped to ±1 — so " +
                 "±0.02..0.1 is the useful range and ±0.1 already saturates the flow channel.")]
        public Vector2 ambientWind = Vector2.zero;
        [Range(0f, 1f)]   public float temperatureToPermeability = 0.3f;   // phase transitions (now a bounded offset, see Biome.compute)
        [Tooltip("Open-ground permeability the field starts at and slowly relaxes toward. Termite mounds build downward from this; replaces the old noise terrain.")]
        [Range(0f, 1f)] public float permeabilityOpenBaseline = 0.9f;
        [Tooltip("Evaporation: Humidity sinks where Temperature is above its 0.5 baseline " +
                 "(humidity -= rate·max(0, temp-0.5) each step). Dries the field behind the hot zones; " +
                 "the |∇Humidity| edge this leaves is the termite build cue. 0 = no thermal evaporation.")]
        [Range(0f, 0.2f)] public float temperatureToEvaporation = 0.05f;   // Temperature dries Humidity
        [Tooltip("Gain applied to |∇Humidity| before saturate when writing CH_HUMIDITY_GRAD. The raw " +
                 "central-difference magnitude is tiny (Humidity diffuseRate 0.97 smooths the field), so " +
                 "this lifts the drying-wake edge into a usable termite build cue. 0 = no gradient signal.")]
        [Range(0f, 32f)]  public float humidityGradientGain = 12f;         // |∇Humidity| magnitude gain

        [Header("Initial Matter Map")]
        [Range(0f, 10f)] public float noiseScale = 3f;
        [Range(0f, 1f)]  public float noiseThreshold = 0.3f;  // below this = solid (low permeability)
    }
}

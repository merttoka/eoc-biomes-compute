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
        public const int Count      = 10;
    }

    [Serializable]
    public class FieldChannelSettings
    {
        public string name;
        [Range(0f, 1f)] public float diffuseRate = 0.95f;
        [Range(0f, 1f)] public float decayRate = 0f;
        public bool advectedByFlow = false;

        // Initial value (uniform fill on reset)
        [Range(0f, 1f)] public float initialValue = 0f;
    }

    [CreateAssetMenu(fileName = "BiomeFieldConfig", menuName = "Biomes/BiomeFieldConfig")]
    public class BiomeFieldConfig : ScriptableObject
    {
        public List<FieldChannelSettings> channels = new()
        {
            new() { name = "Nutrient",      diffuseRate = 0.995f, decayRate = 0f,     advectedByFlow = true,  initialValue = 0.3f },
            new() { name = "Pheromone_0",    diffuseRate = 0.98f,  decayRate = 0.002f, advectedByFlow = true,  initialValue = 0f },
            new() { name = "Pheromone_1",    diffuseRate = 0.98f,  decayRate = 0.002f, advectedByFlow = true,  initialValue = 0f },
            new() { name = "Pheromone_2",    diffuseRate = 0.98f,  decayRate = 0.002f, advectedByFlow = true,  initialValue = 0f },
            new() { name = "Oxygen",         diffuseRate = 0.995f, decayRate = 0f,     advectedByFlow = true,  initialValue = 0.8f },
            new() { name = "Temperature",    diffuseRate = 0.997f, decayRate = 0.0005f, advectedByFlow = false, initialValue = 0.5f },
            new() { name = "Waste",          diffuseRate = 0.99f,  decayRate = 0f,     advectedByFlow = false, initialValue = 0f },
            new() { name = "Permeability",   diffuseRate = 0f,     decayRate = 0f,     advectedByFlow = false, initialValue = 0.7f },
            new() { name = "Flow_X",         diffuseRate = 0.92f,  decayRate = 0.02f,  advectedByFlow = false, initialValue = 0f },
            new() { name = "Flow_Y",         diffuseRate = 0.92f,  decayRate = 0.02f,  advectedByFlow = false, initialValue = 0f },
        };

        // Cross-field interaction rates
        [Header("Cross-Field Interactions")]
        [Range(0f, 0.1f)] public float wasteToNutrientRate = 0.005f;       // decomposition
        [Range(0f, 1f)]   public float temperatureToFlowStrength = 0.5f;   // convection
        [Range(0f, 1f)]   public float temperatureToPermeability = 0.3f;   // phase transitions

        [Header("Initial Matter Map")]
        [Range(0f, 10f)] public float noiseScale = 3f;
        [Range(0f, 1f)]  public float noiseThreshold = 0.3f;  // below this = solid (low permeability)
    }
}

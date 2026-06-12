using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    [Serializable]
    public class UmweltReadEntry
    {
        [BiomeChannelField] public int channel;    // BiomeChannel index to read
        [Range(-2f, 2f)] public float weight = 1f; // positive = attract, negative = repel
        public UmweltEffect effect = UmweltEffect.Chemotaxis;
    }

    [Serializable]
    public class UmweltWriteEntry
    {
        [BiomeChannelField] public int channel;     // BiomeChannel index to write
        [Range(-1f, 1f)] public float amount = 0.01f; // positive = deposit, negative = consume
    }

    public enum UmweltEffect
    {
        Chemotaxis,     // affects movement direction (sensor turns / food seeking)
        SpeedPenalty,   // scales movement speed down (permeability → speed multiplier, perception.g)
        Avoidance,      // hard repulsion (flee when above threshold, perception.b)
        SpeedBoost,     // accelerates agents (dispersal → speed burst, perception.a)
    }

    [CreateAssetMenu(fileName = "UmweltMapping", menuName = "Biomes/UmweltMapping")]
    public class UmweltMapping : ScriptableObject
    {
        [Header("What this species perceives")]
        public List<UmweltReadEntry> reads = new();

        [Header("What this species deposits/consumes")]
        public List<UmweltWriteEntry> writes = new();

        [Header("Habitat")]
        [Range(0f, 1f)] public float preferredPermeabilityMin = 0.5f;
        [Range(0f, 1f)] public float preferredPermeabilityMax = 1.0f;

        [Header("Metabolism")]
        [Range(0f, 0.1f)] public float metabolicHeat = 0.001f;  // heat deposited per agent per step
        [Range(0f, 0.1f)] public float oxygenConsumption = 0.001f;

        [Header("Lifecycle")]
        public bool enableDeath = false;
        [Range(0f, 1f)] public float deathThresholdOxygen = 0.05f;
        [Range(0f, 1f)] public float deathThresholdPermeability = 0.1f;
        [Range(0f, 1f)] public float corpseWasteAmount = 0.5f;
        [Range(0f, 0.1f)] public float corpseDecayRate = 0.01f;
    }
}

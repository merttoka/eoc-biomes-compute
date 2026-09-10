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

        // ─────────── Flat key access (ParameterInterpolator) ───────────
        // Keys per UmweltKeys: 8 scalars, "read:<ch>:<effect>.weight", "write:<ch>.amount".

        public IEnumerable<string> Keys
        {
            get
            {
                foreach (var s in UmweltKeys.Scalars) yield return s;
                foreach (var r in reads)  yield return UmweltKeys.Read(r.channel, (int)r.effect);
                foreach (var w in writes) yield return UmweltKeys.Write(w.channel);
            }
        }

        public bool TryGetValue(string key, out float value)
        {
            value = 0f;
            switch (key)
            {
                case UmweltKeys.PermMin:           value = preferredPermeabilityMin;  return true;
                case UmweltKeys.PermMax:           value = preferredPermeabilityMax;  return true;
                case UmweltKeys.MetabolicHeat:     value = metabolicHeat;             return true;
                case UmweltKeys.OxygenConsumption: value = oxygenConsumption;         return true;
                case UmweltKeys.DeathO2:           value = deathThresholdOxygen;      return true;
                case UmweltKeys.DeathPerm:         value = deathThresholdPermeability; return true;
                case UmweltKeys.CorpseWaste:       value = corpseWasteAmount;         return true;
                case UmweltKeys.CorpseDecay:       value = corpseDecayRate;           return true;
            }
            if (UmweltKeys.TryParseRead(key, out int rch, out int rfx))
            {
                var r = FindRead(rch, rfx);
                if (r == null) return false;
                value = r.weight; return true;
            }
            if (UmweltKeys.TryParseWrite(key, out int wch))
            {
                var w = FindWrite(wch);
                if (w == null) return false;
                value = w.amount; return true;
            }
            return false;
        }

        public float GetValue(string key) => TryGetValue(key, out float v) ? v : 0f;

        public void SetValue(string key, float value)
        {
            switch (key)
            {
                case UmweltKeys.PermMin:           preferredPermeabilityMin   = value; return;
                case UmweltKeys.PermMax:           preferredPermeabilityMax   = value; return;
                case UmweltKeys.MetabolicHeat:     metabolicHeat              = value; return;
                case UmweltKeys.OxygenConsumption: oxygenConsumption          = value; return;
                case UmweltKeys.DeathO2:           deathThresholdOxygen       = value; return;
                case UmweltKeys.DeathPerm:         deathThresholdPermeability = value; return;
                case UmweltKeys.CorpseWaste:       corpseWasteAmount          = value; return;
                case UmweltKeys.CorpseDecay:       corpseDecayRate            = value; return;
            }
            if (UmweltKeys.TryParseRead(key, out int rch, out int rfx))
            {
                var r = FindRead(rch, rfx);
                if (r == null)
                {
                    r = new UmweltReadEntry { channel = rch, effect = (UmweltEffect)rfx };
                    reads.Add(r);
                }
                r.weight = value;
                return;
            }
            if (UmweltKeys.TryParseWrite(key, out int wch))
            {
                var w = FindWrite(wch);
                if (w == null)
                {
                    w = new UmweltWriteEntry { channel = wch };
                    writes.Add(w);
                }
                w.amount = value;
            }
        }

        public bool RemoveEntry(string key)
        {
            if (UmweltKeys.TryParseRead(key, out int rch, out int rfx))
                return reads.RemoveAll(r => r.channel == rch && (int)r.effect == rfx) > 0;
            if (UmweltKeys.TryParseWrite(key, out int wch))
                return writes.RemoveAll(w => w.channel == wch) > 0;
            return false;
        }

        public void Snapshot(Dictionary<string, float> into)
        {
            into.Clear();
            foreach (var k in Keys)
            {
                if (into.ContainsKey(k)) Debug.LogWarning($"UmweltMapping '{name}': duplicate entry {k} — only the first is interpolated; both are removed together.");
                into[k] = GetValue(k);
            }
        }

        private UmweltReadEntry FindRead(int channel, int effect)
        {
            foreach (var r in reads) if (r.channel == channel && (int)r.effect == effect) return r;
            return null;
        }

        private UmweltWriteEntry FindWrite(int channel)
        {
            foreach (var w in writes) if (w.channel == channel) return w;
            return null;
        }
    }
}

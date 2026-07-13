using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    [Serializable]
    public class TermiteAgentType
    {
        public float senseAngle = 45f;        // degrees (→ radians on upload)
        public float senseDistance = 20f;
        public float turnAngle = 15f;         // degrees (→ radians on upload)
        public float moveSpeed = 0.5f;
        public float firingSpeedMul = 2f;
        public float depositAmount = 0.3f;
        public float firingDepositAmount = 3f; // > 1 → rendered toward white
        public float depositProbability = 0.2f;
        public float firingDepositProbability = 0.3f;
        public float diffuseRate = 0.97f;
        public float hue = 0.6f;              // blue-ish default
        public float saturation = 0.7f;
    }

    [CreateAssetMenu(fileName = "TermiteParams", menuName = "Biomes/TermiteParams")]
    public class TermiteParams : ScriptableObject, IParamSet
    {
        [Range(1, 8)] public int typeCount = 1;
        public List<TermiteAgentType> types = new() { new TermiteAgentType() };

        [Header("MIDI/OSC Ranges (min/max for 0-1 mapping)")]
        public List<ParamRange> ranges = new()
        {
            new("moveSpeed",                0.05f, 10f),
            new("senseAngle",               0.1f,  180f),
            new("turnAngle",                0.1f,  180f),
            new("senseDistance",            0.1f,  200f),
            new("depositAmount",            0.01f, 1f),
            new("firingDepositAmount",      1f,    4f),
            new("depositProbability",       0f,    1f),
            new("firingDepositProbability", 0f,    1f),
            new("firingSpeedMul",           1f,    5f),
            new("diffuseRate",              0.9f,  1f),
            new("hue",                      0f,    1f),
            new("saturation",               0f,    1f),
        };

        public (float min, float max) GetRange(string paramName)
            => ParamRangeUtil.GetRange(ranges, paramName);

        public int TypeCount => types.Count;

        public float GetValue(string name, int typeIndex)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return 0f;
            var t = types[typeIndex];
            return name switch
            {
                "moveSpeed"                => t.moveSpeed,
                "senseAngle"               => t.senseAngle,
                "turnAngle"                => t.turnAngle,
                "senseDistance"            => t.senseDistance,
                "depositAmount"            => t.depositAmount,
                "firingDepositAmount"      => t.firingDepositAmount,
                "depositProbability"       => t.depositProbability,
                "firingDepositProbability" => t.firingDepositProbability,
                "firingSpeedMul"           => t.firingSpeedMul,
                "diffuseRate"              => t.diffuseRate,
                "hue"                      => t.hue,
                "saturation"               => t.saturation,
                _ => 0f,
            };
        }

        public void SetValue(string name, int typeIndex, float raw)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return;
            var t = types[typeIndex];
            switch (name)
            {
                case "moveSpeed":                t.moveSpeed = raw; break;
                case "senseAngle":               t.senseAngle = raw; break;
                case "turnAngle":                t.turnAngle = raw; break;
                case "senseDistance":            t.senseDistance = raw; break;
                case "depositAmount":            t.depositAmount = raw; break;
                case "firingDepositAmount":      t.firingDepositAmount = raw; break;
                case "depositProbability":       t.depositProbability = raw; break;
                case "firingDepositProbability": t.firingDepositProbability = raw; break;
                case "firingSpeedMul":           t.firingSpeedMul = raw; break;
                case "diffuseRate":              t.diffuseRate = raw; break;
                case "hue":                      t.hue = raw; break;
                case "saturation":               t.saturation = raw; break;
            }
        }

        public void SyncTypesList()
        {
            while (types.Count < typeCount) types.Add(new TermiteAgentType());
            while (types.Count > typeCount) types.RemoveAt(types.Count - 1);
        }

        public void ResetToDefaults()
        {
            typeCount = 1;
            types.Clear();
            types.Add(new TermiteAgentType());
        }

        // Pixel-unit distance params → ×k for resolution-independence (see IParamSet).
        public void ScaleSpatial(float k)
        {
            foreach (var t in types)
            {
                t.senseDistance *= k;
                t.moveSpeed     *= k;
            }
        }

        // Trail-density param → ×k so visual density holds across resolutions (see IParamSet).
        // Termite has no separate eatAmount; depositProbability is a probability, left unscaled.
        public void ScaleDensity(float k)
        {
            foreach (var t in types)
                t.depositAmount *= k;
        }

        public void RandomizeParams()
        {
            foreach (var t in types)
            {
                var r = GetRange("senseAngle");    t.senseAngle    = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("senseDistance");     t.senseDistance = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("turnAngle");         t.turnAngle     = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("moveSpeed");         t.moveSpeed     = UnityEngine.Random.Range(r.min, r.max);
            }
        }

        public void RandomizeColors()
        {
            var palette = ColorPalette.GenerateHS(types.Count);
            for (int i = 0; i < types.Count && i < palette.Count; i++)
            {
                types[i].hue = palette[i].hue;
                types[i].saturation = palette[i].saturation;
            }
        }
    }
}

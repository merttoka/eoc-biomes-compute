using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    [Serializable]
    public class PhysarumAgentType
    {
        public float senseAngle = 22.5f;
        public float senseDistance = 9f;
        public float turnAngle = 45f;
        public float moveSpeed = 1.0f;
        public float depositAmount = 0.02f;
        public float eatAmount = 0.01f;
        public float diffuseRate = 0.985f;
        public float hue = 0f;
        public float saturation = 0.5f;
        public float firingSpeedMul = 2f;
        public float firingDepositAmount = 1f;
    }

    [CreateAssetMenu(fileName = "PhysarumParams", menuName = "Biomes/PhysarumParams")]
    public class PhysarumParams : ScriptableObject, IParamSet
    {
        [Range(1, 8)] public int typeCount = 2;
        public List<PhysarumAgentType> types = new()
        {
            new PhysarumAgentType(),
            new PhysarumAgentType(),
        };

        [Header("MIDI/OSC Ranges (min/max for 0-1 mapping)")]
        public List<ParamRange> ranges = new()
        {
            new("moveSpeed",     0.1f,  50f),
            new("senseAngle",    0.1f,  360f),
            new("turnAngle",     0.1f,  360f),
            new("senseDistance",  0.1f,  200f),
            new("depositAmount", 0.01f, 1f),
            new("eatAmount",     0f,    0.1f),
            new("diffuseRate",   0.9f,  1f),
            new("hue",           0f,    1f),
            new("saturation",    0f,    1f),
            new("firingSpeedMul",      1f,  5f),
            new("firingDepositAmount", 0f,  1f),
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
                "moveSpeed"     => t.moveSpeed,
                "senseAngle"    => t.senseAngle,
                "turnAngle"     => t.turnAngle,
                "senseDistance" => t.senseDistance,
                "depositAmount" => t.depositAmount,
                "eatAmount"     => t.eatAmount,
                "diffuseRate"   => t.diffuseRate,
                "hue"           => t.hue,
                "saturation"    => t.saturation,
                _ => 0f,
            };
        }

        public void SetValue(string name, int typeIndex, float raw)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return;
            var t = types[typeIndex];
            switch (name)
            {
                case "moveSpeed":     t.moveSpeed     = raw; break;
                case "senseAngle":    t.senseAngle    = raw; break;
                case "turnAngle":     t.turnAngle     = raw; break;
                case "senseDistance": t.senseDistance = raw; break;
                case "depositAmount": t.depositAmount = raw; break;
                case "eatAmount":     t.eatAmount     = raw; break;
                case "diffuseRate":   t.diffuseRate   = raw; break;
                case "hue":           t.hue           = raw; break;
                case "saturation":    t.saturation    = raw; break;
            }
        }

        public void SyncTypesList()
        {
            while (types.Count < typeCount) types.Add(new PhysarumAgentType());
            while (types.Count > typeCount) types.RemoveAt(types.Count - 1);
        }

        public void ResetToDefaults()
        {
            typeCount = 2;
            types.Clear();
            for (int i = 0; i < 2; i++)
                types.Add(new PhysarumAgentType());
        }

        public void RandomizeParams()
        {
            foreach (var t in types)
            {
                var r = GetRange("senseAngle");    t.senseAngle    = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("senseDistance");      t.senseDistance  = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("turnAngle");          t.turnAngle     = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("moveSpeed");          t.moveSpeed     = UnityEngine.Random.Range(r.min, r.max);
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

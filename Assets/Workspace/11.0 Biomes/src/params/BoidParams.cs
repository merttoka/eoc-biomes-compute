using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    [Serializable]
    public class BoidAgentType
    {
        public float separationRange = 10f;
        public float alignmentRange = 10f;
        public float attractionRange = 10f;
        public float maxSpeed = 1f;
        public float maxForce = 1f;
        public float depositAmount = 0.03f;
        public float eatAmount = 0.01f;
        public float foodSensorDistance = 15f;
        public float sensorAngleRad = Mathf.PI / 6f;
        public float foodSeekingStrength = 0.5f;
        public float diffuseRate = 0.985f;
        public float hue = 0f;
        public float saturation = 0.5f;
        public float firingSpeedMul = 2f;
        public float firingDepositAmount = 1f;
    }

    [CreateAssetMenu(fileName = "BoidParams", menuName = "Biomes/BoidParams")]
    public class BoidParams : ScriptableObject, IParamSet
    {
        [Range(1, 8)] public int typeCount = 1;
        public List<BoidAgentType> types = new() { new BoidAgentType() };

        [Header("MIDI/OSC Ranges (min/max for 0-1 mapping)")]
        public List<ParamRange> ranges = new()
        {
            new("maxSpeed",        0.1f,  5f),
            new("maxForce",        0.1f,  5f),
            new("separateRange",   1f,    500f),
            new("alignRange",      1f,    500f),
            new("attractRange",    1f,    500f),
            new("depositAmount",   0f,    1f),
            new("eatAmount",       0f,    1f),
            new("foodSeek",        0f,    5f),
            new("hue",             0f,    1f),
            new("saturation",      0f,    1f),
            new("diffuseRate",     0.9f,  1f),
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
                "separateRange" => t.separationRange,
                "alignRange"    => t.alignmentRange,
                "attractRange"  => t.attractionRange,
                "maxSpeed"      => t.maxSpeed,
                "maxForce"      => t.maxForce,
                "depositAmount" => t.depositAmount,
                "eatAmount"     => t.eatAmount,
                "foodSeek"      => t.foodSeekingStrength,
                "hue"           => t.hue,
                "saturation"    => t.saturation,
                "diffuseRate"   => t.diffuseRate,
                _ => 0f,
            };
        }

        public void SetValue(string name, int typeIndex, float raw)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return;
            var t = types[typeIndex];
            switch (name)
            {
                case "separateRange": t.separationRange     = raw; break;
                case "alignRange":    t.alignmentRange      = raw; break;
                case "attractRange":  t.attractionRange     = raw; break;
                case "maxSpeed":      t.maxSpeed            = raw; break;
                case "maxForce":      t.maxForce           = raw; break;
                case "depositAmount": t.depositAmount       = raw; break;
                case "eatAmount":     t.eatAmount           = raw; break;
                case "foodSeek":      t.foodSeekingStrength = raw; break;
                case "hue":           t.hue                = raw; break;
                case "saturation":    t.saturation          = raw; break;
                case "diffuseRate":   t.diffuseRate         = raw; break;
            }
        }

        public void SyncTypesList()
        {
            while (types.Count < typeCount) types.Add(new BoidAgentType());
            while (types.Count > typeCount) types.RemoveAt(types.Count - 1);
        }

        public void ResetToDefaults()
        {
            typeCount = 1;
            types.Clear();
            types.Add(new BoidAgentType());
        }

        // Pixel-unit distance params → ×k for resolution-independence (see IParamSet). Ranges
        // are uploaded squared (sep*sep), so scaling the linear field here gives the correct k².
        public void ScaleSpatial(float k)
        {
            foreach (var t in types)
            {
                t.separationRange    *= k;
                t.alignmentRange     *= k;
                t.attractionRange    *= k;
                t.maxSpeed           *= k;
                t.maxForce           *= k;
                t.foodSensorDistance *= k;
            }
        }

        // Trail-density params → ×k so visual density holds across resolutions (see IParamSet).
        public void ScaleDensity(float k)
        {
            foreach (var t in types)
            {
                t.depositAmount *= k;
                t.eatAmount     *= k;
            }
        }

        public void RandomizeParams()
        {
            foreach (var t in types)
            {
                var r = GetRange("separateRange");  t.separationRange = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("alignRange");          t.alignmentRange  = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("attractRange");        t.attractionRange = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("maxSpeed");            t.maxSpeed        = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("maxForce");            t.maxForce        = UnityEngine.Random.Range(r.min, r.max);
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

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
    }

    [CreateAssetMenu(fileName = "BoidParams", menuName = "Biomes/BoidParams")]
    public class BoidParams : ScriptableObject
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
        };

        public (float min, float max) GetRange(string paramName)
            => ParamRangeUtil.GetRange(ranges, paramName);

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

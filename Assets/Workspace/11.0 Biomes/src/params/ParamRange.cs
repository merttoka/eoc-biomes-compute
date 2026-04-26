using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    [Serializable]
    public class ParamRange
    {
        public string name;
        public float min;
        public float max;

        public ParamRange(string name, float min, float max)
        {
            this.name = name;
            this.min = min;
            this.max = max;
        }
    }

    /// <summary>
    /// Lookup helper. Call GetRange(paramName) to get (min, max).
    /// Used by SetParameter (maps 0-1 MIDI → min-max) and Randomize.
    /// </summary>
    public static class ParamRangeUtil
    {
        public static (float min, float max) GetRange(List<ParamRange> ranges, string paramName)
        {
            if (ranges != null)
            {
                for (int i = 0; i < ranges.Count; i++)
                {
                    if (ranges[i].name == paramName)
                        return (ranges[i].min, ranges[i].max);
                }
            }
            return (0f, 1f);
        }
    }
}

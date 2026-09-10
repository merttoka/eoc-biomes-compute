using System.Collections.Generic;

namespace Biomes
{
    /// <summary>
    /// Crossfade between two name→float snapshots whose key sets may differ (umwelt
    /// read/write entries come and go between waypoints). A key missing on one side counts
    /// as 0, so new entries fade in from 0 and dropped entries fade out to 0.
    /// Pure — unit-tested in Biomes.Core.
    /// </summary>
    public static class KeyedCrossfade
    {
        public static List<string> UnionKeys(IReadOnlyDictionary<string, float> from,
                                             IReadOnlyDictionary<string, float> to)
        {
            var keys = new List<string>(from.Count + to.Count);
            foreach (var k in from.Keys) keys.Add(k);
            foreach (var k in to.Keys) if (!from.ContainsKey(k)) keys.Add(k);
            return keys;
        }

        public static float Lerp(IReadOnlyDictionary<string, float> from,
                                 IReadOnlyDictionary<string, float> to,
                                 string key, float t)
        {
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float a = from.TryGetValue(key, out float fa) ? fa : 0f;
            float b = to.TryGetValue(key, out float tb) ? tb : 0f;
            return a + (b - a) * t;
        }

        public static List<string> KeysToRemove(IReadOnlyDictionary<string, float> from,
                                                IReadOnlyDictionary<string, float> to)
        {
            var gone = new List<string>();
            foreach (var k in from.Keys) if (!to.ContainsKey(k)) gone.Add(k);
            return gone;
        }
    }
}

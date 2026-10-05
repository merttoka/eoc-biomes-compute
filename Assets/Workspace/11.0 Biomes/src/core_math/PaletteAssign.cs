using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>Which agent sim a palette swatch is for. <see cref="Any"/> = untagged, serves every sim.
    /// Serialized as int — append only.</summary>
    public enum AgentFamily { Any, Physarum, Boid, Termite }

    /// <summary>One palette color: what a type shows on screen at full trail intensity (sRGB),
    /// optionally tagged with the sim family it belongs to.</summary>
    [Serializable]
    public class PaletteSwatch
    {
        public Color color = Color.white;
        public AgentFamily family;

        public PaletteSwatch() { }
        public PaletteSwatch(Color color, AgentFamily family) { this.color = color; this.family = family; }
    }

    /// <summary>
    /// Palette → agent type assignment. A family draws from the swatches tagged with it; if there
    /// are none, from the untagged ones; if there are none either, from all of them. Type i takes
    /// the (i mod n)-th, so a show palette tagged per sim reproduces each sim, and an untagged
    /// curated palette serves every sim.
    /// </summary>
    public static class PaletteAssign
    {
        /// <summary>Index into <paramref name="swatches"/> for agent type <paramref name="typeIndex"/>;
        /// −1 when the palette has no swatches.</summary>
        public static int SwatchIndex(IReadOnlyList<PaletteSwatch> swatches, AgentFamily family, int typeIndex)
        {
            if (swatches == null || swatches.Count == 0 || typeIndex < 0) return -1;
            var pool = PoolFor(swatches, family);
            int n = 0;
            for (int i = 0; i < swatches.Count; i++)
                if (InPool(swatches[i], pool)) n++;
            int want = typeIndex % n;
            for (int i = 0, k = 0; i < swatches.Count; i++)
                if (InPool(swatches[i], pool) && k++ == want) return i;
            return -1;
        }

        /// <summary>True when swatch <paramref name="index"/> is one that <paramref name="family"/> draws from.</summary>
        public static bool IsCandidate(IReadOnlyList<PaletteSwatch> swatches, AgentFamily family, int index) =>
            InPool(swatches[index], PoolFor(swatches, family));

        /// <summary>Replaces the swatches tagged <paramref name="family"/> with <paramref name="colors"/>,
        /// in order, where the first of them was (appended when the family had none).</summary>
        public static void StoreFamily(List<PaletteSwatch> swatches, AgentFamily family, IReadOnlyList<Color> colors)
        {
            int at = swatches.FindIndex(s => s.family == family);
            if (at < 0) at = swatches.Count;
            swatches.RemoveAll(s => s.family == family);   // all at or after `at`: the index still holds
            for (int i = 0; i < colors.Count; i++)
                swatches.Insert(at + i, new PaletteSwatch(colors[i], family));
        }

        /// <summary>Steps a cycle over Preset (−1) and palettes 0..count−1, wrapping both ways.</summary>
        public static int StepCycle(int active, int count, int delta)
        {
            int stops = count + 1;
            return ((active + 1 + delta) % stops + stops) % stops - 1;
        }

        // The family's own tag, else Any, else every swatch (null).
        private static AgentFamily? PoolFor(IReadOnlyList<PaletteSwatch> swatches, AgentFamily family)
        {
            if (family != AgentFamily.Any && Has(swatches, family)) return family;
            if (Has(swatches, AgentFamily.Any)) return AgentFamily.Any;
            return null;
        }

        private static bool InPool(PaletteSwatch s, AgentFamily? pool) => pool == null || s.family == pool.Value;

        private static bool Has(IReadOnlyList<PaletteSwatch> swatches, AgentFamily tag)
        {
            for (int i = 0; i < swatches.Count; i++)
                if (swatches[i].family == tag) return true;
            return false;
        }
    }
}

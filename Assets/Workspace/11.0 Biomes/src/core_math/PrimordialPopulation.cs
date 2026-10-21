using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// How many primordial particles a sim gets and how they split into species. One source of truth
    /// for PrimordialSim's allocation, its reset kernel's type assignment (it receives
    /// <see cref="TypeEnds"/>) and the inspector's population readout.
    /// </summary>
    public static class PrimordialPopulation
    {
        public const int MinCount = 64, MaxCount = 4_000_000;

        /// <summary>Density bands, in particles per su² counted the way each particle counts its
        /// neighbours (<see cref="FeltDensity"/>). Measured in 11.4 (scatter, 60 s): cells emerge from
        /// a random soup at ≳ <see cref="EmergeMin"/> (the paper's soup is 0.08); at 0.06 a species
        /// stays soup unless spawned around the neurons, where seeded spores take hold (34 % in cells
        /// after 45 s). The paper's injected cells survive down to ~<see cref="SurviveMin"/>. Above
        /// <see cref="TissueAbove"/> cells crowd into tissue.</summary>
        public const float SurviveMin = 0.03f, EmergeMin = 0.07f, TissueAbove = 0.18f;

        public static Vector2 WorldSu(int rezX, int rezY, float unitPx)
        {
            float u = Mathf.Max(0.01f, unitPx);
            return new Vector2(rezX / u, rezY / u);
        }

        public static int Count(float density, Vector2 worldSu) =>
            (int)Math.Min(MaxCount, Math.Max(MinCount, Math.Round(density * (double)worldSu.x * worldSu.y)));

        /// <summary>Exclusive end index of each type's id range: type t owns ids in
        /// [ends[t − 1], ends[t]), sized by its relative share (non-positive shares get none). The last
        /// end is <paramref name="count"/>. All shares ≤ 0 split evenly.</summary>
        public static int[] TypeEnds(int count, IReadOnlyList<float> shares)
        {
            int types = shares.Count;
            var ends = new int[types];
            double total = 0;
            foreach (var s in shares) total += Math.Max(0f, s);
            double cumulative = 0;
            for (int t = 0; t < types; t++)
            {
                cumulative += total > 0 ? Math.Max(0f, shares[t]) / total : 1.0 / types;
                ends[t] = t == types - 1 ? count : (int)Math.Round(cumulative * count);
            }
            return ends;
        }

        /// <summary>The type of particle <paramref name="id"/> — the reset kernel's rule.</summary>
        public static int TypeOf(int id, int[] ends)
        {
            int t = 0;
            while (t < ends.Length - 1 && id >= ends[t]) t++;
            return t;
        }

        /// <summary>The density type <paramref name="type"/> perceives: its own, plus every other
        /// type's weighted by its <c>otherTypeWeight</c> (how it counts them in L / R). With weight 0
        /// a species lives alone at its own share of the density; with 1 all share one physics.</summary>
        public static float FeltDensity(int type, IReadOnlyList<float> ownDensity, float otherTypeWeight)
        {
            float felt = ownDensity[type];
            for (int j = 0; j < ownDensity.Count; j++)
                if (j != type) felt += otherTypeWeight * ownDensity[j];
            return felt;
        }
    }
}

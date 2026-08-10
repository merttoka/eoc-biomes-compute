using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Cell-grid sizing for field sims. Pure math with no scene dependencies so it can be
    /// unit-tested — the same reason <see cref="NeuronLayout"/> lives in this assembly.
    ///
    /// <para><b>Height is absolute, width is derived.</b> The grid height is authored in cells
    /// and the width comes from the master's aspect. This is the whole point of the type:
    /// <c>cellResolutionScale</c> was a fraction of master, so changing output resolution
    /// silently changed the automaton's on-screen cell size. The rule never changed; the
    /// picture did.</para>
    ///
    /// <para>Note the cost on wide canvases: at 11.84:1 an absolute height of 540 gives a
    /// 5683-wide grid (3.07 M cells). Aspect preservation means width scales with the canvas,
    /// so ultra-wide scenes want a smaller height.</para>
    /// </summary>
    public static class CellGrid
    {
        /// <summary>Smallest grid any field sim will allocate, per axis.</summary>
        public const int MinRez = 8;

        public static int Height(int cellRezHeight) => Mathf.Max(MinRez, cellRezHeight);

        /// <summary>
        /// Width preserving the master's aspect at the given cell height. A degenerate master
        /// (either axis at or below zero) falls back to a square grid rather than dividing by zero.
        /// </summary>
        public static int Width(int cellRezHeight, int masterRezX, int masterRezY)
        {
            int h = Height(cellRezHeight);
            if (masterRezX <= 0 || masterRezY <= 0) return h;
            return Mathf.Max(MinRez, Mathf.RoundToInt(h * (float)masterRezX / masterRezY));
        }

        /// <summary>Total cells — the number that actually costs GPU time.</summary>
        public static int CellCount(int cellRezHeight, int masterRezX, int masterRezY)
            => Width(cellRezHeight, masterRezX, masterRezY) * Height(cellRezHeight);
    }
}

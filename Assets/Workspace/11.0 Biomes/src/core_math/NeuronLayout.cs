using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// The neuron-layout mapping: where a neuron's normalized CSV position lands in the
    /// field. Pure math, no scene dependencies, so it can be unit-tested — which is the
    /// point. This is the CPU mirror of <c>src/computes/Includes/neuron_layout.hlsl</c>;
    /// the two must agree, and <c>NeuronLayoutTests</c> asserts it.
    ///
    /// The scale itself is NOT stored here — it is owned by
    /// <see cref="NeuronFiringSource.spawnScale"/>, the single authored copy. This class
    /// only knows how to apply one.
    ///
    /// History: the mapping was previously written out in five places and its scale
    /// declared in three separate serialized fields, each with a tooltip asking the author
    /// to keep them in sync by hand. They desynced — 11.2 SIGGRAPH and 11.3 DAC ran sims at
    /// (0.5, 0.6) while the firing rings and dispersal stamps stayed at (0.4, 0.75).
    /// </summary>
    public static class NeuronLayout
    {
        /// <summary>Default when no NeuronFiringSource is wired (the historical value).</summary>
        public static readonly Vector2 DefaultScale = new Vector2(0.8f, 0.9f);

        /// <summary>
        /// Normalized neuron position (0..1) -> normalized field UV. The layout is scaled
        /// about the canvas centre, so scale (0.5, 0.6) places the neuron cloud in the
        /// middle 50% x 60% of the field and the centre is a fixed point at every scale.
        /// Mirrors HLSL <c>NeuronToFieldUV</c>.
        /// </summary>
        public static Vector2 ToFieldUV(Vector2 npNorm, Vector2 scale) => new Vector2(
            npNorm.x * scale.x + (1f - scale.x) * 0.5f,
            npNorm.y * scale.y + (1f - scale.y) * 0.5f);

        /// <summary>
        /// Pixel-space neuron position -> pixel-space field position. Algebraically
        /// <c>ToFieldUV(npPx / rez, scale) * rez</c>, without the round trip — the sims
        /// pre-multiply by rez when they upload, so they arrive in this space.
        /// Mirrors HLSL <c>NeuronPxToFieldPx</c>.
        /// </summary>
        public static Vector2 PxToFieldPx(Vector2 npPx, Vector2 scale, float rezX, float rezY) => new Vector2(
            npPx.x * scale.x + rezX * (1f - scale.x) * 0.5f,
            npPx.y * scale.y + rezY * (1f - scale.y) * 0.5f);
    }
}

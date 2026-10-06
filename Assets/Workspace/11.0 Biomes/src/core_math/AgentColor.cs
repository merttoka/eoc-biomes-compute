using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Agent color conversions that match the render kernels exactly. <see cref="ToRgb"/> ports
    /// <c>hsb2rgb</c> from <c>computes/includes/color.hlsl</c> line for line; its hue channels are
    /// smoothstepped, so between primaries it differs from <see cref="Color.HSVToRGB(float,float,float)"/>.
    /// <see cref="FromRgb"/> is its exact inverse, so a color picked in the Editor renders as picked.
    /// </summary>
    public static class AgentColor
    {
        /// <summary>The rendered color of hue / saturation / brightness (each 0..1), alpha 1.</summary>
        public static Color ToRgb(float h, float s, float b) =>
            new Color(Channel(h, s, b, 0f), Channel(h, s, b, 4f), Channel(h, s, b, 2f));

        private static float Channel(float h, float s, float b, float offset)
        {
            float p = Mathf.Clamp01(Mathf.Abs((h * 6f + offset) % 6f - 3f) - 1f);
            float q = p * p * (3f - 2f * p);
            return b * (1f - s + s * q);   // = b * lerp(1, q, s)
        }

        /// <summary>
        /// The hue / saturation / brightness that render as <paramref name="c"/>. A grey has no hue
        /// and keeps <paramref name="fallbackHue"/>; black has neither hue nor saturation and keeps both.
        /// </summary>
        public static (float h, float s, float b) FromRgb(Color c, float fallbackHue = 0f, float fallbackSat = 0f)
        {
            float r = Mathf.Clamp01(c.r), g = Mathf.Clamp01(c.g), bl = Mathf.Clamp01(c.b);
            float max = Mathf.Max(r, Mathf.Max(g, bl));
            float min = Mathf.Min(r, Mathf.Min(g, bl));
            if (max <= 0f) return (fallbackHue, fallbackSat, 0f);
            float range = max - min;
            if (range <= 0f) return (fallbackHue, 0f, max);

            // The max channel picks the sextant pair, the middle channel's smoothstepped
            // fraction (inverted) places the hue inside it.
            float h6;
            if (max == r)
                h6 = g >= bl ? InvSmooth((g - min) / range) : 6f - InvSmooth((bl - min) / range);
            else if (max == g)
                h6 = r >= bl ? 2f - InvSmooth((r - min) / range) : 2f + InvSmooth((bl - min) / range);
            else
                h6 = g >= r ? 4f - InvSmooth((g - min) / range) : 4f + InvSmooth((r - min) / range);

            float h = h6 / 6f;
            if (h >= 1f) h -= 1f;
            return (h, range / max, max);
        }

        /// <summary>
        /// The color a type shows on screen: kernels write linear light, which the display
        /// gamma-encodes (linear color space). Palettes, the color picker, the eyedropper and hex
        /// codes all speak this sRGB color.
        /// </summary>
        public static Color ToDisplay(float h, float s, float b) => ToRgb(h, s, b).gamma;

        /// <summary>The hue / saturation / brightness that show on screen as <paramref name="c"/>
        /// (inverse of <see cref="ToDisplay"/>; fallbacks as in <see cref="FromRgb"/>).</summary>
        public static (float h, float s, float b) FromDisplay(Color c, float fallbackHue = 0f, float fallbackSat = 0f) =>
            FromRgb(c.linear, fallbackHue, fallbackSat);

        // Inverse of the smoothstep cubic 3p² − 2p³ on [0, 1].
        private static float InvSmooth(float q) =>
            0.5f - Mathf.Sin(Mathf.Asin(Mathf.Clamp(1f - 2f * q, -1f, 1f)) / 3f);
    }
}

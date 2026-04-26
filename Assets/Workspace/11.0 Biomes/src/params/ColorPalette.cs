using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Perceptually-distinct color palette generator based on Colorgorical (Brown VRL).
    /// Uses CIEDE2000 distance in CIE Lab space with greedy max-distance selection.
    /// </summary>
    public static class ColorPalette
    {
        #region Public API

        /// <summary>
        /// Generate a palette of n perceptually distinct colors.
        /// Returns colors as (hue 0-1, saturation 0-1) pairs for use with sim params.
        /// </summary>
        public static List<(float hue, float saturation)> GenerateHS(int count,
            float lightnessMin = 35f, float lightnessMax = 80f,
            float hueMinDeg = 0f, float hueMaxDeg = 360f,
            int candidateCount = 2000)
        {
            var labPalette = GenerateLab(count, lightnessMin, lightnessMax,
                hueMinDeg, hueMaxDeg, candidateCount);

            var result = new List<(float, float)>();
            foreach (var lab in labPalette)
            {
                var rgb = LabToRGB(lab);
                Color.RGBToHSV(rgb, out float h, out float s, out float _);
                result.Add((h, s));
            }
            return result;
        }

        /// <summary>
        /// Generate a palette of n perceptually distinct colors as Unity Colors.
        /// </summary>
        public static List<Color> GenerateRGB(int count,
            float lightnessMin = 35f, float lightnessMax = 80f,
            float hueMinDeg = 0f, float hueMaxDeg = 360f,
            int candidateCount = 2000)
        {
            var labPalette = GenerateLab(count, lightnessMin, lightnessMax,
                hueMinDeg, hueMaxDeg, candidateCount);

            var result = new List<Color>();
            foreach (var lab in labPalette)
                result.Add(LabToRGB(lab));
            return result;
        }

        /// <summary>
        /// Generate palette in CIE Lab space using greedy CIEDE2000 max-distance selection.
        /// </summary>
        public static List<Vector3> GenerateLab(int count,
            float lightnessMin = 35f, float lightnessMax = 80f,
            float hueMinDeg = 0f, float hueMaxDeg = 360f,
            int candidateCount = 2000)
        {
            // Build candidate pool in Lab space
            var candidates = new List<Vector3>();
            for (int i = 0; i < candidateCount; i++)
            {
                // Sample in LCH for better hue distribution, convert to Lab
                float L = UnityEngine.Random.Range(lightnessMin, lightnessMax);
                float C = UnityEngine.Random.Range(10f, 100f);
                float H = SampleHue(hueMinDeg, hueMaxDeg);
                float hRad = H * Mathf.Deg2Rad;

                var lab = new Vector3(L, C * Mathf.Cos(hRad), C * Mathf.Sin(hRad));

                // Filter: must be valid RGB
                var rgb = LabToRGB(lab);
                if (rgb.r >= 0f && rgb.r <= 1f && rgb.g >= 0f && rgb.g <= 1f && rgb.b >= 0f && rgb.b <= 1f)
                    candidates.Add(lab);
            }

            if (candidates.Count == 0) return new List<Vector3>();

            // Greedy selection: pick the most distant color from the current palette
            var palette = new List<Vector3>();

            // Start with a random candidate
            int startIdx = UnityEngine.Random.Range(0, candidates.Count);
            palette.Add(candidates[startIdx]);
            candidates.RemoveAt(startIdx);

            while (palette.Count < count && candidates.Count > 0)
            {
                float bestScore = -1f;
                int bestIdx = 0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    // Score = minimum CIEDE2000 distance to any palette color
                    float minDist = float.MaxValue;
                    for (int j = 0; j < palette.Count; j++)
                    {
                        float d = CIEDE2000(candidates[i], palette[j]);
                        if (d < minDist) minDist = d;
                    }

                    if (minDist > bestScore)
                    {
                        bestScore = minDist;
                        bestIdx = i;
                    }
                }

                palette.Add(candidates[bestIdx]);
                candidates.RemoveAt(bestIdx);
            }

            return palette;
        }

        #endregion

        #region Hue Sampling

        private static float SampleHue(float minDeg, float maxDeg)
        {
            if (minDeg <= maxDeg)
                return UnityEngine.Random.Range(minDeg, maxDeg);
            // Wrap around (e.g. 300-60 = reds through magentas)
            float range = (360f - minDeg) + maxDeg;
            float h = UnityEngine.Random.Range(0f, range);
            return h < (360f - minDeg) ? minDeg + h : h - (360f - minDeg);
        }

        #endregion

        #region CIEDE2000

        /// <summary>
        /// CIEDE2000 color difference between two Lab colors.
        /// Implementation follows the standard (Sharma, Wu, Dalal 2005).
        /// </summary>
        public static float CIEDE2000(Vector3 lab1, Vector3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float Lbar = (L1 + L2) * 0.5f;
            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;

            float Cbar7 = Mathf.Pow(Cbar, 7f);
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + 6103515625f))); // 25^7
            float a1p = a1 * (1f + G);
            float a2p = a2 * (1f + G);

            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float Cbarp = (C1p + C2p) * 0.5f;

            float h1p = Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg;
            if (h1p < 0) h1p += 360f;
            float h2p = Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg;
            if (h2p < 0) h2p += 360f;

            float dhp;
            if (Mathf.Abs(h1p - h2p) <= 180f)
                dhp = h2p - h1p;
            else if (h2p - h1p > 180f)
                dhp = h2p - h1p - 360f;
            else
                dhp = h2p - h1p + 360f;

            float dHp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp * 0.5f * Mathf.Deg2Rad);

            float Hbarp;
            if (Mathf.Abs(h1p - h2p) <= 180f)
                Hbarp = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360f)
                Hbarp = (h1p + h2p + 360f) * 0.5f;
            else
                Hbarp = (h1p + h2p - 360f) * 0.5f;

            float dLp = L2 - L1;
            float dCp = C2p - C1p;

            float T = 1f
                - 0.17f * Mathf.Cos((Hbarp - 30f) * Mathf.Deg2Rad)
                + 0.24f * Mathf.Cos(2f * Hbarp * Mathf.Deg2Rad)
                + 0.32f * Mathf.Cos((3f * Hbarp + 6f) * Mathf.Deg2Rad)
                - 0.20f * Mathf.Cos((4f * Hbarp - 63f) * Mathf.Deg2Rad);

            float Lbar50sq = (Lbar - 50f) * (Lbar - 50f);
            float SL = 1f + 0.015f * Lbar50sq / Mathf.Sqrt(20f + Lbar50sq);
            float SC = 1f + 0.045f * Cbarp;
            float SH = 1f + 0.015f * Cbarp * T;

            float Cbarp7 = Mathf.Pow(Cbarp, 7f);
            float RC = 2f * Mathf.Sqrt(Cbarp7 / (Cbarp7 + 6103515625f));
            float dtheta = 30f * Mathf.Exp(-((Hbarp - 275f) / 25f) * ((Hbarp - 275f) / 25f));
            float RT = -Mathf.Sin(2f * dtheta * Mathf.Deg2Rad) * RC;

            float rL = dLp / SL;
            float rC = dCp / SC;
            float rH = dHp / SH;

            return Mathf.Sqrt(rL * rL + rC * rC + rH * rH + RT * rC * rH);
        }

        #endregion

        #region Color Space Conversion

        /// <summary>CIE Lab (D65) to Unity RGB Color.</summary>
        public static Color LabToRGB(Vector3 lab)
        {
            // Lab -> XYZ (D65 white point)
            float fy = (lab.x + 16f) / 116f;
            float fx = lab.y / 500f + fy;
            float fz = fy - lab.z / 200f;

            float x = fx > 0.206897f ? fx * fx * fx : (fx - 16f / 116f) / 7.787f;
            float y = fy > 0.206897f ? fy * fy * fy : (fy - 16f / 116f) / 7.787f;
            float z = fz > 0.206897f ? fz * fz * fz : (fz - 16f / 116f) / 7.787f;

            // D65 reference white
            x *= 0.95047f;
            z *= 1.08883f;

            // XYZ -> linear RGB
            float r = x * 3.2406f + y * -1.5372f + z * -0.4986f;
            float g = x * -0.9689f + y * 1.8758f + z * 0.0415f;
            float b = x * 0.0557f + y * -0.2040f + z * 1.0570f;

            // Linear -> sRGB gamma
            r = r > 0.0031308f ? 1.055f * Mathf.Pow(r, 1f / 2.4f) - 0.055f : 12.92f * r;
            g = g > 0.0031308f ? 1.055f * Mathf.Pow(g, 1f / 2.4f) - 0.055f : 12.92f * g;
            b = b > 0.0031308f ? 1.055f * Mathf.Pow(b, 1f / 2.4f) - 0.055f : 12.92f * b;

            return new Color(r, g, b, 1f);
        }

        /// <summary>Unity RGB Color to CIE Lab (D65).</summary>
        public static Vector3 RGBToLab(Color c)
        {
            // sRGB gamma -> linear
            float r = c.r > 0.04045f ? Mathf.Pow((c.r + 0.055f) / 1.055f, 2.4f) : c.r / 12.92f;
            float g = c.g > 0.04045f ? Mathf.Pow((c.g + 0.055f) / 1.055f, 2.4f) : c.g / 12.92f;
            float b = c.b > 0.04045f ? Mathf.Pow((c.b + 0.055f) / 1.055f, 2.4f) : c.b / 12.92f;

            // Linear RGB -> XYZ (D65)
            float x = (r * 0.4124f + g * 0.3576f + b * 0.1805f) / 0.95047f;
            float y = r * 0.2126f + g * 0.7152f + b * 0.0722f;
            float z = (r * 0.0193f + g * 0.1192f + b * 0.9505f) / 1.08883f;

            x = x > 0.008856f ? Mathf.Pow(x, 1f / 3f) : 7.787f * x + 16f / 116f;
            y = y > 0.008856f ? Mathf.Pow(y, 1f / 3f) : 7.787f * y + 16f / 116f;
            z = z > 0.008856f ? Mathf.Pow(z, 1f / 3f) : 7.787f * z + 16f / 116f;

            return new Vector3(116f * y - 16f, 500f * (x - y), 200f * (y - z));
        }

        #endregion
    }
}

using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// The primordial particle system motion law (Schmickl, Stefanec &amp; Crailsheim, Sci. Rep. 6:37969,
    /// 2016) as a CPU reference for <c>PrimordialSim.compute</c>'s MoveAgentsKernel:
    /// <c>Δφ = α + β · N · sign(R − L)</c>, with L / R the neighbours in the left / right half-disc
    /// of radius r and N = L + R.
    /// Conventions, pinned by PrimordialMotionTests because mirroring them maps ⟨α, β⟩ to ⟨−α, β⟩:
    /// y-up frame; a neighbour is on the right when cross(heading, offset) &lt; 0; a positive Δφ turns
    /// counter-clockwise. Each step senses with the current heading, turns, then moves along the NEW
    /// heading (the paper's Fig. 1 order). The update is synchronous: every particle senses the
    /// positions the step started from.
    /// </summary>
    public static class PrimordialMotion
    {
        /// <summary>One species' motion constants in the units the kernel uses: radians and pixels.</summary>
        public struct Species
        {
            public float alpha, beta;        // radians
            public float radius, speed;      // px
            public float otherTypeWeight;    // weight of other species in L / R (1 = same physics)
        }

        /// <summary>HLSL sign(): 0 for 0. Unity's Mathf.Sign(0) is 1, which would turn a balanced
        /// particle by α + β·N instead of α.</summary>
        public static float Sign(float x) => x > 0f ? 1f : x < 0f ? -1f : 0f;

        public static float TurnDelta(float alpha, float beta, float left, float right) =>
            alpha + beta * (left + right) * Sign(right - left);

        public static bool IsRight(Vector2 heading, Vector2 offset) =>
            heading.x * offset.y - heading.y * offset.x < 0f;

        /// <summary>Minimum-image offset on the torus: a neighbour across the seam is a short hop.</summary>
        public static Vector2 ToroidalOffset(Vector2 from, Vector2 to, Vector2 world)
        {
            Vector2 d = to - from;
            d.x -= world.x * Mathf.Round(d.x / world.x);
            d.y -= world.y * Mathf.Round(d.y / world.y);
            return d;
        }

        public static Vector2 Rotate(Vector2 v, float angle)
        {
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        /// <summary>
        /// Advance every particle one step (no noise, no field terms). Writes the new positions and
        /// unit headings into <paramref name="outPos"/> / <paramref name="outHead"/> and each particle's
        /// weighted neighbour count N into <paramref name="outN"/>.
        /// </summary>
        public static void Step(Vector2[] pos, Vector2[] head, int[] type, Species[] species, Vector2 world,
                                Vector2[] outPos, Vector2[] outHead, float[] outN)
        {
            for (int i = 0; i < pos.Length; i++)
            {
                Species sp = species[type[i]];
                float r2 = sp.radius * sp.radius;
                float left = 0f, right = 0f;
                for (int j = 0; j < pos.Length; j++)
                {
                    Vector2 d = ToroidalOffset(pos[i], pos[j], world);
                    float d2 = d.sqrMagnitude;
                    if (d2 <= 0f || d2 >= r2) continue;   // self and exact coincidences excluded
                    float w = type[j] == type[i] ? 1f : sp.otherTypeWeight;
                    if (IsRight(head[i], d)) right += w; else left += w;
                }

                Vector2 h = Rotate(head[i], TurnDelta(sp.alpha, sp.beta, left, right)).normalized;
                Vector2 p = pos[i] + h * sp.speed;
                if (p.x < 0f) p.x += world.x; else if (p.x >= world.x) p.x -= world.x;
                if (p.y < 0f) p.y += world.y; else if (p.y >= world.y) p.y -= world.y;

                outPos[i] = p;
                outHead[i] = h;
                outN[i] = left + right;
            }
        }
    }
}

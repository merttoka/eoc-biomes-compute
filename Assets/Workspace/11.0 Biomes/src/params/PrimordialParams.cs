using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// One primordial-particle species. Distances are in the paper's space units (su), not pixels:
    /// PrimordialSim.spaceUnitPx maps them, so Schmickl et al.'s ⟨r = 5, α = 180°, β = 17°, v = 0.67⟩
    /// (the defaults) transfer verbatim.
    /// </summary>
    [Serializable]
    public class PrimordialAgentType
    {
        [Tooltip("Relative population: this species gets share / Σ shares of the sim's particles. Takes " +
                 "effect on Reset. A species lives at its own density only when otherTypeWeight is 0 — the " +
                 "PrimordialSim inspector shows what each one feels.")]
        public float share = 1f;
        [Tooltip("Fixed turn per step (deg). 180 = isolated particles hold position (they flip back and forth).")]
        public float alpha = 180f;
        [Tooltip("Turn per neighbour (deg): Δφ = α + β·N·sign(R − L), positive = a right turn, so β > 0 turns toward the more crowded side.")]
        public float beta = 17f;
        [Tooltip("Neighbourhood radius (su).")]
        public float radius = 5f;
        [Tooltip("Constant speed (su/step). Load-bearing for the life cycle: v/r sets cell size and stability.")]
        public float speed = 0.67f;
        [Tooltip("Drawn size of one particle (su): its deposit is a soft disc this wide, so membranes read as " +
                 "bodies at any resolution. Render only. Below ~0.75 px it is a single pixel.")]
        public float size = 0.55f;
        [Tooltip("Gaussian turn noise σ (deg). Cells survive a few degrees; tens of degrees dissolve them.")]
        public float noise = 0f;
        [Tooltip("How other species count in L/R. 1 = one shared physics, 0 = species pass through each other, < 0 = inverted turning.")]
        public float otherTypeWeight = 1f;
        [Tooltip("Positional drift along the perceived field gradient (chemotaxis − avoidance), su/step per unit field difference across ±radius, clamped to half the speed. Translates whole cells without breaking them in a transient field (a dispersal pulse); a standing scent with maxima pulls everything onto them. + = toward, − = away.")]
        public float fieldDrift = 0f;
        [Tooltip("Extra turn noise σ (deg) per unit perceived speed boost (perception A: dispersal, or a predator's scent mapped to SpeedBoost). The lever that shatters cells.")]
        public float agitationNoise = 30f;
        public float depositAmount = 0.35f;
        public float diffuseRate = 0.8f;
        [Tooltip("Neighbour count N where the density colour ramp starts. The free-particle soup sits around " +
                 "N ≈ 6 at the paper's density; spores ≈ 13–15 (paper colour classes).")]
        public float densityFloor = 10f;
        [Tooltip("Neighbour count N where the ramp saturates (paper: 35 = nucleus).")]
        public float densityFull = 35f;
        [Tooltip("Hue offset of dense matter (nuclei, membranes) relative to the sparse nutrient soup.")]
        public float densityHueShift = 0.12f;
        [Tooltip("Dense matter glows: brighter and paler by this much at full density.")]
        public float densityGlow = 1f;
        [Tooltip("How far the sparse soup (below densityFloor) dims: 0 = as bright as the creatures' base, 1 = dark.")]
        public float sparseDim = 0.85f;
        public float hue = 0.5f;
        public float saturation = 0.9f;
        public float brightness = 0.9f;
        public float firingSpeedMul = 1.5f;
        public float firingDepositAmount = 1f;
    }

    [CreateAssetMenu(fileName = "PrimordialParams", menuName = "Biomes/PrimordialParams")]
    public class PrimordialParams : ScriptableObject, IAgentColorParams
    {
        [Range(1, 8), KnobIgnore] public int typeCount = 1;
        public List<PrimordialAgentType> types = new()
        {
            new PrimordialAgentType(),
        };

        [Header("MIDI/OSC Ranges (min/max for 0-1 mapping)")]
        public List<ParamRange> ranges = new()
        {
            new("alpha",           -180f, 180f),
            new("beta",            -90f,  90f),
            new("radius",           1f,   20f),
            new("speed",            0.05f, 3f),
            new("size",             0f,   2f),
            new("noise",            0f,   45f),
            new("otherTypeWeight", -1f,   1f),
            new("fieldDrift",      -20f,  20f),
            new("agitationNoise",   0f,   90f),
            new("depositAmount",    0f,   1f),
            new("diffuseRate",      0.8f, 1f),
            new("densityFloor",     0f,   40f),
            new("densityFull",      5f,   80f),
            new("densityHueShift", -0.5f, 0.5f),
            new("densityGlow",      0f,   2f),
            new("sparseDim",        0f,   1f),
            new("hue",              0f,   1f),
            new("saturation",       0f,   1f),
            new("brightness",       0f,   1f),
            new("firingSpeedMul",   1f,   5f),
            new("firingDepositAmount", 0f, 1f),
        };

        public (float min, float max) GetRange(string paramName)
            => ParamRangeUtil.GetRange(ranges, paramName);

        public int TypeCount => types.Count;
        public AgentFamily Family => AgentFamily.Primordial;

        public float GetValue(string name, int typeIndex)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return 0f;
            var t = types[typeIndex];
            return name switch
            {
                "alpha"           => t.alpha,
                "beta"            => t.beta,
                "radius"          => t.radius,
                "speed"           => t.speed,
                "size"            => t.size,
                "noise"           => t.noise,
                "otherTypeWeight" => t.otherTypeWeight,
                "fieldDrift"      => t.fieldDrift,
                "agitationNoise"  => t.agitationNoise,
                "depositAmount"   => t.depositAmount,
                "diffuseRate"     => t.diffuseRate,
                "densityFloor"    => t.densityFloor,
                "densityFull"     => t.densityFull,
                "densityHueShift" => t.densityHueShift,
                "densityGlow"     => t.densityGlow,
                "sparseDim"       => t.sparseDim,
                "hue"             => t.hue,
                "saturation"      => t.saturation,
                "brightness"      => t.brightness,
                _ => 0f,
            };
        }

        public void SetValue(string name, int typeIndex, float raw)
        {
            if (typeIndex < 0 || typeIndex >= types.Count) return;
            var t = types[typeIndex];
            switch (name)
            {
                case "alpha":           t.alpha           = raw; break;
                case "beta":            t.beta            = raw; break;
                case "radius":          t.radius          = raw; break;
                case "speed":           t.speed           = raw; break;
                case "size":            t.size            = raw; break;
                case "noise":           t.noise           = raw; break;
                case "otherTypeWeight": t.otherTypeWeight = raw; break;
                case "fieldDrift":      t.fieldDrift      = raw; break;
                case "agitationNoise":  t.agitationNoise  = raw; break;
                case "depositAmount":   t.depositAmount   = raw; break;
                case "diffuseRate":     t.diffuseRate     = raw; break;
                case "densityFloor":    t.densityFloor    = raw; break;
                case "densityFull":     t.densityFull     = raw; break;
                case "densityHueShift": t.densityHueShift = raw; break;
                case "densityGlow":     t.densityGlow     = raw; break;
                case "sparseDim":       t.sparseDim       = raw; break;
                case "hue":             t.hue             = raw; break;
                case "saturation":      t.saturation      = raw; break;
                case "brightness":      t.brightness      = raw; break;
            }
        }

        public void SyncTypesList()
        {
            while (types.Count < typeCount) types.Add(new PrimordialAgentType());
            while (types.Count > typeCount) types.RemoveAt(types.Count - 1);
        }

        public void ResetToDefaults()
        {
            typeCount = 1;
            types.Clear();
            types.Add(new PrimordialAgentType());
        }

        // No-op: PPS distances are in space units, and PrimordialSim scales the unit itself
        // (spaceUnitPx · rezY/referenceHeight). Scaling radius/speed here too would apply k twice.
        public void ScaleSpatial(float k) { }

        // No-op: each particle deposits a disc `size` su wide, so its per-pixel deposit is already
        // resolution-independent; ×k on top would brighten the whole layer with resolution.
        public void ScaleDensity(float k) { }

        // Randomizes the motion law within its ranges. Most of ⟨α, β⟩ space is not life-like
        // (paper Fig. 7); this is an explorer, not a generator of creatures.
        public void RandomizeParams()
        {
            foreach (var t in types)
            {
                var r = GetRange("alpha"); t.alpha = UnityEngine.Random.Range(r.min, r.max);
                r = GetRange("beta");      t.beta  = UnityEngine.Random.Range(r.min, r.max);
            }
        }

        public void RandomizeColors()
        {
            var palette = ColorPalette.GenerateHSB(types.Count);
            for (int i = 0; i < types.Count && i < palette.Count; i++)
                this.SetHsb(i, palette[i]);
        }
    }
}

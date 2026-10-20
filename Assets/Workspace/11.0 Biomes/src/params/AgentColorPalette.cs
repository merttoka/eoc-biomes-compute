using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Agent colors to assign to sim types. A swatch is the color a type shows on screen at full
    /// trail intensity (sRGB — what the picker, eyedropper and hex codes mean; see AgentColor.ToDisplay);
    /// one tagged with a family is meant for that sim (assignment rule: <see cref="PaletteAssign"/>).
    /// Applied from the agent params inspector, or cycled live by SimulationManager.
    /// </summary>
    [CreateAssetMenu(fileName = "Palette", menuName = "Biomes/Agent Color Palette")]
    public class AgentColorPalette : ScriptableObject
    {
        [TextArea] public string notes;
        public List<PaletteSwatch> swatches = new();

        [Tooltip("Also recolour the termite walls (the composite's mound overlay) while this palette is " +
                 "active, fading with the agents. Off = the manager's own Mound Color stays.")]
        public bool tintsMounds;
        [Tooltip("Wall colour while active — same convention as SimulationManager.moundColor.")]
        public Color moundColor = new(0.53f, 0.81f, 0.92f, 1f);

        /// <summary>The hue/saturation/brightness that type <paramref name="typeIndex"/> of a
        /// <paramref name="family"/> sim gets; a grey or black swatch keeps the fallbacks. False when
        /// the palette is empty.</summary>
        public bool TryGetTarget(AgentFamily family, int typeIndex, float fallbackHue, float fallbackSat,
            out (float h, float s, float b) hsb)
        {
            int k = PaletteAssign.SwatchIndex(swatches, family, typeIndex);
            hsb = k < 0 ? default : AgentColor.FromDisplay(swatches[k].color, fallbackHue, fallbackSat);
            return k >= 0;
        }

        /// <summary>Writes this palette into every type of <paramref name="p"/>.</summary>
        public void ApplyTo(IAgentColorParams p)
        {
            for (int i = 0; i < p.TypeCount; i++)
            {
                var current = p.GetHsb(i);
                if (TryGetTarget(p.Family, i, current.h, current.s, out var hsb))
                    p.SetHsb(i, hsb);
            }
        }

        /// <summary>Replaces this palette's swatches for <paramref name="p"/>'s family with its current colors.</summary>
        public void StoreFrom(IAgentColorParams p)
        {
            var colors = new Color[p.TypeCount];
            for (int i = 0; i < colors.Length; i++)
            {
                var (h, s, b) = p.GetHsb(i);
                colors[i] = AgentColor.ToDisplay(h, s, b);
            }
            PaletteAssign.StoreFamily(swatches, p.Family, colors);
        }
    }
}

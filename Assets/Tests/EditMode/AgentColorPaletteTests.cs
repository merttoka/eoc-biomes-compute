using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Biomes;

/// <summary>
/// Palette swatches are on-screen (sRGB) colors; params are the kernel's linear-light HSB.
/// Storing a sim's colors and applying them back must round-trip the params.
/// Reflection: the palette and params live in Assembly-CSharp.
/// </summary>
public class AgentColorPaletteTests
{
    private static Type T(string name) => Type.GetType("Biomes." + name + ", Assembly-CSharp");

    [Test]
    public void StoreFrom_SavesTheOnScreenColor_AndApplyToRestoresTheParams()
    {
        var p = ScriptableObject.CreateInstance(T("PhysarumParams"));   // 2 types by default
        var palette = ScriptableObject.CreateInstance(T("AgentColorPalette"));
        try
        {
            var set = p.GetType().GetMethod("SetValue");
            var get = p.GetType().GetMethod("GetValue");
            void Set(float h, float s, float b)
            {
                set.Invoke(p, new object[] { "hue", 0, h });
                set.Invoke(p, new object[] { "saturation", 0, s });
                set.Invoke(p, new object[] { "brightness", 0, b });
            }
            float Get(string name) => (float)get.Invoke(p, new object[] { name, 0 });

            Set(0.6f, 0.4f, 0.5f);
            palette.GetType().GetMethod("StoreFrom").Invoke(palette, new object[] { p });
            var swatches = (List<PaletteSwatch>)palette.GetType().GetField("swatches").GetValue(palette);
            Assert.That(swatches.Count, Is.EqualTo(2));
            Assert.That(swatches[0].family, Is.EqualTo(AgentFamily.Physarum));
            var onScreen = AgentColor.ToRgb(0.6f, 0.4f, 0.5f).gamma;
            Assert.That(swatches[0].color.r, Is.EqualTo(onScreen.r).Within(1e-4f), "r");
            Assert.That(swatches[0].color.g, Is.EqualTo(onScreen.g).Within(1e-4f), "g");
            Assert.That(swatches[0].color.b, Is.EqualTo(onScreen.b).Within(1e-4f), "b");

            Set(0.1f, 0.9f, 0.2f);
            palette.GetType().GetMethod("ApplyTo").Invoke(palette, new object[] { p });
            Assert.That(Get("hue"), Is.EqualTo(0.6f).Within(1e-4f), "hue");
            Assert.That(Get("saturation"), Is.EqualTo(0.4f).Within(1e-4f), "saturation");
            Assert.That(Get("brightness"), Is.EqualTo(0.5f).Within(1e-4f), "brightness");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(p);
            UnityEngine.Object.DestroyImmediate(palette);
        }
    }
}

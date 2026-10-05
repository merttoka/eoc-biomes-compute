using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using Biomes;

/// <summary>
/// Randomize Colors sets brightness too, so each type renders at the Lab lightness the
/// generator picked it at. Reflection: ColorPalette and the params live in Assembly-CSharp,
/// which the test assembly cannot reference.
/// </summary>
public class RandomizeColorsTests
{
    private static readonly Type Generator = Type.GetType("Biomes.ColorPalette, Assembly-CSharp");

    private static IList GenerateHSB(int count, float lightnessMin, float lightnessMax)
    {
        var method = Generator?.GetMethod("GenerateHSB");
        Assert.That(method, Is.Not.Null, "ColorPalette.GenerateHSB");
        return (IList)method.Invoke(null, new object[] { count, lightnessMin, lightnessMax, 0f, 360f, 2000 });
    }

    [Test]
    public void GenerateHSB_RendersAtTheRequestedLightness()
    {
        UnityEngine.Random.InitState(3);
        var colors = GenerateHSB(6, 40f, 50f);
        Assert.That(colors.Count, Is.EqualTo(6));
        var toLab = Generator.GetMethod("RGBToLab");
        foreach (var boxed in colors)
        {
            var (h, s, b) = ((float, float, float))boxed;
            var lab = (Vector3)toLab.Invoke(null, new object[] { AgentColor.ToRgb(h, s, b) });
            Assert.That(lab.x, Is.InRange(39.5f, 50.5f), $"L* of ({h}, {s}, {b})");
        }
    }

    [Test]
    public void RandomizeColors_WritesHueSaturationAndBrightness()
    {
        var type = Type.GetType("Biomes.PhysarumParams, Assembly-CSharp");
        Assert.That(type, Is.Not.Null);
        var asset = ScriptableObject.CreateInstance(type);
        try
        {
            int n = (int)type.GetProperty("TypeCount").GetValue(asset);
            UnityEngine.Random.InitState(11);
            var expected = GenerateHSB(n, 35f, 80f);   // RandomizeColors' defaults
            UnityEngine.Random.InitState(11);
            type.GetMethod("RandomizeColors").Invoke(asset, null);

            var get = type.GetMethod("GetValue");
            float Get(string name, int i) => (float)get.Invoke(asset, new object[] { name, i });
            for (int i = 0; i < n; i++)
            {
                var (h, s, b) = ((float, float, float))expected[i];
                Assert.That(Get("hue", i), Is.EqualTo(h), $"type {i} hue");
                Assert.That(Get("saturation", i), Is.EqualTo(s), $"type {i} saturation");
                Assert.That(Get("brightness", i), Is.EqualTo(b), $"type {i} brightness");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(asset);
        }
    }
}

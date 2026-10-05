using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Biomes;

/// <summary>
/// Each show palette must reproduce what its scene exhibited: per family, the tagged swatches
/// in order invert (AgentColor.FromRgb) to the source asset's hue/saturation at the brightness
/// the kernels hardcoded (Physarum/Boid 0.8, Termite 1.0).
/// </summary>
public class PalettePresetTests
{
    private const string W = "Assets/Workspace/";
    private const string Palettes = W + "11.0 Biomes/assets/Palettes";
    private const float Eps = 1e-4f;

    private static IEnumerable<TestCaseData> Sources()
    {
        const string c = W + "11.1 CURRENTS Scene/assets/", s = W + "11.2 SIGGRAPH Scene/assets/", b = W + "11.3 Brave New Work Scene/assets/";
        yield return Case("CURRENTS", AgentFamily.Physarum, c + "PhysarumParams.asset");
        yield return Case("CURRENTS", AgentFamily.Boid,     c + "BoidParams.asset");
        yield return Case("CURRENTS", AgentFamily.Termite,  c + "TermiteParams.asset");
        yield return Case("SIGGRAPH", AgentFamily.Physarum, s + "Snapshots/Physarum_20260711_201424_SHOWVERSION 1.asset");
        yield return Case("SIGGRAPH", AgentFamily.Boid,     s + "Snapshots/Boid_20260711_200244.asset");
        yield return Case("SIGGRAPH", AgentFamily.Termite,  s + "Snapshots/Termite_20260711_200244.asset");
        yield return Case("SIGGRAPH DAC", AgentFamily.Physarum, s + "DAC_params/Physarum_20260908_181119_DAC.asset");
        yield return Case("SIGGRAPH DAC", AgentFamily.Boid,     s + "DAC_params/Boid_20260908_181119_DAC.asset");
        yield return Case("SIGGRAPH DAC", AgentFamily.Termite,  s + "DAC_params/Termite_20260908_181119_DAC.asset");
        yield return Case("Brave New Work", AgentFamily.Physarum, b + "PhysarumParams.asset");
        yield return Case("Brave New Work", AgentFamily.Boid,     b + "BoidParams.asset");
        yield return Case("Brave New Work", AgentFamily.Termite,  b + "TermiteParams.asset");
    }

    private static TestCaseData Case(string palette, AgentFamily family, string source) =>
        new TestCaseData(palette, family, source).SetName($"ShowPalette_Reproduces({palette}, {family})");

    [TestCaseSource(nameof(Sources))]
    public void ShowPalette_ReproducesSourceColors(string palette, AgentFamily family, string sourcePath)
    {
        var types = Load(sourcePath).FindProperty("types");
        var source = new List<(float h, float s)>();
        for (int i = 0; i < types.arraySize; i++)
        {
            var t = types.GetArrayElementAtIndex(i);
            source.Add((t.FindPropertyRelative("hue").floatValue, t.FindPropertyRelative("saturation").floatValue));
        }
        AssertFamilyMatches(palette, family, source, family == AgentFamily.Termite ? 1f : 0.8f);
    }

    [Test]
    public void Metaesthetica_ReproducesVisapPhysarum()
    {
        var so = Load(W + "10.0 Metaesthetica/assets/ParamsPhysarum_VISAP.asset");
        Vector4 hues = so.FindProperty("m_Hues").vector4Value, sats = so.FindProperty("m_Saturations").vector4Value;
        var source = new List<(float h, float s)>();
        for (int i = 0; i < 4; i++) source.Add((hues[i], sats[i]));
        AssertFamilyMatches("Metaesthetica VISAP", AgentFamily.Physarum, source, 0.8f);
    }

    [Test]
    public void TenPalettes_EachWithSwatches()
    {
        var guids = AssetDatabase.FindAssets("t:AgentColorPalette", new[] { Palettes });
        Assert.That(guids.Length, Is.EqualTo(10));
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            Assert.That(Load(path).FindProperty("swatches").arraySize, Is.GreaterThan(0), path);
        }
    }

    private static void AssertFamilyMatches(string palette, AgentFamily family, List<(float h, float s)> source, float legacyB)
    {
        var swatches = Load($"{Palettes}/Shows/{palette}.asset").FindProperty("swatches");
        var colors = new List<Color>();
        for (int i = 0; i < swatches.arraySize; i++)
        {
            var sw = swatches.GetArrayElementAtIndex(i);
            if (sw.FindPropertyRelative("family").intValue == (int)family)
                colors.Add(sw.FindPropertyRelative("color").colorValue);
        }
        Assert.That(colors.Count, Is.EqualTo(source.Count), $"{palette} {family}: swatch count");
        for (int i = 0; i < colors.Count; i++)
        {
            // Source values as fallbacks: a grey swatch (s = 0) keeps the source hue, as Apply does.
            var (h, s, b) = AgentColor.FromRgb(colors[i], source[i].h, source[i].s);
            float dh = Mathf.Abs(h - source[i].h) % 1f;
            string at = $"{palette} {family} type {i}";
            Assert.That(Mathf.Min(dh, 1f - dh), Is.LessThan(Eps), at + " hue");
            Assert.That(s, Is.EqualTo(source[i].s).Within(Eps), at + " saturation");
            Assert.That(b, Is.EqualTo(legacyB).Within(Eps), at + " brightness");
        }
    }

    private static SerializedObject Load(string path)
    {
        var asset = AssetDatabase.LoadMainAssetAtPath(path);
        Assert.That(asset, Is.Not.Null, path);
        return new SerializedObject(asset);
    }
}

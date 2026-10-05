using NUnit.Framework;
using UnityEngine;
using Biomes;

public class AgentColorTests
{
    private const float Eps = 1e-4f;

    private static void AssertColor(Color expected, Color actual, string because = "")
    {
        Assert.That(actual.r, Is.EqualTo(expected.r).Within(Eps), "r " + because);
        Assert.That(actual.g, Is.EqualTo(expected.g).Within(Eps), "g " + because);
        Assert.That(actual.b, Is.EqualTo(expected.b).Within(Eps), "b " + because);
    }

    private static float HueDistance(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 1f;
        return Mathf.Min(d, 1f - d);
    }

    [TestCase(0f,       1f, 0f,       0f)]   // red
    [TestCase(1f / 6f,  1f, 1f,       0f)]   // yellow
    [TestCase(1f / 3f,  0f, 1f,       0f)]   // green
    [TestCase(0.5f,     0f, 1f,       1f)]   // cyan
    [TestCase(2f / 3f,  0f, 0f,       1f)]   // blue
    [TestCase(5f / 6f,  1f, 0f,       1f)]   // magenta
    [TestCase(1f,       1f, 0f,       0f)]   // hue 1 wraps to red
    [TestCase(1f / 24f, 1f, 0.15625f, 0f)]   // 15°: smoothstep(0.25); Unity HSV would give 0.25
    [TestCase(0.125f,   1f, 0.84375f, 0f)]   // 45°: smoothstep(0.75)
    public void ToRgb_FullySaturated_FollowsTheShaderCurve(float h, float r, float g, float b)
    {
        AssertColor(new Color(r, g, b), AgentColor.ToRgb(h, 1f, 1f));
    }

    [Test]
    public void ToRgb_SaturationAndBrightness_ScaleTowardWhiteAndBlack()
    {
        AssertColor(new Color(0.8f, 0.4f, 0.4f), AgentColor.ToRgb(0f, 0.5f, 0.8f));
        AssertColor(new Color(0.3f, 0.3f, 0.3f), AgentColor.ToRgb(0.42f, 0f, 0.3f));
        AssertColor(Color.black, AgentColor.ToRgb(0.42f, 0.7f, 0f));
    }

    [Test]
    public void FromRgb_InvertsReferenceColors()
    {
        var (h, s, b) = AgentColor.FromRgb(new Color(1f, 0.15625f, 0f));
        Assert.That(HueDistance(h, 1f / 24f), Is.LessThan(Eps));
        Assert.That(s, Is.EqualTo(1f).Within(Eps));
        Assert.That(b, Is.EqualTo(1f).Within(Eps));

        (h, s, b) = AgentColor.FromRgb(new Color(0.8f, 0.4f, 0.4f));
        Assert.That(HueDistance(h, 0f), Is.LessThan(Eps));
        Assert.That(s, Is.EqualTo(0.5f).Within(Eps));
        Assert.That(b, Is.EqualTo(0.8f).Within(Eps));
    }

    [Test]
    public void FromRgb_Grey_KeepsFallbackHue()
    {
        var (h, s, b) = AgentColor.FromRgb(new Color(0.5f, 0.5f, 0.5f), 0.3f, 0.9f);
        Assert.That(h, Is.EqualTo(0.3f));
        Assert.That(s, Is.EqualTo(0f));
        Assert.That(b, Is.EqualTo(0.5f).Within(Eps));
    }

    [Test]
    public void FromRgb_Black_KeepsFallbackHueAndSaturation()
    {
        var (h, s, b) = AgentColor.FromRgb(Color.black, 0.3f, 0.9f);
        Assert.That(h, Is.EqualTo(0.3f));
        Assert.That(s, Is.EqualTo(0.9f));
        Assert.That(b, Is.EqualTo(0f));
    }

    [Test]
    public void FromRgb_OfToRgb_RecoversHsbAroundTheWheel()
    {
        foreach (float s in new[] { 0.1f, 0.5f, 1f })
        foreach (float b in new[] { 0.2f, 0.8f, 1f })
        for (int i = 0; i < 48; i++)
        {
            float h = i / 48f;
            var (h2, s2, b2) = AgentColor.FromRgb(AgentColor.ToRgb(h, s, b));
            string at = $"h {h} s {s} b {b}";
            Assert.That(HueDistance(h2, h), Is.LessThan(Eps), at);
            Assert.That(s2, Is.EqualTo(s).Within(Eps), at);
            Assert.That(b2, Is.EqualTo(b).Within(Eps), at);
        }
    }

    [Test]
    public void ToRgb_OfFromRgb_RecoversAnyColor()
    {
        var rng = new System.Random(1);
        for (int i = 0; i < 500; i++)
        {
            var c = new Color((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            var (h, s, b) = AgentColor.FromRgb(c);
            AssertColor(c, AgentColor.ToRgb(h, s, b), c.ToString());
        }
    }

    [Test]
    public void FromRgb_HueWrapsIntoUnitRange()
    {
        // Red with a trace of blue lands on hue 6/6 in float; it must wrap to [0, 1).
        var (h, _, _) = AgentColor.FromRgb(new Color(1f, 0f, 1e-12f));
        Assert.That(h, Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
    }
}

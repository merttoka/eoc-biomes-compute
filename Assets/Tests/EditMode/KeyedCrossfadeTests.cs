using System.Collections.Generic;
using NUnit.Framework;
using Biomes;

public class KeyedCrossfadeTests
{
    private const float Eps = 1e-5f;

    private static Dictionary<string, float> D(params (string k, float v)[] kv)
    {
        var d = new Dictionary<string, float>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Test]
    public void UnionKeys_FromFirst_ThenToOnly_NoDuplicates()
    {
        var from = D(("a", 1f), ("b", 2f));
        var to   = D(("b", 5f), ("c", 3f));
        Assert.That(KeyedCrossfade.UnionKeys(from, to), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void Lerp_BothSides_IsPlainLerp()
    {
        var from = D(("k", 2f));
        var to   = D(("k", 4f));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 0.5f), Is.EqualTo(3f).Within(Eps));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 0f),   Is.EqualTo(2f).Within(Eps));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 1f),   Is.EqualTo(4f).Within(Eps));
    }

    [Test]
    public void Lerp_OnlyInTarget_FadesInFromZero()
    {
        var from = D();
        var to   = D(("k", 2f));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 0.25f), Is.EqualTo(0.5f).Within(Eps));
    }

    [Test]
    public void Lerp_OnlyInFrom_FadesOutToZero()
    {
        var from = D(("k", -1f));
        var to   = D();
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 0.75f), Is.EqualTo(-0.25f).Within(Eps));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 1f),    Is.EqualTo(0f).Within(Eps));
    }

    [Test]
    public void Lerp_ClampsT()
    {
        var from = D(("k", 0f));
        var to   = D(("k", 1f));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", 1.5f),  Is.EqualTo(1f).Within(Eps));
        Assert.That(KeyedCrossfade.Lerp(from, to, "k", -0.5f), Is.EqualTo(0f).Within(Eps));
    }

    [Test]
    public void KeysToRemove_IsFromMinusTo()
    {
        var from = D(("a", 1f), ("b", 2f));
        var to   = D(("b", 5f), ("c", 3f));
        Assert.That(KeyedCrossfade.KeysToRemove(from, to), Is.EqualTo(new[] { "a" }));
    }
}

using NUnit.Framework;
using UnityEngine;
using Biomes;

/// <summary>
/// PrimordialPopulation: the particle count, the per-species split by share (the rule the reset
/// kernel runs on TypeEnds) and the density each species perceives.
/// </summary>
public class PrimordialPopulationTests
{
    [Test]
    public void Count_IsDensityTimesWorldArea_Clamped()
    {
        var world = PrimordialPopulation.WorldSu(1920, 1080, 4f);   // 480 × 270 su
        Assert.That(world, Is.EqualTo(new Vector2(480f, 270f)));
        Assert.That(PrimordialPopulation.Count(0.08f, world), Is.EqualTo(10368));
        Assert.That(PrimordialPopulation.Count(0f, world), Is.EqualTo(PrimordialPopulation.MinCount));
    }

    [Test]
    public void TypeEnds_SplitByShare_LastEndIsTheCount()
    {
        Assert.That(PrimordialPopulation.TypeEnds(1000, new[] { 3f, 1f }), Is.EqualTo(new[] { 750, 1000 }));
        Assert.That(PrimordialPopulation.TypeEnds(10, new[] { 1f, 1f, 1f }), Is.EqualTo(new[] { 3, 7, 10 }));
        Assert.That(PrimordialPopulation.TypeEnds(100, new[] { 1f }), Is.EqualTo(new[] { 100 }));
    }

    [Test]
    public void TypeEnds_ZeroShareGetsNone_AllZeroSplitsEvenly()
    {
        Assert.That(PrimordialPopulation.TypeEnds(100, new[] { 1f, 0f, 1f }), Is.EqualTo(new[] { 50, 50, 100 }));
        Assert.That(PrimordialPopulation.TypeEnds(100, new[] { 0f, 0f }), Is.EqualTo(new[] { 50, 100 }));
    }

    [Test]
    public void TypeOf_FollowsTheEnds()
    {
        var ends = new[] { 50, 50, 100 };   // the empty middle type owns no id
        Assert.That(PrimordialPopulation.TypeOf(0, ends), Is.EqualTo(0));
        Assert.That(PrimordialPopulation.TypeOf(49, ends), Is.EqualTo(0));
        Assert.That(PrimordialPopulation.TypeOf(50, ends), Is.EqualTo(2));
        Assert.That(PrimordialPopulation.TypeOf(99, ends), Is.EqualTo(2));
    }

    [Test]
    public void FeltDensity_AddsOthersByWeight()
    {
        var own = new[] { 0.02f, 0.02f, 0.02f, 0.02f };
        Assert.That(PrimordialPopulation.FeltDensity(0, own, 1f), Is.EqualTo(0.08f).Within(1e-6f), "one physics");
        Assert.That(PrimordialPopulation.FeltDensity(0, own, 0f), Is.EqualTo(0.02f).Within(1e-6f), "alone: below the band");
        Assert.That(PrimordialPopulation.FeltDensity(0, own, -1f), Is.EqualTo(-0.04f).Within(1e-6f));
    }
}

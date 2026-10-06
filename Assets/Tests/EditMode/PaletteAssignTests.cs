using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Biomes;

public class PaletteAssignTests
{
    private static List<PaletteSwatch> Swatches(params AgentFamily[] tags)
    {
        var list = new List<PaletteSwatch>();
        for (int i = 0; i < tags.Length; i++)
            list.Add(new PaletteSwatch(new Color(i / 10f, 0f, 0f), tags[i]));
        return list;
    }

    private static int[] Assign(List<PaletteSwatch> swatches, AgentFamily family, int types)
    {
        var r = new int[types];
        for (int i = 0; i < types; i++) r[i] = PaletteAssign.SwatchIndex(swatches, family, i);
        return r;
    }

    [Test]
    public void TaggedSwatches_GoToTheirFamily_InOrder_Wrapping()
    {
        var sw = Swatches(AgentFamily.Physarum, AgentFamily.Physarum, AgentFamily.Boid, AgentFamily.Termite, AgentFamily.Any);
        Assert.That(Assign(sw, AgentFamily.Physarum, 3), Is.EqualTo(new[] { 0, 1, 0 }));
        Assert.That(Assign(sw, AgentFamily.Boid, 2), Is.EqualTo(new[] { 2, 2 }));
        Assert.That(Assign(sw, AgentFamily.Termite, 1), Is.EqualTo(new[] { 3 }));
    }

    [Test]
    public void FamilyWithoutTags_UsesUntaggedSwatches()
    {
        var sw = Swatches(AgentFamily.Any, AgentFamily.Physarum, AgentFamily.Any);
        Assert.That(Assign(sw, AgentFamily.Boid, 3), Is.EqualTo(new[] { 0, 2, 0 }));
    }

    [Test]
    public void NoOwnTagsAndNoUntagged_UsesEverySwatch()
    {
        var sw = Swatches(AgentFamily.Physarum, AgentFamily.Physarum);
        Assert.That(Assign(sw, AgentFamily.Termite, 3), Is.EqualTo(new[] { 0, 1, 0 }));
    }

    [Test]
    public void EmptyOrMissingPalette_HasNoSwatch()
    {
        Assert.That(PaletteAssign.SwatchIndex(new List<PaletteSwatch>(), AgentFamily.Boid, 0), Is.EqualTo(-1));
        Assert.That(PaletteAssign.SwatchIndex(null, AgentFamily.Boid, 0), Is.EqualTo(-1));
    }

    [Test]
    public void IsCandidate_AgreesWithAssignment()
    {
        var sw = Swatches(AgentFamily.Any, AgentFamily.Boid, AgentFamily.Any);
        Assert.That(PaletteAssign.IsCandidate(sw, AgentFamily.Boid, 1), Is.True);
        Assert.That(PaletteAssign.IsCandidate(sw, AgentFamily.Boid, 0), Is.False);
        Assert.That(PaletteAssign.IsCandidate(sw, AgentFamily.Physarum, 0), Is.True);
        Assert.That(PaletteAssign.IsCandidate(sw, AgentFamily.Physarum, 1), Is.False);
    }

    [Test]
    public void StoreFamily_ReplacesInPlace_KeepsOtherFamilies()
    {
        var sw = Swatches(AgentFamily.Any, AgentFamily.Physarum, AgentFamily.Boid, AgentFamily.Physarum);
        PaletteAssign.StoreFamily(sw, AgentFamily.Physarum, new[] { Color.red, Color.green, Color.blue });
        Assert.That(sw.ConvertAll(s => s.family), Is.EqualTo(new[] {
            AgentFamily.Any, AgentFamily.Physarum, AgentFamily.Physarum, AgentFamily.Physarum, AgentFamily.Boid }));
        Assert.That(sw[1].color, Is.EqualTo(Color.red));
        Assert.That(sw[3].color, Is.EqualTo(Color.blue));
    }

    [Test]
    public void StoreFamily_NewFamily_Appends()
    {
        var sw = Swatches(AgentFamily.Physarum);
        PaletteAssign.StoreFamily(sw, AgentFamily.Termite, new[] { Color.yellow });
        Assert.That(sw.Count, Is.EqualTo(2));
        Assert.That(sw[1].family, Is.EqualTo(AgentFamily.Termite));
        Assert.That(sw[1].color, Is.EqualTo(Color.yellow));
    }

    [Test]
    public void StepCycle_WrapsThroughPreset()
    {
        Assert.That(PaletteAssign.StepCycle(-1, 3, +1), Is.EqualTo(0));
        Assert.That(PaletteAssign.StepCycle(2, 3, +1), Is.EqualTo(-1));
        Assert.That(PaletteAssign.StepCycle(-1, 3, -1), Is.EqualTo(2));
        Assert.That(PaletteAssign.StepCycle(0, 3, -1), Is.EqualTo(-1));
        Assert.That(PaletteAssign.StepCycle(-1, 0, +1), Is.EqualTo(-1));
    }
}

using NUnit.Framework;
using Biomes;

public class UmweltKeysTests
{
    [Test]
    public void Read_FormatsChannelAndEffect()
    {
        Assert.That(UmweltKeys.Read(10, 2), Is.EqualTo("read:10:2.weight"));
    }

    [Test]
    public void Write_FormatsChannel()
    {
        Assert.That(UmweltKeys.Write(2), Is.EqualTo("write:2.amount"));
    }

    [Test]
    public void TryParseRead_RoundTrips()
    {
        Assert.That(UmweltKeys.TryParseRead("read:13:3.weight", out int ch, out int fx), Is.True);
        Assert.That(ch, Is.EqualTo(13));
        Assert.That(fx, Is.EqualTo(3));
    }

    [Test]
    public void TryParseWrite_RoundTrips()
    {
        Assert.That(UmweltKeys.TryParseWrite("write:7.amount", out int ch), Is.True);
        Assert.That(ch, Is.EqualTo(7));
    }

    [Test]
    public void TryParse_RejectsWrongShape()
    {
        Assert.That(UmweltKeys.TryParseRead("write:7.amount", out _, out _), Is.False);
        Assert.That(UmweltKeys.TryParseRead("read:x:1.weight", out _, out _), Is.False);
        Assert.That(UmweltKeys.TryParseWrite("read:1:1.weight", out _), Is.False);
        Assert.That(UmweltKeys.TryParseWrite("metabolicHeat", out _), Is.False);
    }

    [Test]
    public void Classifiers_AreExclusive()
    {
        Assert.That(UmweltKeys.IsRead("read:1:0.weight"), Is.True);
        Assert.That(UmweltKeys.IsWrite("read:1:0.weight"), Is.False);
        Assert.That(UmweltKeys.IsScalar("read:1:0.weight"), Is.False);
        Assert.That(UmweltKeys.IsScalar("metabolicHeat"), Is.True);
        Assert.That(UmweltKeys.IsScalar("bogus"), Is.False);
    }

    [Test]
    public void Scalars_HasEightStableNames()
    {
        Assert.That(UmweltKeys.Scalars, Is.EqualTo(new[] {
            "permMin", "permMax", "metabolicHeat", "oxygenConsumption",
            "deathO2", "deathPerm", "corpseWaste", "corpseDecay" }));
    }
}

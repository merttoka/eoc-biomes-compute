using NUnit.Framework;
using Biomes;

// Mirrors wheelScrub in the lab site's sliderMath.test.ts, the behaviour this interaction was modelled on.
public class ScrollGestureTests
{
    private const float Slop = 6f;

    private static ScrollGesture New() => new ScrollGesture(Slop);

    [Test]
    public void FastSidewaysSwipe_SteersFromItsFirstEvent()
    {
        Assert.That(New().Feed(-40f, 2f), Is.EqualTo(-40f));
    }

    [Test]
    public void GestureTooSmallToTell_IsHeld()
    {
        var g = New();
        Assert.That(g.Feed(-1.7f, 0f), Is.EqualTo(0f));
        Assert.That(g.Feed(0f, 0.2f), Is.EqualTo(0f));
    }

    [Test]
    public void SmallGestureThatSetsOffSideways_AppliesWhatItHeldBack()
    {
        var g = New();
        Assert.That(g.Feed(-1f, 1f), Is.EqualTo(0f));
        Assert.That(g.Feed(-6f, 1f), Is.EqualTo(-7f));
        Assert.That(g.Feed(-3f, 0f), Is.EqualTo(-3f));
    }

    [Test]
    public void VerticalNoiseAsASwipeSetsOff_StaysSideways()
    {
        var g = New();
        Assert.That(g.Feed(0f, 1f), Is.EqualTo(0f));
        Assert.That(g.Feed(-2f, 1f), Is.EqualTo(0f));
        Assert.That(g.Feed(-5f, 0.5f), Is.EqualTo(-7f));
    }

    [Test]
    public void FastVerticalScroll_IsNotASwipe()
    {
        Assert.That(New().Feed(0f, 40f), Is.Null);
    }

    [Test]
    public void SlowVerticalStart_IsHeldThenReleased()
    {
        var g = New();
        Assert.That(g.Feed(0f, 2f), Is.EqualTo(0f));
        Assert.That(g.Feed(0f, 2f), Is.EqualTo(0f));
        Assert.That(g.Feed(0f, 3f), Is.Null);
    }

    // Scrolling an inspector past a field must not move it, however the scroll drifts.
    [Test]
    public void VerticalScroll_StaysVerticalWhenItDriftsSideways()
    {
        var g = New();
        Assert.That(g.Feed(0f, 40f), Is.Null);
        Assert.That(g.Feed(60f, 2f), Is.Null);
        Assert.That(g.Feed(60f, 0f), Is.Null);
    }

    [Test]
    public void OnceSteering_VerticalDriftIsIgnored()
    {
        var g = New();
        g.Feed(-40f, 0f);
        Assert.That(g.Feed(-1f, 50f), Is.EqualTo(-1f));
    }
}

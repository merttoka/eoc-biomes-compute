using NUnit.Framework;
using Biomes;

public class FramePlayheadTests
{
    private const float Eps = 1e-4f;

    [Test]
    public void Configure_SetsRangeAndRewinds()
    {
        var p = new FramePlayhead();
        p.Configure(125000, 131000, 120f, loop: false);
        Assert.That(p.Start, Is.EqualTo(125000));
        Assert.That(p.End, Is.EqualTo(131000));
        Assert.That(p.Length, Is.EqualTo(6001));
        Assert.That(p.FramesPerSecond, Is.EqualTo(6001f / 120f).Within(Eps));
        Assert.That(p.Frame, Is.EqualTo(125000));
        Assert.That(p.Finished, Is.False);
    }

    [Test]
    public void Configure_SwapsReversedRange_AndFloorsDuration()
    {
        var p = new FramePlayhead();
        p.Configure(20, 10, -5f, loop: false);
        Assert.That(p.Start, Is.EqualTo(10));
        Assert.That(p.End, Is.EqualTo(20));
        Assert.That(p.DurationSeconds, Is.GreaterThan(0f));
    }

    [Test]
    public void Advance_AccumulatesFractionalFrames()
    {
        var p = new FramePlayhead();
        p.Configure(0, 99, 10f, loop: false); // 10 fps
        Assert.That(p.Advance(0.05f), Is.EqualTo(0)); // 0.5 frame
        Assert.That(p.Advance(0.05f), Is.EqualTo(1)); // 1.0 frame
        Assert.That(p.Advance(0.25f), Is.EqualTo(3)); // 3.5 frames
    }

    [Test]
    public void Advance_NonLooping_HoldsOnEndAndFinishes()
    {
        var p = new FramePlayhead();
        p.Configure(100, 109, 1f, loop: false); // 10 fps, 10 frames
        Assert.That(p.Advance(0.95f), Is.EqualTo(109));
        Assert.That(p.Finished, Is.False);
        Assert.That(p.Advance(1f), Is.EqualTo(109));
        Assert.That(p.Finished, Is.True);
        Assert.That(p.Advance(5f), Is.EqualTo(109));
        Assert.That(p.Progress, Is.EqualTo(1f).Within(Eps));
    }

    [Test]
    public void Advance_Looping_WrapsToStartKeepingRemainder()
    {
        var p = new FramePlayhead();
        p.Configure(100, 109, 1f, loop: true); // 10 fps
        Assert.That(p.Advance(0.5f), Is.EqualTo(105));
        Assert.That(p.Advance(0.55f), Is.EqualTo(100)); // 10.5 -> wraps to 0.5
        Assert.That(p.Position, Is.EqualTo(0.5f).Within(Eps));
        Assert.That(p.Finished, Is.False);
    }

    [Test]
    public void Seek_ClampsIntoRange_AndClearsFinished()
    {
        var p = new FramePlayhead();
        p.Configure(100, 109, 1f, loop: false);
        p.Advance(10f);
        Assert.That(p.Finished, Is.True);
        p.Seek(105);
        Assert.That(p.Frame, Is.EqualTo(105));
        Assert.That(p.Finished, Is.False);
        p.Seek(-1);
        Assert.That(p.Frame, Is.EqualTo(100));
        p.Seek(99999);
        Assert.That(p.Frame, Is.EqualTo(109));
    }

    [Test]
    public void Advance_ZeroOrNegativeDt_IsANoOp()
    {
        var p = new FramePlayhead();
        p.Configure(0, 9, 1f, loop: true);
        p.Advance(0.35f);
        int before = p.Frame;
        Assert.That(p.Advance(0f), Is.EqualTo(before));
        Assert.That(p.Advance(-1f), Is.EqualTo(before));
    }

    [Test]
    public void ShanghaiRange_LandsOnLastFrameAtDuration()
    {
        var p = new FramePlayhead();
        p.Configure(125000, 131000, 120f, loop: false);
        // 60 Hz update for 120 s.
        for (int i = 0; i < 7200; i++) p.Advance(1f / 60f);
        Assert.That(p.Frame, Is.EqualTo(131000));
        Assert.That(p.Finished, Is.True);
    }

    [Test]
    public void SingleFrameRange_IsStable()
    {
        var p = new FramePlayhead();
        p.Configure(7, 7, 2f, loop: true);
        Assert.That(p.Length, Is.EqualTo(1));
        Assert.That(p.Advance(3f), Is.EqualTo(7));
        Assert.That(p.Progress, Is.EqualTo(1f).Within(Eps));
    }
}

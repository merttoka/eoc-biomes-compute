using NUnit.Framework;
using Biomes;

/// <summary>
/// The burst clock, in isolation from Unity. Durations are SIM steps: a burst's wall-clock
/// length must not change when stepEvery changes, because stepEvery decimates only the rule.
///
/// The retrigger contract is the subtle part. A trigger arriving during a live burst extends
/// it — resets the clock, keeps the lattice — so sustained firing sustains ONE evolving
/// automaton instead of restarting it. Only a trigger from Idle re-seeds.
/// </summary>
public class BurstEnvelopeTests
{
    const float Eps = 1e-4f;

    // fadeIn 4, sustain 3, fadeOut 2 — small enough to step by hand in the assertions below.
    static void Advance(ref BurstEnvelope e, int times)
    {
        for (int i = 0; i < times; i++) e.Advance(4, 3, 2);
    }

    [Test]
    public void NewEnvelope_IsIdleAndSilent()
    {
        var e = new BurstEnvelope();
        Assert.That(e.IsIdle, Is.True);
        Assert.That(e.Value, Is.EqualTo(0f).Within(Eps));
    }

    [Test]
    public void AdvanceWhileIdle_IsANoOp()
    {
        var e = new BurstEnvelope();
        Advance(ref e, 10);
        Assert.That(e.IsIdle, Is.True);
        Assert.That(e.Value, Is.EqualTo(0f).Within(Eps));
    }

    [Test]
    public void TriggerFromIdle_RequestsReseed()
    {
        var e = new BurstEnvelope();
        Assert.That(e.Trigger(), Is.True);
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Attack));
    }

    [Test]
    public void TriggerDuringBurst_DoesNotRequestReseed()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 2);
        Assert.That(e.Trigger(), Is.False, "a live burst must keep its lattice");
    }

    [Test]
    public void Attack_RampsToFullOverFadeInSteps()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 1); Assert.That(e.Value, Is.EqualTo(0.25f).Within(Eps));
        Advance(ref e, 1); Assert.That(e.Value, Is.EqualTo(0.50f).Within(Eps));
        Advance(ref e, 1); Assert.That(e.Value, Is.EqualTo(0.75f).Within(Eps));
        Advance(ref e, 1);
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps));
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Sustain));
    }

    [Test]
    public void Sustain_HoldsThenReleases()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 6);   // 4 attack + 2 sustain
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Sustain));
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps));
        Advance(ref e, 1);   // age 7 == fadeIn(4) + sustain(3)
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Release));
    }

    [Test]
    public void Release_FadesToIdle()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 8);   // one release step
        Assert.That(e.Value, Is.EqualTo(0.5f).Within(Eps));
        Advance(ref e, 1);
        Assert.That(e.IsIdle, Is.True);
        Assert.That(e.Value, Is.EqualTo(0f).Within(Eps));
        Assert.That(e.Age, Is.EqualTo(0));
    }

    [Test]
    public void RetriggerDuringSustain_DoesNotDipTheOutput()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 5);                       // in sustain, Value == 1
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps));
        e.Trigger();
        Advance(ref e, 1);
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps), "re-attack must not restart from zero");
    }

    [Test]
    public void RetriggerDuringRelease_ClimbsBack()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        Advance(ref e, 8);                       // releasing, Value == 0.5
        e.Trigger();
        Advance(ref e, 1);
        Assert.That(e.Value, Is.GreaterThan(0.5f));
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Attack));
    }

    [Test]
    public void ZeroFadeIn_ReachesFullImmediately()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        e.Advance(0, 3, 2);
        Assert.That(e.Value, Is.EqualTo(1f).Within(Eps));
        Assert.That(e.Phase, Is.EqualTo(BurstPhase.Sustain));
    }

    [Test]
    public void ZeroFadeOut_GoesIdleImmediately()
    {
        var e = new BurstEnvelope();
        e.Trigger();
        for (int i = 0; i < 4; i++) e.Advance(0, 0, 0);
        Assert.That(e.IsIdle, Is.True);
    }

    [Test]
    public void RisingEdge_FiresOnceOnCrossing()
    {
        var edge = new RisingEdge();
        Assert.That(edge.Update(0.1f, 0.35f), Is.False);
        Assert.That(edge.Update(0.5f, 0.35f), Is.True,  "crossing up fires");
        Assert.That(edge.Update(0.9f, 0.35f), Is.False, "staying above must not refire");
        Assert.That(edge.Update(0.0f, 0.35f), Is.False);
        Assert.That(edge.Update(0.4f, 0.35f), Is.True,  "crossing again fires again");
    }
}

using NUnit.Framework;
using Biomes;

public class KnobGestureTests
{
    [Test]
    public void Release_AfterAPlainPress_IsATap()
    {
        var s = new KnobPressState(4);
        s.Press(1);
        Assert.That(s.Release(1), Is.True);
    }

    // Press-and-turn must not also fire the push action, or RandomizeParam would scramble what was just moved.
    [Test]
    public void Release_AfterTurning_IsNotATap()
    {
        var s = new KnobPressState(4);
        s.Press(1);
        s.Turned(1);
        Assert.That(s.Release(1), Is.False);
    }

    [Test]
    public void Release_WithoutAPress_IsNotATap()
    {
        Assert.That(new KnobPressState(4).Release(1), Is.False);
    }

    [Test]
    public void Turning_BeforeThePress_DoesNotCancelTheTap()
    {
        var s = new KnobPressState(4);
        s.Turned(1);
        s.Press(1);
        Assert.That(s.Release(1), Is.True);
    }

    [Test]
    public void Press_AfterAnEarlierTurnedPress_StartsFresh()
    {
        var s = new KnobPressState(4);
        s.Press(1);
        s.Turned(1);
        s.Release(1);
        s.Press(1);
        Assert.That(s.Release(1), Is.True);
    }

    [Test]
    public void IsPressed_FollowsPressAndRelease_PerKnob()
    {
        var s = new KnobPressState(4);
        s.Press(1);
        Assert.That(s.IsPressed(1), Is.True);
        Assert.That(s.IsPressed(2), Is.False);
        s.Release(1);
        Assert.That(s.IsPressed(1), Is.False);
    }

    [Test]
    public void TurningOneKnob_LeavesAnotherKnobsTapAlone()
    {
        var s = new KnobPressState(4);
        s.Press(1);
        s.Press(2);
        s.Turned(2);
        Assert.That(s.Release(1), Is.True);
        Assert.That(s.Release(2), Is.False);
    }

    // A knob held across a bank change or a device loss must not fire its push action, or stay pressed.
    [Test]
    public void ReleaseAll_ClearsPresses_AndALaterReleaseIsNotATap()
    {
        var s = new KnobPressState(4);
        s.Press(1);
        s.ReleaseAll();
        Assert.That(s.IsPressed(1), Is.False);
        Assert.That(s.Release(1), Is.False);
    }

    [TestCase(PressTurnScope.AllTypesOfSim, true, true, true)]
    [TestCase(PressTurnScope.AllTypesOfSim, false, true, false)]
    [TestCase(PressTurnScope.AllSims, true, true, true)]
    [TestCase(PressTurnScope.AllSims, false, true, true)]
    [TestCase(PressTurnScope.AllSims, false, false, false)]
    [TestCase(PressTurnScope.Off, true, true, false)]
    [TestCase(PressTurnScope.Off, false, true, false)]
    public void Scope_DecidesWhichSimsMove(PressTurnScope scope, bool ownSim, bool hasParam, bool expected)
    {
        Assert.That(PressTurnScopes.Includes(scope, ownSim, hasParam), Is.EqualTo(expected));
    }
}

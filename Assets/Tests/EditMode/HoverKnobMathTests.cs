using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Biomes;

public class HoverKnobMathTests
{
    private const float Eps = 1e-5f;
    private const float Tick = 1f / HoverKnobMath.StepsPerUnit;

    [Test]
    public void Step_FullDelta_CrossesWholeRange()
    {
        Assert.That(HoverKnobMath.Step(0f, 1f, 0f, 10f), Is.EqualTo(10f).Within(Eps));
    }

    [Test]
    public void Step_ClampsAtMax()
    {
        Assert.That(HoverKnobMath.Step(9.5f, 0.5f, 0f, 10f), Is.EqualTo(10f).Within(Eps));
    }

    [Test]
    public void Step_ClampsAtMin()
    {
        Assert.That(HoverKnobMath.Step(0.5f, -0.5f, 0f, 10f), Is.EqualTo(0f).Within(Eps));
    }

    // eatAmount: authored 0.94 against a ranges max of 0.1. Turning down must not snap it to 0.1.
    [Test]
    public void Step_AboveMax_TurningDown_MovesInwardWithoutSnapping()
    {
        Assert.That(HoverKnobMath.Step(0.94f, -Tick, 0f, 0.1f), Is.EqualTo(0.94f - 0.1f * Tick).Within(Eps));
    }

    [Test]
    public void Step_AboveMax_TurningUp_Holds()
    {
        Assert.That(HoverKnobMath.Step(0.94f, Tick, 0f, 0.1f), Is.EqualTo(0.94f).Within(Eps));
    }

    [Test]
    public void Step_BelowMin_TurningUp_MovesInwardWithoutSnapping()
    {
        Assert.That(HoverKnobMath.Step(-5f, Tick, 0f, 1f), Is.EqualTo(-5f + Tick).Within(Eps));
    }

    [Test]
    public void Step_BelowMin_TurningDown_Holds()
    {
        Assert.That(HoverKnobMath.Step(-5f, -Tick, 0f, 1f), Is.EqualTo(-5f).Within(Eps));
    }

    [Test]
    public void Step_ZeroDelta_LeavesOutOfRangeValueAlone()
    {
        Assert.That(HoverKnobMath.Step(5f, 0f, 0f, 1f), Is.EqualTo(5f).Within(Eps));
    }

    // Integer fields round at write time, so slow turns only register if the raw value accumulates.
    [Test]
    public void Step_SlowTurns_AccumulateAcrossAnIntegerBoundary()
    {
        float raw = 1f;
        raw = HoverKnobMath.Step(raw, Tick, 1f, 8f);
        Assert.That(Mathf.RoundToInt(raw), Is.EqualTo(1), "one tick alone rounds away");

        for (int i = 0; i < 19; i++) raw = HoverKnobMath.Step(raw, Tick, 1f, 8f);
        Assert.That(Mathf.RoundToInt(raw), Is.EqualTo(2));
    }

    [Test]
    public void StepFree_OneTick_MovesOnePercentOfTheValue()
    {
        Assert.That(HoverKnobMath.StepFree(50f, Tick), Is.EqualTo(50.5f).Within(Eps));
    }

    [Test]
    public void StepFree_NegativeValue_TurningUp_MovesTowardZero()
    {
        Assert.That(HoverKnobMath.StepFree(-50f, Tick), Is.EqualTo(-49.5f).Within(Eps));
    }

    [Test]
    public void StepFree_AtZero_StillMoves()
    {
        Assert.That(HoverKnobMath.StepFree(0f, Tick), Is.GreaterThan(0f));
        Assert.That(HoverKnobMath.StepFree(0f, -Tick), Is.LessThan(0f));
    }

    [TestCase(0.6f, 1f)]
    [TestCase(-0.6f, -1f)]
    [TestCase(0f, 0f)]
    public void FirstTick_KeepsDirection_AtOneStep(float delta, float expectedSteps)
    {
        Assert.That(HoverKnobMath.FirstTick(delta), Is.EqualTo(expectedSteps * Tick).Within(Eps));
    }

    [Test]
    public void FirstTick_SmallerThanOneStep_IsKept()
    {
        float fine = 0.1f * Tick;
        Assert.That(HoverKnobMath.FirstTick(fine), Is.EqualTo(fine).Within(Eps * 0.01f));
        Assert.That(HoverKnobMath.FirstTick(-fine), Is.EqualTo(-fine).Within(Eps * 0.01f));
    }

    [TestCase(0.6f, 4f)]
    [TestCase(-0.6f, -4f)]
    public void LimitSteps_CapsAnOversizedDelta(float delta, float expectedSteps)
    {
        Assert.That(HoverKnobMath.LimitSteps(delta, 4f), Is.EqualTo(expectedSteps * Tick).Within(Eps));
    }

    [Test]
    public void LimitSteps_KeepsADeltaWithinTheCap()
    {
        Assert.That(HoverKnobMath.LimitSteps(2f * Tick, 4f), Is.EqualTo(2f * Tick).Within(Eps));
    }

    [TestCase(0f, 0)]
    [TestCase(1f, 127)]
    [TestCase(0.5f, 64)]
    public void RingPosition_MapsAcrossTheRing(float norm, int expected)
    {
        Assert.That(HoverKnobMath.RingPosition(norm), Is.EqualTo(expected));
    }

    // A knob on its end stop reports nothing further, so these could never be turned the rest of the way.
    [TestCase(0.001f, 1)]
    [TestCase(0.999f, 126)]
    public void RingPosition_ValueShortOfAnEnd_StaysOffTheEndStop(float norm, int expected)
    {
        Assert.That(HoverKnobMath.RingPosition(norm), Is.EqualTo(expected));
    }

    [TestCase("types.Array.data[2].hue", "types")]
    [TestCase("a.Array.data[1].b.Array.data[2].x", "a.Array.data[1].b")]
    [TestCase("values.Array.data[3]", "values")]
    [TestCase("dispersalSpeedMult", null)]
    public void ArrayPathOf_FindsInnermostArray(string path, string expected)
    {
        Assert.That(HoverKnobMath.ArrayPathOf(path), Is.EqualTo(expected));
    }

    [Test]
    public void SiblingPaths_ArrayElementField_ListsEveryElement()
    {
        Assert.That(HoverKnobMath.SiblingPaths("types.Array.data[1].hue", 3), Is.EqualTo(new[]
        {
            "types.Array.data[0].hue", "types.Array.data[1].hue", "types.Array.data[2].hue",
        }));
    }

    [Test]
    public void SiblingPaths_NestedArrays_OnlyVaryInnermostIndex()
    {
        Assert.That(HoverKnobMath.SiblingPaths("a.Array.data[1].b.Array.data[2].x", 2), Is.EqualTo(new[]
        {
            "a.Array.data[1].b.Array.data[0].x", "a.Array.data[1].b.Array.data[1].x",
        }));
    }

    [Test]
    public void SiblingPaths_PlainArrayOfFloats_ListsEveryElement()
    {
        Assert.That(HoverKnobMath.SiblingPaths("values.Array.data[0]", 2), Is.EqualTo(new[]
        {
            "values.Array.data[0]", "values.Array.data[1]",
        }));
    }

    [Test]
    public void SiblingPaths_SingleField_IsJustItself()
    {
        Assert.That(HoverKnobMath.SiblingPaths("dispersalSpeedMult", 5), Is.EqualTo(new[] { "dispersalSpeedMult" }));
    }

    private class FakeBase
    {
#pragma warning disable CS0414
        [SerializeField] private float _inherited = 0f;
#pragma warning restore CS0414
    }

    [System.Serializable]
    private class FakeElement
    {
        [UnityEngine.Range(0f, 1f)] public float hue;
        public FakeElement[] children;
    }

    private class FakeRoot : FakeBase
    {
        [KnobIgnore] public int count;
        public FakeElement single;
        public List<FakeElement> list;
        public FakeElement[] array;
        public float[] values;
    }

    [TestCase("count", "count")]
    [TestCase("_inherited", "_inherited")]
    [TestCase("single.hue", "hue")]
    [TestCase("list.Array.data[2].hue", "hue")]
    [TestCase("array.Array.data[0].hue", "hue")]
    [TestCase("list.Array.data[0].children.Array.data[1].hue", "hue")]
    [TestCase("values.Array.data[1]", "values")]
    public void TryFindField_ResolvesSerializedPaths(string path, string expectedField)
    {
        Assert.That(HoverKnobMath.TryFindField(typeof(FakeRoot), path, out var field), Is.True);
        Assert.That(field.Name, Is.EqualTo(expectedField));
    }

    [TestCase("missing")]
    [TestCase("single.missing")]
    [TestCase("list.Array.data[0].missing")]
    public void TryFindField_UnknownPath_ReturnsFalse(string path)
    {
        Assert.That(HoverKnobMath.TryFindField(typeof(FakeRoot), path, out _), Is.False);
    }

    [Test]
    public void TryFindField_ExposesAttributes()
    {
        HoverKnobMath.TryFindField(typeof(FakeRoot), "count", out var ignored);
        HoverKnobMath.TryFindField(typeof(FakeRoot), "list.Array.data[0].hue", out var ranged);
        Assert.That(ignored.IsDefined(typeof(KnobIgnoreAttribute), false), Is.True);
        Assert.That(ranged.IsDefined(typeof(UnityEngine.RangeAttribute), false), Is.True);
    }
}

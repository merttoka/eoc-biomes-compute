using NUnit.Framework;
using UnityEngine;
using Biomes;

/// <summary>
/// Pins PrimordialMotion to the paper's conventions (Schmickl et al. 2016): handedness, the sign(0)
/// branch, turn-then-move order, the α = 180° stand-still, the torus seam, and the synchronous update.
/// The GPU kernel is in turn tested against PrimordialMotion (PrimordialKernelTests).
/// </summary>
public class PrimordialMotionTests
{
    private const float Deg = Mathf.Deg2Rad;
    private static readonly Vector2 World = new Vector2(100f, 60f);

    private static PrimordialMotion.Species Paper(float radius = 5f, float speed = 0.67f) =>
        new PrimordialMotion.Species { alpha = 180f * Deg, beta = 17f * Deg, radius = radius, speed = speed, otherTypeWeight = 1f };

    private static (Vector2[] pos, Vector2[] head, float[] n) Run(Vector2[] pos, Vector2[] head, int[] type,
                                                                PrimordialMotion.Species[] sp, int steps = 1)
    {
        var p = (Vector2[])pos.Clone(); var h = (Vector2[])head.Clone(); var n = new float[pos.Length];
        for (int s = 0; s < steps; s++)
        {
            var np = new Vector2[p.Length]; var nh = new Vector2[p.Length];
            PrimordialMotion.Step(p, h, type, sp, World, np, nh, n);
            p = np; h = nh;
        }
        return (p, h, n);
    }

    [Test]
    public void RightSide_IsClockwiseOfHeading_InYUpFrame()
    {
        Assert.That(PrimordialMotion.IsRight(Vector2.right, Vector2.down), Is.True);
        Assert.That(PrimordialMotion.IsRight(Vector2.right, Vector2.up), Is.False);
        Assert.That(PrimordialMotion.IsRight(Vector2.up, Vector2.right), Is.True);
    }

    [Test]
    public void TurnDelta_Balanced_IsAlphaOnly()
    {
        Assert.That(PrimordialMotion.TurnDelta(1f, 2f, 3f, 3f), Is.EqualTo(1f));
        Assert.That(PrimordialMotion.TurnDelta(1f, 2f, 0f, 0f), Is.EqualTo(1f));
    }

    [Test]
    public void TurnDelta_ScalesWithTotalCount_SignedByMajority()
    {
        Assert.That(PrimordialMotion.TurnDelta(0f, 1f, 1f, 3f), Is.EqualTo(4f));
        Assert.That(PrimordialMotion.TurnDelta(0f, 1f, 3f, 1f), Is.EqualTo(-4f));
    }

    [Test]
    public void PositiveTurn_IsARightTurn_TowardTheRightSide()
    {
        // Paper: "a positive change of φ is a turn to the right". It must turn toward the side
        // IsRight calls right — the two conventions agreeing is what makes this ⟨α, β⟩, not ⟨α, −β⟩.
        var h = PrimordialMotion.Turn(Vector2.right, 90f * Deg);
        Assert.That(h.x, Is.EqualTo(0f).Within(1e-6f));
        Assert.That(h.y, Is.EqualTo(-1f).Within(1e-6f));
        Assert.That(PrimordialMotion.IsRight(Vector2.right, h), Is.True);
    }

    [Test]
    public void Step_TurnsBeforeMoving()
    {
        var sp = new[] { new PrimordialMotion.Species { alpha = 90f * Deg, beta = 0f, radius = 5f, speed = 1f, otherTypeWeight = 1f } };
        var (p, h, _) = Run(new[] { new Vector2(50f, 30f) }, new[] { Vector2.right }, new[] { 0 }, sp);
        Assert.That(p[0].x, Is.EqualTo(50f).Within(1e-5f), "moved along the new heading, not the old");
        Assert.That(p[0].y, Is.EqualTo(29f).Within(1e-5f));
        Assert.That(h[0].y, Is.EqualTo(-1f).Within(1e-5f));
    }

    [Test]
    public void IsolatedParticle_AtAlpha180_HoldsPositionEveryTwoSteps()
    {
        var start = new Vector2(20f, 20f);
        var (p, _, n) = Run(new[] { start }, new[] { new Vector2(0.6f, 0.8f) }, new[] { 0 }, new[] { Paper() }, steps: 2);
        Assert.That(n[0], Is.EqualTo(0f));
        Assert.That(Vector2.Distance(p[0], start), Is.LessThan(1e-4f));
    }

    [Test]
    public void Neighbours_AcrossTheSeam_AreCounted_WithMinimumImageSide()
    {
        // A at x = 1, B at x = 99 (2 px away across the seam). A heads up: B is to A's left (−x).
        var pos = new[] { new Vector2(1f, 30f), new Vector2(99f, 30f) };
        var head = new[] { Vector2.up, Vector2.up };
        var sp = new[] { new PrimordialMotion.Species { alpha = 0f, beta = 10f * Deg, radius = 5f, speed = 0f, otherTypeWeight = 1f } };
        var (_, h, n) = Run(pos, head, new[] { 0, 0 }, sp);
        Assert.That(n[0], Is.EqualTo(1f));
        Assert.That(n[1], Is.EqualTo(1f));
        // A: L = 1 → Δφ = −β, a left turn toward −x (B's side). B: R = 1 → Δφ = +β, a right
        // turn toward +x (A's side): both turn toward the seam, the minimum-image neighbour.
        Assert.That(h[0].x, Is.LessThan(0f), "A turns left, toward −x");
        Assert.That(h[1].x, Is.GreaterThan(0f), "B turns right, toward +x");
    }

    [Test]
    public void OutsideRadius_IsNotANeighbour()
    {
        var (_, _, n) = Run(new[] { new Vector2(10f, 10f), new Vector2(15f, 10f) },
                            new[] { Vector2.up, Vector2.up }, new[] { 0, 0 }, new[] { Paper(radius: 5f) });
        Assert.That(n[0], Is.EqualTo(0f), "distance == r is outside (strict <)");
    }

    [Test]
    public void OtherTypeWeight_ScalesCrossSpeciesCounts()
    {
        var a = Paper(); a.otherTypeWeight = 0.25f;
        var b = Paper(); b.otherTypeWeight = 0f;
        var (_, _, n) = Run(new[] { new Vector2(10f, 10f), new Vector2(12f, 10f) },
                            new[] { Vector2.up, Vector2.up }, new[] { 0, 1 }, new[] { a, b });
        Assert.That(n[0], Is.EqualTo(0.25f));
        Assert.That(n[1], Is.EqualTo(0f));
    }

    [Test]
    public void Update_IsSynchronous_IndependentOfOrder()
    {
        var pos = new[] { new Vector2(10f, 10f), new Vector2(12f, 11f), new Vector2(11f, 13f) };
        var head = new[] { new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(-0.6f, 0.8f) };
        var sp = new[] { Paper() };
        var (p, h, _) = Run(pos, head, new[] { 0, 0, 0 }, sp);
        var (pr, hr, _) = Run(new[] { pos[2], pos[1], pos[0] }, new[] { head[2], head[1], head[0] }, new[] { 0, 0, 0 }, sp);
        for (int i = 0; i < 3; i++)
        {
            Assert.That(Vector2.Distance(p[i], pr[2 - i]), Is.LessThan(1e-5f));
            Assert.That(Vector2.Distance(h[i], hr[2 - i]), Is.LessThan(1e-5f));
        }
    }
}

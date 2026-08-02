using NUnit.Framework;
using UnityEngine;
using Biomes;

/// <summary>
/// Guards the neuron-layout mapping against CPU/GPU drift.
///
/// The mapping used to be written out in five places (three sim shaders, the composite
/// ring shader, and BiomeInjector's CPU path) with its scale declared in three separate
/// serialized fields. That invariant was hand-maintained and silently broke: 11.2 SIGGRAPH
/// and 11.3 DAC ran sims at (0.5,0.6) while rings and dispersal stamps stayed at
/// (0.4,0.75), displacing them by up to 5% of canvas width.
///
/// There are now two definitions — C# here and HLSL in Includes/neuron_layout.hlsl. These
/// tests transcribe the HLSL algebra literally and assert the C# agrees, so a future edit
/// to one without the other fails here instead of in a render.
/// </summary>
public class NeuronLayoutTests
{
    const float Eps = 1e-5f;

    // Literal transcription of HLSL NeuronToFieldUV. Deliberately NOT calling the
    // production code — an independent restatement is what makes this a real check.
    static Vector2 HlslNeuronToFieldUV(Vector2 np, Vector2 scale) => new Vector2(
        np.x * scale.x + (1f - scale.x) * 0.5f,
        np.y * scale.y + (1f - scale.y) * 0.5f);

    // Literal transcription of HLSL NeuronPxToFieldPx.
    static Vector2 HlslNeuronPxToFieldPx(Vector2 npPx, Vector2 scale, float rezX, float rezY) => new Vector2(
        npPx.x * scale.x + rezX * (1f - scale.x) * 0.5f,
        npPx.y * scale.y + rezY * (1f - scale.y) * 0.5f);

    static readonly Vector2[] Scales =
    {
        new Vector2(0.5f, 0.6f),    // 11.2 SIGGRAPH, 11.3 DAC
        new Vector2(0.4f, 0.75f),   // 11.1 CURRENTS
        new Vector2(0.8f, 0.9f),    // TestScene / default
        Vector2.one,                // full canvas
    };

    static readonly Vector2[] Corners =
    {
        new Vector2(0f, 0f),
        new Vector2(1f, 1f),
        new Vector2(0.5f, 0.5f),
        new Vector2(0f, 1f),
        new Vector2(1f, 0f),
    };

    [Test]
    public void UV_mapping_matches_hlsl([ValueSource(nameof(Scales))] Vector2 scale)
    {
        foreach (var np in Corners)
        {
            var expected = HlslNeuronToFieldUV(np, scale);
            var actual = NeuronLayout.ToFieldUV(np, scale);
            Assert.AreEqual(expected.x, actual.x, Eps, $"x at np={np} scale={scale}");
            Assert.AreEqual(expected.y, actual.y, Eps, $"y at np={np} scale={scale}");
        }
    }

    [Test]
    public void Pixel_mapping_matches_hlsl([ValueSource(nameof(Scales))] Vector2 scale)
    {
        const float rezX = 9472f, rezY = 900f;   // the DAC ultra-wide master
        foreach (var np in Corners)
        {
            var npPx = new Vector2(np.x * rezX, np.y * rezY);
            var expected = HlslNeuronPxToFieldPx(npPx, scale, rezX, rezY);
            var actual = NeuronLayout.PxToFieldPx(npPx, scale, rezX, rezY);
            Assert.AreEqual(expected.x, actual.x, 1e-2f, $"x at np={np} scale={scale}");
            Assert.AreEqual(expected.y, actual.y, 1e-2f, $"y at np={np} scale={scale}");
        }
    }

    /// <summary>
    /// The two entry points must describe the same mapping in different spaces — this is
    /// the identity that let the sims (pixel space) and the ring overlay (normalized space)
    /// agree despite looking different. If it ever fails, one of them was "optimised".
    /// </summary>
    [Test]
    public void Pixel_and_uv_paths_describe_the_same_mapping([ValueSource(nameof(Scales))] Vector2 scale)
    {
        const float rezX = 1920f, rezY = 1080f;
        foreach (var np in Corners)
        {
            var viaUV = NeuronLayout.ToFieldUV(np, scale);
            var viaPx = NeuronLayout.PxToFieldPx(
                new Vector2(np.x * rezX, np.y * rezY), scale, rezX, rezY);
            Assert.AreEqual(viaUV.x * rezX, viaPx.x, 1e-2f, $"x at np={np} scale={scale}");
            Assert.AreEqual(viaUV.y * rezY, viaPx.y, 1e-2f, $"y at np={np} scale={scale}");
        }
    }

    /// <summary>Scale (1,1) must be identity — the layout fills the canvas untransformed.</summary>
    [Test]
    public void Full_scale_is_identity()
    {
        foreach (var np in Corners)
        {
            var uv = NeuronLayout.ToFieldUV(np, Vector2.one);
            Assert.AreEqual(np.x, uv.x, Eps);
            Assert.AreEqual(np.y, uv.y, Eps);
        }
    }

    /// <summary>
    /// Centre is a fixed point at every scale — this is why the old desync was invisible in
    /// the middle of frame and worst at the edges, and why it went unnoticed for so long.
    /// </summary>
    [Test]
    public void Centre_is_a_fixed_point([ValueSource(nameof(Scales))] Vector2 scale)
    {
        var uv = NeuronLayout.ToFieldUV(new Vector2(0.5f, 0.5f), scale);
        Assert.AreEqual(0.5f, uv.x, Eps);
        Assert.AreEqual(0.5f, uv.y, Eps);
    }

    /// <summary>
    /// Regression witness for the defect this change fixes: at the frame edge, the desynced
    /// scales really did disagree by ~5% of width / 7.5% of height. Documents the magnitude
    /// so the fix cannot be quietly reverted as cosmetic.
    /// </summary>
    [Test]
    public void Desynced_scales_disagree_at_the_edges()
    {
        var sims = new Vector2(0.5f, 0.6f);
        var stale = new Vector2(0.4f, 0.75f);

        var simEdge = NeuronLayout.ToFieldUV(Vector2.one, sims);
        var staleEdge = NeuronLayout.ToFieldUV(Vector2.one, stale);

        Assert.AreEqual(0.05f, Mathf.Abs(simEdge.x - staleEdge.x), 1e-4f,
            "horizontal displacement at the right edge");
        Assert.AreEqual(0.075f, Mathf.Abs(simEdge.y - staleEdge.y), 1e-4f,
            "vertical displacement at the top edge");

        var simCentre = NeuronLayout.ToFieldUV(new Vector2(0.5f, 0.5f), sims);
        var staleCentre = NeuronLayout.ToFieldUV(new Vector2(0.5f, 0.5f), stale);
        Assert.AreEqual(0f, (simCentre - staleCentre).magnitude, Eps,
            "centre must be unaffected — that is why the defect hid");
    }
}

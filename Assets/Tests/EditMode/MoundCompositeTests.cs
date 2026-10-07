using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The composite's mound overlay (SimulationManager.compute) reads the walls from the biome's
/// sim-resolution detail layer when moundDetailOn is set, and from the coarse permeability
/// channel otherwise. Dispatches CompositeRenderKernel directly with no sims (black base).
/// </summary>
public class MoundCompositeTests
{
    private const string Path = "Assets/Workspace/11.0 Biomes/src/computes/SimulationManager.compute";
    private const int W = 8, H = 4;
    private static readonly Color Wall = Color.red;

    private ComputeShader _cs;
    private int _kernel;
    private RenderTexture _black, _out, _coarse, _detail;
    private ComputeBuffer _weights;

    [SetUp]
    public void SetUp()
    {
        if (!SystemInfo.supportsComputeShaders) Assert.Ignore("No compute support in this Editor (-nographics?)");
        _cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(Path);
        Assert.That(_cs, Is.Not.Null, Path);
        _kernel = _cs.FindKernel("CompositeRenderKernel");

        _black = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBHalf);
        _black.Create();
        var prev = RenderTexture.active;
        RenderTexture.active = _black;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;

        _out = new RenderTexture(W, H, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true };
        _out.Create();

        // A full wall everywhere in the coarse field (2×1 cells) …
        _coarse = new RenderTexture(2, 1, 0, RenderTextureFormat.RHalf)
            { dimension = TextureDimension.Tex2DArray, volumeDepth = 1, filterMode = FilterMode.Bilinear };
        _coarse.Create();
        Upload(_coarse, new[] { 0f, 0f }, TextureFormat.RHalf);

        // … and open ground in the detail layer except one wall pixel at (5, 2).
        _detail = new RenderTexture(W, H, 0, RenderTextureFormat.RFloat) { filterMode = FilterMode.Bilinear };
        _detail.Create();
        var d = new float[W * H];
        for (int i = 0; i < d.Length; i++) d[i] = 0.9f;
        d[2 * W + 5] = 0f;
        Upload(_detail, d, TextureFormat.RFloat);

        _weights = new ComputeBuffer(8, sizeof(float));
        _weights.SetData(new float[] { 1, 1, 1, 1, 1, 1, 1, 1 });
    }

    [TearDown]
    public void TearDown()
    {
        _weights?.Release();
        foreach (var rt in new[] { _black, _out, _coarse, _detail })
            if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
    }

    [Test]
    public void MoundDetailOn_PaintsWallsFromTheDetailLayer()
    {
        var px = Composite(detailOn: true);
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
            AssertColor(px[y * W + x], x == 5 && y == 2 ? Wall : Color.black, $"({x},{y})");
    }

    // Regression guard: with the detail off, the overlay is the coarse field's, as before.
    [Test]
    public void MoundDetailOff_PaintsWallsFromTheCoarseField()
    {
        var px = Composite(detailOn: false);
        for (int i = 0; i < px.Length; i++) AssertColor(px[i], Wall, $"#{i}");
    }

    private Color[] Composite(bool detailOn)
    {
        for (int i = 0; i < 8; i++) _cs.SetTexture(_kernel, "simInput" + i, _black);
        _cs.SetTexture(_kernel, "externalOverlay", _black);
        _cs.SetTexture(_kernel, "compositeOut", _out);
        _cs.SetBuffer(_kernel, "simWeights", _weights);
        _cs.SetInt("rezX", W);
        _cs.SetInt("rezY", H);
        _cs.SetInt("simCount", 0);
        _cs.SetFloat("overlayStrength", 0f);
        _cs.SetInt("keepOutCount", 0);
        _cs.SetFloat("keepOutFeather", 0f);

        _cs.SetTexture(_kernel, "permField", _coarse);
        _cs.SetInt("permChannel", 0);
        _cs.SetFloat("permOpenBaselineOv", 0.9f);
        _cs.SetFloat("moundStrength", 1f);
        _cs.SetVector("moundColor", Wall);
        _cs.SetTexture(_kernel, "permDetail", _detail);
        _cs.SetInt("moundDetailOn", detailOn ? 1 : 0);
        _cs.SetFloat("masterLevel", 1f);   // show blackout master: full output
        _cs.Dispatch(_kernel, 1, 1, 1);   // numthreads(8,8,1) covers 8×4

        var request = AsyncGPUReadback.Request(_out);
        request.WaitForCompletion();
        Assert.That(request.hasError, Is.False, "readback");
        return request.GetData<Color>().ToArray();
    }

    private static void AssertColor(Color got, Color want, string at)
    {
        Assert.That(got.r, Is.EqualTo(want.r).Within(1e-3f), at + " r");
        Assert.That(got.g, Is.EqualTo(want.g).Within(1e-3f), at + " g");
        Assert.That(got.b, Is.EqualTo(want.b).Within(1e-3f), at + " b");
    }

    private static void Upload(RenderTexture dst, float[] values, TextureFormat format)
    {
        var src = new Texture2D(dst.width, dst.height, format, false, true);
        if (format == TextureFormat.RHalf)
        {
            var h = new ushort[values.Length];
            for (int i = 0; i < h.Length; i++) h[i] = Mathf.FloatToHalf(values[i]);
            src.SetPixelData(h, 0);
        }
        else src.SetPixelData(values, 0);
        src.Apply(false);
        Graphics.CopyTexture(src, 0, 0, dst, 0, 0);
        Object.DestroyImmediate(src);
    }
}

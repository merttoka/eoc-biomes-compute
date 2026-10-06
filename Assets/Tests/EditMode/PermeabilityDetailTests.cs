using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The termite mound detail layer in Biome.compute: a sim-resolution copy of CH_PERMEABILITY
/// that every build event also lowers, with a Gaussian brush at the agent's sub-pixel position,
/// so the composite can paint the walls sharper than the biome grid. Dispatches the kernels
/// directly on a 16×8 canvas over a 4×2 biome (4×4-px cells).
/// </summary>
public class PermeabilityDetailTests
{
    private const string BiomePath = "Assets/Workspace/11.0 Biomes/src/computes/Biome.compute";
    private const int Channels = 15, Perm = 7;
    private const int SimW = 16, SimH = 8, FieldW = 4, FieldH = 2;
    private const float Cell = 4f;           // sqrt(cell width × cell height), in sim px
    private const float Tol = 5e-4f;         // the RHalf field: one ulp near 0.9 (stores may truncate)
    private const float DetailTol = 1e-5f;   // the RFloat detail layer
    private static readonly float Open = Mathf.HalfToFloat(Mathf.FloatToHalf(0.9f));   // exact in both

    private ComputeShader _cs;
    private int _build;
    private RenderTexture _field, _before, _detail;   // _before: the interact pass's input
    private ComputeBuffer _firing;
    private readonly List<ComputeBuffer> _agents = new();

    [SetUp]
    public void SetUp()
    {
        if (!SystemInfo.supportsComputeShaders) Assert.Ignore("No compute support in this Editor (-nographics?)");
        _cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(BiomePath);
        Assert.That(_cs, Is.Not.Null, BiomePath);
        _build = _cs.FindKernel("BuildPermeabilityKernel");

        _field = NewField();
        _before = NewField();

        _detail = new RenderTexture(SimW, SimH, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = true, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat,
        };
        _detail.Create();
        Upload(_detail, 0, Filled(SimW * SimH, Open));

        _firing = new ComputeBuffer(1, sizeof(float));
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var b in _agents) b.Release();
        _agents.Clear();
        _firing?.Release();
        foreach (var rt in new[] { _field, _before, _detail })
            if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
    }

    [Test]
    public void Build_LowersDetailWithGaussianAtSubPixelPosition()
    {
        var pos = new Vector2(6.3f, 3.6f);
        Build(pos, amount: 0.02f, sigma: 1f, gain: 1f);

        float peak = Peak(0.02f, 1f, 1f);
        var detail = Read(_detail, 0);
        for (int y = 0; y < SimH; y++)
        for (int x = 0; x < SimW; x++)
            Assert.That(detail[y * SimW + x], Is.EqualTo(Open - peak * Weight(x, y, pos, 1f)).Within(DetailTol), $"detail ({x},{y})");
    }

    // The canvas is a torus (agents wrap), so a brush at an edge continues on the far side.
    [Test]
    public void Build_SplatWrapsAcrossCanvasEdges()
    {
        var pos = new Vector2(0.2f, 0.3f);
        Build(pos, amount: 0.02f, sigma: 1f, gain: 1f);

        float peak = Peak(0.02f, 1f, 1f);
        var detail = Read(_detail, 0);
        for (int y = 0; y < SimH; y++)
        for (int x = 0; x < SimW; x++)
            Assert.That(detail[y * SimW + x], Is.EqualTo(Open - peak * Weight(x, y, pos, 1f)).Within(DetailTol), $"detail ({x},{y})");
    }

    [Test]
    public void Build_DetailOff_LeavesDetailOpen()
    {
        Build(new Vector2(6.3f, 3.6f), amount: 0.02f, sigma: 1f, gain: 1f, detailOn: false);
        foreach (float v in Read(_detail, 0)) Assert.That(v, Is.EqualTo(Open));
    }

    // The detail draws the same events, not its own: no build event, no splat.
    [Test]
    public void Build_NoEvent_LeavesDetailOpen()
    {
        Build(new Vector2(6.3f, 3.6f), amount: 0.02f, sigma: 1f, gain: 1f, prob: 0f);
        foreach (float v in Read(_detail, 0)) Assert.That(v, Is.EqualTo(Open));
    }

    // gain 1 = a termite's path reads as strong in the detail layer as in the coarse field:
    // a dense line of builds lowers the line's centre row by what it lowers each cell it crosses.
    [Test]
    public void Build_LineOfEvents_MatchesTheCoarseCellWall()
    {
        const float amount = 10f / 2048f;   // 10 half ulps near 0.9, so the coarse writes are exact
        for (float x = 0.25f; x < SimW; x += 0.5f)   // 2 events per px → 8 per cell
            Build(new Vector2(x, 3.5f), amount, sigma: 1f, gain: 1f);

        var perm = Read(_field, Perm);
        var detail = Read(_detail, 0);
        for (int x = 4; x < SimW - 4; x++)   // interior; the wrap seam has its own test
        {
            float coarse = perm[(int)(x / Cell)];   // row 0 holds y = 3.5
            Assert.That(coarse, Is.EqualTo(Open - 8 * amount).Within(Tol), $"coarse cell {(int)(x / Cell)}");
            Assert.That(detail[3 * SimW + x], Is.EqualTo(coarse).Within(1e-4f), $"detail line px {x}");
        }
    }

    // Regression guard: the coarse write the termites perceive is unchanged by the detail splat.
    [Test]
    public void Build_StillLowersTheCoarseCellByBuildAmount()
    {
        Build(new Vector2(6.3f, 3.6f), amount: 0.02f, sigma: 1f, gain: 1f);
        var perm = Read(_field, Perm);
        for (int y = 0; y < FieldH; y++)
        for (int x = 0; x < FieldW; x++)
            Assert.That(perm[y * FieldW + x], Is.EqualTo(x == 1 && y == 0 ? Open - 0.02f : Open).Within(Tol), $"cell ({x},{y})");
    }

    // ── healing ──────────────────────────────────────────────────────────────

    // The detail heals by what its coarse cell healed in the interact pass: every cell's wall
    // (open − perm) went 0.4 → 0.15 here, so every detail wall scales by 0.375.
    [Test]
    public void Relax_ScalesDetailWallsByTheirCellsHealing()
    {
        SetPerm(_before, 0.5f);
        SetPerm(_field, 0.75f);
        Upload(_detail, 0, Filled(SimW * SimH, 0.3f));
        Relax();

        float ratio = (0.9f - 0.75f) / (0.9f - 0.5f);
        foreach (float v in Read(_detail, 0))
            Assert.That(v, Is.EqualTo(0.9f - (0.9f - 0.3f) * ratio).Within(DetailTol));
    }

    // RHalf rounding stalls the coarse relax near open ground; the detail stalls with it rather
    // than fading where the coarse walls (and the termites' habitat) stay.
    [Test]
    public void Relax_StalledCell_LeavesDetailUnchanged()
    {
        SetPerm(_before, 0.625f);
        SetPerm(_field, 0.625f);
        Upload(_detail, 0, Filled(SimW * SimH, 0.3f));
        Relax();
        foreach (float v in Read(_detail, 0)) Assert.That(v, Is.EqualTo(0.3f).Within(DetailTol));
    }

    // Brush tails spill past the cells that were built in; with no coarse wall around there is
    // no healing to copy, even when rounding nudges the open field by an ulp (ratio -4 → 0
    // would wipe the tails).
    [Test]
    public void Relax_NoCoarseWall_LeavesDetailUnchanged()
    {
        SetPerm(_field, 0.9004f);   // one half ulp above open
        Upload(_detail, 0, Filled(SimW * SimH, 0.8f));
        Relax();
        foreach (float v in Read(_detail, 0)) Assert.That(v, Is.EqualTo(0.8f).Within(DetailTol));
    }

    // Neighbouring cells that heal differently blend across their boundary: a step at every cell
    // edge would bring the blocks back as the walls heal.
    [Test]
    public void Relax_BlendsAcrossCellBoundaries()
    {
        SetPerm(_before, 0.5f);                                  // wall 0.4 everywhere
        Upload(_field, Perm, ByColumn(x => x < 2 ? 0.75f : 0.5f));   // cells 0-1 heal to 0.15, 2-3 stall
        Upload(_detail, 0, Filled(SimW * SimH, 0.3f));
        Relax();

        var d = Read(_detail, 0);
        for (int x = 5; x <= 10; x++)   // from left of cell 1's centre (x = 6) to cell 2's (x = 10)
        {
            float t = Mathf.Clamp01((x + 0.5f - 6f) / Cell);
            float ratio = Mathf.Lerp(0.15f, 0.4f, t) / 0.4f;
            Assert.That(d[x], Is.EqualTo(0.9f - 0.6f * ratio).Within(3e-3f), $"px {x}");   // filter weights are ~8-bit
        }
    }

    // ── sync (allocation and every permeability reset) ───────────────────────

    [Test]
    public void Sync_CopiesTheCoarseField()
    {
        SetPerm(_field, 0.75f);
        Sync();
        foreach (float v in Read(_detail, 0)) Assert.That(v, Is.EqualTo(0.75f).Within(DetailTol));
    }

    // Switched on mid-run, the walls built so far carry over smoothly, not as blocks.
    [Test]
    public void Sync_UpsamplesBilinearly()
    {
        Upload(_field, Perm, ByColumn(x => x < 2 ? 0.5f : 0.75f));
        Sync();

        var d = Read(_detail, 0);
        for (int x = 5; x <= 10; x++)
        {
            float t = Mathf.Clamp01((x + 0.5f - 6f) / Cell);
            Assert.That(d[x], Is.EqualTo(Mathf.Lerp(0.5f, 0.75f, t)).Within(3e-3f), $"px {x}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void Relax()
    {
        int k = Kernel("RelaxPermDetailKernel");
        _cs.SetFloat("permOpenBaseline", 0.9f);
        _cs.SetInt("permDetailRezX", SimW);
        _cs.SetInt("permDetailRezY", SimH);
        _cs.SetTexture(k, "fieldRead", _field);
        _cs.SetTexture(k, "permBefore", _before);
        _cs.SetTexture(k, "permDetail", _detail);
        _cs.Dispatch(k, (SimW + 7) / 8, (SimH + 7) / 8, 1);
    }

    private void Sync()
    {
        int k = Kernel("SyncPermDetailKernel");
        _cs.SetInt("permDetailRezX", SimW);
        _cs.SetInt("permDetailRezY", SimH);
        _cs.SetTexture(k, "fieldRead", _field);
        _cs.SetTexture(k, "permDetail", _detail);
        _cs.Dispatch(k, (SimW + 7) / 8, (SimH + 7) / 8, 1);
    }

    private int Kernel(string name)
    {
        Assert.That(_cs.HasKernel(name), Is.True, name);
        return _cs.FindKernel(name);
    }

    private static RenderTexture NewField()
    {
        var rt = new RenderTexture(FieldW, FieldH, 0, RenderTextureFormat.RHalf)
        {
            dimension = TextureDimension.Tex2DArray, volumeDepth = Channels,
            enableRandomWrite = true, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat,
        };
        rt.Create();
        for (int c = 0; c < Channels; c++) Upload(rt, c, Filled(FieldW * FieldH, c == Perm ? Open : 0f));
        return rt;
    }

    private static void SetPerm(RenderTexture field, float v) => Upload(field, Perm, Filled(FieldW * FieldH, v));

    private static float[] ByColumn(System.Func<int, float> value)
    {
        var a = new float[FieldW * FieldH];
        for (int y = 0; y < FieldH; y++)
        for (int x = 0; x < FieldW; x++) a[y * FieldW + x] = value(x);
        return a;
    }

    // One build dispatch for one agent (separate dispatches never race on overlapping splats).
    private void Build(Vector2 pos, float amount, float sigma, float gain, bool detailOn = true, float prob = 1f)
    {
        var agent = new ComputeBuffer(1, 20);   // AgentPos: position, direction, typeId
        agent.SetData(new[] { pos.x, pos.y, 0f, 0f, 0f });
        _agents.Add(agent);

        _cs.SetInt("rezX", FieldW);
        _cs.SetInt("rezY", FieldH);
        _cs.SetInt("agentCount", 1);
        _cs.SetFloat("simToFieldX", (float)FieldW / SimW);
        _cs.SetFloat("simToFieldY", (float)FieldH / SimH);
        _cs.SetFloat("buildDepositProb", prob);
        _cs.SetFloat("buildFiringDepositProb", prob);
        _cs.SetFloat("buildFiringThreshold", 1f);
        _cs.SetFloat("buildAmount", amount);
        _cs.SetInt("buildNeuronCount", 0);
        _cs.SetInt("buildTimeSeed", 7);
        _cs.SetBuffer(_build, "buildFiring", _firing);
        _cs.SetBuffer(_build, "agentPositions", agent);
        _cs.SetTexture(_build, "fieldWrite", _field);

        _cs.SetTexture(_build, "permDetail", _detail);
        _cs.SetInt("permDetailOn", detailOn ? 1 : 0);
        _cs.SetInt("permDetailRezX", SimW);
        _cs.SetInt("permDetailRezY", SimH);
        _cs.SetFloat("permDetailSigma", sigma);
        _cs.SetFloat("permDetailGain", gain);
        _cs.Dispatch(_build, 1, 1, 1);
    }

    // Per-event peak lowering: gain 1 spreads a build's cell-wide lowering along the path.
    private static float Peak(float amount, float gain, float sigma) =>
        amount * gain * Cell / (sigma * Mathf.Sqrt(2f * Mathf.PI));

    // Brush weight at a pixel centre: a Gaussian cut to the ±ceil(3σ) pixel box around the
    // agent's pixel, with offsets taken by minimum image on the torus.
    private static float Weight(int x, int y, Vector2 pos, float sigma)
    {
        int r = Mathf.CeilToInt(3f * sigma);
        var cell = new Vector2(Mathf.Floor(pos.x), Mathf.Floor(pos.y));
        float ox = Wrap(x - cell.x, SimW), oy = Wrap(y - cell.y, SimH);
        if (Mathf.Abs(ox) > r || Mathf.Abs(oy) > r) return 0f;
        float dx = ox + 0.5f - (pos.x - cell.x), dy = oy + 0.5f - (pos.y - cell.y);
        return Mathf.Exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
    }

    private static float Wrap(float d, int size) => d - size * Mathf.Round(d / size);

    private static float[] Filled(int n, float v)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = v;
        return a;
    }

    // RHalf (the biome field) or RFloat (the detail layer), one slice.
    private static void Upload(RenderTexture dst, int slice, float[] values)
    {
        bool half = dst.format == RenderTextureFormat.RHalf;
        var src = new Texture2D(dst.width, dst.height, half ? TextureFormat.RHalf : TextureFormat.RFloat, false, true);
        if (half)
        {
            var h = new ushort[values.Length];
            for (int i = 0; i < h.Length; i++) h[i] = Mathf.FloatToHalf(values[i]);
            src.SetPixelData(h, 0);
        }
        else src.SetPixelData(values, 0);
        src.Apply(false);
        Graphics.CopyTexture(src, 0, 0, dst, slice, 0);
        Object.DestroyImmediate(src);
    }

    private static float[] Read(RenderTexture rt, int slice)
    {
        var request = rt.dimension == TextureDimension.Tex2DArray
            ? AsyncGPUReadback.Request(rt, 0, 0, rt.width, 0, rt.height, slice, 1)
            : AsyncGPUReadback.Request(rt);
        request.WaitForCompletion();
        Assert.That(request.hasError, Is.False, "readback");
        if (rt.format != RenderTextureFormat.RHalf) return request.GetData<float>().ToArray();
        var raw = request.GetData<ushort>();
        var values = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++) values[i] = Mathf.HalfToFloat(raw[i]);
        return values;
    }
}

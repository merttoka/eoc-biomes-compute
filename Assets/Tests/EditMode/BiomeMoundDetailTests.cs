using System;
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

/// <summary>
/// Biome's mound detail layer, end to end through the real component: it exists only while
/// enabled, at the sim resolution BuildPermeability is given; builds draw into it; the PDE step
/// heals it with the field; every permeability reset re-syncs it. A 32×16 sim over an 8×4 biome.
/// Reflection: Biome and its config live in Assembly-CSharp.
/// </summary>
public class BiomeMoundDetailTests
{
    private const int SimW = 32, SimH = 16, Perm = 7;
    private static readonly float Open = Mathf.HalfToFloat(Mathf.FloatToHalf(0.9f));
    private static Type T(string name) => Type.GetType("Biomes." + name + ", Assembly-CSharp");

    private GameObject _go;
    private Component _biome;
    private ScriptableObject _config;
    private ComputeBuffer _agent;

    [SetUp]
    public void SetUp()
    {
        if (!SystemInfo.supportsComputeShaders) Assert.Ignore("No compute support in this Editor (-nographics?)");
        _go = new GameObject("biome-mound-detail-test");
        // Adding a component in edit mode sends Unity's Reset message; unconfigured (no compute
        // shader), Biome.Reset throws in Allocate. Expect that one log; the real Reset follows.
        LogAssert.Expect(LogType.Exception, new Regex("NullReferenceException"));
        _biome = _go.AddComponent(T("Biome"));
        Set("cs", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Workspace/11.0 Biomes/src/computes/Biome.compute"));
        Set("biomeRezX", 8);
        Set("biomeRezY", 4);
        Set("showDebugGrid", false);

        // Permeability heals hard (visible in one RHalf step) toward exactly the open baseline,
        // and nothing else moves it.
        _config = ScriptableObject.CreateInstance(T("BiomeFieldConfig"));
        _config.GetType().GetField("temperatureToPermeability").SetValue(_config, 0f);
        var perm = ((IList)_config.GetType().GetField("channels").GetValue(_config))[Perm];
        perm.GetType().GetField("relaxRate").SetValue(perm, 0.5f);
        perm.GetType().GetField("diffuseRate").SetValue(perm, 0f);
        perm.GetType().GetField("advectedByFlow").SetValue(perm, false);
        Set("fieldConfig", _config);
        Call("Reset");

        _agent = new ComputeBuffer(1, 20);   // AgentPos: position, direction, typeId
        _agent.SetData(new[] { 13.3f, 6.6f, 0f, 0f, 0f });
    }

    [TearDown]
    public void TearDown()
    {
        _agent?.Release();
        Call("Release");
        UnityEngine.Object.DestroyImmediate(_go);
        UnityEngine.Object.DestroyImmediate(_config);
    }

    [Test]
    public void DetailOff_NoLayer_AndBuildsStillLowerTheField()
    {
        Build();
        Assert.That(Detail, Is.Null);
        Assert.That(Coarse(3, 1), Is.EqualTo(Open - 0.02f).Within(5e-4f));
    }

    [Test]
    public void DetailOn_BuildAllocatesAtSimResolution_AndDrawsTheEvent()
    {
        Enable(true);
        Build();
        Assert.That(Detail, Is.Not.Null);
        Assert.That(Detail.width, Is.EqualTo(SimW));
        Assert.That(Detail.height, Is.EqualTo(SimH));
        Assert.That(DetailAt(13, 6), Is.LessThan(Open - 0.01f), "the event's pixel");
        Assert.That(DetailAt(2, 2), Is.EqualTo(Open).Within(1e-3f), "elsewhere");
    }

    [Test]
    public void Step_HealsTheDetailWithTheField()
    {
        Enable(true);
        for (int i = 0; i < 10; i++) Build(seed: i);
        float before = Open - DetailAt(13, 6);
        Call("Step");
        float after = Open - DetailAt(13, 6);
        Assert.That(after, Is.LessThan(before * 0.9f), "healed");
        Assert.That(after, Is.GreaterThan(0f), "not wiped");
    }

    [Test]
    public void ClearPermeability_ResyncsTheDetailToOpen()
    {
        Enable(true);
        Build();
        Call("ClearPermeability");
        Assert.That(DetailAt(13, 6), Is.EqualTo(Open).Within(1e-3f));
    }

    [Test]
    public void Reset_ResyncsTheDetailToOpen()
    {
        Enable(true);
        Build();
        Call("Reset");
        Assert.That(DetailAt(13, 6), Is.EqualTo(Open).Within(1e-3f));
    }

    [Test]
    public void SwitchingOff_ReleasesTheLayer()
    {
        Enable(true);
        Build();
        Enable(false);
        Assert.That(Detail, Is.Null);
    }

    // Switched on mid-run, the walls built so far carry over instead of vanishing.
    [Test]
    public void SwitchingOnLater_CarriesOverExistingWalls()
    {
        for (int i = 0; i < 10; i++) Build(seed: i);   // detail off: coarse walls only
        Enable(true);
        Build(prob: 0f);                                // allocates; no new event
        Assert.That(DetailAt(13, 6), Is.LessThan(Open - 0.05f));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private RenderTexture Detail => (RenderTexture)_biome.GetType().GetProperty("PermeabilityDetail").GetValue(_biome);

    private void Enable(bool on) => Call("SetPermeabilityDetail", on, 1.5f, 1f);

    private void Build(int seed = 0, float prob = 1f) =>
        Call("BuildPermeability", _agent, 1, null, 0, prob, prob, 1f, 0.02f, seed, SimW, SimH);

    private float DetailAt(int x, int y)
    {
        var request = AsyncGPUReadback.Request(Detail);
        request.WaitForCompletion();
        Assert.That(request.hasError, Is.False, "readback");
        return request.GetData<float>()[y * SimW + x];
    }

    private float Coarse(int x, int y)
    {
        var field = (RenderTexture)_biome.GetType().GetProperty("FieldReadArray").GetValue(_biome);
        var request = AsyncGPUReadback.Request(field, 0, 0, field.width, 0, field.height, Perm, 1);
        request.WaitForCompletion();
        Assert.That(request.hasError, Is.False, "readback");
        return Mathf.HalfToFloat(request.GetData<ushort>()[y * field.width + x]);
    }

    private void Set(string field, object value) => _biome.GetType().GetField(field).SetValue(_biome, value);

    private void Call(string method, params object[] args) => _biome.GetType().GetMethod(method).Invoke(_biome, args);
}

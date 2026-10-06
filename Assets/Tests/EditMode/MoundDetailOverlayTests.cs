using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

/// <summary>
/// SimulationManager's mound detail switch, end to end: Step pushes it to the biome, the termite
/// builds land in the biome's detail layer, and the composite paints the walls from it. A 64×32
/// output over a 4×2 biome (16-px cells), so a pixel can sit inside a walled cell yet far from the
/// termite that built it. Reflection: the manager and biome live in Assembly-CSharp.
/// </summary>
public class MoundDetailOverlayTests
{
    private const int W = 64, H = 32;
    private static Type T(string name) => Type.GetType("Biomes." + name + ", Assembly-CSharp");

    private GameObject _go;
    private Component _biome, _manager;
    private ScriptableObject _config;
    private ComputeBuffer _agent;

    [SetUp]
    public void SetUp()
    {
        if (!SystemInfo.supportsComputeShaders) Assert.Ignore("No compute support in this Editor (-nographics?)");
        _go = new GameObject("mound-detail-overlay-test");
        // Unity's Reset message on AddComponent runs Biome.Reset unconfigured, which throws.
        LogAssert.Expect(LogType.Exception, new Regex("NullReferenceException"));
        _biome = _go.AddComponent(T("Biome"));
        Set(_biome, "cs", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Workspace/11.0 Biomes/src/computes/Biome.compute"));
        Set(_biome, "biomeRezX", 4);
        Set(_biome, "biomeRezY", 2);
        Set(_biome, "showDebugGrid", false);
        _config = ScriptableObject.CreateInstance(T("BiomeFieldConfig"));
        _config.GetType().GetField("temperatureToPermeability").SetValue(_config, 0f);
        var perm = ((IList)_config.GetType().GetField("channels").GetValue(_config))[7];
        perm.GetType().GetField("relaxRate").SetValue(perm, 0f);   // walls hold still
        Set(_biome, "fieldConfig", _config);

        _manager = _go.AddComponent(T("SimulationManager"));
        Set(_manager, "compositeCS", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Workspace/11.0 Biomes/src/computes/SimulationManager.compute"));
        Set(_manager, "biome", _biome);
        Set(_manager, "rezX", W);
        Set(_manager, "rezY", H);
        Set(_manager, "moundOverlayStrength", 1f);
        Set(_manager, "moundColor", Color.red);
        Call(_manager, "Reset");

        _agent = new ComputeBuffer(1, 20);   // AgentPos: position, direction, typeId
        _agent.SetData(new[] { 8.3f, 8.6f, 0f, 0f, 0f });
    }

    [TearDown]
    public void TearDown()
    {
        _agent?.Release();
        Call(_manager, "Release");
        Call(_biome, "Release");
        UnityEngine.Object.DestroyImmediate(_go);
        UnityEngine.Object.DestroyImmediate(_config);
    }

    [Test]
    public void DetailOn_PaintsTheWallWhereTheTermiteBuilt_NotAcrossItsCell()
    {
        Set(_manager, "moundDetail", true);
        var red = BuildAndComposite();
        Assert.That(red(8, 8), Is.GreaterThan(0.5f), "at the termite");
        Assert.That(red(14, 14), Is.LessThan(0.02f), "same cell, 8 px away");
    }

    // Regression guard: off, the overlay is the coarse field's — the whole cell, blurred out.
    [Test]
    public void DetailOff_PaintsTheCoarseCell()
    {
        var red = BuildAndComposite();
        Assert.That(red(14, 14), Is.GreaterThan(0.1f));
        Assert.That(Get(_biome, "PermeabilityDetail"), Is.Null);
    }

    [Test]
    public void SwitchingDetailOff_ReleasesTheBiomeLayer()
    {
        Set(_manager, "moundDetail", true);
        BuildAndComposite();
        Assert.That(Get(_biome, "PermeabilityDetail"), Is.Not.Null);
        Set(_manager, "moundDetail", false);
        Call(_manager, "Step");
        Assert.That(Get(_biome, "PermeabilityDetail"), Is.Null);
    }

    // One manager step (pushes the switch), ten builds that wall the termite's cell, a composite.
    private Func<int, int, float> BuildAndComposite()
    {
        Call(_manager, "Step");
        for (int i = 0; i < 10; i++)
            Call(_biome, "BuildPermeability", _agent, 1, null, 0, 1f, 1f, 1f, 0.09f, i, W, H);
        _manager.GetType().GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_manager, null);

        var output = (RenderTexture)Get(_manager, "CompositeOutputTexture");
        var request = AsyncGPUReadback.Request(output);
        request.WaitForCompletion();
        Assert.That(request.hasError, Is.False, "readback");
        var px = request.GetData<ushort>().ToArray();   // ARGBHalf: 4 halves per pixel
        return (x, y) => Mathf.HalfToFloat(px[(y * W + x) * 4]);
    }

    private static void Set(Component c, string field, object value) => c.GetType().GetField(field).SetValue(c, value);
    private static object Get(Component c, string property) => c.GetType().GetProperty(property).GetValue(c);
    private static void Call(Component c, string method, params object[] args) => c.GetType().GetMethod(method).Invoke(c, args);
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Biomes;

/// <summary>
/// The live palette cycle (AgentPaletteCycler) on a real PhysarumSim component with plain params
/// instances — no GPU, nothing started. Covers fades (shortest-arc hue), grey swatches keeping the
/// hue, re-imposing on a freshly reset sim, and the edge inputs OSC and the inspector can feed it.
/// Reflection: the cycler, sims and params live in Assembly-CSharp.
/// </summary>
public class AgentPaletteCyclerTests
{
    private const float Eps = 1e-4f;
    private static Type T(string name) => Type.GetType("Biomes." + name + ", Assembly-CSharp");

    private static readonly (float h, float s, float b) Authored0 = (0.9f, 0.5f, 0.8f);
    private static readonly (float h, float s, float b) Authored1 = (0.3f, 0.5f, 0.8f);

    private GameObject _go;
    private Component _sim;
    private object _cycler;
    private IList _sims;
    private readonly List<UnityEngine.Object> _made = new();

    [SetUp]
    public void SetUp()
    {
        var cyclerType = T("AgentPaletteCycler");
        Assert.That(cyclerType, Is.Not.Null, "Biomes.AgentPaletteCycler");
        _go = new GameObject("palette-cycler-test");
        // Adding a component in edit mode sends Unity's Reset message, which runs the sim's own
        // Reset(): unconfigured (no compute shader) it throws in Allocate after making textures and
        // a params clone. Expect that one log; TearDown releases what it made.
        LogAssert.Expect(LogType.Exception, new Regex("NullReferenceException"));
        _sim = _go.AddComponent(T("PhysarumSim"));
        _made.Add((ScriptableObject)SimField("agentParams").GetValue(_sim));
        var preset = MakeParams();
        SimField("paramsSO").SetValue(_sim, preset);
        SimField("agentParams").SetValue(_sim, MakeParams());
        SimField("runState").SetValue(_sim, Enum.Parse(T("SimRunState"), "Running"));
        _sims = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("SimulationBase")));
        _sims.Add(_sim);

        _cycler = Activator.CreateInstance(cyclerType);
        Palettes.Add(Palette(Color.red, Color.blue));                    // 0
        Palettes.Add(Palette(new Color(0.5f, 0.5f, 0.5f)));              // 1: grey
        cyclerType.GetField("fadeSeconds").SetValue(_cycler, 2f);
    }

    [TearDown]
    public void TearDown()
    {
        _sim.GetType().GetMethod("Release").Invoke(_sim, null);
        UnityEngine.Object.DestroyImmediate(_go);
        foreach (var o in _made) UnityEngine.Object.DestroyImmediate(o);
        _made.Clear();
    }

    [Test]
    public void InstantSelect_WritesThePaletteColors()
    {
        Select(0, instant: true);
        AssertHsb((0f, 1f, 1f), 0);
        AssertHsb((2f / 3f, 1f, 1f), 1);
    }

    [Test]
    public void Fade_TakesTheShortHueArc_AndEndsOnTarget()
    {
        Select(0, instant: false);
        AssertHsb(Authored0, 0);                  // nothing moves before the first tick
        Tick(1f);                                 // half of fadeSeconds 2; smoothstep(0.5) = 0.5
        AssertHsb((0.95f, 0.75f, 0.9f), 0);       // 0.9 → 0.0 through 1.0, not back through 0.45
        Tick(1f);
        AssertHsb((0f, 1f, 1f), 0);
        AssertHsb((2f / 3f, 1f, 1f), 1);
    }

    [Test]
    public void CompletedFade_LandsExactlyOnThePaletteValue()
    {
        // 0.12 → red: the shortest-arc lerp's float wrap at t = 1 lands on hue 1.0, not red's ~0
        // (same color, but a knob reading the param would sit at the wrong end).
        SetHsb(Live, 0, (0.12f, 0.5f, 0.8f));
        Select(0, instant: false);
        Tick(2f);
        float hue = (float)Live.GetType().GetMethod("GetValue").Invoke(Live, new object[] { "hue", 0 });
        Assert.That(hue, Is.EqualTo(AgentColor.FromRgb(Color.red, 0.12f, 0.5f).h));
    }

    [Test]
    public void GreySwatch_KeepsEachTypesHue()
    {
        Select(1, instant: true);
        AssertHsb((Authored0.h, 0f, 0.5f), 0);
        AssertHsb((Authored1.h, 0f, 0.5f), 1);
    }

    [Test]
    public void ResetMidFade_FreshParamsGetTheFadesCurrentValue()
    {
        Select(0, instant: false);
        Tick(1f);
        SimulateReset();
        Call("Reimpose", _sim);
        AssertHsb((0.95f, 0.75f, 0.9f), 0);       // where the fade is, not the preset
        Tick(1f);
        AssertHsb((0f, 1f, 1f), 0);               // and the fade carries on into the new params
    }

    [Test]
    public void ResetOnSettledPalette_PutsThePaletteBack()
    {
        Select(0, instant: true);
        SimulateReset();
        Call("Reimpose", _sim);
        AssertHsb((0f, 1f, 1f), 0);
    }

    [Test]
    public void ResetOnSettledPreset_LeavesTheResetAlone()
    {
        SetHsb(Live, 0, (0.5f, 0.5f, 0.5f));      // e.g. a live knob edit after the reset
        Call("Reimpose", _sim);
        AssertHsb((0.5f, 0.5f, 0.5f), 0);
    }

    [Test]
    public void PresetStop_RestoresAuthoredColors()
    {
        Select(0, instant: true);
        Select(-1, instant: true);
        AssertHsb(Authored0, 0);
        AssertHsb(Authored1, 1);
    }

    [Test]
    public void OutOfRangeIndex_Clamps()
    {
        Select(99, instant: true);
        Assert.That(ActiveIndex, Is.EqualTo(1));
        Select(-5, instant: true);
        Assert.That(ActiveIndex, Is.EqualTo(-1));
        AssertHsb(Authored0, 0);
    }

    [Test]
    public void PaletteRemovedDuringShow_DoesNotThrow()
    {
        Select(1, instant: true);
        Palettes.RemoveAt(1);
        Assert.That(ActiveName, Is.EqualTo("(missing palette)"));
        SimulateReset();
        Assert.DoesNotThrow(() => Call("Reimpose", _sim));
        Assert.DoesNotThrow(() => Tick(0.5f));
    }

    [Test]
    public void FadeSecondsSetToZeroMidFade_SettlesOnTarget()
    {
        Select(0, instant: false);
        _cycler.GetType().GetField("fadeSeconds").SetValue(_cycler, 0f);
        Tick(0f);
        AssertHsb((0f, 1f, 1f), 0);
    }

    [Test]
    public void StoppedSim_IsSkipped_ThenJumpsWhenStarted()
    {
        SimField("runState").SetValue(_sim, Enum.Parse(T("SimRunState"), "Stopped"));
        Select(0, instant: false);
        Tick(2f);
        AssertHsb(Authored0, 0);                  // not running: untouched
        SimField("runState").SetValue(_sim, Enum.Parse(T("SimRunState"), "Running"));
        SimulateReset();
        Call("Reimpose", _sim);                   // StartSim → ConfigureAndReset → Reimpose
        AssertHsb((0f, 1f, 1f), 0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private IList Palettes => (IList)_cycler.GetType().GetField("palettes").GetValue(_cycler);
    private int ActiveIndex => (int)_cycler.GetType().GetProperty("ActiveIndex").GetValue(_cycler);
    private string ActiveName => (string)_cycler.GetType().GetProperty("ActiveName").GetValue(_cycler);
    private ScriptableObject Live => (ScriptableObject)SimField("agentParams").GetValue(_sim);
    private System.Reflection.FieldInfo SimField(string name) => _sim.GetType().GetField(name);

    private object Call(string method, params object[] args) => _cycler.GetType().GetMethod(method).Invoke(_cycler, args);
    private void Select(int index, bool instant) => Call("Select", index, _sims, instant);
    private void Tick(float dt) => Call("Tick", dt);

    // A reset swaps in a fresh clone of the preset.
    private void SimulateReset()
    {
        var fresh = MakeParams();
        SimField("agentParams").SetValue(_sim, fresh);
    }

    private ScriptableObject MakeParams()
    {
        var p = Make(T("PhysarumParams"));        // 2 types by default
        SetHsb(p, 0, Authored0);
        SetHsb(p, 1, Authored1);
        return p;
    }

    private ScriptableObject Palette(params Color[] colors)
    {
        var palette = Make(T("AgentColorPalette"));
        var swatches = new List<PaletteSwatch>();
        foreach (var c in colors) swatches.Add(new PaletteSwatch(c, AgentFamily.Any));
        palette.GetType().GetField("swatches").SetValue(palette, swatches);
        return palette;
    }

    private ScriptableObject Make(Type type)
    {
        var o = ScriptableObject.CreateInstance(type);
        _made.Add(o);
        return o;
    }

    private static void SetHsb(ScriptableObject p, int i, (float h, float s, float b) c)
    {
        var set = p.GetType().GetMethod("SetValue");
        set.Invoke(p, new object[] { "hue", i, c.h });
        set.Invoke(p, new object[] { "saturation", i, c.s });
        set.Invoke(p, new object[] { "brightness", i, c.b });
    }

    private void AssertHsb((float h, float s, float b) expected, int type)
    {
        var get = Live.GetType().GetMethod("GetValue");
        float Get(string name) => (float)get.Invoke(Live, new object[] { name, type });
        float dh = Mathf.Abs(Get("hue") - expected.h) % 1f;
        Assert.That(Mathf.Min(dh, 1f - dh), Is.LessThan(Eps), $"type {type} hue {Get("hue")}");
        Assert.That(Get("saturation"), Is.EqualTo(expected.s).Within(Eps), $"type {type} saturation");
        Assert.That(Get("brightness"), Is.EqualTo(expected.b).Within(Eps), $"type {type} brightness");
    }
}

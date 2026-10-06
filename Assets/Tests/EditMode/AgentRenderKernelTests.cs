using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Biomes;

/// <summary>
/// Dispatches each agent sim's RenderKernel on a hand-made trail field and compares the output
/// with AgentColor.ToRgb. Pins at once: the C# port matches the GPU, each kernel reads brightness
/// from the last float of its type-params struct, and the rest of the color path is unchanged.
/// The mirror tests pin the sims' C# upload structs to the same layout (reflection: the test
/// assembly cannot reference Assembly-CSharp).
/// </summary>
public class AgentRenderKernelTests
{
    private const string Dir = "Assets/Workspace/11.0 Biomes/src/computes/";

    // Float offsets inside each kernel's type-params struct (computes/includes/*_type_params.hlsl).
    private struct Layout
    {
        public string file, sim, gpuStruct; public int floats, hue, sat, brightness; public bool termite;
    }

    private static readonly Layout Physarum = new Layout { file = "PhysarumSim.compute", sim = "Biomes.PhysarumSim", gpuStruct = "PhysarumTypeParamsGPU", floats = 12, hue = 7,  sat = 8,  brightness = 11 };
    private static readonly Layout Boid     = new Layout { file = "BoidSim.compute",     sim = "Biomes.BoidSim",     gpuStruct = "BoidTypeParamsGPU",     floats = 16, hue = 11, sat = 12, brightness = 15 };
    private static readonly Layout Termite  = new Layout { file = "TermiteSim.compute",  sim = "Biomes.TermiteSim",  gpuStruct = "TermiteTypeParamsGPU",  floats = 13, hue = 10, sat = 11, brightness = 12, termite = true };

    // One texel per case; values per type. 1.5 exercises the >1 paths (termite → white).
    private static readonly float[] Type0 = { 1f, 0.5f, 0f, 1.5f };
    private static readonly float[] Type1 = { 0f, 0.5f, 1f, 0.25f };
    private static readonly (float h, float s, float b)[] Hsb = { (0.05f, 0.9f, 0.5f), (0.6f, 0.4f, 0.95f) };

    [Test] public void PhysarumRender_UsesTypeBrightness() => AssertRender(Physarum);
    [Test] public void BoidRender_UsesTypeBrightness()     => AssertRender(Boid);
    [Test] public void TermiteRender_UsesTypeBrightness()  => AssertRender(Termite);

    [Test] public void PhysarumUploadStruct_MatchesKernel() => AssertMirror(Physarum);
    [Test] public void BoidUploadStruct_MatchesKernel()     => AssertMirror(Boid);
    [Test] public void TermiteUploadStruct_MatchesKernel()  => AssertMirror(Termite);

    private static void AssertMirror(Layout k)
    {
        var type = System.Type.GetType(k.sim + ", Assembly-CSharp")?.GetNestedType(k.gpuStruct, BindingFlags.NonPublic);
        Assert.That(type, Is.Not.Null, k.sim + "." + k.gpuStruct);
        Assert.That(Marshal.SizeOf(type), Is.EqualTo(k.floats * 4), "size");
        Assert.That((int)Marshal.OffsetOf(type, "hue"), Is.EqualTo(k.hue * 4), "hue");
        Assert.That((int)Marshal.OffsetOf(type, "saturation"), Is.EqualTo(k.sat * 4), "saturation");
        Assert.That((int)Marshal.OffsetOf(type, "brightness"), Is.EqualTo(k.brightness * 4), "brightness");
    }

    private static void AssertRender(Layout k)
    {
        if (!SystemInfo.supportsComputeShaders) Assert.Ignore("No compute support in this Editor (-nographics?)");
        var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(Dir + k.file);
        Assert.That(cs, Is.Not.Null, k.file);
        int kernel = cs.FindKernel("RenderKernel");
        const int types = 2, w = 4, h = 1;

        var trail = new Texture2DArray(w, h, types + 1, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point };
        trail.SetPixelData(Type0, 0, 0);
        trail.SetPixelData(Type1, 0, 1);
        trail.SetPixelData(new float[w * h], 0, 2);
        trail.Apply(false);

        var outTex = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true };
        outTex.Create();
        var prev = RenderTexture.active;
        RenderTexture.active = outTex;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;

        var data = new float[types * k.floats];
        for (int t = 0; t < types; t++)
        {
            data[t * k.floats + k.hue] = Hsb[t].h;
            data[t * k.floats + k.sat] = Hsb[t].s;
            data[t * k.floats + k.brightness] = Hsb[t].b;
        }
        var buffer = new ComputeBuffer(types, k.floats * sizeof(float));
        buffer.SetData(data);

        try
        {
            cs.SetTexture(kernel, "trailRead", trail);
            cs.SetTexture(kernel, "outTex", outTex);
            cs.SetBuffer(kernel, "typeParams", buffer);
            cs.SetInt("typeCount", types);
            cs.SetInt("rezX", w);
            cs.SetInt("rezY", h);
            cs.SetFloat("persistence", 1f);
            cs.Dispatch(kernel, 1, 1, 1);   // numthreads(8,8,1) covers 4×1

            var request = AsyncGPUReadback.Request(outTex);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "readback");
            var px = request.GetData<Color>();
            for (int x = 0; x < w; x++)
            {
                var e = Expected(k, Type0[x], Type1[x]);
                string at = $"{k.file} texel {x}";
                Assert.That(px[x].r, Is.EqualTo(e.r).Within(1e-4f), at + " r");
                Assert.That(px[x].g, Is.EqualTo(e.g).Within(1e-4f), at + " g");
                Assert.That(px[x].b, Is.EqualTo(e.b).Within(1e-4f), at + " b");
                Assert.That(px[x].a, Is.EqualTo(e.a).Within(1e-4f), at + " a");
            }
        }
        finally
        {
            buffer.Release();
            outTex.Release();
            Object.DestroyImmediate(outTex);
            Object.DestroyImmediate(trail);
        }
    }

    // RenderKernel with outTex = 0 and persistence = 1: saturate(Σ color_t / typeCount).
    private static Color Expected(Layout k, float v0, float v1)
    {
        var sum = Color.clear;
        float[] v = { v0, v1 };
        for (int t = 0; t < 2; t++)
        {
            Color c;
            if (!k.termite)
            {
                c = AgentColor.ToRgb(Hsb[t].h, Hsb[t].s, Hsb[t].b * v[t]);
                c.a = v[t];
            }
            else
            {
                float baseB = Mathf.Clamp01(v[t]), white = Mathf.Clamp01(v[t] - 1f);
                c = AgentColor.ToRgb(Hsb[t].h, Hsb[t].s * (1f - white), Hsb[t].b * baseB);
                c = new Color(Mathf.Lerp(c.r, 1f, white), Mathf.Lerp(c.g, 1f, white), Mathf.Lerp(c.b, 1f, white), baseB);
            }
            sum += c / 2f;
        }
        return new Color(Mathf.Clamp01(sum.r), Mathf.Clamp01(sum.g), Mathf.Clamp01(sum.b), Mathf.Clamp01(sum.a));
    }
}

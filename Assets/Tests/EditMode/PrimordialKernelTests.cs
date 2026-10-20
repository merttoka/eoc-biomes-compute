using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Biomes;

/// <summary>
/// Runs PrimordialSim.compute's spatial hash + MoveAgentsKernel on a random soup and compares every
/// particle with PrimordialMotion.Step (the CPU reference pinned to the paper by PrimordialMotionTests):
/// the hash finds exactly the brute-force neighbours, the kernel turns before it moves, and the seam
/// is a short hop. The perception texture is neutral, so no biome term moves anything.
/// </summary>
public class PrimordialKernelTests
{
    private const string Path = "Assets/Workspace/11.0 Biomes/src/computes/PrimordialSim.compute";
    private const int Floats = 18;   // primordial_type_params.hlsl
    private const int AgentStride = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct Agent { public Vector2 position, direction; public uint typeId; }

    [Test]
    public void MoveKernel_MatchesCpuReference_OnARandomSoup()
    {
        if (!SystemInfo.supportsComputeShaders) Assert.Ignore("No compute support in this Editor (-nographics?)");
        var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(Path);
        Assert.That(cs, Is.Not.Null, Path);

        const int w = 64, h = 48, n = 400;
        var world = new Vector2(w, h);
        var species = new[]
        {
            new PrimordialMotion.Species { alpha = 180f * Mathf.Deg2Rad, beta = 17f * Mathf.Deg2Rad, radius = 6f, speed = 0.8f, otherTypeWeight = 0.5f },
            new PrimordialMotion.Species { alpha = -40f * Mathf.Deg2Rad, beta = 9f * Mathf.Deg2Rad, radius = 4f, speed = 1.3f, otherTypeWeight = 1f },
        };

        var rng = new System.Random(1234);
        var pos = new Vector2[n]; var head = new Vector2[n]; var type = new int[n];
        var agents = new Agent[n];
        for (int i = 0; i < n; i++)
        {
            pos[i] = new Vector2((float)rng.NextDouble() * w, (float)rng.NextDouble() * h);
            float a = (float)(rng.NextDouble() * 2 * System.Math.PI);
            head[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            type[i] = i * species.Length / n;
            agents[i] = new Agent { position = pos[i], direction = head[i], typeId = (uint)type[i] };
        }

        var cpuPos = new Vector2[n]; var cpuHead = new Vector2[n]; var cpuN = new float[n];
        PrimordialMotion.Step(pos, head, type, species, world, cpuPos, cpuHead, cpuN);

        var p = new float[species.Length * Floats];
        for (int t = 0; t < species.Length; t++)
        {
            var s = species[t];
            p[t * Floats + 0] = s.alpha;
            p[t * Floats + 1] = s.beta;
            p[t * Floats + 2] = s.radius * s.radius;
            p[t * Floats + 3] = s.speed;
            p[t * Floats + 5] = s.otherTypeWeight;
            p[t * Floats + 13] = 1f;   // firingSpeedMul
        }

        // Grid as PrimordialSim.BindHashGrid builds it: cells >= the largest radius, tiling exactly.
        int gw = Mathf.FloorToInt(w / 6f), gh = Mathf.FloorToInt(h / 6f);

        var agentsIn = new ComputeBuffer(n, AgentStride); agentsIn.SetData(agents);
        var agentsOut = new ComputeBuffer(n, AgentStride);
        var sorted = new ComputeBuffer(n, AgentStride);
        var sortedIdx = new ComputeBuffer(n, sizeof(uint));
        var density = new ComputeBuffer(n, sizeof(float));
        var counts = new ComputeBuffer(gw * gh, sizeof(uint));
        var offsets = new ComputeBuffer(gw * gh, sizeof(uint));
        var typeParams = new ComputeBuffer(species.Length, Floats * sizeof(float)); typeParams.SetData(p);
        var firing = new ComputeBuffer(1, sizeof(float)); firing.SetData(new[] { 0f });
        var perception = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBHalf);
        perception.Create();
        var prev = RenderTexture.active;
        RenderTexture.active = perception;
        GL.Clear(false, true, new Color(0.5f, 1f, 0f, 0f));   // neutral: no drift, full speed, no boost
        RenderTexture.active = prev;

        try
        {
            cs.SetInt("rezX", w); cs.SetInt("rezY", h);
            cs.SetInt("time", 0); cs.SetInt("substep", 0);
            cs.SetInt("agentsCount", n); cs.SetInt("typeCount", species.Length);
            cs.SetInt("gridW", gw); cs.SetInt("gridH", gh);
            cs.SetVector("cellSize", new Vector2((float)w / gw, (float)h / gh));
            cs.SetInt("neuronFiringCount", 0); cs.SetInt("neuronCount", 0); cs.SetFloat("firingThreshold", 0.1f);
            cs.SetInt("dispersalSpeedMode", 0); cs.SetFloat("dispersalSpeedMult", 0f);

            int clear = cs.FindKernel("ClearCellCountsKernel"), hash = cs.FindKernel("HashAndCountKernel"),
                scan = cs.FindKernel("PrefixSumKernel"), scatter = cs.FindKernel("ScatterKernel"),
                reorder = cs.FindKernel("ReorderAgentsKernel"), move = cs.FindKernel("MoveAgentsKernel");

            cs.SetBuffer(clear, "cellCounts", counts);
            cs.Dispatch(clear, 1, 1, 1);
            cs.SetBuffer(hash, "agentsIn", agentsIn); cs.SetBuffer(hash, "cellCounts", counts);
            cs.Dispatch(hash, 1, 1, 1);
            cs.SetBuffer(scan, "cellCounts", counts); cs.SetBuffer(scan, "cellOffsets", offsets);
            cs.Dispatch(scan, 1, 1, 1);
            cs.SetBuffer(scatter, "agentsIn", agentsIn); cs.SetBuffer(scatter, "cellCounts", counts);
            cs.SetBuffer(scatter, "cellOffsets", offsets); cs.SetBuffer(scatter, "sortedIndices", sortedIdx);
            cs.Dispatch(scatter, 1, 1, 1);
            cs.SetBuffer(reorder, "agentsIn", agentsIn); cs.SetBuffer(reorder, "sortedIndicesRead", sortedIdx);
            cs.SetBuffer(reorder, "agentsSorted", sorted);
            cs.Dispatch(reorder, 1, 1, 1);

            cs.SetBuffer(move, "agentsOut", agentsOut); cs.SetBuffer(move, "agentDensity", density);
            cs.SetBuffer(move, "sortedIndicesRead", sortedIdx); cs.SetBuffer(move, "cellOffsetsRead", offsets);
            cs.SetBuffer(move, "agentsSortedRead", sorted); cs.SetBuffer(move, "typeParams", typeParams);
            cs.SetBuffer(move, "neuronFiring", firing); cs.SetTexture(move, "perceptionTex", perception);
            cs.Dispatch(move, Mathf.CeilToInt(n / 256f), 1, 1);

            var gpu = new Agent[n]; agentsOut.GetData(gpu);
            var gpuN = new float[n]; density.GetData(gpuN);

            int neighboured = 0;
            for (int i = 0; i < n; i++)
            {
                string at = $"particle {i}";
                Assert.That(gpuN[i], Is.EqualTo(cpuN[i]).Within(1e-4f), at + " N");
                Assert.That(Vector2.Distance(gpu[i].position, cpuPos[i]), Is.LessThan(1e-3f), at + " position");
                Assert.That(Vector2.Distance(gpu[i].direction, cpuHead[i]), Is.LessThan(1e-4f), at + " heading");
                Assert.That(gpu[i].typeId, Is.EqualTo((uint)type[i]), at + " type");
                if (cpuN[i] > 0) neighboured++;
            }
            Assert.That(neighboured, Is.GreaterThan(n / 2), "the soup is dense enough to exercise the hash");
        }
        finally
        {
            agentsIn.Release(); agentsOut.Release(); sorted.Release(); sortedIdx.Release(); density.Release();
            counts.Release(); offsets.Release(); typeParams.Release(); firing.Release();
            perception.Release();
            Object.DestroyImmediate(perception);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Primordial particle system (Schmickl et al., Sci. Rep. 6:37969, 2016): Δφ = α + β·N·sign(R − L)
    /// at constant speed. At the paper's ⟨r = 5, α = 180°, β = 17°, v = 0.67⟩ and ~0.08 particles/su²
    /// a random soup grows spores, then cells that eat the free "nutrient" particles, divide and decay —
    /// creatures, coupled to the biome through the shared perception slots (see PrimordialSim.compute).
    /// Distances are in the paper's space units; <see cref="spaceUnitPx"/> maps them to pixels, and the
    /// agent count follows from <see cref="density"/> × the world's area in su.
    /// </summary>
    public class PrimordialSim : SimulationBase
    {
        public override string SimName => "Primordial";
        public override string OscPrefix => "pps";   // "p" is Physarum's

        private static readonly IReadOnlyList<string> s_ModulatableParams = new[]
            { "alpha", "beta", "radius", "speed", "noise", "otherTypeWeight",
              "fieldDrift", "agitationNoise", "depositAmount", "diffuseRate",
              "densityHueShift", "densityGlow", "hue", "saturation", "brightness" };
        public override IReadOnlyList<string> ModulatableParams => s_ModulatableParams;

        [Header("World (takes effect on Reset)")]
        [Tooltip("Pixels per space unit at the reference height (scaled by rezY/referenceHeight like every " +
                 "spatial param). The world is referenceHeight/spaceUnitPx su tall at any resolution: 6 → " +
                 "640×360 su at 16:9, cells ≈ 15–30 su. Smaller = more, smaller creatures (and more particles).")]
        [Range(2f, 32f)] public float spaceUnitPx = 6f;
        [Tooltip("Particles per su². The paper's soup is 0.08; cells need roughly 0.06–0.12 to emerge, " +
                 "denser runs crowd into tissue. Sets the agent count: density × world area in su.")]
        [Range(0.01f, 0.3f)] public float density = 0.08f;
        [Tooltip("PPS iterations per sim step. A paper cell divides in ~10³ iterations (~17 s at 60 Hz with 1); " +
                 "2–4 make the life cycle legible without speeding up the other sims. Each costs one hash + move.")]
        [Range(1, 8)] public int substeps = 2;

        [Header("Neuron seeding (Spawn Mode = NeuronPositions)")]
        [Tooltip("Share of particles born in a cloud around neuron i % neuronCount — one creature per neuron " +
                 "(~40 particles at the defaults). The rest scatter as the nutrient soup.")]
        [Range(0f, 1f)] public float neuronSeedFraction = 0.3f;
        [Tooltip("Gaussian σ of each neuron's birth cloud (su).")]
        [Range(0.5f, 20f)] public float seedRadius = 3f;

        [Header("Parameters (assign preset, runtime clone appears on Play)")]
        public PrimordialParams paramsSO;
        [Header("Runtime Parameters (live tweaking)")]
        public PrimordialParams agentParams;

        public override IParamSet LiveParamSet => agentParams;
        public override ScriptableObject PresetParamSet => paramsSO;

        protected override int TypeCount => agentParams != null ? agentParams.types.Count : 1;
        // Per-type trail, total, then the density-weighted bank (see PrimordialSim.compute).
        protected override int TrailLayerCount => 2 * TypeCount + 1;

        private ComputeBuffer readAgentsBuffer;
        private ComputeBuffer writeAgentsBuffer;
        private ComputeBuffer agentDensityBuffer;

        // Derived from density on Reset and cached: the manager dispatches biome write-back with
        // GetAgentCount() every step, so a live density edit must not change it before the buffers.
        private int _agentCount;

        // Spatial hash
        private int clearCellCountsKernel;
        private int hashAndCountKernel;
        private int prefixSumKernel;
        private int scatterKernel;
        private int reorderAgentsKernel;
        private ComputeBuffer cellCountsBuffer;
        private ComputeBuffer cellOffsetsBuffer;
        private ComputeBuffer sortedIndicesBuffer;
        private ComputeBuffer sortedAgentsBuffer;
        private int _gridCells;   // this step's gw·gh (BindHashGrid)

        private ComputeBuffer typeParamsBuffer;
        private PrimordialTypeParamsGPU[] _typeParamsCache;

        #region GPU struct
        [StructLayout(LayoutKind.Sequential)]
        struct PrimordialTypeParamsGPU
        {
            public float alpha, beta;              // rad
            public float radiusSq, speed;          // px², px/step
            public float noise;                    // rad σ
            public float otherTypeWeight;
            public float fieldDrift;               // px/step per unit field difference
            public float agitationNoise;           // rad σ per unit perception.a
            public float depositAmount, diffuseRate;
            public float densityFull, densityHueShift, densityGlow;
            public float firingSpeedMul, firingDepositAmount;
            public float hue, saturation;
            public float brightness;   // last, matching the HLSL struct
        }
        #endregion

        // Agent: float2 position + float2 heading + uint typeId = the 20 bytes every consumer
        // of GetAgentPositionBuffer (biome write-back, injector) reads.
        private const int AgentStride = sizeof(float) * 4 + sizeof(uint);

        private static readonly int s_GridWID = Shader.PropertyToID("gridW");
        private static readonly int s_GridHID = Shader.PropertyToID("gridH");
        private static readonly int s_CellSizeID = Shader.PropertyToID("cellSize");
        private static readonly int s_CellCountsID = Shader.PropertyToID("cellCounts");
        private static readonly int s_CellOffsetsID = Shader.PropertyToID("cellOffsets");
        private static readonly int s_SortedIndicesID = Shader.PropertyToID("sortedIndices");
        private static readonly int s_CellOffsetsReadID = Shader.PropertyToID("cellOffsetsRead");
        private static readonly int s_SortedIndicesReadID = Shader.PropertyToID("sortedIndicesRead");
        private static readonly int s_AgentsSortedID = Shader.PropertyToID("agentsSorted");
        private static readonly int s_AgentsSortedReadID = Shader.PropertyToID("agentsSortedRead");
        private static readonly int s_AgentDensityID = Shader.PropertyToID("agentDensity");
        private static readonly int s_AgentDensityReadID = Shader.PropertyToID("agentDensityRead");
        private static readonly int s_SubstepID = Shader.PropertyToID("substep");
        private static readonly int s_SubstepScaleID = Shader.PropertyToID("substepScale");
        private static readonly int s_NeuronSeedFractionID = Shader.PropertyToID("neuronSeedFraction");
        private static readonly int s_SeedRadiusPxID = Shader.PropertyToID("seedRadiusPx");

        public override ComputeBuffer GetAgentPositionBuffer() => readAgentsBuffer;
        public override int GetAgentCount() => _agentCount;

        /// <summary>Pixels per space unit at the current resolution.</summary>
        public float UnitPx => spaceUnitPx * (scaleSpatialToResolution ? ResolutionScale : 1f);

        // Hash cells never get narrower than this, so the buffers sized for it in InitBuffers hold
        // the grid for any live radius: a smaller radius is floored here, a larger one widens cells.
        private float MinCellPx => Mathf.Max(1f, rezY / 160f);

        private int DerivedAgentCount()
        {
            float u = Mathf.Max(0.01f, UnitPx);
            double count = density * (rezX / u) * (double)(rezY / u);
            return (int)Math.Min(4_000_000, Math.Max(64, Math.Round(count)));
        }

        public override void Reset()
        {
            agentParams = CloneParams(paramsSO);
            _agentCount = DerivedAgentCount();
            base.Reset();
        }

        protected override void InitSimKernels()
        {
            clearCellCountsKernel = cs.FindKernel("ClearCellCountsKernel");
            hashAndCountKernel = cs.FindKernel("HashAndCountKernel");
            prefixSumKernel = cs.FindKernel("PrefixSumKernel");
            scatterKernel = cs.FindKernel("ScatterKernel");
            reorderAgentsKernel = cs.FindKernel("ReorderAgentsKernel");
        }

        protected override void InitBuffers()
        {
            int n = _agentCount;
            readAgentsBuffer = gpu.CreateBuffer(n, AgentStride);
            writeAgentsBuffer = gpu.CreateBuffer(n, AgentStride);
            sortedAgentsBuffer = gpu.CreateBuffer(n, AgentStride);
            sortedIndicesBuffer = gpu.CreateBuffer(n, sizeof(uint));
            agentDensityBuffer = gpu.CreateBuffer(n, sizeof(float));

            int maxCells = Mathf.Max(1, Mathf.FloorToInt(rezX / MinCellPx)) * Mathf.Max(1, Mathf.FloorToInt(rezY / MinCellPx));
            cellCountsBuffer = gpu.CreateBuffer(maxCells, sizeof(uint));
            cellOffsetsBuffer = gpu.CreateBuffer(maxCells, sizeof(uint));
            typeParamsBuffer = gpu.CreateBuffer(8, Marshal.SizeOf<PrimordialTypeParamsGPU>());

            float u = UnitPx;
            Debug.Log($"[{SimName}] {n} particles · world {rezX / u:F0}×{rezY / u:F0} su at {u:F2} px/su");
        }

        protected override void GPUReset()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetInt(s_TimeID, WrappedStep);
            UploadTypeParams();
            ResetTrailArrays();

            // Neutral perception until the manager's first build (and for good with no umwelt):
            // an uninitialized G of 0 would freeze every particle.
            var prev = RenderTexture.active;
            RenderTexture.active = perceptionTex;
            GL.Clear(false, true, new Color(0.5f, 1f, 0f, 0f));
            RenderTexture.active = prev;

            cs.SetInt(s_AgentsCountID, _agentCount);
            BuildNeuronPositions(resetAgentsKernel);
            cs.SetFloat(s_NeuronSeedFractionID, neuronSeedFraction);
            cs.SetFloat(s_SeedRadiusPxID, seedRadius * UnitPx);
            cs.SetBuffer(resetAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            cs.SetBuffer(resetAgentsKernel, s_AgentDensityID, agentDensityBuffer);
            Dispatch(resetAgentsKernel, _agentCount, 1, 1);
            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);
        }

        private void UploadTypeParams()
        {
            int count = agentParams.types.Count;
            if (_typeParamsCache == null || _typeParamsCache.Length != count)
                _typeParamsCache = new PrimordialTypeParamsGPU[count];
            float u = UnitPx;
            for (int i = 0; i < count; i++)
            {
                var t = agentParams.types[i];
                float r = t.radius * u;
                _typeParamsCache[i] = new PrimordialTypeParamsGPU
                {
                    alpha = t.alpha * Mathf.Deg2Rad,
                    beta = t.beta * Mathf.Deg2Rad,
                    radiusSq = r * r,
                    speed = t.speed * u,
                    noise = t.noise * Mathf.Deg2Rad,
                    otherTypeWeight = t.otherTypeWeight,
                    fieldDrift = t.fieldDrift * u,
                    agitationNoise = t.agitationNoise * Mathf.Deg2Rad,
                    depositAmount = t.depositAmount,
                    diffuseRate = t.diffuseRate,
                    densityFull = t.densityFull,
                    densityHueShift = t.densityHueShift,
                    densityGlow = t.densityGlow,
                    firingSpeedMul = t.firingSpeedMul,
                    firingDepositAmount = t.firingDepositAmount,
                    hue = t.hue,
                    saturation = t.saturation,
                    brightness = t.brightness,
                };
            }
            typeParamsBuffer.SetData(_typeParamsCache);
            BindTypeParams(typeParamsBuffer, count);
        }

        // Fade ticks re-bind typeParams (diffuseRate drives the decay) like every live step does.
        protected override void BindFadeParams() => UploadTypeParams();

        protected override void GPUStep()
        {
            UploadTypeParams();
            BindPerceptionTex(moveAgentsKernel);
            BindNeuronFiring(moveAgentsKernel, writeTrailsKernel);
            BindTrailAnisotropy();
            BindDispersalSpeedParams();

            int n = AllocatedAgentCount;   // buffers are sized for this; a density edit takes effect on Reset
            cs.SetInt(s_AgentsCountID, n);

            // Diffuse once per sim step; every iteration then deposits its share into the result.
            cs.SetTexture(diffuseTextureKernel, s_TrailReadID, trailReadArray);
            cs.SetTexture(diffuseTextureKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(diffuseTextureKernel, rezX, rezY, 1);

            int iterations = Mathf.Clamp(substeps, 1, 8);
            cs.SetFloat(s_SubstepScaleID, 1f / iterations);
            BindHashGrid();
            for (int s = 0; s < iterations; s++)
            {
                cs.SetInt(s_SubstepID, s);
                GPUSpatialHashBuild(n);
                GPUMoveAgents(n);
                GPUWriteTrails(n);
                (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);
            }
        }

        // Cells at least one radius wide that tile the torus exactly (see BoidSim): a partial last
        // row/column at the wrap would put neighbours across the seam two cells apart, unseen.
        private void BindHashGrid()
        {
            float maxRadius = 0f;
            foreach (var t in agentParams.types)
                maxRadius = Mathf.Max(maxRadius, t.radius);
            float cell = Mathf.Max(maxRadius * UnitPx, MinCellPx);
            int gw = Mathf.Max(1, Mathf.FloorToInt(rezX / cell));
            int gh = Mathf.Max(1, Mathf.FloorToInt(rezY / cell));
            cs.SetVector(s_CellSizeID, new Vector2((float)rezX / gw, (float)rezY / gh));
            cs.SetInt(s_GridWID, gw);
            cs.SetInt(s_GridHID, gh);
            _gridCells = gw * gh;
        }

        private void GPUSpatialHashBuild(int n)
        {
            cs.SetBuffer(clearCellCountsKernel, s_CellCountsID, cellCountsBuffer);
            Dispatch(clearCellCountsKernel, _gridCells, 1, 1);

            cs.SetBuffer(hashAndCountKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(hashAndCountKernel, s_CellCountsID, cellCountsBuffer);
            Dispatch(hashAndCountKernel, n, 1, 1);

            cs.SetBuffer(prefixSumKernel, s_CellCountsID, cellCountsBuffer);
            cs.SetBuffer(prefixSumKernel, s_CellOffsetsID, cellOffsetsBuffer);
            Dispatch(prefixSumKernel, 1, 1, 1);

            cs.SetBuffer(scatterKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(scatterKernel, s_CellCountsID, cellCountsBuffer);
            cs.SetBuffer(scatterKernel, s_CellOffsetsID, cellOffsetsBuffer);
            cs.SetBuffer(scatterKernel, s_SortedIndicesID, sortedIndicesBuffer);
            Dispatch(scatterKernel, n, 1, 1);

            cs.SetBuffer(reorderAgentsKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(reorderAgentsKernel, s_SortedIndicesReadID, sortedIndicesBuffer);
            cs.SetBuffer(reorderAgentsKernel, s_AgentsSortedID, sortedAgentsBuffer);
            Dispatch(reorderAgentsKernel, n, 1, 1);
        }

        private void GPUMoveAgents(int n)
        {
            cs.SetBuffer(moveAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            cs.SetBuffer(moveAgentsKernel, s_AgentDensityID, agentDensityBuffer);
            cs.SetBuffer(moveAgentsKernel, s_SortedIndicesReadID, sortedIndicesBuffer);
            cs.SetBuffer(moveAgentsKernel, s_CellOffsetsReadID, cellOffsetsBuffer);
            cs.SetBuffer(moveAgentsKernel, s_AgentsSortedReadID, sortedAgentsBuffer);
            Dispatch(moveAgentsKernel, n, 1, 1);
        }

        private void GPUWriteTrails(int n)
        {
            cs.SetBuffer(writeTrailsKernel, s_AgentsOutID, writeAgentsBuffer);
            cs.SetBuffer(writeTrailsKernel, s_AgentDensityReadID, agentDensityBuffer);
            cs.SetTexture(writeTrailsKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(writeTrailsKernel, n, 1, 1);
        }

        #region Parameter Control
        // Every modulatable name is a PrimordialParams field, so the by-name IParamSet access
        // covers them all; values arrive normalized (0-1) and map through the preset's ranges.

        public override void SetParameter(string paramName, int index, float value)
        {
            if (agentParams == null || index < 0 || index >= agentParams.types.Count) return;
            var (mn, mx) = agentParams.GetRange(paramName);
            agentParams.SetValue(paramName, index, MapAndClamp(value, mn, mx));
        }

        public override void SetParameterDelta(string paramName, int index, float delta)
        {
            if (agentParams == null || index < 0 || index >= agentParams.types.Count) return;
            var (mn, mx) = agentParams.GetRange(paramName);
            agentParams.SetValue(paramName, index, ClampDelta(agentParams.GetValue(paramName, index), delta, mn, mx));
        }

        public override float GetParameter(string paramName, int index)
        {
            if (agentParams == null || index < 0 || index >= agentParams.types.Count) return 0f;
            return agentParams.GetValue(paramName, index);
        }
        #endregion

        [Button] public void RandomizeParams() => agentParams?.RandomizeParams();
        [Button] public void RandomizeColors() => agentParams?.RandomizeColors();
    }
}

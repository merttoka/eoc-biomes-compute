using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public class BoidSim : SimulationBase
    {
        public override string SimName => "Boid";

        private static readonly IReadOnlyList<string> s_ModulatableParams = new[]
            { "maxSpeed", "maxForce", "separateRange", "alignRange",
              "attractRange", "depositAmount", "eatAmount", "foodSeek",
              "hue", "saturation", "diffuseRate" };
        public override IReadOnlyList<string> ModulatableParams => s_ModulatableParams;

        [Header("Agents")]
        [Range(32, 150000)] public int agentsCount = 500;
        private ComputeBuffer readAgentsBuffer;
        private ComputeBuffer writeAgentsBuffer;

        [Header("Parameters (assign preset, runtime clone appears on Play)")]
        public BoidParams paramsSO;
        [Header("Runtime Parameters (live tweaking)")]
        public BoidParams agentParams;

        public override IParamSet LiveParamSet => agentParams;
        public override ScriptableObject PresetParamSet => paramsSO;

        protected override int TypeCount => agentParams != null ? agentParams.types.Count : 1;

        // Spatial hash
        private int clearCellCountsKernel;
        private int hashAndCountKernel;
        private int prefixSumKernel;
        private int scatterKernel;
        private int reorderAgentsKernel;
        private ComputeBuffer cellCountsBuffer;
        private ComputeBuffer cellOffsetsBuffer;
        private ComputeBuffer sortedBoidIndicesBuffer;
        private ComputeBuffer sortedAgentsBuffer;

        private ComputeBuffer typeParamsBuffer;
        private BoidTypeParamsGPU[] _typeParamsCache;

        #region GPU struct
        [StructLayout(LayoutKind.Sequential)]
        struct BoidTypeParamsGPU
        {
            public float separateRange;
            public float alignRange;
            public float attractRange;
            public float maxSpeed;
            public float maxForce;
            public float depositAmount;
            public float eatAmount;
            public float foodSensorDistance;
            public float sensorAngleRad;
            public float foodSeekingStrength;
            public float diffuseRate;
            public float hue;
            public float saturation;
            public float firingSpeedMul;
            public float firingDepositAmount;
        }
        #endregion

        private static readonly int s_GridWID = Shader.PropertyToID("gridW");
        private static readonly int s_GridHID = Shader.PropertyToID("gridH");
        private static readonly int s_CellSizeID = Shader.PropertyToID("cellSize");
        private static readonly int s_CellCountsID = Shader.PropertyToID("cellCounts");
        private static readonly int s_CellOffsetsID = Shader.PropertyToID("cellOffsets");
        private static readonly int s_SortedBoidIndicesID = Shader.PropertyToID("sortedBoidIndices");
        private static readonly int s_CellOffsetsReadID = Shader.PropertyToID("cellOffsetsRead");
        private static readonly int s_SortedIndicesReadID = Shader.PropertyToID("sortedIndicesRead");
        private static readonly int s_AgentsSortedID = Shader.PropertyToID("agentsSorted");
        private static readonly int s_AgentsSortedReadID = Shader.PropertyToID("agentsSortedRead");

        public override ComputeBuffer GetAgentPositionBuffer() => readAgentsBuffer;
        public override int GetAgentCount() => agentsCount;

        public override void Reset()
        {
            agentParams = paramsSO != null
                ? Instantiate(paramsSO)
                : ScriptableObject.CreateInstance<BoidParams>();
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
            readAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            writeAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));

            int maxCells = Mathf.CeilToInt((float)rezX / 32f) * Mathf.CeilToInt((float)rezY / 32f);
            cellCountsBuffer = gpu.CreateBuffer(maxCells, sizeof(uint));
            cellOffsetsBuffer = gpu.CreateBuffer(maxCells, sizeof(uint));
            sortedBoidIndicesBuffer = gpu.CreateBuffer(agentsCount, sizeof(uint));
            sortedAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            typeParamsBuffer = gpu.CreateBuffer(8, Marshal.SizeOf<BoidTypeParamsGPU>());
        }

        protected override void GPUReset()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetInt(s_TimeID, WrappedFrame);
            UploadTypeParams();
            ResetTrailArrays();

            cs.SetInt(s_AgentsCountID, agentsCount);
            BuildNeuronPositions(resetAgentsKernel);
            cs.SetBuffer(resetAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            Dispatch(resetAgentsKernel, agentsCount, 1, 1);
            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);
        }

        private void UploadTypeParams()
        {
            int count = agentParams.types.Count;
            if (_typeParamsCache == null || _typeParamsCache.Length != count)
                _typeParamsCache = new BoidTypeParamsGPU[count];
            for (int i = 0; i < count; i++)
            {
                var t = agentParams.types[i];
                _typeParamsCache[i] = new BoidTypeParamsGPU
                {
                    separateRange = t.separationRange * t.separationRange,
                    alignRange = t.alignmentRange * t.alignmentRange,
                    attractRange = t.attractionRange * t.attractionRange,
                    maxSpeed = t.maxSpeed,
                    maxForce = t.maxForce,
                    depositAmount = t.depositAmount,
                    eatAmount = t.eatAmount,
                    foodSensorDistance = t.foodSensorDistance,
                    sensorAngleRad = t.sensorAngleRad,
                    foodSeekingStrength = t.foodSeekingStrength,
                    diffuseRate = t.diffuseRate,
                    hue = t.hue,
                    saturation = t.saturation,
                    firingSpeedMul = t.firingSpeedMul,
                    firingDepositAmount = t.firingDepositAmount,
                };
            }
            typeParamsBuffer.SetData(_typeParamsCache);
            cs.SetInt(s_TypeCountID, count);

            int[] kernels = { moveAgentsKernel, writeTrailsKernel, diffuseTextureKernel, renderKernel };
            foreach (int k in kernels)
                cs.SetBuffer(k, s_TypeParamsID, typeParamsBuffer);
        }

        protected override void GPUStep()
        {
            UploadTypeParams();
            BindPerceptionTex(moveAgentsKernel);
            BindNeuronFiring(moveAgentsKernel, writeTrailsKernel);
            GPUSpatialHashBuild();
            GPUMoveAgentsKernel();
            GPUDiffuseTextureKernel();
            GPUWriteTrailsKernel();
            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);
        }

        private float MaxRangeAcrossTypes()
        {
            float max = 32f;
            foreach (var t in agentParams.types)
            {
                max = Mathf.Max(max, t.separationRange);
                max = Mathf.Max(max, t.alignmentRange);
                max = Mathf.Max(max, t.attractionRange);
            }
            return max;
        }

        private void GPUSpatialHashBuild()
        {
            float cs_cellSize = MaxRangeAcrossTypes();
            int gw = Mathf.CeilToInt((float)rezX / cs_cellSize);
            int gh = Mathf.CeilToInt((float)rezY / cs_cellSize);

            cs.SetFloat(s_CellSizeID, cs_cellSize);
            cs.SetInt(s_GridWID, gw);
            cs.SetInt(s_GridHID, gh);
            cs.SetInt(s_AgentsCountID, agentsCount);

            cs.SetBuffer(clearCellCountsKernel, s_CellCountsID, cellCountsBuffer);
            Dispatch(clearCellCountsKernel, gw * gh, 1, 1);

            cs.SetBuffer(hashAndCountKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(hashAndCountKernel, s_CellCountsID, cellCountsBuffer);
            Dispatch(hashAndCountKernel, agentsCount, 1, 1);

            cs.SetBuffer(prefixSumKernel, s_CellCountsID, cellCountsBuffer);
            cs.SetBuffer(prefixSumKernel, s_CellOffsetsID, cellOffsetsBuffer);
            Dispatch(prefixSumKernel, 1, 1, 1);

            cs.SetBuffer(scatterKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(scatterKernel, s_CellCountsID, cellCountsBuffer);
            cs.SetBuffer(scatterKernel, s_CellOffsetsID, cellOffsetsBuffer);
            cs.SetBuffer(scatterKernel, s_SortedBoidIndicesID, sortedBoidIndicesBuffer);
            Dispatch(scatterKernel, agentsCount, 1, 1);

            // Copy agents into cell order so the Move neighbour loop reads contiguously.
            cs.SetBuffer(reorderAgentsKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(reorderAgentsKernel, s_SortedIndicesReadID, sortedBoidIndicesBuffer);
            cs.SetBuffer(reorderAgentsKernel, s_AgentsSortedID, sortedAgentsBuffer);
            Dispatch(reorderAgentsKernel, agentsCount, 1, 1);
        }

        private void GPUMoveAgentsKernel()
        {
            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetBuffer(moveAgentsKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(moveAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            cs.SetBuffer(moveAgentsKernel, s_CellOffsetsReadID, cellOffsetsBuffer);
            cs.SetBuffer(moveAgentsKernel, s_SortedIndicesReadID, sortedBoidIndicesBuffer);
            cs.SetBuffer(moveAgentsKernel, s_AgentsSortedReadID, sortedAgentsBuffer);
            cs.SetTexture(moveAgentsKernel, s_TrailReadID, trailReadArray);
            Dispatch(moveAgentsKernel, agentsCount, 1, 1);
        }

        private void GPUWriteTrailsKernel()
        {
            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetBuffer(writeTrailsKernel, s_AgentsOutID, writeAgentsBuffer);
            cs.SetTexture(writeTrailsKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(writeTrailsKernel, agentsCount, 1, 1);
        }

        private void GPUDiffuseTextureKernel()
        {
            cs.SetTexture(diffuseTextureKernel, s_TrailReadID, trailReadArray);
            cs.SetTexture(diffuseTextureKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(diffuseTextureKernel, rezX, rezY, 1);
        }

        protected override void Render()
        {
            cs.SetTexture(renderKernel, s_TrailReadID, trailReadArray);
            cs.SetTexture(renderKernel, s_OutTexID, outTex);
            Dispatch(renderKernel, rezX, rezY, 1);
            if (outputMat != null)
                outputMat.SetTexture("_UnlitColorMap", outTex);
        }

        #region Parameter Control

        private float R(string p, float v) { var (mn, mx) = agentParams.GetRange(p); return MapAndClamp(v, mn, mx); }
        private float D(string p, float f, float d) { var (mn, mx) = agentParams.GetRange(p); return ClampDelta(f, d, mn, mx); }

        public override void SetParameter(string paramName, int index, float value)
        {
            if (index < 0 || index >= agentParams.types.Count) return;
            var t = agentParams.types[index];
            switch (paramName)
            {
                case "separateRange": t.separationRange  = R(paramName, value); break;
                case "alignRange":    t.alignmentRange   = R(paramName, value); break;
                case "attractRange":  t.attractionRange  = R(paramName, value); break;
                case "maxSpeed":      t.maxSpeed         = R(paramName, value); break;
                case "maxForce":      t.maxForce         = R(paramName, value); break;
                case "depositAmount": t.depositAmount    = R(paramName, value); break;
                case "eatAmount":     t.eatAmount        = R(paramName, value); break;
                case "foodSeek":      t.foodSeekingStrength = R(paramName, value); break;
                case "hue":           t.hue              = R(paramName, value); break;
                case "saturation":    t.saturation       = R(paramName, value); break;
                case "diffuseRate":   t.diffuseRate      = R(paramName, value); break;
            }
        }

        public override void SetParameterDelta(string paramName, int index, float delta)
        {
            if (index < 0 || index >= agentParams.types.Count) return;
            var t = agentParams.types[index];
            switch (paramName)
            {
                case "separateRange": t.separationRange  = D(paramName, t.separationRange, delta); break;
                case "alignRange":    t.alignmentRange   = D(paramName, t.alignmentRange, delta); break;
                case "attractRange":  t.attractionRange  = D(paramName, t.attractionRange, delta); break;
                case "maxSpeed":      t.maxSpeed         = D(paramName, t.maxSpeed, delta); break;
                case "maxForce":      t.maxForce         = D(paramName, t.maxForce, delta); break;
                case "depositAmount": t.depositAmount    = D(paramName, t.depositAmount, delta); break;
                case "eatAmount":     t.eatAmount        = D(paramName, t.eatAmount, delta); break;
                case "foodSeek":      t.foodSeekingStrength = D(paramName, t.foodSeekingStrength, delta); break;
                case "hue":           t.hue              = D(paramName, t.hue, delta); break;
                case "saturation":    t.saturation       = D(paramName, t.saturation, delta); break;
                case "diffuseRate":   t.diffuseRate      = D(paramName, t.diffuseRate, delta); break;
            }
        }

        public override float GetParameter(string paramName, int index)
        {
            if (index < 0 || index >= agentParams.types.Count) return 0f;
            var t = agentParams.types[index];
            return paramName switch
            {
                "separateRange" => t.separationRange,
                "alignRange"    => t.alignmentRange,
                "attractRange"  => t.attractionRange,
                "maxSpeed"      => t.maxSpeed,
                "maxForce"      => t.maxForce,
                "depositAmount" => t.depositAmount,
                "eatAmount"     => t.eatAmount,
                "foodSeek"      => t.foodSeekingStrength,
                "hue"           => t.hue,
                "saturation"    => t.saturation,
                "diffuseRate"   => t.diffuseRate,
                _ => 0f,
            };
        }
        #endregion

        [Button] public void RandomizeParams() => agentParams?.RandomizeParams();
        [Button] public void RandomizeColors() => agentParams?.RandomizeColors();
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public class TermiteSim : SimulationBase
    {
        public override string SimName => "Termite";

        private static readonly IReadOnlyList<string> s_ModulatableParams = new[]
            { "moveSpeed", "senseAngle", "turnAngle", "senseDistance",
              "depositAmount", "diffuseRate", "hue", "saturation" };
        public override IReadOnlyList<string> ModulatableParams => s_ModulatableParams;

        [Header("Agents")]
        [Range(1024, 4000000)] public int agentsCount = 131 * 100; // 13100
        private ComputeBuffer readAgentsBuffer;
        private ComputeBuffer writeAgentsBuffer;

        [Header("Parameters (assign preset, runtime clone appears on Play)")]
        public TermiteParams paramsSO;
        [Header("Runtime Parameters (live tweaking)")]
        public TermiteParams agentParams;
        public override IParamSet LiveParamSet => agentParams;
        public override ScriptableObject PresetParamSet => paramsSO;

        protected override int TypeCount => agentParams != null ? agentParams.types.Count : 1;

        private ComputeBuffer typeParamsBuffer;
        private TermiteTypeParamsGPU[] _typeParamsCache;

        #region GPU struct
        [StructLayout(LayoutKind.Sequential)]
        struct TermiteTypeParamsGPU
        {
            public float senseAngle, senseDistance, turnAngle, moveSpeed;
            public float firingSpeedMul;
            public float depositAmount, firingDepositAmount;
            public float depositProbability, firingDepositProbability;
            public float diffuseRate, hue, saturation;
        }
        #endregion

        public override ComputeBuffer GetAgentPositionBuffer() => readAgentsBuffer;
        public override int GetAgentCount() => agentsCount;

        public override void Reset()
        {
            agentParams = paramsSO != null
                ? Instantiate(paramsSO)
                : ScriptableObject.CreateInstance<TermiteParams>();
            base.Reset();
        }

        protected override void InitBuffers()
        {
            // Agent: float2 position + float2 direction + uint typeId = 20 bytes
            readAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            writeAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            typeParamsBuffer = gpu.CreateBuffer(8, Marshal.SizeOf<TermiteTypeParamsGPU>());
        }

        protected override void GPUReset()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetInt(s_TimeID, WrappedFrame);
            UploadTypeParams();
            ResetTrailArrays();

            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetBuffer(resetAgentsKernel, s_AgentsOutID, writeAgentsBuffer);

            // Init positions from CSV (inherited) or random scatter
            BuildNeuronPositions(resetAgentsKernel);

            Dispatch(resetAgentsKernel, agentsCount, 1, 1);
            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);
        }

        private void UploadTypeParams()
        {
            int count = agentParams.types.Count;
            if (_typeParamsCache == null || _typeParamsCache.Length != count)
                _typeParamsCache = new TermiteTypeParamsGPU[count];
            for (int i = 0; i < count; i++)
            {
                var t = agentParams.types[i];
                _typeParamsCache[i] = new TermiteTypeParamsGPU
                {
                    senseAngle = t.senseAngle * Mathf.Deg2Rad,
                    senseDistance = t.senseDistance,
                    turnAngle = t.turnAngle * Mathf.Deg2Rad,
                    moveSpeed = t.moveSpeed,
                    firingSpeedMul = t.firingSpeedMul,
                    depositAmount = t.depositAmount,
                    firingDepositAmount = t.firingDepositAmount,
                    depositProbability = t.depositProbability,
                    firingDepositProbability = t.firingDepositProbability,
                    diffuseRate = t.diffuseRate,
                    hue = t.hue,
                    saturation = t.saturation,
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

            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetTexture(moveAgentsKernel, s_TrailReadID, trailReadArray);
            cs.SetBuffer(moveAgentsKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(moveAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            Dispatch(moveAgentsKernel, agentsCount, 1, 1);

            cs.SetTexture(diffuseTextureKernel, s_TrailReadID, trailReadArray);
            cs.SetTexture(diffuseTextureKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(diffuseTextureKernel, rezX, rezY, 1);

            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetBuffer(writeTrailsKernel, s_AgentsOutID, writeAgentsBuffer);
            cs.SetTexture(writeTrailsKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(writeTrailsKernel, agentsCount, 1, 1);

            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);
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
                case "moveSpeed":     t.moveSpeed     = R(paramName, value); break;
                case "senseAngle":    t.senseAngle    = R(paramName, value); break;
                case "turnAngle":     t.turnAngle     = R(paramName, value); break;
                case "senseDistance": t.senseDistance = R(paramName, value); break;
                case "depositAmount": t.depositAmount = R(paramName, value); break;
                case "diffuseRate":   t.diffuseRate   = R(paramName, value); break;
                case "hue":           t.hue           = R(paramName, value); break;
                case "saturation":    t.saturation    = R(paramName, value); break;
            }
        }

        public override void SetParameterDelta(string paramName, int index, float delta)
        {
            if (index < 0 || index >= agentParams.types.Count) return;
            var t = agentParams.types[index];
            switch (paramName)
            {
                case "moveSpeed":     t.moveSpeed     = D(paramName, t.moveSpeed, delta); break;
                case "senseAngle":    t.senseAngle    = D(paramName, t.senseAngle, delta); break;
                case "turnAngle":     t.turnAngle     = D(paramName, t.turnAngle, delta); break;
                case "senseDistance": t.senseDistance = D(paramName, t.senseDistance, delta); break;
                case "depositAmount": t.depositAmount = D(paramName, t.depositAmount, delta); break;
                case "diffuseRate":   t.diffuseRate   = D(paramName, t.diffuseRate, delta); break;
                case "hue":           t.hue           = D(paramName, t.hue, delta); break;
                case "saturation":    t.saturation    = D(paramName, t.saturation, delta); break;
            }
        }

        public override float GetParameter(string paramName, int index)
        {
            if (index < 0 || index >= agentParams.types.Count) return 0f;
            var t = agentParams.types[index];
            return paramName switch
            {
                "moveSpeed"     => t.moveSpeed,
                "senseAngle"    => t.senseAngle,
                "turnAngle"     => t.turnAngle,
                "senseDistance" => t.senseDistance,
                "depositAmount" => t.depositAmount,
                "diffuseRate"   => t.diffuseRate,
                "hue"           => t.hue,
                "saturation"    => t.saturation,
                _ => 0f,
            };
        }
        #endregion

        [Button] public void RandomizeParams() => agentParams?.RandomizeParams();
        [Button] public void RandomizeColors() => agentParams?.RandomizeColors();

    }
}

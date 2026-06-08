using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public class PhysarumSim : SimulationBase
    {
        public override string SimName => "Physarum";

        private static readonly IReadOnlyList<string> s_ModulatableParams = new[]
            { "moveSpeed", "senseAngle", "turnAngle", "senseDistance",
              "depositAmount", "eatAmount", "diffuseRate",
              "hue", "saturation" };
        public override IReadOnlyList<string> ModulatableParams => s_ModulatableParams;

        [Header("Agents")]
        [Range(1024, 40000000)] public int agentsCount = 100000;
        private ComputeBuffer readAgentsBuffer;
        private ComputeBuffer writeAgentsBuffer;

        [Header("Parameters (assign preset, runtime clone appears on Play)")]
        public PhysarumParams paramsSO;
        [Header("Runtime Parameters (live tweaking)")]
        public PhysarumParams agentParams;

        public override IParamSet LiveParamSet => agentParams;
        public override ScriptableObject PresetParamSet => paramsSO;

        [Header("Initial Positions Neurons CSV")]
        public TextAsset labelsPositionsCsv;
        public bool csvCoordinatesAreNormalized = false;
        [Tooltip("How much of the canvas neurons fill (0-1). x=width, y=height. (1,1)=full canvas")]
        public Vector2 neuronScale = new Vector2(0.8f, 0.9f);
        private ComputeBuffer neuronPositionsBuffer;
        private ComputeBuffer dummyNeuronBuffer;

        private static readonly int s_NeuronPositionsID = Shader.PropertyToID("neuronPositions");
        private static readonly int s_NeuronCountID = Shader.PropertyToID("neuronCount");
        private static readonly int s_NeuronScaleID = Shader.PropertyToID("neuronScale");

        protected override int TypeCount => agentParams != null ? agentParams.types.Count : 1;

        private ComputeBuffer typeParamsBuffer;
        private PhysarumTypeParamsGPU[] _typeParamsCache;

        #region GPU struct
        [StructLayout(LayoutKind.Sequential)]
        struct PhysarumTypeParamsGPU
        {
            public float senseAngle, senseDistance, turnAngle, moveSpeed;
            public float depositAmount, eatAmount;
            public float diffuseRate, hue, saturation;
        }
        #endregion

        public override ComputeBuffer GetAgentPositionBuffer() => readAgentsBuffer;
        public override int GetAgentCount() => agentsCount;

        public override void Reset()
        {
            agentParams = paramsSO != null
                ? Instantiate(paramsSO)
                : ScriptableObject.CreateInstance<PhysarumParams>();
            base.Reset();
        }

        protected override void InitBuffers()
        {
            // Agent: float2 position + float2 direction + uint typeId = 20 bytes
            readAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            writeAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            typeParamsBuffer = gpu.CreateBuffer(8, Marshal.SizeOf<PhysarumTypeParamsGPU>());

            dummyNeuronBuffer = gpu.CreateBuffer(1, sizeof(float) * 2);
            var zero = new Vector2[1] { Vector2.zero };
            dummyNeuronBuffer.SetData(zero);
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

            // Parse neuron positions CSV and bind to reset kernel
            int neuronCount = 0;
            if (labelsPositionsCsv != null && !string.IsNullOrEmpty(labelsPositionsCsv.text))
            {
                var positions = ParseCsvFloat2(labelsPositionsCsv.text);
                if (csvCoordinatesAreNormalized || LooksNormalized01(positions))
                {
                    for (int i = 0; i < positions.Count; i++)
                    {
                        var p = positions[i];
                        p.x *= rezX;
                        p.y *= rezY;
                        positions[i] = p;
                    }
                }
                neuronCount = positions.Count;
                if (neuronCount > 0)
                {
                    neuronPositionsBuffer = gpu.CreateBuffer(neuronCount, sizeof(float) * 2);
                    neuronPositionsBuffer.SetData(positions);
                    cs.SetBuffer(resetAgentsKernel, s_NeuronPositionsID, neuronPositionsBuffer);
                }
                else
                {
                    cs.SetBuffer(resetAgentsKernel, s_NeuronPositionsID, dummyNeuronBuffer);
                }
            }
            else
            {
                cs.SetBuffer(resetAgentsKernel, s_NeuronPositionsID, dummyNeuronBuffer);
            }
            cs.SetInt(s_NeuronCountID, neuronCount);
            cs.SetVector(s_NeuronScaleID, new Vector4(neuronScale.x, neuronScale.y, 0, 0));

            Dispatch(resetAgentsKernel, agentsCount, 1, 1);
            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);
        }

        private void UploadTypeParams()
        {
            int count = agentParams.types.Count;
            if (_typeParamsCache == null || _typeParamsCache.Length != count)
                _typeParamsCache = new PhysarumTypeParamsGPU[count];
            for (int i = 0; i < count; i++)
            {
                var t = agentParams.types[i];
                _typeParamsCache[i] = new PhysarumTypeParamsGPU
                {
                    senseAngle = t.senseAngle * Mathf.Deg2Rad,
                    senseDistance = t.senseDistance,
                    turnAngle = t.turnAngle * Mathf.Deg2Rad,
                    moveSpeed = t.moveSpeed,
                    depositAmount = t.depositAmount,
                    eatAmount = t.eatAmount,
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

            // Move
            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetTexture(moveAgentsKernel, s_TrailReadID, trailReadArray);
            cs.SetBuffer(moveAgentsKernel, s_AgentsInID, readAgentsBuffer);
            cs.SetBuffer(moveAgentsKernel, s_AgentsOutID, writeAgentsBuffer);
            Dispatch(moveAgentsKernel, agentsCount, 1, 1);

            // Diffuse
            cs.SetTexture(diffuseTextureKernel, s_TrailReadID, trailReadArray);
            cs.SetTexture(diffuseTextureKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(diffuseTextureKernel, rezX, rezY, 1);

            // Write trails
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
                case "senseDistance":  t.senseDistance  = R(paramName, value); break;
                case "turnAngle":     t.turnAngle     = R(paramName, value); break;
                case "depositAmount": t.depositAmount = R(paramName, value); break;
                case "eatAmount":     t.eatAmount     = R(paramName, value); break;
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
                case "senseDistance":  t.senseDistance  = D(paramName, t.senseDistance, delta); break;
                case "turnAngle":     t.turnAngle     = D(paramName, t.turnAngle, delta); break;
                case "depositAmount": t.depositAmount = D(paramName, t.depositAmount, delta); break;
                case "eatAmount":     t.eatAmount     = D(paramName, t.eatAmount, delta); break;
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
                "senseDistance"  => t.senseDistance,
                "turnAngle"     => t.turnAngle,
                "depositAmount" => t.depositAmount,
                "eatAmount"     => t.eatAmount,
                "diffuseRate"   => t.diffuseRate,
                "hue"           => t.hue,
                "saturation"    => t.saturation,
                _ => 0f,
            };
        }
        #endregion

        [Button] public void RandomizeParams() => agentParams?.RandomizeParams();
        [Button] public void RandomizeColors() => agentParams?.RandomizeColors();

        #region CSV Parsing
        private static List<Vector2> ParseCsvFloat2(string csv)
        {
            var list = new List<Vector2>();
            var lines = csv.Split('\n');
            var inv = CultureInfo.InvariantCulture;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (float.TryParse(parts[1], NumberStyles.Float, inv, out float x) &&
                    float.TryParse(parts[2], NumberStyles.Float, inv, out float y))
                    list.Add(new Vector2(x, (1 - y)));
            }
            return list;
        }

        private static bool LooksNormalized01(List<Vector2> points)
        {
            if (points == null || points.Count == 0) return false;
            float maxX = float.MinValue, maxY = float.MinValue;
            float minX = float.MaxValue, minY = float.MaxValue;
            int sampleCount = Mathf.Min(points.Count, 2048);
            for (int i = 0; i < sampleCount; i++)
            {
                var p = points[i];
                if (float.IsNaN(p.x) || float.IsNaN(p.y)) continue;
                maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
                minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y);
            }
            return (minX >= -0.01f && maxX <= 1.01f && minY >= -0.01f && maxY <= 1.01f);
        }
        #endregion
    }
}

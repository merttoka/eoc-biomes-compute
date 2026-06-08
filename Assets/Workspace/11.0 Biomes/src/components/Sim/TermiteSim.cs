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

        [Header("Initial Positions Neurons CSV")]
        public TextAsset labelsPositionsCsv;
        public bool csvCoordinatesAreNormalized = false;
        [Tooltip("How much of the canvas agents fill (0-1). (1,1)=full canvas")]
        public Vector2 spawnScale = new Vector2(0.8f, 0.9f);
        private ComputeBuffer neuronPositionsBuffer;
        private ComputeBuffer dummyNeuronBuffer;

        [Header("Firing (optional, float16 blob in StreamingAssets)")]
        public bool enableFiring = false;
        [Tooltip("Path under Assets/StreamingAssets, produced by tools/firing_csv_to_f16.py")]
        public string firingBlobFile = "biomes11/termite_firing.f16";
        [Range(0f, 1f)] public float firingThreshold = 0.1f;
        public bool loopFiring = true;
        private ushort[] _firingHalf;               // flat float16 bits: frame*_neuronZCount + neuron
        private int _frameCount;
        private int _neuronZCount;
        private int _currentFrame;
        private float[] _frameScratch;              // decoded current-frame z values
        private uint[] _firingScratch;              // agentsCount, uploaded per step
        private ComputeBuffer firingBuffer;
        private ComputeBuffer dummyFiringBuffer;

        private static readonly int s_NeuronPositionsID = Shader.PropertyToID("neuronPositions");
        private static readonly int s_NeuronCountID = Shader.PropertyToID("neuronCount");
        private static readonly int s_NeuronScaleID = Shader.PropertyToID("neuronScale");
        private static readonly int s_FiringID = Shader.PropertyToID("firing");
        private static readonly int s_FiringEnabledID = Shader.PropertyToID("firingEnabled");

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
            LoadFiringBlob();
            base.Reset();
        }

        protected override void InitBuffers()
        {
            // Agent: float2 position + float2 direction + uint typeId = 20 bytes
            readAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            writeAgentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4 + sizeof(uint));
            typeParamsBuffer = gpu.CreateBuffer(8, Marshal.SizeOf<TermiteTypeParamsGPU>());

            dummyNeuronBuffer = gpu.CreateBuffer(1, sizeof(float) * 2);
            dummyNeuronBuffer.SetData(new Vector2[1] { Vector2.zero });

            dummyFiringBuffer = gpu.CreateBuffer(1, sizeof(uint));
            dummyFiringBuffer.SetData(new uint[1] { 0u });

            bool firingActive = enableFiring && _firingHalf != null && _frameCount > 0;
            if (firingActive)
            {
                firingBuffer = gpu.CreateBuffer(agentsCount, sizeof(uint));
                _firingScratch = new uint[agentsCount];
            }
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

            // Init positions from CSV (like Physarum) or random scatter
            int neuronCount = 0;
            if (labelsPositionsCsv != null && !string.IsNullOrEmpty(labelsPositionsCsv.text))
            {
                var positions = ParseCsvFloat2(labelsPositionsCsv.text);
                if (csvCoordinatesAreNormalized || LooksNormalized01(positions))
                {
                    for (int i = 0; i < positions.Count; i++)
                    {
                        var p = positions[i];
                        p.x *= rezX; p.y *= rezY;
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
                else cs.SetBuffer(resetAgentsKernel, s_NeuronPositionsID, dummyNeuronBuffer);
            }
            else cs.SetBuffer(resetAgentsKernel, s_NeuronPositionsID, dummyNeuronBuffer);

            cs.SetInt(s_NeuronCountID, neuronCount);
            cs.SetVector(s_NeuronScaleID, new Vector4(spawnScale.x, spawnScale.y, 0, 0));

            Dispatch(resetAgentsKernel, agentsCount, 1, 1);
            (readAgentsBuffer, writeAgentsBuffer) = (writeAgentsBuffer, readAgentsBuffer);

            _currentFrame = 0;
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

        private void UploadFiring()
        {
            bool firingActive = enableFiring && firingBuffer != null
                                && _firingHalf != null && _frameCount > 0 && _neuronZCount > 0;

            if (!firingActive)
            {
                cs.SetInt(s_FiringEnabledID, 0);
                cs.SetBuffer(moveAgentsKernel, s_FiringID, dummyFiringBuffer);
                cs.SetBuffer(writeTrailsKernel, s_FiringID, dummyFiringBuffer);
                return;
            }

            // Decode the current frame's float16 z-values, then threshold per agent.
            int baseIdx = _currentFrame * _neuronZCount;
            for (int n = 0; n < _neuronZCount; n++)
                _frameScratch[n] = Mathf.HalfToFloat(_firingHalf[baseIdx + n]);
            for (int i = 0; i < agentsCount; i++)
                _firingScratch[i] = _frameScratch[i % _neuronZCount] >= firingThreshold ? 1u : 0u;
            firingBuffer.SetData(_firingScratch);

            cs.SetInt(s_FiringEnabledID, 1);
            cs.SetBuffer(moveAgentsKernel, s_FiringID, firingBuffer);
            cs.SetBuffer(writeTrailsKernel, s_FiringID, firingBuffer);

            _currentFrame++;
            if (_currentFrame >= _frameCount)
                _currentFrame = loopFiring ? 0 : _frameCount - 1;
        }

        protected override void GPUStep()
        {
            UploadTypeParams();
            BindPerceptionTex(moveAgentsKernel);
            UploadFiring();

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

        #region CSV / blob parsing
        // Loads the float16 firing blob written by tools/firing_csv_to_f16.py.
        // Layout: "TFR1" magic, uint32 neuronCount, uint32 frameCount, then
        // frameCount*neuronCount float16 (row-major frame→neuron). ~47 MB, read once.
        private void LoadFiringBlob()
        {
            _firingHalf = null; _frameCount = 0; _neuronZCount = 0; _frameScratch = null;
            if (!enableFiring || string.IsNullOrEmpty(firingBlobFile)) return;

            string path = System.IO.Path.Combine(Application.streamingAssetsPath, firingBlobFile);
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"TermiteSim: firing blob not found at {path} (run tools/firing_csv_to_f16.py)");
                return;
            }

            using var br = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
            var magic = br.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != (byte)'T' || magic[1] != (byte)'F'
                || magic[2] != (byte)'R' || magic[3] != (byte)'1')
            {
                Debug.LogWarning("TermiteSim: firing blob has bad magic; ignoring");
                return;
            }
            _neuronZCount = (int)br.ReadUInt32();
            _frameCount   = (int)br.ReadUInt32();
            long count = (long)_frameCount * _neuronZCount;
            if (count <= 0 || count > int.MaxValue / 2)
            {
                Debug.LogWarning($"TermiteSim: firing blob size out of range ({_frameCount}x{_neuronZCount})");
                _frameCount = 0; _neuronZCount = 0;
                return;
            }
            var bytes = br.ReadBytes((int)(count * 2));
            _firingHalf = new ushort[count];
            System.Buffer.BlockCopy(bytes, 0, _firingHalf, 0, bytes.Length);
            _frameScratch = new float[_neuronZCount];
        }

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

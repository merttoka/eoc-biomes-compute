using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using EasyButtons;

namespace Metaesthetica
{
    public class PhysarumSim : SimulationBase
    {
        public override string SimName => "Physarum";

        [Header("Agents")]
        [Range(1024, 40000000)] public int agentsCount = 100000;
        private ComputeBuffer agentsBuffer;

        [Header("Parameters")]
        public PhysarumParams paramsSO;
        [HideInInspector] public PhysarumParams agentParams;

        [Header("Neuron Positions CSV")]
        public TextAsset labelsPositionsCsv;
        public bool csvCoordinatesAreNormalized = false;
        [Tooltip("How much of the canvas neurons fill (0-1). x=width, y=height. (1,1)=full canvas")]
        public Vector2 neuronScale = new Vector2(0.8f, 0.9f);
        private ComputeBuffer neuronPositionsBuffer;
        private ComputeBuffer dummyNeuronBuffer;
        private RenderTexture dummyInfluenceTex;

        // Kernels
        private int moveAgentsKernel;
        private int writeTrailsKernel;
        private int diffuseTextureKernel;
        private int renderKernel;

        #region Shader Property IDs
        private static readonly int s_AgentsCountID = Shader.PropertyToID("agentsCount");
        private static readonly int s_AgentsBufferID = Shader.PropertyToID("agentsBuffer");
        private static readonly int s_SenseDistancesID = Shader.PropertyToID("m_SenseDistances");
        private static readonly int s_SenseAnglesID = Shader.PropertyToID("m_SenseAngles");
        private static readonly int s_TurnAnglesID = Shader.PropertyToID("m_TurnAngles");
        private static readonly int s_MoveSpeedsID = Shader.PropertyToID("m_MoveSpeeds");
        private static readonly int s_EatAmountsID = Shader.PropertyToID("m_EatAmounts");
        private static readonly int s_DepositAmountsID = Shader.PropertyToID("m_DepositAmounts");
        private static readonly int s_ExternalInfluenceStrengthsID = Shader.PropertyToID("m_ExternalInfluenceStrengths");
        private static readonly int s_DiffuseRatesID = Shader.PropertyToID("m_DiffuseRates");
        private static readonly int s_HuesID = Shader.PropertyToID("m_Hues");
        private static readonly int s_SaturationsID = Shader.PropertyToID("m_Saturations");
        private static readonly int s_NeuronPositionsID = Shader.PropertyToID("neuronPositions");
        private static readonly int s_NeuronCountID = Shader.PropertyToID("neuronCount");
        private static readonly int s_NeuronScaleID = Shader.PropertyToID("neuronScale");
        #endregion

        public override void Reset()
        {
            // Clone SO at runtime so Editor asset stays clean
            if (paramsSO != null)
                agentParams = Instantiate(paramsSO);
            else
                agentParams = ScriptableObject.CreateInstance<PhysarumParams>();

            base.Reset();
        }

        protected override void InitKernels()
        {
            moveAgentsKernel = cs.FindKernel("MoveAgentsKernel");
            writeTrailsKernel = cs.FindKernel("WriteTrailsKernel");
            diffuseTextureKernel = cs.FindKernel("DiffuseTextureKernel");
            renderKernel = cs.FindKernel("RenderKernel");
        }

        protected override void InitTextures()
        {
            dummyInfluenceTex = gpu.CreateTexture2D(1, 1, FilterMode.Point, name: "physarum_dummy_influence");
            var activeRT = RenderTexture.active;
            RenderTexture.active = dummyInfluenceTex;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = activeRT;
        }

        protected override void InitBuffers()
        {
            agentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 4);

            dummyNeuronBuffer = gpu.CreateBuffer(1, sizeof(float) * 2);
            var zero = new Vector2[1] { Vector2.zero };
            dummyNeuronBuffer.SetData(zero);
        }

        protected override void GPUReset()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetInt(s_TimeID, Time.frameCount);

            int resetTexKernel = cs.FindKernel("ResetTextureKernel");
            ResetTexture(resetTexKernel);

            int resetAgentsKernel = cs.FindKernel("ResetAgentsKernel");
            cs.SetInt(s_AgentsCountID, agentsCount);
            cs.SetBuffer(resetAgentsKernel, s_AgentsBufferID, agentsBuffer);

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
        }

        protected override void GPUStep()
        {
            GPUMoveAgentsKernel();
            GPUDiffuseTextureKernel();
            GPUWriteTrailsKernel();
        }

        private void GPUMoveAgentsKernel()
        {
            cs.SetVector(s_SenseDistancesID, agentParams.m_SenseDistances);
            cs.SetVector(s_SenseAnglesID, new Vector4(
                agentParams.m_SenseAngles.x * Mathf.Deg2Rad,
                agentParams.m_SenseAngles.y * Mathf.Deg2Rad,
                agentParams.m_SenseAngles.z * Mathf.Deg2Rad,
                agentParams.m_SenseAngles.w * Mathf.Deg2Rad
            ));
            cs.SetVector(s_TurnAnglesID, new Vector4(
                agentParams.m_TurnAngles.x * Mathf.Deg2Rad,
                agentParams.m_TurnAngles.y * Mathf.Deg2Rad,
                agentParams.m_TurnAngles.z * Mathf.Deg2Rad,
                agentParams.m_TurnAngles.w * Mathf.Deg2Rad
            ));
            cs.SetVector(s_MoveSpeedsID, agentParams.m_MoveSpeeds);
            cs.SetInt(s_AgentsCountID, agentsCount);

            Texture textureToUseOnShader = null;
            RenderTexture tempBlittedTexture = null;

            bool validExternal = false;
            if (externalInfluenceTex != null)
            {
                if (externalInfluenceTex is RenderTexture inputRT)
                {
                    validExternal = inputRT.IsCreated() && inputRT.width > 0 && inputRT.height > 0;
                    if (validExternal)
                    {
                        if (inputRT.format == RenderTextureFormat.ARGBFloat)
                            textureToUseOnShader = inputRT;
                        else
                        {
                            tempBlittedTexture = RenderTexture.GetTemporary(inputRT.width, inputRT.height, 0, RenderTextureFormat.ARGBFloat);
                            Graphics.Blit(inputRT, tempBlittedTexture);
                            textureToUseOnShader = tempBlittedTexture;
                        }
                    }
                }
                else if (externalInfluenceTex is Texture2D inputTex2D)
                {
                    validExternal = inputTex2D.width > 0 && inputTex2D.height > 0;
                    if (validExternal)
                    {
                        tempBlittedTexture = RenderTexture.GetTemporary(inputTex2D.width, inputTex2D.height, 0, RenderTextureFormat.ARGBFloat);
                        Graphics.Blit(inputTex2D, tempBlittedTexture);
                        textureToUseOnShader = tempBlittedTexture;
                    }
                }
            }

            if (!validExternal || textureToUseOnShader == null)
            {
                textureToUseOnShader = dummyInfluenceTex;
                cs.SetVector(s_ExternalInfluenceStrengthsID, Vector4.zero);
            }
            else
            {
                cs.SetVector(s_ExternalInfluenceStrengthsID, agentParams.m_ExternalInfluenceStrengths);
            }

            cs.SetTexture(moveAgentsKernel, s_ExternalInfluenceTexID, textureToUseOnShader);
            cs.SetTexture(moveAgentsKernel, s_ReadTexID, readTex);
            cs.SetBuffer(moveAgentsKernel, s_AgentsBufferID, agentsBuffer);
            Dispatch(moveAgentsKernel, agentsCount, 1, 1);

            if (tempBlittedTexture != null) RenderTexture.ReleaseTemporary(tempBlittedTexture);
        }

        private void GPUWriteTrailsKernel()
        {
            cs.SetVector(s_EatAmountsID, agentParams.m_EatAmounts);
            cs.SetVector(s_DepositAmountsID, agentParams.m_DepositAmounts);
            cs.SetInt(s_AgentsCountID, agentsCount);

            cs.SetBuffer(writeTrailsKernel, s_AgentsBufferID, agentsBuffer);
            cs.SetTexture(writeTrailsKernel, s_WriteTexID, writeTex);
            Dispatch(writeTrailsKernel, agentsCount, 1, 1);
        }

        private void GPUDiffuseTextureKernel()
        {
            cs.SetVector(s_DiffuseRatesID, agentParams.m_DiffuseRates);
            cs.SetTexture(diffuseTextureKernel, s_ReadTexID, readTex);
            cs.SetTexture(diffuseTextureKernel, s_WriteTexID, writeTex);
            Dispatch(diffuseTextureKernel, rezX, rezY, 1);
        }

        protected override void Render()
        {
            cs.SetVector(s_HuesID, agentParams.m_Hues);
            cs.SetVector(s_SaturationsID, agentParams.m_Saturations);
            cs.SetTexture(renderKernel, s_ReadTexID, readTex);
            cs.SetTexture(renderKernel, s_OutTexID, outTex);
            Dispatch(renderKernel, rezX, rezY, 1);

            if (outputMat != null)
                outputMat.SetTexture("_UnlitColorMap", outTex);
        }

        #region IControllableSim
        public override void SetParameter(string paramName, int index, float value)
        {
            switch (paramName)
            {
                case "moveSpeed": agentParams.m_MoveSpeeds[index] = MapAndClamp(value, 0.01f, 5.0f); break;
                case "senseAngle": agentParams.m_SenseAngles[index] = MapAndClamp(value, 0.1f, 360.0f); break;
                case "senseDistance": agentParams.m_SenseDistances[index] = MapAndClamp(value, 0.1f, 200.0f); break;
                case "turnAngle": agentParams.m_TurnAngles[index] = MapAndClamp(value, 0.1f, 360.0f); break;
                case "externalInfluenceStrength": agentParams.m_ExternalInfluenceStrengths[index] = MapAndClamp(value, -1.0f, 20.0f); break;
                case "depositAmount": agentParams.m_DepositAmounts[index] = MapAndClamp(value, 0.01f, 1f); break;
                case "eatAmount": agentParams.m_EatAmounts[index] = MapAndClamp(value, 0.01f, 2.0f); break;
                case "hue": agentParams.m_Hues[index] = MapAndClamp(value, 0f, 1f); break;
                case "saturation": agentParams.m_Saturations[index] = MapAndClamp(value, 0f, 1f); break;
                case "diffuseRate": agentParams.m_DiffuseRates[index] = MapAndClamp(value, 0f, 1f); break;
                default: Debug.LogWarning($"PhysarumSim: Unknown param '{paramName}'"); break;
            }
        }

        public override void SetParameterDelta(string paramName, int index, float delta)
        {
            switch (paramName)
            {
                case "moveSpeed": agentParams.m_MoveSpeeds[index] = ClampDelta(agentParams.m_MoveSpeeds[index], delta, 0.01f, 5.0f); break;
                case "senseAngle": agentParams.m_SenseAngles[index] = ClampDelta(agentParams.m_SenseAngles[index], delta, 0.1f, 360.0f); break;
                case "senseDistance": agentParams.m_SenseDistances[index] = ClampDelta(agentParams.m_SenseDistances[index], delta, 0.1f, 200.0f); break;
                case "turnAngle": agentParams.m_TurnAngles[index] = ClampDelta(agentParams.m_TurnAngles[index], delta, 0.1f, 360.0f); break;
                case "externalInfluenceStrength": agentParams.m_ExternalInfluenceStrengths[index] = ClampDelta(agentParams.m_ExternalInfluenceStrengths[index], delta, -1.0f, 20.0f); break;
                case "depositAmount": agentParams.m_DepositAmounts[index] = ClampDelta(agentParams.m_DepositAmounts[index], delta, 0.01f, 1f); break;
                case "eatAmount": agentParams.m_EatAmounts[index] = ClampDelta(agentParams.m_EatAmounts[index], delta, 0.01f, 2.0f); break;
                case "hue": agentParams.m_Hues[index] = ClampDelta(agentParams.m_Hues[index], delta, 0f, 1f); break;
                case "saturation": agentParams.m_Saturations[index] = ClampDelta(agentParams.m_Saturations[index], delta, 0f, 1f); break;
                case "diffuseRate": agentParams.m_DiffuseRates[index] = ClampDelta(agentParams.m_DiffuseRates[index], delta, 0f, 1f); break;
                default: Debug.LogWarning($"PhysarumSim: Unknown param '{paramName}'"); break;
            }
        }

        public override float GetParameter(string paramName, int index)
        {
            switch (paramName)
            {
                case "moveSpeed": return agentParams.m_MoveSpeeds[index];
                case "senseAngle": return agentParams.m_SenseAngles[index];
                case "senseDistance": return agentParams.m_SenseDistances[index];
                case "turnAngle": return agentParams.m_TurnAngles[index];
                case "externalInfluenceStrength": return agentParams.m_ExternalInfluenceStrengths[index];
                case "depositAmount": return agentParams.m_DepositAmounts[index];
                case "eatAmount": return agentParams.m_EatAmounts[index];
                case "hue": return agentParams.m_Hues[index];
                case "saturation": return agentParams.m_Saturations[index];
                case "diffuseRate": return agentParams.m_DiffuseRates[index];
                default: Debug.LogWarning($"PhysarumSim: Unknown param '{paramName}'"); return 0f;
            }
        }
        #endregion

        #region Control Buttons
        [Button]
        public void ResetParams()
        {
            if (agentParams != null) agentParams.ResetToDefaults();
        }

        [Button]
        public void RandomizeParams()
        {
            agentParams.m_SenseAngles = new Vector4(
                UnityEngine.Random.Range(0.1f, 360f), UnityEngine.Random.Range(0.1f, 360f),
                UnityEngine.Random.Range(0.1f, 360f), UnityEngine.Random.Range(0.1f, 360f));
            agentParams.m_SenseDistances = new Vector4(
                UnityEngine.Random.Range(0.1f, 200f), UnityEngine.Random.Range(0.1f, 200f),
                UnityEngine.Random.Range(0.1f, 200f), UnityEngine.Random.Range(0.1f, 200f));
            agentParams.m_TurnAngles = new Vector4(
                UnityEngine.Random.Range(0.1f, 360f), UnityEngine.Random.Range(0.1f, 360f),
                UnityEngine.Random.Range(0.1f, 360f), UnityEngine.Random.Range(0.1f, 360f));
            agentParams.m_MoveSpeeds = new Vector4(
                UnityEngine.Random.Range(0.1f, 2f), UnityEngine.Random.Range(0.1f, 2f),
                UnityEngine.Random.Range(0.1f, 2f), UnityEngine.Random.Range(0.1f, 2f));
            agentParams.m_EatAmounts = new Vector4(
                UnityEngine.Random.Range(0.01f, 0.2f), UnityEngine.Random.Range(0.01f, 0.2f),
                UnityEngine.Random.Range(0.01f, 0.2f), UnityEngine.Random.Range(0.01f, 0.2f));
        }

        [Button]
        public void RandomizeColors()
        {
            agentParams.m_Hues = new Vector4(
                UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(0f, 1f),
                UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(0f, 1f));
            agentParams.m_Saturations = new Vector4(
                UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(0f, 1f),
                UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(0f, 1f));
        }
        #endregion

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

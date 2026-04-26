using System;
using UnityEngine;
using EasyButtons;

namespace Metaesthetica
{
    public class BoidSim : SimulationBase
    {
        public override string SimName => "Boid";

        [Header("Agents")]
        [Range(32, 20000)] public int agentsCount = 500;
        private ComputeBuffer agentsBuffer;

        [Header("Parameters")]
        public BoidParams paramsSO;
        [HideInInspector] public BoidParams agentParams;

        // Kernels
        private int moveAgentsKernel;
        private int writeTrailsKernel;
        private int diffuseTextureKernel;
        private int renderKernel;

        #region Shader Property IDs
        private static readonly int s_AgentCountID = Shader.PropertyToID("agentsCount");
        private static readonly int s_AgentsBufferID = Shader.PropertyToID("agentsBuffer");
        private static readonly int s_SeparateRangeID = Shader.PropertyToID("separateRange");
        private static readonly int s_AlignRangeID = Shader.PropertyToID("alignRange");
        private static readonly int s_AttractRangeID = Shader.PropertyToID("attractRange");
        private static readonly int s_MaxSpeedID = Shader.PropertyToID("maxSpeed");
        private static readonly int s_MaxForceID = Shader.PropertyToID("maxForce");
        private static readonly int s_EatAmountID = Shader.PropertyToID("eatAmount");
        private static readonly int s_DepositAmountID = Shader.PropertyToID("depositAmount");
        private static readonly int s_DiffuseRateID = Shader.PropertyToID("diffuseRate");
        private static readonly int s_HueID = Shader.PropertyToID("hue");
        private static readonly int s_SaturationID = Shader.PropertyToID("saturation");
        private static readonly int s_FoodSensorDistanceID = Shader.PropertyToID("foodSensorDistance");
        private static readonly int s_SensorAngleRadID = Shader.PropertyToID("sensorAngleRad");
        private static readonly int s_FoodSeekingStrengthID = Shader.PropertyToID("foodSeekingStrength");
        private static readonly int s_UseExternalInfluenceID = Shader.PropertyToID("useExternalInfluence");
        private static readonly int s_ExternalInfluenceStrengthID = Shader.PropertyToID("externalInfluenceStrength");
        #endregion

        public override void Reset()
        {
            if (paramsSO != null)
                agentParams = Instantiate(paramsSO);
            else
                agentParams = ScriptableObject.CreateInstance<BoidParams>();

            base.Reset();
        }

        protected override void InitKernels()
        {
            moveAgentsKernel = cs.FindKernel("MoveAgentsKernel");
            writeTrailsKernel = cs.FindKernel("WriteTrailsKernel");
            diffuseTextureKernel = cs.FindKernel("DiffuseTextureKernel");
            renderKernel = cs.FindKernel("RenderKernel");
        }

        protected override void InitTextures() { }

        protected override void InitBuffers()
        {
            agentsBuffer = gpu.CreateBuffer(agentsCount, sizeof(float) * 9);
        }

        protected override void GPUReset()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetInt(s_TimeID, Time.frameCount);

            int resetTexKernel = cs.FindKernel("ResetTextureKernel");
            ResetTexture(resetTexKernel);

            int resetAgentsKernel = cs.FindKernel("ResetAgentsKernel");
            cs.SetInt(s_AgentCountID, agentsCount);
            cs.SetBuffer(resetAgentsKernel, s_AgentsBufferID, agentsBuffer);
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
            cs.SetFloat(s_SeparateRangeID, agentParams.seperationRange * agentParams.seperationRange);
            cs.SetFloat(s_AlignRangeID, agentParams.alignmentRange * agentParams.alignmentRange);
            cs.SetFloat(s_AttractRangeID, agentParams.attractionRange * agentParams.attractionRange);
            cs.SetFloat(s_MaxSpeedID, agentParams.maxspeed);
            cs.SetFloat(s_MaxForceID, agentParams.maxforce);
            cs.SetInt(s_AgentCountID, agentsCount);

            cs.SetFloat(s_FoodSensorDistanceID, agentParams.foodSensorDistance);
            cs.SetFloat(s_SensorAngleRadID, agentParams.sensorAngleRad);
            cs.SetFloat(s_FoodSeekingStrengthID, agentParams.foodSeekingStrength);

            bool useExternalInfluence = false;
            if (externalInfluenceTex != null)
            {
                Texture textureToUseOnShader = null;

                if (externalInfluenceTex is RenderTexture inputRT)
                {
                    if (inputRT.IsCreated() && inputRT.width > 0 && inputRT.height > 0)
                        textureToUseOnShader = inputRT;
                }
                else if (externalInfluenceTex is Texture2D inputTex2D)
                {
                    if (inputTex2D.width > 0 && inputTex2D.height > 0)
                        textureToUseOnShader = inputTex2D;
                }

                if (textureToUseOnShader != null)
                {
                    cs.SetTexture(moveAgentsKernel, s_ExternalInfluenceTexID, textureToUseOnShader);
                    useExternalInfluence = true;
                }
                else
                {
                    cs.SetTexture(moveAgentsKernel, s_ExternalInfluenceTexID, Texture2D.blackTexture);
                }
            }
            else
            {
                cs.SetTexture(moveAgentsKernel, s_ExternalInfluenceTexID, Texture2D.blackTexture);
            }
            cs.SetBool(s_UseExternalInfluenceID, useExternalInfluence);
            cs.SetFloat(s_ExternalInfluenceStrengthID, agentParams.externalInfluenceStrength);

            cs.SetBuffer(moveAgentsKernel, s_AgentsBufferID, agentsBuffer);
            cs.SetTexture(moveAgentsKernel, s_ReadTexID, readTex);
            Dispatch(moveAgentsKernel, agentsCount, 1, 1);
        }

        private void GPUWriteTrailsKernel()
        {
            cs.SetFloat(s_EatAmountID, agentParams.eatAmount);
            cs.SetFloat(s_DepositAmountID, agentParams.depositAmount);

            cs.SetBuffer(writeTrailsKernel, s_AgentsBufferID, agentsBuffer);
            cs.SetTexture(writeTrailsKernel, s_WriteTexID, writeTex);
            Dispatch(writeTrailsKernel, agentsCount, 1, 1);
        }

        private void GPUDiffuseTextureKernel()
        {
            cs.SetFloat(s_DiffuseRateID, agentParams.diffuseRate);
            cs.SetTexture(diffuseTextureKernel, s_ReadTexID, readTex);
            cs.SetTexture(diffuseTextureKernel, s_WriteTexID, writeTex);
            Dispatch(diffuseTextureKernel, rezX, rezY, 1);
        }

        protected override void Render()
        {
            cs.SetFloat(s_HueID, agentParams.hue);
            cs.SetFloat(s_SaturationID, agentParams.saturation);
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
                case "separationRange": agentParams.seperationRange = MapAndClamp(value, 1f, 500f); break;
                case "alignmentRange": agentParams.alignmentRange = MapAndClamp(value, 1f, 500f); break;
                case "attractionRange": agentParams.attractionRange = MapAndClamp(value, 1f, 500f); break;
                case "maxSpeed": agentParams.maxspeed = MapAndClamp(value, 0.1f, 5f); break;
                case "maxForce": agentParams.maxforce = MapAndClamp(value, 0.1f, 5f); break;
                case "depositAmount": agentParams.depositAmount = MapAndClamp(value, 0f, 1f); break;
                case "eatAmount": agentParams.eatAmount = MapAndClamp(value, 0f, 1f); break;
                case "externalInfluenceStrength": agentParams.externalInfluenceStrength = MapAndClamp(value, 0f, 5f); break;
                case "hue": agentParams.hue = MapAndClamp(value, 0f, 1f); break;
                case "saturation": agentParams.saturation = MapAndClamp(value, 0f, 1f); break;
                case "diffuseRate": agentParams.diffuseRate = MapAndClamp(value, 0f, 1f); break;
                default: Debug.LogWarning($"BoidSim: Unknown param '{paramName}'"); break;
            }
        }

        public override void SetParameterDelta(string paramName, int index, float delta)
        {
            switch (paramName)
            {
                case "separationRange": agentParams.seperationRange = ClampDelta(agentParams.seperationRange, delta, 1f, 500f); break;
                case "alignmentRange": agentParams.alignmentRange = ClampDelta(agentParams.alignmentRange, delta, 1f, 500f); break;
                case "attractionRange": agentParams.attractionRange = ClampDelta(agentParams.attractionRange, delta, 1f, 500f); break;
                case "maxSpeed": agentParams.maxspeed = ClampDelta(agentParams.maxspeed, delta, 0.1f, 5f); break;
                case "maxForce": agentParams.maxforce = ClampDelta(agentParams.maxforce, delta, 0.1f, 5f); break;
                case "depositAmount": agentParams.depositAmount = ClampDelta(agentParams.depositAmount, delta, 0f, 1f); break;
                case "eatAmount": agentParams.eatAmount = ClampDelta(agentParams.eatAmount, delta, 0f, 1f); break;
                case "externalInfluenceStrength": agentParams.externalInfluenceStrength = ClampDelta(agentParams.externalInfluenceStrength, delta, 0f, 5f); break;
                case "hue": agentParams.hue = ClampDelta(agentParams.hue, delta, 0f, 1f); break;
                case "saturation": agentParams.saturation = ClampDelta(agentParams.saturation, delta, 0f, 1f); break;
                case "diffuseRate": agentParams.diffuseRate = ClampDelta(agentParams.diffuseRate, delta, 0f, 1f); break;
                default: Debug.LogWarning($"BoidSim: Unknown param '{paramName}'"); break;
            }
        }

        public override float GetParameter(string paramName, int index)
        {
            switch (paramName)
            {
                case "separationRange": return agentParams.seperationRange;
                case "alignmentRange": return agentParams.alignmentRange;
                case "attractionRange": return agentParams.attractionRange;
                case "maxSpeed": return agentParams.maxspeed;
                case "maxForce": return agentParams.maxforce;
                case "depositAmount": return agentParams.depositAmount;
                case "eatAmount": return agentParams.eatAmount;
                case "externalInfluenceStrength": return agentParams.externalInfluenceStrength;
                case "hue": return agentParams.hue;
                case "saturation": return agentParams.saturation;
                case "diffuseRate": return agentParams.diffuseRate;
                default: Debug.LogWarning($"BoidSim: Unknown param '{paramName}'"); return 0f;
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
            agentParams.seperationRange = UnityEngine.Random.Range(0.1f, 500);
            agentParams.alignmentRange = UnityEngine.Random.Range(0.1f, 500);
            agentParams.attractionRange = UnityEngine.Random.Range(0.1f, 500);
            agentParams.maxspeed = UnityEngine.Random.Range(0.05f, 5f);
            agentParams.maxforce = UnityEngine.Random.Range(0.05f, 5f);
        }

        [Button]
        public void RandomizeColors()
        {
            agentParams.hue = UnityEngine.Random.Range(0f, 1f);
            agentParams.saturation = UnityEngine.Random.Range(0f, 1f);
        }
        #endregion
    }
}

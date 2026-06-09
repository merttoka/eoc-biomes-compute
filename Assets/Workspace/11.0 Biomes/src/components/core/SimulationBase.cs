using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using EasyButtons;

namespace Biomes
{
    public abstract class SimulationBase : MonoBehaviour
    {
        [Header("Setup")]
        public ComputeShader cs;
        public Material outputMat;

        [HideInInspector] public int rezX = 1024;
        [HideInInspector] public int rezY = 1024;

        [Header("Biome Integration")]
        public UmweltMapping umwelt;

        // Perception texture: biome fields filtered through Umwelt (built by Biome each frame)
        // R=chemotaxis, G=speed multiplier, B=avoidance
        [NonSerialized] public RenderTexture perceptionTex;

        // External influence texture (assigned by SimulationManager from ExternalTextureReceiver)
        [NonSerialized] public Texture externalInfluenceTex;

        // Shared neuron firing (assigned by SimulationManager from NeuronFiringSource)
        [NonSerialized] public ComputeBuffer neuronFiring;
        [NonSerialized] public int neuronFiringCount;
        [Header("Neuron Firing")]
        [Range(0f, 1f)] public float firingThreshold = 0.1f;
        private ComputeBuffer dummyNeuronFiringBuffer;

        // Trail texture array: layers 0..typeCount-1 = per-type, layer typeCount = total
        protected RenderTexture trailReadArray;
        protected RenderTexture trailWriteArray;
        protected RenderTexture outTex;

        protected GPUResourceManager gpu;

        // Common kernel handles
        protected int resetTexKernel;
        protected int resetAgentsKernel;
        protected int moveAgentsKernel;
        protected int writeTrailsKernel;
        protected int diffuseTextureKernel;
        protected int renderKernel;

        protected abstract int TypeCount { get; }

        #region Shader Property IDs
        protected static readonly int s_RezXID = Shader.PropertyToID("rezX");
        protected static readonly int s_RezYID = Shader.PropertyToID("rezY");
        protected static readonly int s_TimeID = Shader.PropertyToID("time");
        protected static readonly int s_TrailReadID = Shader.PropertyToID("trailRead");
        protected static readonly int s_TrailWriteID = Shader.PropertyToID("trailWrite");
        protected static readonly int s_OutTexID = Shader.PropertyToID("outTex");
        protected static readonly int s_AgentsCountID = Shader.PropertyToID("agentsCount");
        protected static readonly int s_AgentsInID = Shader.PropertyToID("agentsIn");
        protected static readonly int s_AgentsOutID = Shader.PropertyToID("agentsOut");
        protected static readonly int s_TypeParamsID = Shader.PropertyToID("typeParams");
        protected static readonly int s_TypeCountID = Shader.PropertyToID("typeCount");
        protected static readonly int s_PerceptionTexID = Shader.PropertyToID("perceptionTex");
        protected static readonly int s_NeuronFiringID = Shader.PropertyToID("neuronFiring");
        protected static readonly int s_NeuronFiringCountID = Shader.PropertyToID("neuronFiringCount");
        protected static readonly int s_FiringThresholdID = Shader.PropertyToID("firingThreshold");
        #endregion

        public abstract string SimName { get; }

        // IControllableSim interface
        public abstract IReadOnlyList<string> ModulatableParams { get; }
        public abstract void SetParameter(string paramName, int index, float value);
        public abstract void SetParameterDelta(string paramName, int index, float delta);
        public abstract float GetParameter(string paramName, int index);

        /// <summary>Live runtime params (agentParams) exposed for interpolation.</summary>
        public abstract IParamSet LiveParamSet { get; }

        /// <summary>The assigned preset asset (paramsSO) that LiveParamSet was cloned from.</summary>
        public abstract ScriptableObject PresetParamSet { get; }

        /// <summary>Editor-only: copy the current live params back into the assigned preset
        /// asset in place (overwrites it; no new snapshot file). Returns true if written.
        /// Caller batches AssetDatabase.SaveAssets() after looping sims.</summary>
        public bool SaveLiveParamsToPreset()
        {
#if UNITY_EDITOR
            var live = LiveParamSet as ScriptableObject;
            var preset = PresetParamSet;
            if (live == null || preset == null) return false;
            string presetName = preset.name;                        // CopySerialized would stamp "(Clone)";
            UnityEditor.EditorUtility.CopySerialized(live, preset);  // copy all tuned fields into the asset
            preset.name = presetName;                               // restore the asset's name
            UnityEditor.EditorUtility.SetDirty(preset);
            return true;
#else
            return false;
#endif
        }

        protected abstract void InitBuffers();
        protected abstract void GPUReset();
        protected abstract void GPUStep();
        protected abstract void Render();
        protected virtual void InitSimKernels() { }

        public RenderTexture GetOutputTexture() => outTex;

        /// <summary>Returns the agent position buffer for biome write-back.</summary>
        public abstract ComputeBuffer GetAgentPositionBuffer();
        public abstract int GetAgentCount();

        public void SetResolution(int x, int y)
        {
            rezX = x;
            rezY = y;
        }

        // Wrapped frame counter fed to shaders as `time`. Keeps (float)time small so RNG
        // seeds (e.g. time*0.001 + id*0.0001, sin(time)) keep per-agent precision over
        // long installation runs — raw Time.frameCount degrades them within hours.
        // Wraps every 65536 frames (~18 min @60fps); the one-frame discontinuity at wrap
        // is imperceptible.
        protected const int TimeWrap = 65536;
        protected int WrappedFrame => Time.frameCount % TimeWrap;

        [Button]
        public virtual void Reset()
        {
            Release();
            gpu = new GPUResourceManager();

            int layers = TypeCount + 1;
            trailReadArray = CreateTrailArray(layers, SimName + "_trailRead");
            trailWriteArray = CreateTrailArray(layers, SimName + "_trailWrite");
            outTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Trilinear, name: SimName + "_out");

            // Perception texture (populated by Biome)
            perceptionTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Bilinear,
                RenderTextureFormat.ARGBFloat, SimName + "_perception");

            resetTexKernel = cs.FindKernel("ResetTextureKernel");
            resetAgentsKernel = cs.FindKernel("ResetAgentsKernel");
            moveAgentsKernel = cs.FindKernel("MoveAgentsKernel");
            writeTrailsKernel = cs.FindKernel("WriteTrailsKernel");
            diffuseTextureKernel = cs.FindKernel("DiffuseTextureKernel");
            renderKernel = cs.FindKernel("RenderKernel");

            InitSimKernels();
            InitBuffers();
            GPUReset();
            Render();
        }

        public virtual void Step()
        {
            cs.SetInt(s_TimeID, WrappedFrame);
            GPUStep();
            SwapTrailArrays();
            Render();
        }

        protected RenderTexture CreateTrailArray(int layers, string name)
        {
            return gpu.CreateTextureArray(rezX, rezY, layers, FilterMode.Point,
                RenderTextureFormat.RHalf, name);
        }

        protected void Dispatch(int kernel, int x, int y, int z)
        {
            cs.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out uint wz);
            cs.Dispatch(kernel,
                Mathf.CeilToInt((float)x / wx),
                Mathf.CeilToInt((float)y / wy),
                Mathf.CeilToInt((float)z / wz));
        }

        protected void SwapTrailArrays()
        {
            (trailReadArray, trailWriteArray) = (trailWriteArray, trailReadArray);
        }

        protected void ResetTrailArrays()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);
            cs.SetTexture(resetTexKernel, s_TrailWriteID, trailWriteArray);
            Dispatch(resetTexKernel, rezX, rezY, 1);
            cs.SetTexture(resetTexKernel, s_TrailWriteID, trailReadArray);
            Dispatch(resetTexKernel, rezX, rezY, 1);
            var prev = RenderTexture.active;
            RenderTexture.active = outTex;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        protected void BindPerceptionTex(params int[] kernels)
        {
            Texture tex = perceptionTex;
            foreach (int k in kernels)
                cs.SetTexture(k, s_PerceptionTexID, tex);
        }

        // Bind the shared neuron-firing buffer + count + threshold to the given kernels.
        // Falls back to a 1-element dummy (count 0 => no firing) when no source is wired.
        protected void BindNeuronFiring(params int[] kernels)
        {
            ComputeBuffer buf = neuronFiring;
            int count = neuronFiringCount;
            if (buf == null)
            {
                if (dummyNeuronFiringBuffer == null)
                {
                    dummyNeuronFiringBuffer = gpu.CreateBuffer(1, sizeof(float));
                    dummyNeuronFiringBuffer.SetData(new float[1] { 0f });
                }
                buf = dummyNeuronFiringBuffer;
                count = 0;
            }
            foreach (int k in kernels)
                cs.SetBuffer(k, s_NeuronFiringID, buf);
            cs.SetInt(s_NeuronFiringCountID, count);
            cs.SetFloat(s_FiringThresholdID, firingThreshold);
        }

        public virtual void Release()
        {
            gpu?.ReleaseAll();
            gpu = null;
            trailReadArray = null;
            trailWriteArray = null;
            perceptionTex = null;
            dummyNeuronFiringBuffer = null;
        }

        void OnDisable() => Release();
        void OnDestroy() => Release();

        [Button("Export as PNG")]
        public void ExportPNG()
        {
            if (outTex == null) return;
            var tex = new Texture2D(rezX, rezY, TextureFormat.RGBA32, false);
            RenderTexture.active = outTex;
            tex.ReadPixels(new Rect(0, 0, rezX, rezY), 0, 0);
            tex.Apply();
            byte[] bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes($"Recordings/Sim{SimName}-{DateTime.Now.ToFileTime()}.png", bytes);
            Destroy(tex);
        }

        protected float MapAndClamp(float value, float minValue, float maxValue, float min = 0, float max = 1)
        {
            float mapped = (value - min) / (max - min) * (maxValue - minValue) + minValue;
            return Mathf.Clamp(mapped, minValue, maxValue);
        }

        protected float ClampDelta(float field, float delta, float minValue, float maxValue)
        {
            float value = field + delta * (maxValue - minValue);
            return Mathf.Clamp(value, minValue, maxValue);
        }
    }
}

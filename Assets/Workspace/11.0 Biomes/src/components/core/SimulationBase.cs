using System;
using System.Collections.Generic;
using System.Globalization;
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

        [Tooltip("Weight of this sim's output in the final additive composite (1 = full). Lower a dense sim (e.g. physarum) so it stops saturating the canvas and drowning the others.")]
        [Range(0f, 4f)] public float compositeWeight = 1f;

        [Tooltip("Per-frame retention of the rendered output (was hardcoded 0.9). Raising toward 0.95-0.98 makes trails linger and fill the canvas — the main lever to keep the dense look with fewer agents.")]
        [Range(0.5f, 0.995f)] public float renderPersistence = 0.9f;

        // Dispersal speed response — shared by all sims (consumes perception.a = SpeedBoost).
        public enum DispersalSpeedMode { Multiplier = 0, Constant = 1 }
        [Header("Dispersal speed response")]
        [Tooltip("Constant = snap toward a fixed flee speed (fast reaction even at low base speed). Multiplier = scale current speed up with local dispersal.")]
        public DispersalSpeedMode dispersalSpeedMode = DispersalSpeedMode.Constant;
        [Tooltip("Multiplier mode gain: speed *= 1 + dispersal*mult.")]
        [Range(0f, 20f)] public float dispersalSpeedMult = 4f;
        [Tooltip("Constant mode target flee speed (agents snap toward this as dispersal→1).")]
        [Range(0f, 50f)] public float dispersalConstantSpeed = 6f;

        // Scale of the perception texture relative to sim resolution (set by
        // SimulationManager before Reset). Perception is built from the low-res biome
        // field and sampled by UV, so it can be much smaller than the sim canvas.
        [NonSerialized] public float perceptionResScale = 1f;

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

        [Header("Neuron Positions (optional CSV seeding)")]
        public TextAsset labelsPositionsCsv;
        public bool csvCoordinatesAreNormalized = false;
        [Tooltip("How much of the canvas agents fill (0-1). (1,1)=full canvas")]
        public Vector2 spawnScale = new Vector2(0.8f, 0.9f);
        protected ComputeBuffer neuronPositionsBuffer;
        protected ComputeBuffer dummyNeuronBuffer;

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
        protected static readonly int s_NeuronPositionsID = Shader.PropertyToID("neuronPositions");
        protected static readonly int s_NeuronCountID = Shader.PropertyToID("neuronCount");
        protected static readonly int s_NeuronScaleID = Shader.PropertyToID("neuronScale");
        protected static readonly int s_PersistenceID = Shader.PropertyToID("persistence");
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
            // ARGBHalf (8 B/px) instead of ARGBFloat (16 B/px): output color is saturated
            // 0..1 so half precision is ample, and it halves bandwidth on the per-pixel
            // render/composite/Syphon path — the dominant memory traffic at 2×FHD.
            outTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Trilinear,
                RenderTextureFormat.ARGBHalf, SimName + "_out");

            // Perception texture (populated by Biome). RGB carry chemotaxis/speed/avoidance,
            // all in 0..1 — half precision is plenty and halves the per-frame read cost in
            // every sim's MoveAgents kernel (the hottest sampler in the project).
            // Built from the low-res biome field and read by UV everywhere, so it can be
            // smaller than the sim canvas (perceptionResScale, set by SimulationManager).
            int pw = Mathf.Max(8, Mathf.RoundToInt(rezX * Mathf.Clamp(perceptionResScale, 0.05f, 1f)));
            int ph = Mathf.Max(8, Mathf.RoundToInt(rezY * Mathf.Clamp(perceptionResScale, 0.05f, 1f)));
            perceptionTex = gpu.CreateTexture2D(pw, ph, FilterMode.Bilinear,
                RenderTextureFormat.ARGBHalf, SimName + "_perception");

            resetTexKernel = cs.FindKernel("ResetTextureKernel");
            resetAgentsKernel = cs.FindKernel("ResetAgentsKernel");
            moveAgentsKernel = cs.FindKernel("MoveAgentsKernel");
            writeTrailsKernel = cs.FindKernel("WriteTrailsKernel");
            diffuseTextureKernel = cs.FindKernel("DiffuseTextureKernel");
            renderKernel = cs.FindKernel("RenderKernel");

            InitSimKernels();
            InitBuffers();
            GPUReset();
            cs.SetFloat(s_PersistenceID, renderPersistence);
            Render();
        }

        public virtual void Step()
        {
            cs.SetInt(s_TimeID, WrappedFrame);
            cs.SetFloat(s_PersistenceID, renderPersistence);
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

        // Bind the shared dispersal speed-response params (consumed via includes/dispersal_speed_response.hlsl).
        protected void BindDispersalSpeedParams()
        {
            cs.SetInt("dispersalSpeedMode", (int)dispersalSpeedMode);
            cs.SetFloat("dispersalSpeedMult", dispersalSpeedMult);
            cs.SetFloat("dispersalConstantSpeed", dispersalConstantSpeed);
        }

        // Parse labelsPositionsCsv, upload neuron positions, bind to the given reset
        // kernel, and set neuronCount/neuronScale globals. Returns the neuron count
        // (0 => the reset kernel should random-scatter).
        protected int BuildNeuronPositions(int resetKernel)
        {
            if (dummyNeuronBuffer == null)
            {
                dummyNeuronBuffer = gpu.CreateBuffer(1, sizeof(float) * 2);
                dummyNeuronBuffer.SetData(new Vector2[1] { Vector2.zero });
            }

            int neuronCount = 0;
            if (labelsPositionsCsv != null && !string.IsNullOrEmpty(labelsPositionsCsv.text))
            {
                var positions = ParseCsvFloat2(labelsPositionsCsv.text);
                if (csvCoordinatesAreNormalized || LooksNormalized01(positions))
                    for (int i = 0; i < positions.Count; i++)
                        positions[i] = new Vector2(positions[i].x * rezX, positions[i].y * rezY);

                neuronCount = positions.Count;
                if (neuronCount > 0)
                {
                    neuronPositionsBuffer = gpu.CreateBuffer(neuronCount, sizeof(float) * 2);
                    neuronPositionsBuffer.SetData(positions);
                    cs.SetBuffer(resetKernel, s_NeuronPositionsID, neuronPositionsBuffer);
                }
                else cs.SetBuffer(resetKernel, s_NeuronPositionsID, dummyNeuronBuffer);
            }
            else cs.SetBuffer(resetKernel, s_NeuronPositionsID, dummyNeuronBuffer);

            cs.SetInt(s_NeuronCountID, neuronCount);
            cs.SetVector(s_NeuronScaleID, new Vector4(spawnScale.x, spawnScale.y, 0, 0));
            return neuronCount;
        }

        public static List<Vector2> ParseCsvFloat2(string csv)
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

        protected static bool LooksNormalized01(List<Vector2> points)
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

        public virtual void Release()
        {
            gpu?.ReleaseAll();
            gpu = null;
            trailReadArray = null;
            trailWriteArray = null;
            perceptionTex = null;
            dummyNeuronFiringBuffer = null;
            neuronPositionsBuffer = null;
            dummyNeuronBuffer = null;
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

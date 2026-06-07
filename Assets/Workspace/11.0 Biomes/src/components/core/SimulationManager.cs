using System;
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public class SimulationManager : MonoBehaviour
    {
        [Header("Resolution & Timing")]
        [Range(32, 4096)] public int rezX = 1024;
        [Range(32, 4096)] public int rezY = 1024;
        [Range(0, 10)] public int stepsPerFrame = 1;
        [Range(1, 50)] public int stepMod = 1;
        public bool limitFPS = true;
        [Range(24, 165)] public int targetFPS = 60;

        [Header("Biome")]
        public Biome biome;

        [Header("Simulations")]
        public List<SimulationBase> simulations = new();

        [Header("External Input")]
        [SerializeField] private ExternalTextureReceiver externalInput;

        [Header("Debug Overlay")]
        [SerializeField] private bool m_DebugOverlayVideoOnOutput = false;
        [SerializeField, Range(0f, 1f)] private float m_DebugOverlayStrength = 0.5f;

        [Header("Output")]
        public ComputeShader compositeCS;
        public Material compositeOutMat;
        public Transform compositeOutputQuad;
        public Camera recordingCamera;

        private RenderTexture compositeOutTex;
        private int compositeRenderKernel;
        private GPUResourceManager gpu;
        private RenderTexture _dummyBlackTex;

        private int _simStepCount;
        public int SimStepCount => _simStepCount;

        /// <summary>Final composited output texture (null until Reset()).</summary>
        public RenderTexture CompositeOutputTexture => compositeOutTex;

        private static readonly int s_RezXID = Shader.PropertyToID("rezX");
        private static readonly int s_RezYID = Shader.PropertyToID("rezY");
        private static readonly int s_CompositeOutTexID = Shader.PropertyToID("compositeOut");
        private static readonly int s_SimCountID = Shader.PropertyToID("simCount");
        private static readonly int s_ExternalOverlayTexID = Shader.PropertyToID("externalOverlay");
        private static readonly int s_OverlayStrengthID = Shader.PropertyToID("overlayStrength");

        void Awake()
        {
            if (limitFPS)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = targetFPS;
            }
        }

        [Button]
        public void Reset()
        {
            Release();
            gpu = new GPUResourceManager();
            _simStepCount = 0;

            if (compositeOutputQuad != null)
            {
                float aspect = (float)rezX / rezY;
                var s = compositeOutputQuad.localScale;
                compositeOutputQuad.localScale = new Vector3(s.y * aspect, s.y, s.z);
            }

            _dummyBlackTex = gpu.CreateTexture2D(1, 1, FilterMode.Point, name: "composite_dummy");
            var activeRT = RenderTexture.active;
            RenderTexture.active = _dummyBlackTex;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = activeRT;

            // Initialize external input
            if (externalInput != null)
                externalInput.Initialize();

            // Reset biome (persists unless explicitly cleared)
            if (biome != null)
                biome.Reset();

            // Reset sims
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.SetResolution(rezX, rezY);
                sim.Reset();
            }

            compositeOutTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Trilinear, name: "composite_out");
            if (compositeCS != null)
                compositeRenderKernel = compositeCS.FindKernel("CompositeRenderKernel");

            Render();
        }

        void Update()
        {
            if (Time.frameCount % stepMod == 0)
                for (int i = 0; i < stepsPerFrame; i++)
                    Step();
        }

        public void Step()
        {
            _simStepCount++;

            // 0. Update external input
            externalInput?.UpdateInput();

            // Assign influence texture to sims
            Texture influenceTex = externalInput != null ? externalInput.OutputTexture : null;
            foreach (var sim in simulations)
            {
                if (sim != null)
                    sim.externalInfluenceTex = influenceTex;
            }

            // 1. Build perception textures from biome for each sim
            if (biome != null)
            {
                foreach (var sim in simulations)
                {
                    if (sim == null || sim.umwelt == null) continue;
                    biome.BuildPerceptionTex(sim.perceptionTex, sim.umwelt, sim.rezX, sim.rezY);
                }
            }

            // 2. Step each sim
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.Step();
            }

            // 3. Sims write back to biome
            if (biome != null)
            {
                for (int i = 0; i < simulations.Count; i++)
                {
                    var sim = simulations[i];
                    if (sim == null || sim.umwelt == null) continue;

                    var posBuffer = sim.GetAgentPositionBuffer();
                    int agentCount = sim.GetAgentCount();
                    if (posBuffer == null) continue;

                    // Write each channel specified in Umwelt
                    foreach (var write in sim.umwelt.writes)
                    {
                        biome.WriteField(write.channel, posBuffer, agentCount,
                            write.amount, sim.rezX, sim.rezY);
                    }

                    // Metabolic heat
                    if (sim.umwelt.metabolicHeat > 0)
                    {
                        biome.WriteField(BiomeChannel.Temperature, posBuffer, agentCount,
                            sim.umwelt.metabolicHeat, sim.rezX, sim.rezY);
                    }

                    // Oxygen consumption
                    if (sim.umwelt.oxygenConsumption > 0)
                    {
                        biome.WriteField(BiomeChannel.Oxygen, posBuffer, agentCount,
                            -sim.umwelt.oxygenConsumption, sim.rezX, sim.rezY);
                    }
                }
            }

            // 4. Step biome (diffusion, interactions, advection)
            if (biome != null)
                biome.Step();

            Render();
        }

        void Render()
        {
            if (compositeCS == null) return;

            int simCount = Mathf.Min(simulations.Count, 8);
            compositeCS.SetInt(s_SimCountID, simCount);
            compositeCS.SetInt(s_RezXID, rezX);
            compositeCS.SetInt(s_RezYID, rezY);

            for (int i = 0; i < 8; i++)
            {
                string propName = "simInput" + i;
                if (i < simulations.Count && simulations[i] != null)
                {
                    var outTex = simulations[i].GetOutputTexture();
                    compositeCS.SetTexture(compositeRenderKernel, propName, outTex ?? _dummyBlackTex);
                }
                else
                {
                    compositeCS.SetTexture(compositeRenderKernel, propName, _dummyBlackTex);
                }
            }

            compositeCS.SetTexture(compositeRenderKernel, s_CompositeOutTexID, compositeOutTex);

            // Overlay external input on composite
            RenderTexture overlayTex = (externalInput != null) ? externalInput.OutputTexture : null;
            if (m_DebugOverlayVideoOnOutput && overlayTex != null && overlayTex.IsCreated())
            {
                compositeCS.SetTexture(compositeRenderKernel, s_ExternalOverlayTexID, overlayTex);
                compositeCS.SetFloat(s_OverlayStrengthID, m_DebugOverlayStrength);
            }
            else
            {
                compositeCS.SetTexture(compositeRenderKernel, s_ExternalOverlayTexID, _dummyBlackTex);
                compositeCS.SetFloat(s_OverlayStrengthID, 0f);
            }

            uint wx, wy, wz;
            compositeCS.GetKernelThreadGroupSizes(compositeRenderKernel, out wx, out wy, out wz);
            compositeCS.Dispatch(compositeRenderKernel,
                Mathf.CeilToInt((float)rezX / wx),
                Mathf.CeilToInt((float)rezY / wy),
                Mathf.CeilToInt(1f / wz));

            if (compositeOutMat != null)
                compositeOutMat.SetTexture("_UnlitColorMap", compositeOutTex);
        }

        public void Release()
        {
            // Release external input
            if (externalInput != null)
                externalInput.Release();

            // Release sims first (they own their own GPU resources)
            foreach (var sim in simulations)
                if (sim != null) sim.Release();

            if (biome != null)
                biome.Release();

            gpu?.ReleaseAll();
            gpu = null;
        }

        void OnDestroy() => Release();
        void OnEnable()
        {
            if (compositeCS != null)
                Reset();
        }
        void OnDisable() => Release();

        [Button("Export as PNG")]
        public void ExportPNG()
        {
            if (compositeOutTex == null) return;
            var tex = new Texture2D(rezX, rezY, TextureFormat.RGBA32, false);
            RenderTexture.active = compositeOutTex;
            tex.ReadPixels(new Rect(0, 0, rezX, rezY), 0, 0);
            tex.Apply();
            byte[] bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes($"Recordings/Biomes-{DateTime.Now.ToFileTime()}.png", bytes);
            Destroy(tex);
        }

        [Button("Reset Sims Only (preserve biome)")]
        public void ResetSimsOnly()
        {
            _simStepCount = 0;
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.SetResolution(rezX, rezY);
                sim.Reset();
            }
        }
    }
}

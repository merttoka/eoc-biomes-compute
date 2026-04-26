using System;
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Metaesthetica
{
    public class SimulationManager : MonoBehaviour
    {
        [Header("Setup")]
        [Range(32, 14336)] public int rezX = 10240;
        [Range(32, 14336)] public int rezY = 2048;
        [Range(0, 10)] public int stepsPerFrame = 1;
        [Range(1, 50)] public int stepMod = 1;
        public Material m_compositeOutMat;
        public Camera recordingCamera;
        public Transform compositeOutputQuad;
        public bool limitFPS = true;
        [Range(24, 165)] public int targetFPS = 60;

        public Material m_compositeOutMatRight;
        public Material m_compositeOutMatLeft;

        public ComputeShader cs;

        [Header("Simulations")]
        [SerializeField] private List<SimulationBase> simulations = new List<SimulationBase>();

        [Header("External Input")]
        [SerializeField] private ExternalInputProvider externalInput;

        [Header("Debug Overlay")]
        [SerializeField] private bool m_DebugOverlayVideoOnOutput = false;
        [SerializeField, Range(0f, 1f)] private float m_DebugOverlayStrength = 0.5f;

        private RenderTexture compositeOutTex;
        private int compositeRenderKernel;
        private GPUResourceManager gpu;

        // 8 fixed dummy textures for unused composite slots
        private RenderTexture _dummyBlackTex;

        [Header("NDI")]
        public bool sendNdi = false;
        public Klak.Ndi.NdiSender ndiSender;
        [SerializeField] Klak.Ndi.NdiResources _ndiResources = null;

        [Header("Spout")]
        public bool sendSpout = false;
        public Klak.Spout.SpoutSender spoutSender;
        [SerializeField] private Klak.Spout.SpoutResources _spoutResources = null;

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

        void Start()
        {
            Reset();
        }

        [Button]
        public void Reset()
        {
            Release();
            gpu = new GPUResourceManager();

            if (compositeOutputQuad != null)
            {
                float aspect = (float)rezX / rezY;
                var s = compositeOutputQuad.localScale;
                compositeOutputQuad.localScale = new Vector3(s.y * aspect, s.y, s.z);
            }

            // Create 1x1 black dummy for unused composite slots
            _dummyBlackTex = gpu.CreateTexture2D(1, 1, FilterMode.Point, name: "composite_dummy_black");
            var activeRT = RenderTexture.active;
            RenderTexture.active = _dummyBlackTex;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = activeRT;

            // Initialize external input
            if (externalInput != null)
                externalInput.Initialize();

            // Reset all simulations
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.SetResolution(rezX, rezY);
                sim.Reset();
            }

            compositeOutTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Trilinear, name: "composite_out");
            compositeRenderKernel = cs.FindKernel("CompositeRenderKernel");

            GPUResetKernel();
            Render();
        }

        private void GPUResetKernel()
        {
            cs.SetInt(s_RezXID, rezX);
            cs.SetInt(s_RezYID, rezY);

            int kernel = cs.FindKernel("ResetTextureKernel");
            cs.SetTexture(kernel, s_CompositeOutTexID, compositeOutTex);
            gpu.CreateTexture2D(1, 1, FilterMode.Point); // just to satisfy dispatch
            uint wx, wy, wz;
            cs.GetKernelThreadGroupSizes(kernel, out wx, out wy, out wz);
            cs.Dispatch(kernel,
                Mathf.CeilToInt((float)rezX / wx),
                Mathf.CeilToInt((float)rezY / wy),
                Mathf.CeilToInt(1f / wz));
        }

        void Update()
        {
            if (Time.frameCount % stepMod == 0)
                for (int i = 0; i < stepsPerFrame; i++)
                    Step();
        }

        public void Step()
        {
            // Update external input
            if (externalInput != null)
                externalInput.UpdateInput();

            // Assign influence texture and step each sim
            Texture influenceTex = externalInput != null ? externalInput.OutputTexture : null;
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.externalInfluenceTex = influenceTex;
                sim.Step();
            }

            Render();
        }

        void Render()
        {
            GPUCompositeRenderKernel();

            m_compositeOutMat.SetTexture("_UnlitColorMap", compositeOutTex);
            if (recordingCamera != null)
                recordingCamera.targetTexture = compositeOutTex;
            if (m_compositeOutMatRight != null && m_compositeOutMatLeft != null)
            {
                m_compositeOutMatRight.SetTexture("_UnlitColorMap", compositeOutTex);
                m_compositeOutMatLeft.SetTexture("_UnlitColorMap", compositeOutTex);
            }

            if (spoutSender != null && sendSpout)
            {
                spoutSender.SetResources(_spoutResources);
                spoutSender.spoutName = "EoC_output_tex";
                spoutSender.keepAlpha = true;
                spoutSender.captureMethod = Klak.Spout.CaptureMethod.Texture;
                spoutSender.sourceTexture = compositeOutTex;
            }
            if (ndiSender != null && sendNdi)
            {
                ndiSender.SetResources(_ndiResources);
                ndiSender.ndiName = "EoC_output_tex";
                ndiSender.keepAlpha = true;
                ndiSender.captureMethod = Klak.Ndi.CaptureMethod.Texture;
                ndiSender.sourceTexture = compositeOutTex;
            }
        }

        private void GPUCompositeRenderKernel()
        {
            int simCount = Mathf.Min(simulations.Count, 8);
            cs.SetInt(s_SimCountID, simCount);

            // Bind active sim outputs + dummy for unused slots
            for (int i = 0; i < 8; i++)
            {
                string propName = "simInput" + i;
                if (i < simulations.Count && simulations[i] != null)
                {
                    var outTex = simulations[i].GetOutputTexture();
                    cs.SetTexture(compositeRenderKernel, propName, outTex != null ? outTex : _dummyBlackTex);
                }
                else
                {
                    cs.SetTexture(compositeRenderKernel, propName, _dummyBlackTex);
                }
            }

            cs.SetTexture(compositeRenderKernel, s_CompositeOutTexID, compositeOutTex);

            // Overlay
            RenderTexture overlayTex = (externalInput != null) ? externalInput.OutputTexture : null;
            if (m_DebugOverlayVideoOnOutput && overlayTex != null && overlayTex.IsCreated())
            {
                cs.SetTexture(compositeRenderKernel, s_ExternalOverlayTexID, overlayTex);
                cs.SetFloat(s_OverlayStrengthID, m_DebugOverlayStrength);
            }
            else
            {
                cs.SetTexture(compositeRenderKernel, s_ExternalOverlayTexID, _dummyBlackTex);
                cs.SetFloat(s_OverlayStrengthID, 0f);
            }

            uint wx, wy, wz;
            cs.GetKernelThreadGroupSizes(compositeRenderKernel, out wx, out wy, out wz);
            cs.Dispatch(compositeRenderKernel,
                Mathf.CeilToInt((float)rezX / wx),
                Mathf.CeilToInt((float)rezY / wy),
                Mathf.CeilToInt(1f / wz));
        }

        public void Release()
        {
            if (externalInput != null)
                externalInput.Release();
            gpu?.ReleaseAll();
            gpu = null;
        }

        void OnDestroy() => Release();
        void OnEnable() => Reset();
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
            System.IO.File.WriteAllBytes($"Recordings/EoC-{DateTime.Now.ToFileTime()}.png", bytes);
            Destroy(tex);
        }

        [Button("Reset and randomize physarum")]
        public void ResetAndRandomizePhysarum()
        {
            Reset();
            foreach (var sim in simulations)
            {
                if (sim is PhysarumSim physarum)
                {
                    physarum.RandomizeParams();
                    break;
                }
            }
        }
    }
}

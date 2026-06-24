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
        [Tooltip("Render each sim at this fraction of the manager resolution; the composite upsamples to full output. Aspect ratio is preserved (both dims scale equally). 1 = full res. Lowering this is roughly a 1/scale^2 lever on per-pixel work (diffuse/render/perception). Takes effect on Reset.")]
        [Range(0.1f, 1f)] public float simResolutionScale = 1f;
        [Tooltip("Build each sim's perception texture at this fraction of its sim resolution. Perception only upsamples the low-res biome field (it carries no sim-res detail), and every sim reads it through bilinear UV sampling — 0.25-0.5 is visually identical and removes a full-res build pass per sim per step. Takes effect on Reset.")]
        [Range(0.05f, 1f)] public float perceptionResScale = 1f;
        [Range(0, 10)] public int stepsPerFrame = 1;
        [Range(1, 50)] public int stepMod = 1;
        public bool limitFPS = true;
        [Range(24, 330)] public int targetFPS = 60;

        [Header("Biome")]
        public Biome biome;

        [Tooltip("Optional: routes external drivers (plants/robot/neurons) into biome channels at mapped locations.")]
        public BiomeInjector injector;

        [Tooltip("Use the fused single-dispatch write-back (one dispatch applies all of a sim's channel deposits) instead of one dispatch per channel. Requires BiomeWriteFused.compute assigned to Biome.fusedWriteCS. Off = default per-channel path.")]
        [SerializeField] private bool fusedWriteback = false;

        [Tooltip("Write metabolic heat / oxygen consumption to the biome only every Nth step, with the amount scaled by N to conserve total flux. They feed slow PDE channels, so 2-4 is invisible; at 10 M physarum each skipped step saves tens of millions of scatter writes. 1 = every step (default).")]
        [Range(1, 8)] public int metabolismEvery = 1;

        [Header("Simulations")]
        public List<SimulationBase> simulations = new();

        [Header("External Input")]
        [SerializeField] private ExternalTextureReceiver externalInput;

        [Header("Neuron Firing")]
        [SerializeField] private NeuronFiringSource neuronFiring;

        [Header("Debug Overlay")]
        [SerializeField] private bool m_DebugOverlayVideoOnOutput = false;
        [SerializeField, Range(0f, 1f)] private float m_DebugOverlayStrength = 0.5f;

        [Header("Neuron Firing Ring Overlay")]
        [SerializeField] private bool m_NeuronRingOverlay = true;
        [SerializeField] private Color m_RingColor = new Color(1f, 0.95f, 0.8f, 1f);
        [SerializeField, Range(1f, 80f)] private float m_RingRadius = 14f;
        [SerializeField, Range(0.5f, 40f)] private float m_RingThickness = 4f;
        [SerializeField, Range(0f, 5f)] private float m_RingStrength = 1.5f;
        [SerializeField, Range(0f, 6f)] private float m_RingExpandGain = 2f;
        [SerializeField, Range(0f, 4f)] private float m_RingCoreStrength = 1.5f;
        [SerializeField, Range(0f, 1f)] private float m_RingThreshold = 0.1f;
        [Tooltip("Match the sims' spawnScale so rings land on agent clusters")]
        [SerializeField] private Vector2 m_RingSpawnScale = new Vector2(0.8f, 0.9f);

        [Header("Output")]
        public ComputeShader compositeCS;
        public Material compositeOutMat;
        public Transform compositeOutputQuad;
        public Camera recordingCamera;

        private RenderTexture compositeOutTex;
        private int compositeRenderKernel;
        private int neuronRingKernel = -1;
        private ComputeBuffer simWeightsBuffer;
        private readonly float[] _simWeightsCache = new float[8];

        // Compacted firing-ring data: only neurons above threshold are uploaded, so the
        // per-pixel ring loop runs over the handful of ACTIVE neurons instead of all 131,
        // and the whole dispatch is skipped while the network is quiet.
        private ComputeBuffer ringPosCompactBuffer;
        private ComputeBuffer ringFireCompactBuffer;
        private Vector2[] _ringPosCache;
        private float[] _ringFireCache;
        private readonly List<Biome.FusedWrite> _writeScratch = new();
        private GPUResourceManager gpu;
        private RenderTexture _dummyBlackTex;

        // Clear-in-place: the composite output is allocated once and reused across resets so
        // its RenderTexture instance never changes — otherwise the ExternalTextureSender
        // would re-assign SyphonServer.SourceTexture and force a native server teardown
        // (server drops + re-announces in the Syphon directory) on every reset. Reallocate
        // only when the output resolution changes.
        private int _allocRezX = -1, _allocRezY = -1;

        private int _simStepCount;
        public int SimStepCount => _simStepCount;

        /// <summary>Final composited output texture (null until Reset()).</summary>
        public RenderTexture CompositeOutputTexture => compositeOutTex;

        private static readonly int s_RezXID = Shader.PropertyToID("rezX");
        private static readonly int s_RezYID = Shader.PropertyToID("rezY");
        private static readonly int s_CompositeOutTexID = Shader.PropertyToID("compositeOut");
        private static readonly int s_SimCountID = Shader.PropertyToID("simCount");
        private static readonly int s_SimWeightsID = Shader.PropertyToID("simWeights");
        private static readonly int s_ExternalOverlayTexID = Shader.PropertyToID("externalOverlay");
        private static readonly int s_OverlayStrengthID = Shader.PropertyToID("overlayStrength");
        private static readonly int s_RingFiringID = Shader.PropertyToID("ringFiring");
        private static readonly int s_RingPositionsID = Shader.PropertyToID("ringPositions");
        private static readonly int s_RingCountID = Shader.PropertyToID("ringCount");
        private static readonly int s_RingThresholdID = Shader.PropertyToID("ringThreshold");
        private static readonly int s_RingSpawnScaleID = Shader.PropertyToID("ringSpawnScale");
        private static readonly int s_RingRadiusID = Shader.PropertyToID("ringRadius");
        private static readonly int s_RingThicknessID = Shader.PropertyToID("ringThickness");
        private static readonly int s_RingStrengthID = Shader.PropertyToID("ringStrength");
        private static readonly int s_RingExpandGainID = Shader.PropertyToID("ringExpandGain");
        private static readonly int s_RingCoreStrengthID = Shader.PropertyToID("ringCoreStrength");
        private static readonly int s_RingColorID = Shader.PropertyToID("ringColor");

        void Awake()
        {
            if (limitFPS)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = targetFPS;
            }
        }

        private bool ManagerNeedsAllocation() =>
            gpu == null || rezX != _allocRezX || rezY != _allocRezY;

        [Button]
        public void Reset()
        {
            // Clear-in-place: only (re)allocate the composite/dummy/weights when the output
            // resolution changes. On a normal reset the composite RenderTexture instance is
            // preserved (no Syphon teardown) and we just re-run the cascade + re-render.
            if (ManagerNeedsAllocation())
                Allocate();

            _simStepCount = 0;

            // Initialize external input (idempotent — keeps its GPU pool across resets)
            if (externalInput != null)
                externalInput.Initialize();

            // Initialize neuron firing source (idempotent — blob/buffers persist; only
            // the firing envelope is reset)
            if (neuronFiring != null)
                neuronFiring.Initialize();

            // Reset biome (persists unless explicitly cleared; itself clear-in-place)
            if (biome != null)
                biome.Reset();

            // Reset sims
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                // Scale sim resolution by simResolutionScale, preserving the manager's
                // aspect ratio (both dims scale equally). Composite UV-samples back up.
                sim.SetResolution(
                    Mathf.Max(8, Mathf.RoundToInt(rezX * simResolutionScale)),
                    Mathf.Max(8, Mathf.RoundToInt(rezY * simResolutionScale)));
                sim.perceptionResScale = perceptionResScale;
                sim.Reset();
            }

            Render();
        }

        // Allocate (or reallocate) the manager-owned GPU resources for the current output
        // resolution. Called by Reset() only when the resolution changes.
        private void Allocate()
        {
            Release();
            gpu = new GPUResourceManager();

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

            compositeOutTex = gpu.CreateTexture2D(rezX, rezY, FilterMode.Trilinear,
                RenderTextureFormat.ARGBHalf, name: "composite_out");
            simWeightsBuffer = gpu.CreateBuffer(8, sizeof(float));
            if (compositeCS != null)
            {
                compositeRenderKernel = compositeCS.FindKernel("CompositeRenderKernel");
                neuronRingKernel = compositeCS.HasKernel("NeuronRingKernel")
                    ? compositeCS.FindKernel("NeuronRingKernel") : -1;
            }

            _allocRezX = rezX; _allocRezY = rezY;
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

            // 0b. Update neuron firing source (OSC frame + decay) and broadcast its buffer
            neuronFiring?.UpdateFiring();
            ComputeBuffer firingBuf = neuronFiring != null ? neuronFiring.Buffer : null;
            int firingCount = neuronFiring != null ? neuronFiring.NeuronCount : 0;
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.neuronFiring = firingBuf;
                sim.neuronFiringCount = firingCount;
            }

            // 1. Build perception textures from biome for each sim. The build runs at
            //    the perception texture's own resolution (perceptionResScale × sim res);
            //    sims read it by UV so the sizes need not match.
            if (biome != null)
            {
                foreach (var sim in simulations)
                {
                    if (sim == null || sim.umwelt == null || sim.perceptionTex == null) continue;
                    biome.BuildPerceptionTex(sim.perceptionTex, sim.umwelt,
                        sim.perceptionTex.width, sim.perceptionTex.height);
                }
            }

            // 2. Step each sim
            foreach (var sim in simulations)
            {
                if (sim == null) continue;
                sim.Step();
            }

            // 3. Sims write back to biome. Metabolic heat / oxygen feed slow PDE channels,
            //    so they may run at a decimated cadence (metabolismEvery), amount scaled
            //    by the cadence to conserve total flux.
            if (biome != null)
            {
                int metabEvery = Mathf.Max(1, metabolismEvery);
                bool metabolismNow = (_simStepCount % metabEvery) == 0;
                float metabScale = metabEvery;

                for (int i = 0; i < simulations.Count; i++)
                {
                    var sim = simulations[i];
                    if (sim == null || sim.umwelt == null) continue;

                    var posBuffer = sim.GetAgentPositionBuffer();
                    int agentCount = sim.GetAgentCount();
                    if (posBuffer == null) continue;

                    if (fusedWriteback && biome.SupportsFusedWriteback)
                    {
                        // Fused: gather all deposits, apply in ONE dispatch.
                        _writeScratch.Clear();
                        foreach (var write in sim.umwelt.writes)
                            _writeScratch.Add(new Biome.FusedWrite { channel = write.channel, amount = write.amount });
                        if (metabolismNow && sim.umwelt.metabolicHeat > 0)
                            _writeScratch.Add(new Biome.FusedWrite { channel = BiomeChannel.Temperature, amount = sim.umwelt.metabolicHeat * metabScale });
                        if (metabolismNow && sim.umwelt.oxygenConsumption > 0)
                            _writeScratch.Add(new Biome.FusedWrite { channel = BiomeChannel.Oxygen, amount = -sim.umwelt.oxygenConsumption * metabScale });
                        biome.WriteFields(_writeScratch, posBuffer, agentCount, sim.rezX, sim.rezY);
                    }
                    else
                    {
                        // Default per-channel path (one dispatch per deposit) — unchanged.
                        foreach (var write in sim.umwelt.writes)
                            biome.WriteField(write.channel, posBuffer, agentCount,
                                write.amount, sim.rezX, sim.rezY);
                        if (metabolismNow && sim.umwelt.metabolicHeat > 0)
                            biome.WriteField(BiomeChannel.Temperature, posBuffer, agentCount,
                                sim.umwelt.metabolicHeat * metabScale, sim.rezX, sim.rezY);
                        if (metabolismNow && sim.umwelt.oxygenConsumption > 0)
                            biome.WriteField(BiomeChannel.Oxygen, posBuffer, agentCount,
                                -sim.umwelt.oxygenConsumption * metabScale, sim.rezX, sim.rezY);
                    }
                }
            }

            // 3.5 External sources (plants/robot/neurons) → biome channels at mapped
            //     locations. Writes into fieldReadArray pre-Step (same seam as WriteField).
            if (biome != null)
                injector?.Inject(biome);

            // 4. Step biome (diffusion, interactions, advection). Biome self-decimates the
            //    PDE internally via its stepEvery (the field is slow-changing). Deposits from
            //    sims accumulate into the field every step regardless (WriteField above).
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

            // Per-sim composite weights (index matches simInput0..7)
            for (int i = 0; i < 8; i++)
                _simWeightsCache[i] = (i < simulations.Count && simulations[i] != null)
                    ? simulations[i].compositeWeight : 1f;
            simWeightsBuffer.SetData(_simWeightsCache);
            compositeCS.SetBuffer(compositeRenderKernel, s_SimWeightsID, simWeightsBuffer);

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

            // Neuron firing-ring overlay: count-independent markers at firing neurons,
            // drawn on top of the composite so termite/boid firing isn't lost in physarum's flood.
            // Compacted CPU-side to the neurons above threshold: the per-pixel kernel loop
            // shrinks from all 131 neurons (~540 M iterations/frame at 2×FHD) to the few
            // active ones, and quiet frames skip the dispatch entirely.
            if (m_NeuronRingOverlay && neuronRingKernel >= 0 && neuronFiring != null)
            {
                var scaled = neuronFiring.ScaledValues;
                var posCPU = neuronFiring.PositionsCPU;
                int cap = (scaled != null && posCPU != null) ? Mathf.Min(scaled.Length, posCPU.Count) : 0;
                int active = 0;
                if (cap > 0)
                {
                    if (_ringFireCache == null || _ringFireCache.Length < cap)
                    {
                        _ringFireCache = new float[cap];
                        _ringPosCache = new Vector2[cap];
                    }
                    for (int i = 0; i < cap; i++)
                    {
                        float f = scaled[i];
                        if (f < m_RingThreshold) continue;
                        _ringFireCache[active] = f;
                        _ringPosCache[active] = posCPU[i];
                        active++;
                    }
                }
                if (active > 0)
                {
                    if (ringFireCompactBuffer == null || ringFireCompactBuffer.count < cap)
                    {
                        ringFireCompactBuffer = gpu.CreateBuffer(cap, sizeof(float));
                        ringPosCompactBuffer = gpu.CreateBuffer(cap, sizeof(float) * 2);
                    }
                    ringFireCompactBuffer.SetData(_ringFireCache, 0, 0, active);
                    ringPosCompactBuffer.SetData(_ringPosCache, 0, 0, active);

                    compositeCS.SetInt(s_RezXID, rezX);
                    compositeCS.SetInt(s_RezYID, rezY);
                    compositeCS.SetTexture(neuronRingKernel, s_CompositeOutTexID, compositeOutTex);
                    compositeCS.SetBuffer(neuronRingKernel, s_RingFiringID, ringFireCompactBuffer);
                    compositeCS.SetBuffer(neuronRingKernel, s_RingPositionsID, ringPosCompactBuffer);
                    compositeCS.SetInt(s_RingCountID, active);
                    compositeCS.SetFloat(s_RingThresholdID, m_RingThreshold);
                    compositeCS.SetVector(s_RingSpawnScaleID, new Vector4(m_RingSpawnScale.x, m_RingSpawnScale.y, 0, 0));
                    compositeCS.SetFloat(s_RingRadiusID, m_RingRadius);
                    compositeCS.SetFloat(s_RingThicknessID, m_RingThickness);
                    compositeCS.SetFloat(s_RingStrengthID, m_RingStrength);
                    compositeCS.SetFloat(s_RingExpandGainID, m_RingExpandGain);
                    compositeCS.SetFloat(s_RingCoreStrengthID, m_RingCoreStrength);
                    compositeCS.SetVector(s_RingColorID, m_RingColor);
                    compositeCS.GetKernelThreadGroupSizes(neuronRingKernel, out uint rwx, out uint rwy, out uint _);
                    compositeCS.Dispatch(neuronRingKernel,
                        Mathf.CeilToInt((float)rezX / rwx),
                        Mathf.CeilToInt((float)rezY / rwy), 1);
                }
            }

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

            gpu?.ReleaseAll();   // frees ring compact buffers too (gpu-tracked)
            gpu = null;
            ringPosCompactBuffer = null;
            ringFireCompactBuffer = null;
            _allocRezX = _allocRezY = -1;   // force reallocation on next Reset()
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
                // Scale sim resolution by simResolutionScale, preserving the manager's
                // aspect ratio (both dims scale equally). Composite UV-samples back up.
                sim.SetResolution(
                    Mathf.Max(8, Mathf.RoundToInt(rezX * simResolutionScale)),
                    Mathf.Max(8, Mathf.RoundToInt(rezY * simResolutionScale)));
                sim.perceptionResScale = perceptionResScale;
                sim.Reset();
            }
        }
    }
}

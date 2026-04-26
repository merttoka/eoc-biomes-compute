using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public class Biome : MonoBehaviour
    {
        [Header("Resolution (independent of sim resolution)")]
        [Range(32, 1024)] public int biomeRezX = 256;
        [Range(32, 1024)] public int biomeRezY = 256;

        [Header("Config")]
        public BiomeFieldConfig fieldConfig;
        public ComputeShader cs;

        // Field storage: double-buffered Texture2DArray (BiomeChannel.Count layers)
        private RenderTexture fieldReadArray;
        private RenderTexture fieldWriteArray;

        // Debug visualization
        [Header("Debug Grid (all channels)")]
        public bool showDebugGrid = true;
        [Range(0.1f, 2f)] public float debugQuadSize = 0.5f;
        [Range(0f, 0.05f)] public float debugGridSpacing = 0.02f;
        public Vector3 debugGridOrigin = new Vector3(3f, 0f, 0f);

        private RenderTexture[] debugTextures;
        private GameObject[] debugQuads;
        private Material[] debugMaterials;

        // Legacy single-channel debug (kept for backward compat)
        private RenderTexture debugOutTex;
        public Material debugOutputMat;
        [Range(0, BiomeChannel.Count - 1)] public int debugChannel = 0;

        private GPUResourceManager gpu;

        // Kernel handles
        private int resetFieldsKernel;
        private int initPermeabilityKernel;
        private int diffuseFieldsKernel;
        private int interactFieldsKernel;
        private int advectFieldsKernel;
        private int generateFlowKernel;
        private int renderDebugKernel;
        private int writeFieldKernel;
        private int readFieldKernel;

        // GPU data: per-channel settings uploaded as structured buffer
        private ComputeBuffer channelSettingsBuffer;

        private static readonly int s_RezXID = Shader.PropertyToID("rezX");
        private static readonly int s_RezYID = Shader.PropertyToID("rezY");
        private static readonly int s_FieldReadID = Shader.PropertyToID("fieldRead");
        private static readonly int s_FieldWriteID = Shader.PropertyToID("fieldWrite");
        private static readonly int s_ChannelCountID = Shader.PropertyToID("channelCount");
        private static readonly int s_ChannelSettingsID = Shader.PropertyToID("channelSettings");
        private static readonly int s_DebugOutTexID = Shader.PropertyToID("debugOutTex");
        private static readonly int s_DebugChannelID = Shader.PropertyToID("debugChannel");
        private static readonly int s_WasteToNutrientRateID = Shader.PropertyToID("wasteToNutrientRate");
        private static readonly int s_TempToFlowStrengthID = Shader.PropertyToID("tempToFlowStrength");
        private static readonly int s_TempToPermID = Shader.PropertyToID("tempToPermeability");
        private static readonly int s_NoiseScaleID = Shader.PropertyToID("noiseScale");
        private static readonly int s_NoiseThresholdID = Shader.PropertyToID("noiseThreshold");

        public RenderTexture FieldReadArray => fieldReadArray;
        public int RezX => biomeRezX;
        public int RezY => biomeRezY;

        private static readonly string[] ChannelNames = {
            "Nutrient", "Pheromone_0", "Pheromone_1", "Oxygen",
            "Temperature", "Waste", "Permeability", "Flow_X", "Flow_Y"
        };

        [Button]
        public void Reset()
        {
            Release();
            gpu = new GPUResourceManager();

            fieldReadArray = gpu.CreateTextureArray(biomeRezX, biomeRezY, BiomeChannel.Count,
                FilterMode.Bilinear, RenderTextureFormat.RHalf, "biome_fieldRead");
            fieldWriteArray = gpu.CreateTextureArray(biomeRezX, biomeRezY, BiomeChannel.Count,
                FilterMode.Bilinear, RenderTextureFormat.RHalf, "biome_fieldWrite");
            debugOutTex = gpu.CreateTexture2D(biomeRezX, biomeRezY, FilterMode.Bilinear,
                name: "biome_debugOut");

            FindKernels();
            UploadChannelSettings();
            GPUReset();

            if (showDebugGrid)
                CreateDebugGrid();
        }

        private void FindKernels()
        {
            resetFieldsKernel = cs.FindKernel("ResetFieldsKernel");
            initPermeabilityKernel = cs.FindKernel("InitPermeabilityKernel");
            diffuseFieldsKernel = cs.FindKernel("DiffuseFieldsKernel");
            interactFieldsKernel = cs.FindKernel("InteractFieldsKernel");
            advectFieldsKernel = cs.FindKernel("AdvectFieldsKernel");
            generateFlowKernel = cs.FindKernel("GenerateFlowKernel");
            renderDebugKernel = cs.FindKernel("RenderDebugKernel");
            writeFieldKernel = cs.FindKernel("WriteFieldKernel");
            readFieldKernel = cs.FindKernel("ReadFieldKernel");
        }

        public void UploadChannelSettings()
        {
            // Pack per-channel settings: diffuseRate, decayRate, advectedByFlow, initialValue
            int stride = sizeof(float) * 4;
            channelSettingsBuffer = gpu.CreateBuffer(BiomeChannel.Count, stride);
            var data = new float[BiomeChannel.Count * 4];
            for (int i = 0; i < BiomeChannel.Count && i < fieldConfig.channels.Count; i++)
            {
                var ch = fieldConfig.channels[i];
                data[i * 4 + 0] = ch.diffuseRate;
                data[i * 4 + 1] = ch.decayRate;
                data[i * 4 + 2] = ch.advectedByFlow ? 1f : 0f;
                data[i * 4 + 3] = ch.initialValue;
            }
            channelSettingsBuffer.SetData(data);
        }

        private void GPUReset()
        {
            cs.SetInt(s_RezXID, biomeRezX);
            cs.SetInt(s_RezYID, biomeRezY);
            cs.SetInt(s_ChannelCountID, BiomeChannel.Count);
            cs.SetBuffer(resetFieldsKernel, s_ChannelSettingsID, channelSettingsBuffer);

            // Clear both buffers
            cs.SetTexture(resetFieldsKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(resetFieldsKernel, biomeRezX, biomeRezY, 1);
            cs.SetTexture(resetFieldsKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(resetFieldsKernel, biomeRezX, biomeRezY, 1);

            // Init permeability from noise
            cs.SetFloat(s_NoiseScaleID, fieldConfig.noiseScale);
            cs.SetFloat(s_NoiseThresholdID, fieldConfig.noiseThreshold);
            cs.SetTexture(initPermeabilityKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(initPermeabilityKernel, biomeRezX, biomeRezY, 1);
            cs.SetTexture(initPermeabilityKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(initPermeabilityKernel, biomeRezX, biomeRezY, 1);
        }

        public void Step()
        {
            cs.SetInt(s_RezXID, biomeRezX);
            cs.SetInt(s_RezYID, biomeRezY);
            cs.SetInt(s_ChannelCountID, BiomeChannel.Count);
            cs.SetBuffer(diffuseFieldsKernel, s_ChannelSettingsID, channelSettingsBuffer);
            cs.SetBuffer(advectFieldsKernel, s_ChannelSettingsID, channelSettingsBuffer);

            // 1. Generate flow from temperature gradients
            cs.SetFloat(s_TempToFlowStrengthID, fieldConfig.temperatureToFlowStrength);
            cs.SetTexture(generateFlowKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(generateFlowKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(generateFlowKernel, biomeRezX, biomeRezY, 1);

            // 2. Advect fields by flow
            cs.SetTexture(advectFieldsKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(advectFieldsKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(advectFieldsKernel, biomeRezX, biomeRezY, 1);

            // 3. Cross-field interactions (waste→nutrient, temp→permeability)
            cs.SetFloat(s_WasteToNutrientRateID, fieldConfig.wasteToNutrientRate);
            cs.SetFloat(s_TempToPermID, fieldConfig.temperatureToPermeability);
            cs.SetTexture(interactFieldsKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(interactFieldsKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(interactFieldsKernel, biomeRezX, biomeRezY, 1);

            // 4. Diffuse and decay
            cs.SetTexture(diffuseFieldsKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(diffuseFieldsKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(diffuseFieldsKernel, biomeRezX, biomeRezY, 1);

            // Swap
            (fieldReadArray, fieldWriteArray) = (fieldWriteArray, fieldReadArray);

            // Debug render
            RenderDebug();
        }

        private void CreateDebugGrid()
        {
            debugTextures = new RenderTexture[BiomeChannel.Count];
            debugQuads = new GameObject[BiomeChannel.Count];
            debugMaterials = new Material[BiomeChannel.Count];

            var shader = Shader.Find("HDRP/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");

            int cols = 3;
            float step = debugQuadSize + debugGridSpacing;

            for (int i = 0; i < BiomeChannel.Count; i++)
            {
                debugTextures[i] = gpu.CreateTexture2D(biomeRezX, biomeRezY,
                    FilterMode.Bilinear, name: $"biome_debug_{i}");

                debugMaterials[i] = new Material(shader);
                debugMaterials[i].name = $"BiomeDebug_{ChannelNames[i]}";

                int col = i % cols;
                int row = i / cols;
                var pos = debugGridOrigin + new Vector3(col * step, -row * step, 0);

                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = $"Biome_{ChannelNames[i]}";
                quad.transform.SetParent(transform);
                quad.transform.localPosition = pos;
                quad.transform.localScale = Vector3.one * debugQuadSize;
                quad.GetComponent<MeshRenderer>().material = debugMaterials[i];

                // Remove collider
                var col2 = quad.GetComponent<Collider>();
                if (col2 != null) Destroy(col2);

                debugQuads[i] = quad;
            }
        }

        private void RenderDebug()
        {
            // Render all channels for debug grid
            if (showDebugGrid && debugTextures != null)
            {
                cs.SetTexture(renderDebugKernel, s_FieldReadID, fieldReadArray);
                for (int i = 0; i < BiomeChannel.Count; i++)
                {
                    cs.SetInt(s_DebugChannelID, i);
                    cs.SetTexture(renderDebugKernel, s_DebugOutTexID, debugTextures[i]);
                    Dispatch(renderDebugKernel, biomeRezX, biomeRezY, 1);
                    debugMaterials[i].SetTexture("_UnlitColorMap", debugTextures[i]);
                }
            }

            // Legacy single-channel debug
            if (debugOutputMat != null)
            {
                cs.SetInt(s_DebugChannelID, debugChannel);
                cs.SetTexture(renderDebugKernel, s_FieldReadID, fieldReadArray);
                cs.SetTexture(renderDebugKernel, s_DebugOutTexID, debugOutTex);
                Dispatch(renderDebugKernel, biomeRezX, biomeRezY, 1);
                debugOutputMat.SetTexture("_UnlitColorMap", debugOutTex);
            }
        }

        // --- Public API for sims to write/read ---

        /// <summary>
        /// Sims call this to deposit/consume a specific biome channel at agent positions.
        /// agentPositions buffer: float2 per agent (in sim-space coordinates).
        /// The kernel maps sim coords → biome coords using resolution ratio.
        /// </summary>
        public void WriteField(int channel, ComputeBuffer agentPositions, int agentCount,
            float amount, int simRezX, int simRezY)
        {
            cs.SetInt(s_RezXID, biomeRezX);
            cs.SetInt(s_RezYID, biomeRezY);
            cs.SetInt("writeChannel", channel);
            cs.SetFloat("writeAmount", amount);
            cs.SetInt("agentCount", agentCount);
            cs.SetFloat("simToFieldX", (float)biomeRezX / simRezX);
            cs.SetFloat("simToFieldY", (float)biomeRezY / simRezY);
            cs.SetBuffer(writeFieldKernel, "agentPositions", agentPositions);
            cs.SetTexture(writeFieldKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(writeFieldKernel, agentCount, 1, 1);
        }

        /// <summary>
        /// Build a perception texture for a sim by sampling biome fields through Umwelt weights.
        /// Returns a RenderTexture at sim resolution with weighted sum of biome fields.
        /// </summary>
        public void BuildPerceptionTex(RenderTexture perceptionTex, UmweltMapping umwelt,
            int simRezX, int simRezY)
        {
            // Upload read entries as structured buffer (done each frame — could cache)
            int entryCount = umwelt.reads.Count;
            if (entryCount == 0) return;

            // Pack: int channel, float weight, int effect, float _pad
            var entryData = new float[entryCount * 4];
            for (int i = 0; i < entryCount; i++)
            {
                var r = umwelt.reads[i];
                entryData[i * 4 + 0] = System.BitConverter.Int32BitsToSingle(r.channel);
                entryData[i * 4 + 1] = r.weight;
                entryData[i * 4 + 2] = System.BitConverter.Int32BitsToSingle((int)r.effect);
                entryData[i * 4 + 3] = 0f;
            }

            // TODO: cache this buffer per umwelt to avoid per-frame alloc
            var entryBuffer = new ComputeBuffer(entryCount, sizeof(float) * 4);
            entryBuffer.SetData(entryData);

            cs.SetInt("readEntryCount", entryCount);
            cs.SetInt("perceptionRezX", simRezX);
            cs.SetInt("perceptionRezY", simRezY);
            cs.SetInt(s_RezXID, biomeRezX);
            cs.SetInt(s_RezYID, biomeRezY);
            cs.SetBuffer(readFieldKernel, "readEntries", entryBuffer);
            cs.SetTexture(readFieldKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(readFieldKernel, "perceptionTex", perceptionTex);
            Dispatch(readFieldKernel, simRezX, simRezY, 1);

            entryBuffer.Release();
        }

        // --- Clear modes ---

        [Button("Clear All Fields")]
        public void ClearAll()
        {
            if (gpu == null) return;
            UploadChannelSettings();
            GPUReset();
        }

        [Button("Soft Reset (decay 0.5)")]
        public void SoftReset()
        {
            SoftReset(0.5f);
        }

        public void SoftReset(float decayFactor)
        {
            if (gpu == null) return;
            cs.SetFloat("softResetDecay", decayFactor);
            // TODO: dispatch a multiply kernel on all channels
        }

        public void ClearField(int channel)
        {
            if (gpu == null) return;
            cs.SetInt("clearChannel", channel);
            // TODO: dispatch a per-channel clear kernel
        }

        private void Dispatch(int kernel, int x, int y, int z)
        {
            cs.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out uint wz);
            cs.Dispatch(kernel,
                Mathf.CeilToInt((float)x / wx),
                Mathf.CeilToInt((float)y / wy),
                Mathf.CeilToInt((float)z / wz));
        }

        public void Release()
        {
            DestroyDebugGrid();
            gpu?.ReleaseAll();
            gpu = null;
        }

        private void DestroyDebugGrid()
        {
            if (debugQuads != null)
            {
                foreach (var q in debugQuads)
                    if (q != null) Destroy(q);
                debugQuads = null;
            }
            if (debugMaterials != null)
            {
                foreach (var m in debugMaterials)
                    if (m != null) Destroy(m);
                debugMaterials = null;
            }
            debugTextures = null;
        }

        // Biome is initialized by SimulationManager.Reset(), not OnEnable
        void OnDisable() => Release();
        void OnDestroy() => Release();
    }
}

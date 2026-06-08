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
        [Range(0f, 0.2f)] public float debugGridSpacing = 0.02f;
        public Vector3 debugGridOrigin = new Vector3(3f, 0f, 0f);
        [Tooltip("Columns before wrapping to a new row. Set to channel count (default) for a single horizontal strip.")]
        [Range(1, BiomeChannel.Count)] public int debugGridColumns = BiomeChannel.Count;
        public bool showBiomeLabels = true;
        public Color labelColor = Color.white;
        [Range(0f, 2f)] public float labelYOffset = 0.6f;

        private RenderTexture[] debugTextures;
        private GameObject[] debugQuads;
        private Material[] debugMaterials;


        // Legacy single-channel debug (kept for backward compat)
        private RenderTexture debugOutTex;
        [Header("Legacy Debug")]
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
            "Nutrient", "Pheromone_0", "Pheromone_1", "Pheromone_2", "Oxygen",
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

            // The field PDE runs as a proper ping-pong chain: each pass reads the
            // previous pass's output, then swaps. Every pass writes ALL channels
            // (copy-through for the ones it doesn't modify), so the intermediate
            // swaps never surface stale channels. Previously a single end-of-step
            // swap let DiffuseFieldsKernel (which writes every channel) clobber the
            // flow/advect/interact passes, making them dead code.

            // 1. Generate flow from temperature gradients
            cs.SetFloat(s_TempToFlowStrengthID, fieldConfig.temperatureToFlowStrength);
            DispatchFieldPass(generateFlowKernel);

            // 2. Advect fields by flow (transports chemicals; agents are not pushed)
            cs.SetBuffer(advectFieldsKernel, s_ChannelSettingsID, channelSettingsBuffer);
            DispatchFieldPass(advectFieldsKernel);

            // 3. Cross-field interactions (waste→nutrient, temp→permeability)
            cs.SetFloat(s_WasteToNutrientRateID, fieldConfig.wasteToNutrientRate);
            cs.SetFloat(s_TempToPermID, fieldConfig.temperatureToPermeability);
            DispatchFieldPass(interactFieldsKernel);

            // 4. Diffuse and decay
            cs.SetBuffer(diffuseFieldsKernel, s_ChannelSettingsID, channelSettingsBuffer);
            DispatchFieldPass(diffuseFieldsKernel);

            // Debug render (reads fieldReadArray, which now holds the final state)
            RenderDebug();
        }

        // Bind the current read/write arrays to a field kernel, dispatch, then swap so
        // the next pass reads this pass's output. After an even number of passes the
        // final state lands back in fieldReadArray — the buffer WriteField and
        // BuildPerceptionTex bind.
        private void DispatchFieldPass(int kernel)
        {
            cs.SetTexture(kernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(kernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(kernel, biomeRezX, biomeRezY, 1);
            (fieldReadArray, fieldWriteArray) = (fieldWriteArray, fieldReadArray);
        }

        private void CreateDebugGrid()
        {
            debugTextures = new RenderTexture[BiomeChannel.Count];
            debugQuads = new GameObject[BiomeChannel.Count];
            debugMaterials = new Material[BiomeChannel.Count];

            var shader = Shader.Find("HDRP/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");

            int cols = Mathf.Clamp(debugGridColumns, 1, BiomeChannel.Count);
            // Size quads to the field's aspect ratio; debugQuadSize is the height.
            float aspect = biomeRezY > 0 ? (float)biomeRezX / biomeRezY : 1f;
            float quadW = debugQuadSize * aspect;
            float quadH = debugQuadSize;
            float stepX = quadW + debugGridSpacing;
            float stepY = quadH + debugGridSpacing;

            for (int i = 0; i < BiomeChannel.Count; i++)
            {
                debugTextures[i] = gpu.CreateTexture2D(biomeRezX, biomeRezY,
                    FilterMode.Bilinear, name: $"biome_debug_{i}");

                debugMaterials[i] = new Material(shader);
                debugMaterials[i].name = $"BiomeDebug_{ChannelNames[i]}";

                int col = i % cols;
                int row = i / cols;
                var pos = debugGridOrigin + new Vector3(col * stepX, -row * stepY, 0);

                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = $"Biome_{ChannelNames[i]}";
                quad.transform.SetParent(transform);
                quad.transform.localPosition = pos;
                quad.transform.localScale = new Vector3(quadW, quadH, 1f);
                quad.GetComponent<MeshRenderer>().material = debugMaterials[i];

                // Remove collider
                var col2 = quad.GetComponent<Collider>();
                if (col2 != null) Destroy(col2);

                debugQuads[i] = quad;
            }
        }

        /// <summary>Render one biome channel into a 2D RenderTexture (sized at biome
        /// resolution). Used by debug grid and external texture sending.</summary>
        public void RenderChannelTo(int channel, RenderTexture dst)
        {
            if (gpu == null || dst == null) return;
            cs.SetInt(s_RezXID, biomeRezX);
            cs.SetInt(s_RezYID, biomeRezY);
            cs.SetInt(s_DebugChannelID, channel);
            cs.SetTexture(renderDebugKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(renderDebugKernel, s_DebugOutTexID, dst);
            Dispatch(renderDebugKernel, biomeRezX, biomeRezY, 1);
        }

        private void RenderDebug()
        {
            // Render all channels for debug grid
            if (showDebugGrid && debugTextures != null)
            {
                for (int i = 0; i < BiomeChannel.Count; i++)
                {
                    RenderChannelTo(i, debugTextures[i]);
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
        /// agentPositions is the sim's 20-byte Agent buffer (position, direction, typeId);
        /// the shader reads .position. The kernel maps sim coords → biome coords by ratio.
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

#if UNITY_EDITOR
        // Draw channel names above each debug quad in the Scene view.
        void OnDrawGizmos()
        {
            if (!showBiomeLabels || debugQuads == null) return;

            var style = new GUIStyle
            {
                normal = { textColor = labelColor },
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            for (int i = 0; i < debugQuads.Length; i++)
            {
                if (debugQuads[i] == null) continue;
                var pos = debugQuads[i].transform.position + Vector3.up * labelYOffset;
                UnityEditor.Handles.Label(pos, ChannelNames[i], style);
            }
        }
#endif
    }
}

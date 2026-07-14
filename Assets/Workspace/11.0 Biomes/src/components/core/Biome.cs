using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public class Biome : MonoBehaviour
    {
        [Header("Resolution (independent of sim resolution)")]
        [Range(32, 1024)] public int biomeRezX = 256;
        [Range(32, 1024)] public int biomeRezY = 256;

        [Tooltip("Run the field PDE (flow/advect/interact/diffuse + debug render) once every N times Step() is called. The field is slow-changing, so 2-4 is visually invisible and removes its full-array passes from most frames. Agent write-back accumulates into the field every step regardless.")]
        [Range(1, 16)] public int stepEvery = 1;
        private int _stepCounter;

        [Header("Config")]
        public BiomeFieldConfig fieldConfig;
        public ComputeShader cs;

        [Header("Habitat confinement")]
        [Tooltip("Strength of steer-back when an agent is outside its preferred permeability band.")]
        [Range(0f, 8f)] public float habitatAvoidGain = 2f;
        [Tooltip("Speed penalty when an agent is outside its band (1 unit out-of-band -> this fraction slower).")]
        [Range(0f, 8f)] public float habitatSlowGain = 3f;

        [Tooltip("Optional swappable write-back shader (assign BiomeWriteFused.compute). When set AND SimulationManager.fusedWriteback is on, all of a sim's channel deposits are applied in ONE dispatch instead of one per channel. Leave null to use the default per-channel path.")]
        public ComputeShader fusedWriteCS;

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

        [Header("PNG Export")]
        [Tooltip("Folder (relative to the project root, i.e. the parent of Assets/) where channel PNGs are written. A subfolder named after this GameObject is created so multiple biomes don't collide.")]
        public string exportFolder = "Exports/Biomes";
        [Tooltip("Per-channel contrast stretch (each channel's min..max -> full range) so faint fields are visible. Export-only — realtime rendering is unaffected.")]
        public bool exportNormalized = true;

        private GPUResourceManager gpu;

        // Clear-in-place: field arrays, channel buffers and the 11-quad debug grid are
        // allocated once and reused across resets. A normal Reset() only re-uploads channel
        // settings and re-clears the fields; it reallocates (and rebuilds the debug grid)
        // ONLY when the biome resolution changes. This keeps the heavy realloc + 11×
        // GameObject.CreatePrimitive out of every reset frame.
        private int _allocRezX = -1, _allocRezY = -1;

        // Kernel handles
        private int resetFieldsKernel;
        private int initPermeabilityKernel;
        private int diffuseFieldsKernel;
        private int interactFieldsKernel;
        private int advectFieldsKernel;
        private int generateFlowKernel;
        private int renderDebugKernel;
        private int writeFieldKernel;
        private int injectStampKernel;
        private int readFieldKernel;
        private int buildPermeabilityKernel;
        private ComputeBuffer _buildDummyFiring;   // 1-elem dummy bound when no firing source

        // GPU data: per-channel settings uploaded as structured buffer
        private ComputeBuffer channelSettingsBuffer;
        // Parallel per-channel relaxation rates (homeostatic pull toward baseline / terrain).
        private ComputeBuffer channelRelaxBuffer;

        // Reusable perception read-entry buffer (one per Biome, grown on demand). Replaces
        // the per-call new/Release in BuildPerceptionTex, which churned ~180 GPU buffer
        // allocations/sec (3 sims × 60 fps). Data is re-uploaded each call (a few entries).
        private ComputeBuffer perceptionEntryBuffer;
        private float[] _perceptionEntryData;

        private static readonly int s_RezXID = Shader.PropertyToID("rezX");
        private static readonly int s_RezYID = Shader.PropertyToID("rezY");
        private static readonly int s_FieldReadID = Shader.PropertyToID("fieldRead");
        private static readonly int s_FieldWriteID = Shader.PropertyToID("fieldWrite");
        private static readonly int s_ChannelCountID = Shader.PropertyToID("channelCount");
        private static readonly int s_ChannelSettingsID = Shader.PropertyToID("channelSettings");
        private static readonly int s_ChannelRelaxID = Shader.PropertyToID("channelRelax");
        private static readonly int s_DecompTempSpanID = Shader.PropertyToID("decompTempSpan");
        private static readonly int s_DebugOutTexID = Shader.PropertyToID("debugOutTex");
        private static readonly int s_DebugChannelID = Shader.PropertyToID("debugChannel");
        private static readonly int s_DebugNormalizeID = Shader.PropertyToID("debugNormalize");
        private static readonly int s_DebugNormMinID = Shader.PropertyToID("debugNormMin");
        private static readonly int s_DebugNormInvRangeID = Shader.PropertyToID("debugNormInvRange");
        private static readonly int s_WasteToNutrientRateID = Shader.PropertyToID("wasteToNutrientRate");
        private static readonly int s_TempToFlowStrengthID = Shader.PropertyToID("tempToFlowStrength");
        private static readonly int s_TempToPermID = Shader.PropertyToID("tempToPermeability");
        private static readonly int s_PermOpenBaselineID = Shader.PropertyToID("permOpenBaseline");
        private static readonly int s_HabitatBandMinID  = Shader.PropertyToID("habitatBandMin");
        private static readonly int s_HabitatBandMaxID  = Shader.PropertyToID("habitatBandMax");
        private static readonly int s_HabitatAvoidGainID = Shader.PropertyToID("habitatAvoidGain");
        private static readonly int s_HabitatSlowGainID  = Shader.PropertyToID("habitatSlowGain");
        private static readonly int s_TempToEvaporationID = Shader.PropertyToID("tempToEvaporation");
        private static readonly int s_HumidityGradientGainID = Shader.PropertyToID("humidityGradientGain");
        private static readonly int s_NoiseScaleID = Shader.PropertyToID("noiseScale");
        private static readonly int s_NoiseThresholdID = Shader.PropertyToID("noiseThreshold");

        public RenderTexture FieldReadArray => fieldReadArray;
        public int RezX => biomeRezX;
        public int RezY => biomeRezY;

        // Channel display names for debug quads / PNG export — single source of truth is
        // BiomeChannel.Names, so adding a channel never desyncs this list (it auto-grows).
        private static string[] ChannelNames => BiomeChannel.Names;

        private bool BiomeNeedsAllocation() =>
            gpu == null || biomeRezX != _allocRezX || biomeRezY != _allocRezY;

        [Button]
        public void Reset()
        {
            // Clear-in-place: reallocate the field arrays + debug grid only when the biome
            // resolution changes. A normal reset re-uploads channel settings and re-clears
            // the fields, keeping the same texture instances.
            if (BiomeNeedsAllocation())
                Allocate();

            UploadChannelSettings();   // re-pack + SetData so live fieldConfig edits apply
            GPUReset();                // clear both field arrays + init permeability

            // Debug grid: build once and honor a runtime showDebugGrid toggle without
            // recreating 11 GameObjects/materials on every reset.
            if (showDebugGrid && debugTextures == null)
                CreateDebugGrid();
            else if (!showDebugGrid && debugTextures != null)
                DestroyDebugGrid();
        }

        private void Allocate()
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
            AllocateChannelBuffers();

            _allocRezX = biomeRezX; _allocRezY = biomeRezY;
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
            injectStampKernel = cs.FindKernel("InjectStampKernel");
            readFieldKernel = cs.FindKernel("ReadFieldKernel");
            buildPermeabilityKernel = cs.FindKernel("BuildPermeabilityKernel");
        }

        // Allocate the per-channel settings buffers once (sizes are fixed by BiomeChannel.Count).
        // Split out of UploadChannelSettings so a clear-in-place reset re-uploads data into the
        // SAME buffers instead of creating a new pair each time (the old behavior leaked a pair
        // per Reset/ClearAll, freed only at the next full Release).
        private void AllocateChannelBuffers()
        {
            int stride = sizeof(float) * 4;
            channelSettingsBuffer = gpu.CreateBuffer(BiomeChannel.Count, stride);
            channelRelaxBuffer = gpu.CreateBuffer(BiomeChannel.Count, sizeof(float));
        }

        public void UploadChannelSettings()
        {
            if (channelSettingsBuffer == null || channelRelaxBuffer == null) return;
            // Pack per-channel settings: diffuseRate, decayRate, advectedByFlow, initialValue.
            // A config with fewer rows than BiomeChannel.Count leaves the tail channels
            // zero-filled (no diffuse/advect/relax, initialValue 0) — i.e. a silently dead
            // channel. Warn so a stale asset (e.g. an old/ config from before a channel was
            // added) is obvious instead of looking like a physics bug.
            if (fieldConfig.channels.Count < BiomeChannel.Count)
                Debug.LogWarning($"[Biome] {fieldConfig.name} has {fieldConfig.channels.Count} " +
                    $"channel rows but the field has {BiomeChannel.Count}; channels " +
                    $"{fieldConfig.channels.Count}..{BiomeChannel.Count - 1} will be inert " +
                    $"(zero diffuse/relax/initial). Add the missing rows to the asset.", this);
            var data = new float[BiomeChannel.Count * 4];
            var relax = new float[BiomeChannel.Count];
            for (int i = 0; i < BiomeChannel.Count && i < fieldConfig.channels.Count; i++)
            {
                var ch = fieldConfig.channels[i];
                data[i * 4 + 0] = ch.diffuseRate;
                data[i * 4 + 1] = ch.decayRate;
                data[i * 4 + 2] = ch.advectedByFlow ? 1f : 0f;
                data[i * 4 + 3] = ch.initialValue;
                relax[i] = ch.relaxRate;
            }
            channelSettingsBuffer.SetData(data);
            channelRelaxBuffer.SetData(relax);
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

            // Init permeability to the uniform-open baseline (termite mounds build downward from it)
            cs.SetFloat(s_NoiseScaleID, fieldConfig.noiseScale);
            cs.SetFloat(s_NoiseThresholdID, fieldConfig.noiseThreshold);
            cs.SetFloat(s_PermOpenBaselineID, fieldConfig.permeabilityOpenBaseline);
            cs.SetTexture(initPermeabilityKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(initPermeabilityKernel, biomeRezX, biomeRezY, 1);
            cs.SetTexture(initPermeabilityKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(initPermeabilityKernel, biomeRezX, biomeRezY, 1);
        }

        public void Step()
        {
            // Self-decimate: run the PDE only every `stepEvery` calls. Early-return is
            // before any pass/swap, so the field ping-pong stays consistent on skipped
            // frames; sim deposits (WriteField, called separately) still land every step.
            _stepCounter++;
            if (_stepCounter % Mathf.Max(1, stepEvery) != 0) return;

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

            // 3. Cross-field interactions (waste→nutrient via Q10, temp→permeability relax).
            //    Needs the channel settings + relax rates, the Q10 span, and the noise
            //    params (permeability relaxes toward the recomputed terrain).
            cs.SetFloat(s_WasteToNutrientRateID, fieldConfig.wasteToNutrientRate);
            cs.SetFloat(s_DecompTempSpanID, fieldConfig.decompositionTempSpan);
            cs.SetFloat(s_TempToPermID, fieldConfig.temperatureToPermeability);
            cs.SetFloat(s_PermOpenBaselineID, fieldConfig.permeabilityOpenBaseline);
            cs.SetFloat(s_TempToEvaporationID, fieldConfig.temperatureToEvaporation);
            cs.SetFloat(s_HumidityGradientGainID, fieldConfig.humidityGradientGain);
            cs.SetFloat(s_NoiseScaleID, fieldConfig.noiseScale);
            cs.SetFloat(s_NoiseThresholdID, fieldConfig.noiseThreshold);
            cs.SetBuffer(interactFieldsKernel, s_ChannelSettingsID, channelSettingsBuffer);
            cs.SetBuffer(interactFieldsKernel, s_ChannelRelaxID, channelRelaxBuffer);
            DispatchFieldPass(interactFieldsKernel);

            // 4. Diffuse, decay, and homeostatic relaxation toward baseline.
            cs.SetBuffer(diffuseFieldsKernel, s_ChannelSettingsID, channelSettingsBuffer);
            cs.SetBuffer(diffuseFieldsKernel, s_ChannelRelaxID, channelRelaxBuffer);
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
            cs.SetInt(s_DebugNormalizeID, 0);   // realtime: never normalize
            cs.SetTexture(renderDebugKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(renderDebugKernel, s_DebugOutTexID, dst);
            Dispatch(renderDebugKernel, biomeRezX, biomeRezY, 1);
        }

        /// <summary>Like RenderChannelTo but stretches the channel by (val-min)*invRange
        /// before the colormap, so faint fields read clearly. Export-only — realtime paths
        /// use RenderChannelTo (normalize off).</summary>
        public void RenderChannelNormalizedTo(int channel, RenderTexture dst, float min, float invRange)
        {
            if (gpu == null || dst == null) return;
            cs.SetInt(s_RezXID, biomeRezX);
            cs.SetInt(s_RezYID, biomeRezY);
            cs.SetInt(s_DebugChannelID, channel);
            cs.SetInt(s_DebugNormalizeID, 1);
            cs.SetFloat(s_DebugNormMinID, min);
            cs.SetFloat(s_DebugNormInvRangeID, invRange);
            cs.SetTexture(renderDebugKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(renderDebugKernel, s_DebugOutTexID, dst);
            Dispatch(renderDebugKernel, biomeRezX, biomeRezY, 1);
        }

        /// <summary>
        /// Save every biome channel as its own PNG into exportFolder/&lt;GameObject name&gt;/.
        /// Each channel is rendered through the same debug kernel the grid uses, read back
        /// to the CPU, and encoded. Requires Reset() to have run (GPU resources live).
        /// </summary>
        [Button("Export PNGs")]
        public void ExportPNGs()
        {
            if (gpu == null || fieldReadArray == null)
            {
                Debug.LogWarning("[Biome] Cannot export — call Reset() first to initialize GPU resources.");
                return;
            }

            // Resolve output dir relative to the project root (parent of Assets/), with a
            // per-biome subfolder so several Biome instances export side by side.
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
            string dir = System.IO.Path.Combine(projectRoot, exportFolder, gameObject.name);
            System.IO.Directory.CreateDirectory(dir);

            // Float UAV target matches the format the debug kernel already writes; the 8-bit
            // readback texture is what EncodeToPNG reliably supports (ReadPixels converts).
            var tmp = new RenderTexture(biomeRezX, biomeRezY, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
            };
            tmp.Create();
            var readback = new Texture2D(biomeRezX, biomeRezY, TextureFormat.RGBA32, false);

            // For normalized export: a same-format (RHalf) copy of one channel layer, read
            // back to the CPU to find that channel's min/max. Export-only, so a blocking
            // readback is fine.
            RenderTexture rawRT = null;
            Texture2D rawTex = null;
            if (exportNormalized)
            {
                rawRT = new RenderTexture(biomeRezX, biomeRezY, 0, RenderTextureFormat.RHalf)
                {
                    dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
                };
                rawRT.Create();
                rawTex = new Texture2D(biomeRezX, biomeRezY, TextureFormat.RGBAFloat, false);
            }

            var prevActive = RenderTexture.active;
            for (int i = 0; i < BiomeChannel.Count; i++)
            {
                if (exportNormalized)
                {
                    // Pull the raw channel field to the CPU and stretch by its own min..max.
                    Graphics.CopyTexture(fieldReadArray, i, 0, rawRT, 0, 0);
                    RenderTexture.active = rawRT;
                    rawTex.ReadPixels(new Rect(0, 0, biomeRezX, biomeRezY), 0, 0);
                    rawTex.Apply();
                    var px = rawTex.GetPixels();
                    float mn = float.MaxValue, mx = float.MinValue;
                    for (int p = 0; p < px.Length; p++)
                    {
                        float v = px[p].r;
                        if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                        if (v < mn) mn = v;
                        if (v > mx) mx = v;
                    }
                    float invRange = (mx > mn) ? 1f / (mx - mn) : 0f; // flat channel -> 0 (renders black)
                    RenderChannelNormalizedTo(i, tmp, mn, invRange);
                }
                else
                {
                    RenderChannelTo(i, tmp);
                }

                RenderTexture.active = tmp;
                readback.ReadPixels(new Rect(0, 0, biomeRezX, biomeRezY), 0, 0);
                readback.Apply();

                string path = System.IO.Path.Combine(dir, $"{i:D2}_{ChannelNames[i]}.png");
                System.IO.File.WriteAllBytes(path, readback.EncodeToPNG());
            }
            RenderTexture.active = prevActive;

            tmp.Release();
            Destroy(tmp);
            Destroy(readback);
            if (rawRT != null) { rawRT.Release(); Destroy(rawRT); }
            if (rawTex != null) Destroy(rawTex);

            Debug.Log($"[Biome] Exported {BiomeChannel.Count} channel PNGs → {dir}{(exportNormalized ? " (normalized)" : "")}");

#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
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
                cs.SetInt(s_DebugNormalizeID, 0);
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

        // Termite mound build: lower permeability at agent positions, probabilistically and
        // pulsing with neuron firing. Writes into fieldReadArray (same target as WriteField),
        // so it rides the field ping-pong. See permeability-mounds spec.
        public void BuildPermeability(ComputeBuffer agentPositions, int agentCount,
            ComputeBuffer firing, int neuronCount,
            float depositProb, float firingDepositProb, float firingThreshold,
            float buildAmount, int timeSeed, int simRezX, int simRezY)
        {
            if (cs == null || fieldReadArray == null || agentPositions == null || agentCount <= 0) return;
            cs.SetInt("agentCount", agentCount);
            cs.SetFloat("simToFieldX", (float)biomeRezX / Mathf.Max(1, simRezX));
            cs.SetFloat("simToFieldY", (float)biomeRezY / Mathf.Max(1, simRezY));
            cs.SetFloat("buildDepositProb", depositProb);
            cs.SetFloat("buildFiringDepositProb", firingDepositProb);
            cs.SetFloat("buildFiringThreshold", firingThreshold);
            cs.SetFloat("buildAmount", buildAmount);
            // A StructuredBuffer must always be bound; with no firing source, bind a persistent
            // 1-element dummy and force neuronCount 0 so the kernel never indexes it.
            if (firing == null) { _buildDummyFiring ??= new ComputeBuffer(1, sizeof(float)); firing = _buildDummyFiring; neuronCount = 0; }
            cs.SetInt("buildNeuronCount", neuronCount);
            cs.SetInt("buildTimeSeed", timeSeed);
            cs.SetBuffer(buildPermeabilityKernel, "buildFiring", firing);
            cs.SetBuffer(buildPermeabilityKernel, "agentPositions", agentPositions);
            cs.SetTexture(buildPermeabilityKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(buildPermeabilityKernel, agentCount, 1, 1);
        }

        // Reset permeability (the termite-built mounds) to uniform-open in both ping-pong
        // buffers, without touching any other channel. Called when termites reset.
        public void ClearPermeability()
        {
            if (cs == null || fieldReadArray == null) return;
            cs.SetFloat(s_PermOpenBaselineID, fieldConfig.permeabilityOpenBaseline);
            cs.SetTexture(initPermeabilityKernel, s_FieldWriteID, fieldWriteArray);
            Dispatch(initPermeabilityKernel, biomeRezX, biomeRezY, 1);
            cs.SetTexture(initPermeabilityKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(initPermeabilityKernel, biomeRezX, biomeRezY, 1);
        }

        // ── Fused write-back (optional, via fusedWriteCS / BiomeWriteFused.compute) ──
        [StructLayout(LayoutKind.Sequential)]
        public struct FusedWrite { public int channel; public float amount; }   // 8 bytes, matches HLSL

        public bool SupportsFusedWriteback => fusedWriteCS != null;

        private ComputeBuffer _writeEntryBuffer;
        private FusedWrite[] _writeEntryCache;
        private int _writeFieldsKernel = -1;

        /// <summary>Apply ALL of a sim's (channel, amount) deposits in one dispatch using
        /// fusedWriteCS (BiomeWriteFused.compute). Same accumulate semantics as WriteField,
        /// but computes the field position once per agent and loops channels. No-op if
        /// fusedWriteCS is unassigned — callers should gate on SupportsFusedWriteback.</summary>
        public void WriteFields(List<FusedWrite> entries, ComputeBuffer agentPositions,
            int agentCount, int simRezX, int simRezY)
        {
            if (fusedWriteCS == null || agentPositions == null || entries == null || entries.Count == 0) return;
            if (_writeFieldsKernel < 0) _writeFieldsKernel = fusedWriteCS.FindKernel("WriteFieldsKernel");

            int n = entries.Count;
            if (_writeEntryCache == null || _writeEntryCache.Length < n) _writeEntryCache = new FusedWrite[n];
            for (int i = 0; i < n; i++) _writeEntryCache[i] = entries[i];
            if (_writeEntryBuffer == null || _writeEntryBuffer.count < n)
            {
                _writeEntryBuffer?.Release();
                _writeEntryBuffer = new ComputeBuffer(Mathf.Max(1, n), sizeof(int) + sizeof(float));
            }
            _writeEntryBuffer.SetData(_writeEntryCache, 0, 0, n);

            fusedWriteCS.SetInt(s_RezXID, biomeRezX);
            fusedWriteCS.SetInt(s_RezYID, biomeRezY);
            fusedWriteCS.SetInt("agentCount", agentCount);
            fusedWriteCS.SetFloat("simToFieldX", (float)biomeRezX / simRezX);
            fusedWriteCS.SetFloat("simToFieldY", (float)biomeRezY / simRezY);
            fusedWriteCS.SetInt("writeEntryCount", n);
            fusedWriteCS.SetBuffer(_writeFieldsKernel, "writeEntries", _writeEntryBuffer);
            fusedWriteCS.SetBuffer(_writeFieldsKernel, "agentPositions", agentPositions);
            fusedWriteCS.SetTexture(_writeFieldsKernel, s_FieldWriteID, fieldReadArray);
            fusedWriteCS.GetKernelThreadGroupSizes(_writeFieldsKernel, out uint tgx, out uint _, out uint __);
            fusedWriteCS.Dispatch(_writeFieldsKernel, Mathf.CeilToInt((float)agentCount / tgx), 1, 1);
        }

        /// <summary>
        /// External-source injection: stamp soft Gaussian discs into biome channels at
        /// mapped UVs. Writes IN PLACE into fieldReadArray BEFORE Step() — the same seam
        /// WriteField uses — so it rides the field ping-pong with no clobber risk. Call
        /// once per step, after sim write-back and before Step(). stamps = StructuredBuffer
        /// of InjectStamp (see BiomeInjector); count = active stamp count.
        /// </summary>
        public void InjectSources(ComputeBuffer stamps, int count)
        {
            if (gpu == null || stamps == null || count <= 0) return;
            cs.SetInt(s_RezXID, biomeRezX);
            cs.SetInt(s_RezYID, biomeRezY);
            cs.SetInt("injectStampCount", count);
            cs.SetBuffer(injectStampKernel, "injectStamps", stamps);
            cs.SetTexture(injectStampKernel, s_FieldWriteID, fieldReadArray);
            Dispatch(injectStampKernel, biomeRezX, biomeRezY, 1);
        }

        /// <summary>
        /// Build a perception texture for a sim by sampling biome fields through Umwelt weights.
        /// Returns a RenderTexture at sim resolution with weighted sum of biome fields.
        /// </summary>
        public void BuildPerceptionTex(RenderTexture perceptionTex, UmweltMapping umwelt,
            int simRezX, int simRezY)
        {
            // Upload read entries into the reusable buffer (grown on demand).
            int entryCount = umwelt.reads.Count;
            if (entryCount == 0) return;

            // (Re)allocate the shared buffer only when it must grow. Tracked by the GPU
            // manager so Release() frees it with everything else.
            if (perceptionEntryBuffer == null || perceptionEntryBuffer.count < entryCount)
            {
                if (perceptionEntryBuffer != null) perceptionEntryBuffer.Release();
                perceptionEntryBuffer = gpu.CreateBuffer(entryCount, sizeof(float) * 4);
                _perceptionEntryData = new float[entryCount * 4];
            }
            else if (_perceptionEntryData == null || _perceptionEntryData.Length < entryCount * 4)
            {
                _perceptionEntryData = new float[entryCount * 4];
            }

            // Pack: int channel, float weight, int effect, float _pad
            for (int i = 0; i < entryCount; i++)
            {
                var r = umwelt.reads[i];
                _perceptionEntryData[i * 4 + 0] = System.BitConverter.Int32BitsToSingle(r.channel);
                _perceptionEntryData[i * 4 + 1] = r.weight;
                _perceptionEntryData[i * 4 + 2] = System.BitConverter.Int32BitsToSingle((int)r.effect);
                _perceptionEntryData[i * 4 + 3] = 0f;
            }
            perceptionEntryBuffer.SetData(_perceptionEntryData, 0, 0, entryCount * 4);

            cs.SetInt("readEntryCount", entryCount);
            cs.SetInt("perceptionRezX", simRezX);
            cs.SetInt("perceptionRezY", simRezY);
            cs.SetInt(s_RezXID, biomeRezX);
            cs.SetInt(s_RezYID, biomeRezY);
            cs.SetFloat(s_HabitatBandMinID, umwelt != null ? umwelt.preferredPermeabilityMin : 0f);
            cs.SetFloat(s_HabitatBandMaxID, umwelt != null ? umwelt.preferredPermeabilityMax : 1f);
            cs.SetFloat(s_HabitatAvoidGainID, habitatAvoidGain);
            cs.SetFloat(s_HabitatSlowGainID, habitatSlowGain);
            cs.SetBuffer(readFieldKernel, "readEntries", perceptionEntryBuffer);
            cs.SetTexture(readFieldKernel, s_FieldReadID, fieldReadArray);
            cs.SetTexture(readFieldKernel, "perceptionTex", perceptionTex);
            Dispatch(readFieldKernel, simRezX, simRezY, 1);
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
            gpu?.ReleaseAll();   // frees channelSettingsBuffer + perceptionEntryBuffer (both tracked)
            gpu = null;
            channelSettingsBuffer = null;   // tracked + freed above; null so they're rebuilt on realloc
            channelRelaxBuffer = null;
            perceptionEntryBuffer = null;
            _perceptionEntryData = null;
            _writeEntryBuffer?.Release();   // not gpu-tracked (own buffer)
            _writeEntryBuffer = null;
            _buildDummyFiring?.Release();
            _buildDummyFiring = null;
            _allocRezX = _allocRezY = -1;   // force reallocation on next Reset()
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

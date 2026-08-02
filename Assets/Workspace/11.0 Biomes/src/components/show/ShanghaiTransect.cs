using System.IO;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Seeds the biome's Permeability channel from a 12-epoch transect of Shanghai built-up
    /// surface (GHSL GHS-BUILT-S R2023A, 1975→2030 in 5-year steps).
    ///
    /// <para><b>The transect.</b> A horizontal band of the shared TD_biomes data frame, 2048 px
    /// wide by 195 px tall, centred on grid row 1077 — the venue cluster centre. Sized from the
    /// master's aspect (9472x900 = 10.52:1, so 2048/10.52 = 195). The consequences are the
    /// piece: screen centre is where the audience physically stands and has been saturated
    /// since before 1975, so it closes first and stays closed, while the 20-60 km band where
    /// the growth actually happened lands out toward the frame edges — in shot, but over the
    /// viewer's horizon in life.</para>
    ///
    /// <para><b>Monotonic closure, never an overwrite.</b> Built-up only ever grows, so the
    /// layer is applied as a floor via MinToward: <c>perm = min(perm, openness)</c>. Termite
    /// mounds survive because they have already closed further than the city has. A SetToward
    /// re-seed would stomp them every step and fight ADR-0010's persistence design; a static
    /// 2030 seed would waste 11 of the 12 epochs.</para>
    ///
    /// <para>Runs at <c>DefaultExecutionOrder(-100)</c> so its FixedUpdate lands before
    /// SimulationManager's, putting the seed at the top of each sim step — the same pre-Step
    /// seam WriteField and InjectSources use.</para>
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ShanghaiTransect : MonoBehaviour
    {
        [Header("Wiring")]
        public Biome biome;
        [Tooltip("ShanghaiTransect.compute — crossfades two epochs into the openness map.")]
        public ComputeShader transectCS;

        [Header("Baked data")]
        [Tooltip("Path under StreamingAssets, written by Biomes > Bake Shanghai Transect.")]
        public string blobPath = "biomes11/shanghai_transect.bytes";

        [Header("Arc drive")]
        [Tooltip("Position along the 12-epoch sequence, 0 = 1975, 1 = 2030. Driven by ShowArc; " +
                 "adjacent epochs are crossfaded so the city grows continuously rather than " +
                 "stepping 12 times.")]
        [Range(0f, 1f)] public float epochPhase = 0f;

        [Tooltip("0 = no closure at all (field entirely open, city absent), 1 = full closure. " +
                 "Ramped by ShowArc so the opening is a genuinely unseeded field and the loop " +
                 "can release the city rather than cutting it.")]
        [Range(0f, 1f)] public float closureStrength = 1f;

        [Header("Response curve")]
        [Tooltip("GHSL stores built-up AREA (m2/cell) normalized linearly against a global max " +
                 "of 7368 — physical, not perceptual. Measured over this transect the 2030 mean " +
                 "is only 0.144, so a raw 1-v seed moves permeability 0.90 -> 0.86 across the " +
                 "whole piece and reads as nothing. What matters is whether an agent can cross a " +
                 "cell, which saturates far sooner than area does, so the curve is concave. At " +
                 "gamma 0.45 / gain 2.0 the transect goes 14% -> 54% closed across the arc and " +
                 "the venue centre is fully closed from 1975 on, which is the thesis.")]
        [Range(0.1f, 2f)] public float builtUpGamma = 0.45f;
        [Range(0.1f, 8f)] public float builtUpGain = 2.0f;

        [Header("Seeding")]
        [Tooltip("Seed every fixed step automatically. Off = ShowArc (or a Timeline signal) " +
                 "calls SeedNow() explicitly.")]
        public bool autoSeed = true;
        [BiomeChannelField] public int targetChannel = BiomeChannel.Permeability;

        // Twelve epoch frames as R16 textures WITH mipmaps — the blend kernel samples an
        // explicit mip so downscaling to the coarse biome field filters instead of aliasing.
        private Texture2D[] _epochs;
        private RenderTexture _opennessRT;
        private int _blendKernel = -1;
        private int _width, _height, _frameCount;
        private int _allocRezX = -1, _allocRezY = -1;
        // Latches so a missing/!corrupt blob is reported once rather than every fixed step.
        private bool _loadAttempted;

        public int EpochCount => _frameCount;
        public RenderTexture OpennessTexture => _opennessRT;
        public bool IsLoaded => _epochs != null && _frameCount > 0;

        private static readonly int s_EpochAID = Shader.PropertyToID("epochA");
        private static readonly int s_EpochBID = Shader.PropertyToID("epochB");
        private static readonly int s_OpennessOutID = Shader.PropertyToID("opennessOut");
        private static readonly int s_OutRezXID = Shader.PropertyToID("outRezX");
        private static readonly int s_OutRezYID = Shader.PropertyToID("outRezY");
        private static readonly int s_EpochTID = Shader.PropertyToID("epochT");
        private static readonly int s_SrcMipID = Shader.PropertyToID("srcMipLevel");
        private static readonly int s_GammaID = Shader.PropertyToID("builtUpGamma");
        private static readonly int s_GainID = Shader.PropertyToID("builtUpGain");
        private static readonly int s_ClosureID = Shader.PropertyToID("closureStrength");

        void OnEnable() => Load();
        void OnDisable() => Release();

        [Button("Load / reload baked transect")]
        public void Load()
        {
            Release();
            _loadAttempted = true;

            string full = Path.Combine(Application.streamingAssetsPath, blobPath);
            if (!File.Exists(full))
            {
                Debug.LogWarning($"[ShanghaiTransect] No baked transect at {full}. " +
                                 "Run Biomes > Bake Shanghai Transect.", this);
                return;
            }

            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                uint magic = br.ReadUInt32();
                int version = br.ReadInt32();
                if (magic != 0x52544853 || version != 1)
                {
                    Debug.LogError($"[ShanghaiTransect] {full} is not a v1 SHTR blob " +
                                   $"(magic 0x{magic:X8}, version {version}).", this);
                    return;
                }

                _width = br.ReadInt32();
                _height = br.ReadInt32();
                _frameCount = br.ReadInt32();
                br.ReadInt32();   // centreRow — informational, recorded by the baker

                long expected = (long)_width * _height * _frameCount * sizeof(ushort);
                long remaining = fs.Length - fs.Position;
                if (remaining != expected)
                {
                    Debug.LogError($"[ShanghaiTransect] {full} payload is {remaining} bytes, " +
                                   $"expected {expected} for {_frameCount}x{_width}x{_height}.", this);
                    _frameCount = 0;
                    return;
                }

                _epochs = new Texture2D[_frameCount];
                var scratch = new ushort[_width * _height];
                for (int f = 0; f < _frameCount; f++)
                {
                    for (int i = 0; i < scratch.Length; i++)
                        scratch[i] = br.ReadUInt16();

                    // R16 keeps the source's exact 16-bit samples — the whole reason the bake
                    // bypasses Unity's texture importer. mipChain: true so the blend kernel can
                    // filter properly on the way down to the biome field resolution.
                    var tex = new Texture2D(_width, _height, TextureFormat.R16, mipChain: true,
                                            linear: true)
                    {
                        name = $"shanghai_epoch_{f:00}",
                        wrapMode = TextureWrapMode.Clamp,   // never wrap the strip's far edge into frame
                        filterMode = FilterMode.Bilinear,
                    };
                    tex.SetPixelData(scratch, 0);
                    tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
                    _epochs[f] = tex;
                }
            }

            if (transectCS != null && transectCS.HasKernel("BlendEpochsKernel"))
                _blendKernel = transectCS.FindKernel("BlendEpochsKernel");
        }

        void FixedUpdate()
        {
            if (autoSeed) SeedNow();
        }

        /// <summary>
        /// Crossfade the two epochs bracketing <see cref="epochPhase"/> into the openness map
        /// and apply it as a closure floor on the target channel.
        /// </summary>
        public void SeedNow()
        {
            // Lazy load. OnEnable is the normal entry point, but it does not fire in edit mode
            // (no [ExecuteAlways]), and a domain reload can clear the textures without another
            // OnEnable. Without this the component silently seeds nothing, which looks exactly
            // like "the transect has no effect" rather than "the transect never loaded".
            if (!IsLoaded && !_loadAttempted) Load();

            if (!IsLoaded || biome == null || transectCS == null || _blendKernel < 0) return;
            if (biome.FieldReadArray == null) return;   // biome not allocated yet

            EnsureOpennessRT();
            if (_opennessRT == null) return;

            // Epoch phase -> bracketing pair + fraction. Clamped at the top so phase == 1
            // lands cleanly on the final epoch instead of indexing past it.
            float scaled = Mathf.Clamp01(epochPhase) * (_frameCount - 1);
            int a = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, _frameCount - 1);
            int b = Mathf.Min(a + 1, _frameCount - 1);
            float t = scaled - a;

            // Filter on the way down: the transect is 195 px tall and the biome field is
            // coarser still, so pick the mip whose height is nearest the destination.
            float mip = Mathf.Max(0f, Mathf.Log(Mathf.Max(1f, (float)_height / _opennessRT.height), 2f));

            transectCS.SetTexture(_blendKernel, s_EpochAID, _epochs[a]);
            transectCS.SetTexture(_blendKernel, s_EpochBID, _epochs[b]);
            transectCS.SetTexture(_blendKernel, s_OpennessOutID, _opennessRT);
            transectCS.SetInt(s_OutRezXID, _opennessRT.width);
            transectCS.SetInt(s_OutRezYID, _opennessRT.height);
            transectCS.SetFloat(s_EpochTID, t);
            transectCS.SetFloat(s_SrcMipID, mip);
            transectCS.SetFloat(s_GammaID, builtUpGamma);
            transectCS.SetFloat(s_GainID, builtUpGain);
            transectCS.SetFloat(s_ClosureID, closureStrength);

            transectCS.GetKernelThreadGroupSizes(_blendKernel, out uint wx, out uint wy, out _);
            transectCS.Dispatch(_blendKernel,
                Mathf.CeilToInt((float)_opennessRT.width / wx),
                Mathf.CeilToInt((float)_opennessRT.height / wy), 1);

            // MinToward: the city can only ever CLOSE the field, and only past what is
            // already there — which is exactly what lets termite mounds persist through it.
            biome.SeedChannelFromTexture(targetChannel, _opennessRT, 1f,
                BiomeInjector.BlendMode.MinToward);
        }

        // Openness is generated at the biome field's own resolution: the seed's only consumer
        // samples it there, so anything larger is wasted bandwidth every single step.
        private void EnsureOpennessRT()
        {
            int w = Mathf.Max(8, biome.RezX);
            int h = Mathf.Max(8, biome.RezY);
            if (_opennessRT != null && w == _allocRezX && h == _allocRezY) return;

            if (_opennessRT != null) { _opennessRT.Release(); Destroy(_opennessRT); }

            _opennessRT = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat)
            {
                name = "shanghai_openness",
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            _opennessRT.Create();
            _allocRezX = w; _allocRezY = h;
        }

        public void Release()
        {
            if (_epochs != null)
            {
                foreach (var t in _epochs)
                    if (t != null) Destroy(t);
                _epochs = null;
            }
            if (_opennessRT != null)
            {
                _opennessRT.Release();
                Destroy(_opennessRT);
                _opennessRT = null;
            }
            _frameCount = 0;
            _allocRezX = _allocRezY = -1;
        }

        /// <summary>Year at the current phase — for on-screen date readouts and cue export.</summary>
        public int CurrentYear()
        {
            if (_frameCount <= 1) return 1975;
            float scaled = Mathf.Clamp01(epochPhase) * (_frameCount - 1);
            return Mathf.RoundToInt(1975f + scaled * 5f);
        }
    }
}

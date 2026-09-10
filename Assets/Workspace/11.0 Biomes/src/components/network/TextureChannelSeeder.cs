using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Seeds biome channels from a whole raster every sim step — a video clip, a Syphon/NDI
    /// stream, a PNG. Each route reads one component of the source (R, G, B, A or luminance)
    /// into one channel with its own gain and blend mode, so a colour video becomes up to
    /// three independent fields the agents then perceive through their Umwelt mappings.
    ///
    /// <para>Runs at SimulationManager step 3.6, right after <see cref="BiomeInjector"/> and
    /// before <c>Biome.Step()</c>: the raster lands in the field, then the PDE erodes, spreads
    /// and advects it like any other deposit. This is the 11.0 replacement for the 10.0
    /// "external influence" path that steered agents straight off a texture — here the
    /// texture is ecology, not a force.</para>
    ///
    /// <para>Mode guidance: <b>MaxToward</b> for a persistent video (builds a stable gradient
    /// that fades where the picture goes dark), <b>SetToward</b> to let the video own the
    /// channel outright, <b>Additive</b> only with a tiny gain (~0.01) or it saturates.</para>
    /// </summary>
    public class TextureChannelSeeder : MonoBehaviour
    {
        [System.Serializable]
        public class Route
        {
            public string name = "route";
            public bool enabled = true;

            [Tooltip("Which component of the source raster carries the value.")]
            public SeedSource from = SeedSource.Luminance;

            [Tooltip("Target biome channel.")]
            [BiomeChannelField] public int channel = BiomeChannel.Nutrient;

            [Tooltip("Multiplies the sampled 0..1 value before the blend (result saturated). " +
                     "Max/Set modes: target level (~1). Additive: per-step increment (~0.01).")]
            [Range(0f, 4f)] public float gain = 1f;

            [Tooltip("How the value combines with what is already in the channel.")]
            public BiomeInjector.BlendMode mode = BiomeInjector.BlendMode.MaxToward;
        }

        [Header("Source")]
        [Tooltip("Receiver whose OutputTexture is the raster: its Syphon/NDI/Spout stream, or " +
                 "its debug video clip when 'Debug Use Video Input' is on.")]
        public ExternalTextureReceiver receiver;

        [Tooltip("Optional explicit raster (a PNG, a RenderTexture). Wins over the receiver when set.")]
        public Texture textureOverride;

        [Header("Routes")]
        [Tooltip("Seed only every Nth sim step. Video is 25-30 fps and the sim is 60 Hz, so 2 " +
                 "halves the dispatches with no visible change. 1 = every step.")]
        [Range(1, 8)] public int seedEvery = 1;

        public List<Route> routes = new();

        [Tooltip("Log once when a route is skipped for a missing source.")]
        public bool debugLog = false;

        private bool _warnedNoSource;

        /// <summary>The raster being routed, or null when nothing is available yet.</summary>
        public Texture SourceTexture =>
            textureOverride != null ? textureOverride
            : receiver != null && !receiver.IsDebugVideoStopped ? receiver.OutputTexture
            : null;

        /// <summary>Called once per sim step by SimulationManager (main thread).</summary>
        public void Seed(Biome biome, int simStep)
        {
            if (biome == null || routes.Count == 0) return;
            if (seedEvery > 1 && (simStep % seedEvery) != 0) return;

            Texture src = SourceTexture;
            if (src == null)
            {
                if (debugLog && !_warnedNoSource)
                {
                    _warnedNoSource = true;
                    Debug.LogWarning($"TextureChannelSeeder '{name}': no source texture yet (receiver idle or no override).");
                }
                return;
            }
            _warnedNoSource = false;

            for (int i = 0; i < routes.Count; i++)
            {
                var r = routes[i];
                if (r == null || !r.enabled || r.gain <= 0f) continue;
                biome.SeedChannelFromTexture(r.channel, src, r.gain, r.mode, r.from);
            }
        }

        [Button("Add RGB → Temperature / Oxygen / Nutrient")]
        public void AddDefaultRgbRoutes()
        {
            routes.Add(new Route { name = "R → Temperature", from = SeedSource.Red,   channel = BiomeChannel.Temperature });
            routes.Add(new Route { name = "G → Oxygen",      from = SeedSource.Green, channel = BiomeChannel.Oxygen });
            routes.Add(new Route { name = "B → Nutrient",    from = SeedSource.Blue,  channel = BiomeChannel.Nutrient });
        }
    }
}

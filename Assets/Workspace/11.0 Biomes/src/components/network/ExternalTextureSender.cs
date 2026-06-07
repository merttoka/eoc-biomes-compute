using System;
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public enum SendSource { CompositeOutput, SimOutput, BiomeLayer }

    [Serializable]
    public class SendStream
    {
        public bool enabled = true;
        public SendSource source = SendSource.CompositeOutput;
        public int index = 0;               // sim index (SimOutput) or channel index (BiomeLayer)
        public ShareProtocol protocol = ShareProtocol.NDI;
        public string streamName = "";      // blank -> auto default
        [Range(0.05f, 1f)] public float resolutionScale = 1f;
    }

    /// <summary>Sends selected textures (composite / per-sim / biome layer) out over
    /// Syphon/NDI/Spout. One Klak sender per enabled stream, pushed each LateUpdate.</summary>
    public class ExternalTextureSender : MonoBehaviour
    {
        [Header("References")]
        public SimulationManager simManager;
        public ShareResources resources = new();

        [Header("Streams")]
        public List<SendStream> streams = new();

        private class Live
        {
            public ITextureSenderBackend backend;
            public GameObject go;
            public RenderTexture extractRT;  // biome channel extract (biome res)
            public RenderTexture scaleRT;    // downscaled output
            public bool warned;
        }
        private readonly List<Live> _live = new();

        private static readonly string[] ChannelNames = {
            "Nutrient", "Pheromone_0", "Pheromone_1", "Oxygen",
            "Temperature", "Waste", "Permeability", "Flow_X", "Flow_Y" };

        [Button("Rebuild Streams")]
        public void Rebuild()
        {
            Teardown();
            for (int i = 0; i < streams.Count; i++)
            {
                var s = streams[i];
                var live = new Live();
                if (s.enabled && ExternalTextureShare.IsAvailable(s.protocol))
                {
                    string name = string.IsNullOrEmpty(s.streamName) ? DefaultName(s) : s.streamName;
                    live.go = new GameObject($"Sender_{name}");
                    live.go.transform.SetParent(transform, false);
                    live.go.SetActive(false);
                    live.backend = ExternalTextureShare.CreateSender(live.go, s.protocol, name, resources);
                    live.go.SetActive(true);
                }
                _live.Add(live);
            }
        }

        void OnEnable() => Rebuild();
        void OnDisable() => Teardown();
        void OnDestroy() => Teardown();

        void LateUpdate()
        {
            if (simManager == null) return;
            if (_live.Count != streams.Count) Rebuild();

            for (int i = 0; i < streams.Count; i++)
            {
                var s = streams[i];
                var live = _live[i];
                if (live == null || live.backend == null) continue;

                Texture src = ResolveSource(s, live);
                if (src == null) continue;

                if (s.resolutionScale < 0.999f)
                    src = Downscale(src, s.resolutionScale, live);

                live.backend.SetSource(src);
            }
        }

        private Texture ResolveSource(SendStream s, Live live)
        {
            switch (s.source)
            {
                case SendSource.CompositeOutput:
                    return simManager.CompositeOutputTexture;

                case SendSource.SimOutput:
                    if (s.index < 0 || s.index >= simManager.simulations.Count)
                        return WarnOnce(live, $"sim index {s.index} out of range");
                    return simManager.simulations[s.index] != null
                        ? simManager.simulations[s.index].GetOutputTexture() : null;

                case SendSource.BiomeLayer:
                    if (simManager.biome == null) return null;
                    if (s.index < 0 || s.index >= BiomeChannel.Count)
                        return WarnOnce(live, $"biome channel {s.index} out of range");
                    EnsureExtractRT(live);
                    simManager.biome.RenderChannelTo(s.index, live.extractRT);
                    return live.extractRT;

                default: return null;
            }
        }

        private void EnsureExtractRT(Live live)
        {
            int w = simManager.biome.RezX, h = simManager.biome.RezY;
            if (live.extractRT != null && live.extractRT.width == w && live.extractRT.height == h) return;
            if (live.extractRT != null) { live.extractRT.Release(); Destroy(live.extractRT); }
            live.extractRT = new RenderTexture(w, h, 0) { enableRandomWrite = true, name = "BiomeExtract" };
            live.extractRT.Create();
        }

        private Texture Downscale(Texture src, float scale, Live live)
        {
            int w = Mathf.Max(1, Mathf.CeilToInt(src.width * scale));
            int h = Mathf.Max(1, Mathf.CeilToInt(src.height * scale));
            if (live.scaleRT == null || live.scaleRT.width != w || live.scaleRT.height != h)
            {
                if (live.scaleRT != null) { live.scaleRT.Release(); Destroy(live.scaleRT); }
                live.scaleRT = new RenderTexture(w, h, 0) { name = "DownscaleSend" };
                live.scaleRT.Create();
            }
            Graphics.Blit(src, live.scaleRT);
            return live.scaleRT;
        }

        private Texture WarnOnce(Live live, string msg)
        {
            if (!live.warned) { Debug.LogWarning($"ExternalTextureSender: {msg}"); live.warned = true; }
            return null;
        }

        private string DefaultName(SendStream s) => s.source switch
        {
            SendSource.CompositeOutput => "EoC/Composite",
            SendSource.SimOutput => $"EoC/{SimNameOrFallback(s.index)}",
            SendSource.BiomeLayer => $"EoC/{(s.index >= 0 && s.index < ChannelNames.Length ? ChannelNames[s.index] : "Ch" + s.index)}",
            _ => "EoC/Stream",
        };

        private string SimNameOrFallback(int idx) =>
            (simManager != null && idx >= 0 && idx < simManager.simulations.Count && simManager.simulations[idx] != null)
                ? simManager.simulations[idx].SimName : "Sim" + idx;

        private void Teardown()
        {
            foreach (var live in _live)
            {
                if (live == null) continue;
                live.backend?.Dispose();
                if (live.extractRT != null) { live.extractRT.Release(); Destroy(live.extractRT); }
                if (live.scaleRT != null) { live.scaleRT.Release(); Destroy(live.scaleRT); }
                if (live.go != null) Destroy(live.go);
            }
            _live.Clear();
        }
    }
}

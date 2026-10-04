using System;
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    public enum SendSource { CompositeOutput, SimOutput, BiomeLayer, ComposerOutput }

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
        /// <summary>For SendSource.ComposerOutput — the Temporal Composer's output.</summary>
        [Tooltip("For SendSource.ComposerOutput — the Temporal Composer's output.")]
        public CompositeSequencer sequencer;
        public ShareResources resources = new();

        [Header("Streams")]
        public List<SendStream> streams = new();

        private class Live
        {
            public ITextureSenderBackend backend;
            public GameObject go;
            public RenderTexture extractRT;  // biome channel extract (biome res)
            public RenderTexture scaleRT;    // downscaled output
            public RenderTexture cropRT;     // full-res output cropped to NDI-legal size
            public bool warned;
            public bool enabled;             // stream.enabled when built; a toggle rebuilds
        }
        private readonly List<Live> _live = new();

        // Stream names per biome channel — single source of truth is BiomeChannel.Names
        // (was a hand-synced copy that silently drifted; reference it so it can't desync).
        private static string[] ChannelNames => BiomeChannel.Names;

        [Button("Rebuild Streams")]
        public void Rebuild()
        {
            // Streams only get a source in LateUpdate (Play). Built in edit mode they'd be scene
            // objects that orphan into Play beside the set OnEnable builds.
            if (!Application.isPlaying)
            {
                Debug.Log("[ExternalTextureSender] Streams are built in Play mode.");
                return;
            }
            Teardown();
            for (int i = 0; i < streams.Count; i++)
            {
                var s = streams[i];
                var live = new Live { enabled = s.enabled };
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

        // Field initializers are ignored when Unity adds a list element via the
        // inspector "+", so new streams come in with resolutionScale = 0 (below the
        // Range min). Coerce any invalid value back to full resolution.
        void OnValidate()
        {
            foreach (var s in streams)
                if (s != null && s.resolutionScale < 0.05f)
                    s.resolutionScale = 1f;
        }

        void LateUpdate()
        {
            if (simManager == null) return;
            if (_live.Count != streams.Count || EnabledToggled()) Rebuild();

            for (int i = 0; i < streams.Count; i++)
            {
                var s = streams[i];
                var live = _live[i];
                if (live == null || live.backend == null) continue;

                Texture src = ResolveSource(s, live);
                if (src == null) continue;

                bool ndi = s.protocol == ShareProtocol.NDI;
                if (s.resolutionScale < 0.999f)
                    src = Downscale(src, s.resolutionScale, live, ndi);
                else if (ndi)
                    src = CropForNdi(src, live);

                live.backend.SetSource(src);
            }
        }

        private bool EnabledToggled()
        {
            for (int i = 0; i < streams.Count; i++)
                if (streams[i].enabled != _live[i].enabled) return true;
            return false;
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

                case SendSource.ComposerOutput:
                    return sequencer != null ? sequencer.ComposerOutputTexture : null;

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

        // KlakNDI only encodes frames whose width is a multiple of 16 and height a multiple
        // of 8. NDI streams drop the remainder (at most 15 columns / 7 rows), centred.
        private static int NdiWidth(int w) => w >= 16 ? w - w % 16 : w;
        private static int NdiHeight(int h) => h >= 8 ? h - h % 8 : h;

        private Texture Downscale(Texture src, float scale, Live live, bool ndi)
        {
            int w = Mathf.Max(1, Mathf.CeilToInt(src.width * scale));
            int h = Mathf.Max(1, Mathf.CeilToInt(src.height * scale));
            int cw = ndi ? NdiWidth(w) : w;
            int ch = ndi ? NdiHeight(h) : h;
            if (live.scaleRT == null || live.scaleRT.width != cw || live.scaleRT.height != ch)
            {
                if (live.scaleRT != null) { live.scaleRT.Release(); Destroy(live.scaleRT); }
                live.scaleRT = new RenderTexture(cw, ch, 0) { name = "DownscaleSend" };
                live.scaleRT.Create();
            }
            if (cw == w && ch == h)
            {
                Graphics.Blit(src, live.scaleRT);
            }
            else
            {
                // Same scale as the uncropped frame, centre window only (no stretch).
                var window = new Vector2((float)cw / w, (float)ch / h);
                Graphics.Blit(src, live.scaleRT, window, (Vector2.one - window) * 0.5f);
            }
            return live.scaleRT;
        }

        private Texture CropForNdi(Texture src, Live live)
        {
            int w = NdiWidth(src.width), h = NdiHeight(src.height);
            if (w == src.width && h == src.height) return src;
            var fmt = src.graphicsFormat;
            if (live.cropRT == null || live.cropRT.width != w || live.cropRT.height != h
                || live.cropRT.graphicsFormat != fmt)
            {
                if (live.cropRT != null) { live.cropRT.Release(); Destroy(live.cropRT); }
                live.cropRT = new RenderTexture(w, h, 0, fmt) { name = "NdiCropSend" };
                live.cropRT.Create();
            }
            Graphics.CopyTexture(src, 0, 0, (src.width - w) / 2, (src.height - h) / 2, w, h,
                                 live.cropRT, 0, 0, 0, 0);
            return live.cropRT;
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
            SendSource.ComposerOutput => "EoC/Composer",
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
                if (live.cropRT != null) { live.cropRT.Release(); Destroy(live.cropRT); }
                if (live.go != null) Destroy(live.go);
            }
            _live.Clear();
        }
    }
}

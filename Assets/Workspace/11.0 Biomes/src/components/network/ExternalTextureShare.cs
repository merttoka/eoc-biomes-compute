using System;
using UnityEngine;

namespace Biomes
{
    public enum ShareProtocol { NDI, Syphon, Spout }

    /// <summary>Per-protocol Klak resources assets (assigned in inspector).
    /// All three types compile on every platform.</summary>
    [Serializable]
    public class ShareResources
    {
        public Klak.Spout.SpoutResources   spout;
        public Klak.Ndi.NdiResources       ndi;
        public Klak.Syphon.SyphonResources syphon;
    }

    public interface ITextureSenderBackend { void SetSource(Texture tex); void Dispose(); }
    public interface ITextureReceiverBackend { Texture Received { get; } void Dispose(); }

    /// <summary>The ONLY file referencing Klak.Ndi / Klak.Spout / Klak.Syphon.
    /// Wraps each protocol's sender/receiver MonoBehaviour behind a small interface.
    /// Platform gating is runtime (native plugin only works on its OS).</summary>
    public static class ExternalTextureShare
    {
        public static bool IsAvailable(ShareProtocol p) => p switch
        {
            ShareProtocol.NDI => true,
            ShareProtocol.Spout => Application.platform == RuntimePlatform.WindowsPlayer
                                 || Application.platform == RuntimePlatform.WindowsEditor,
            ShareProtocol.Syphon => Application.platform == RuntimePlatform.OSXPlayer
                                  || Application.platform == RuntimePlatform.OSXEditor,
            _ => false,
        };

        /// <summary>Live source names available to receive, for the given protocol.
        /// NDI names look like "&lt;MACHINE&gt; (SourceName)"; Syphon names look like
        /// "App/ServerName". Spout enumeration is Windows-only (empty elsewhere).</summary>
        public static string[] EnumerateSources(ShareProtocol p)
        {
            switch (p)
            {
                case ShareProtocol.NDI:
                    return System.Linq.Enumerable.ToArray(Klak.Ndi.NdiFinder.sourceNames);
                case ShareProtocol.Syphon:
                    return System.Linq.Enumerable.ToArray(Klak.Syphon.SyphonServerDirectory.ServerNames);
                default:
                    return System.Array.Empty<string>();
            }
        }

        public static ITextureSenderBackend CreateSender(GameObject host, ShareProtocol p, string name, ShareResources res)
        {
            if (!IsAvailable(p)) { Debug.LogWarning($"ExternalTextureShare: {p} unavailable on this platform — sender '{name}' skipped"); return null; }
            return p switch
            {
                ShareProtocol.NDI    => new NdiSenderBackend(host, name, res?.ndi),
                ShareProtocol.Spout  => new SpoutSenderBackend(host, name, res?.spout),
                ShareProtocol.Syphon => new SyphonSenderBackend(host, name, res?.syphon),
                _ => null,
            };
        }

        public static ITextureReceiverBackend CreateReceiver(GameObject host, ShareProtocol p, string name, ShareResources res)
        {
            if (!IsAvailable(p)) { Debug.LogWarning($"ExternalTextureShare: {p} unavailable on this platform — receiver '{name}' skipped"); return null; }
            return p switch
            {
                ShareProtocol.NDI    => new NdiReceiverBackend(host, name, res?.ndi),
                ShareProtocol.Spout  => new SpoutReceiverBackend(host, name, res?.spout),
                ShareProtocol.Syphon => new SyphonReceiverBackend(host, name),
                _ => null,
            };
        }

        // ─────────── NDI ───────────
        class NdiSenderBackend : ITextureSenderBackend
        {
            readonly Klak.Ndi.NdiSender _c;
            public NdiSenderBackend(GameObject host, string name, Klak.Ndi.NdiResources res)
            {
                _c = host.AddComponent<Klak.Ndi.NdiSender>();
                _c.captureMethod = Klak.Ndi.CaptureMethod.Texture;
                _c.ndiName = name;
                if (res != null) _c.SetResources(res);
                else Debug.LogWarning($"ExternalTextureShare: NDI sender '{name}' has no NdiResources assigned");
            }
            public void SetSource(Texture tex) => _c.sourceTexture = tex;
            public void Dispose() { if (_c != null) UnityEngine.Object.Destroy(_c); }
        }
        class NdiReceiverBackend : ITextureReceiverBackend
        {
            readonly Klak.Ndi.NdiReceiver _c;
            public NdiReceiverBackend(GameObject host, string name, Klak.Ndi.NdiResources res)
            {
                _c = host.AddComponent<Klak.Ndi.NdiReceiver>();
                _c.ndiName = name;
                if (res != null) _c.SetResources(res);
                else Debug.LogWarning($"ExternalTextureShare: NDI receiver '{name}' has no NdiResources assigned");
            }
            public Texture Received => _c.texture;
            public void Dispose() { if (_c != null) UnityEngine.Object.Destroy(_c); }
        }

        // ─────────── Spout ───────────
        class SpoutSenderBackend : ITextureSenderBackend
        {
            readonly Klak.Spout.SpoutSender _c;
            Texture _last;
            public SpoutSenderBackend(GameObject host, string name, Klak.Spout.SpoutResources res)
            {
                _c = host.AddComponent<Klak.Spout.SpoutSender>();
                _c.captureMethod = Klak.Spout.CaptureMethod.Texture;
                _c.spoutName = name;
                if (res != null) _c.SetResources(res);
                else Debug.LogWarning($"ExternalTextureShare: Spout sender '{name}' has no SpoutResources assigned");
            }
            // Only set on reference change — the source RT is stable and re-read each
            // frame by the sender; redundant sets can re-init the native sender.
            public void SetSource(Texture tex) { if (tex == _last) return; _c.sourceTexture = tex; _last = tex; }
            public void Dispose() { if (_c != null) UnityEngine.Object.Destroy(_c); }
        }
        class SpoutReceiverBackend : ITextureReceiverBackend
        {
            readonly Klak.Spout.SpoutReceiver _c;
            public SpoutReceiverBackend(GameObject host, string name, Klak.Spout.SpoutResources res)
            {
                _c = host.AddComponent<Klak.Spout.SpoutReceiver>();
                _c.sourceName = name;
                if (res != null) _c.SetResources(res);
                else Debug.LogWarning($"ExternalTextureShare: Spout receiver '{name}' has no SpoutResources assigned");
            }
            public Texture Received => _c.receivedTexture;
            public void Dispose() { if (_c != null) UnityEngine.Object.Destroy(_c); }
        }

        // ─────────── Syphon ───────────
        class SyphonSenderBackend : ITextureSenderBackend
        {
            readonly Klak.Syphon.SyphonServer _c;
            Texture _last;
            public SyphonSenderBackend(GameObject host, string name, Klak.Syphon.SyphonResources res)
            {
                _c = host.AddComponent<Klak.Syphon.SyphonServer>();
                _c.CaptureMethod = Klak.Syphon.CaptureMethod.Texture;
                _c.ServerName = name;
                _c.Resources = res;
                if (res == null) Debug.LogWarning($"ExternalTextureShare: Syphon server '{name}' has no SyphonResources assigned");
            }
            // CRITICAL: SyphonServer.SourceTexture's setter calls TeardownPlugin() →
            // StopAllCoroutines(), which kills the publish coroutine before it reaches
            // WaitForEndOfFrame. Setting every frame (our LateUpdate) means it never
            // publishes. Set only on reference change — the source RT is stable and
            // Syphon's coroutine re-blits its content each frame.
            public void SetSource(Texture tex) { if (tex == _last) return; _c.SourceTexture = tex; _last = tex; }
            public void Dispose() { if (_c != null) UnityEngine.Object.Destroy(_c); }
        }
        class SyphonReceiverBackend : ITextureReceiverBackend
        {
            readonly Klak.Syphon.SyphonClient _c;
            public SyphonReceiverBackend(GameObject host, string name)
            {
                _c = host.AddComponent<Klak.Syphon.SyphonClient>();
                _c.ServerName = name;
            }
            public Texture Received => _c.Texture;
            public void Dispose() { if (_c != null) UnityEngine.Object.Destroy(_c); }
        }
    }
}

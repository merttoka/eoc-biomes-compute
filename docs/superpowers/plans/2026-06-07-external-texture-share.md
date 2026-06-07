# External Texture Share Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Send selected Unity textures (composite, per-sim outputs, biome field layers) over Syphon/NDI/Spout with per-stream protocol + resolution scaling, and receive one external texture as sim influence — replacing the Spout-receive-only `ExternalInputProvider`.

**Architecture:** One backend file (`ExternalTextureShare`) isolates every Klak API call behind two small interfaces and a platform `IsAvailable` check. `ExternalTextureReceiver` (rewrite of the old provider) drives one receiver backend; `ExternalTextureSender` manages a list of send streams, resolving each to a 2D texture (extracting biome layers via a new `Biome.RenderChannelTo`) and pushing to its backend in `LateUpdate`.

**Tech Stack:** Unity (HDRP), C#, KlakNDI 2.1.6 / KlakSpout 2.0.3 / KlakSyphon 1.0.4 (all installed, all-platform asmdefs → no `#if` guards), EasyButtons. No test-runner harness; verification is Unity compile + in-editor.

**Spec:** `docs/superpowers/specs/2026-06-07-external-texture-share-design.md`

**Working directory:** `Assets/Workspace/11.0 Biomes/`

**Verified Klak API (used below):**
- NDI: `NdiSender { string ndiName; CaptureMethod captureMethod; Texture sourceTexture; void SetResources(NdiResources) }` (enum `Klak.Ndi.CaptureMethod{GameView,Camera,Texture}`); `NdiReceiver { string ndiName; void SetResources(NdiResources); RenderTexture texture }` (received frame).
- Spout: `SpoutSender { string spoutName; CaptureMethod captureMethod; Texture sourceTexture; void SetResources(SpoutResources) }` (enum `Klak.Spout.CaptureMethod`); `SpoutReceiver { string sourceName; RenderTexture receivedTexture; void SetResources(SpoutResources) }`.
- Syphon: `SyphonServer { string ServerName; CaptureMethod CaptureMethod; Texture SourceTexture; SyphonResources Resources }` (enum `Klak.Syphon.CaptureMethod`); `SyphonClient { string ServerName; Texture2D Texture }`.

**`.meta` note:** Unity generates `.cs.meta` on import. After a task that creates a `.cs`, focus Unity to recompile + generate the `.meta`, then `git add` both.

---

### Task 1: Backend — `ExternalTextureShare`

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureShare.cs`

- [ ] **Step 1: Create the backend file**

```csharp
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
            public SpoutSenderBackend(GameObject host, string name, Klak.Spout.SpoutResources res)
            {
                _c = host.AddComponent<Klak.Spout.SpoutSender>();
                _c.captureMethod = Klak.Spout.CaptureMethod.Texture;
                _c.spoutName = name;
                if (res != null) _c.SetResources(res);
                else Debug.LogWarning($"ExternalTextureShare: Spout sender '{name}' has no SpoutResources assigned");
            }
            public void SetSource(Texture tex) => _c.sourceTexture = tex;
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
            public SyphonSenderBackend(GameObject host, string name, Klak.Syphon.SyphonResources res)
            {
                _c = host.AddComponent<Klak.Syphon.SyphonServer>();
                _c.CaptureMethod = Klak.Syphon.CaptureMethod.Texture;
                _c.ServerName = name;
                _c.Resources = res;
                if (res == null) Debug.LogWarning($"ExternalTextureShare: Syphon server '{name}' has no SyphonResources assigned");
            }
            public void SetSource(Texture tex) => _c.SourceTexture = tex;
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
```

- [ ] **Step 2: Verify Unity compiles**

Focus Unity, wait for recompile. Expected: no Console errors. (Compile failures here mean a Klak member name differs from the verified list — fix in THIS file only, then recompile.)

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureShare.cs"*
git commit -m "feat: ExternalTextureShare backend (ndi/spout/syphon)"
```

---

### Task 2: `Biome.RenderChannelTo`

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs`

- [ ] **Step 1: Add the public extract method**

In `Biome.cs`, add this method just before the existing `private void RenderDebug()`:

```csharp
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
```

- [ ] **Step 2: Refactor the debug-grid loop to use it (DRY)**

In `RenderDebug()`, replace the per-channel grid block:

```csharp
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
```

with:

```csharp
            if (showDebugGrid && debugTextures != null)
            {
                for (int i = 0; i < BiomeChannel.Count; i++)
                {
                    RenderChannelTo(i, debugTextures[i]);
                    debugMaterials[i].SetTexture("_UnlitColorMap", debugTextures[i]);
                }
            }
```

- [ ] **Step 3: Verify Unity compiles**

Focus Unity, recompile. Expected: no Console errors. The debug grid still renders identically (same kernel, same dispatch).

- [ ] **Step 4: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs"
git commit -m "feat: Biome.RenderChannelTo (extract channel to 2D)"
```

---

### Task 3: `SimulationManager.CompositeOutputTexture`

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs`

- [ ] **Step 1: Expose the composite texture**

In `SimulationManager.cs`, find:

```csharp
        private int _simStepCount;
        public int SimStepCount => _simStepCount;
```

Add after it:

```csharp
        /// <summary>Final composited output texture (null until Reset()).</summary>
        public RenderTexture CompositeOutputTexture => compositeOutTex;
```

- [ ] **Step 2: Verify Unity compiles**

Focus Unity, recompile. Expected: no Console errors.

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs"
git commit -m "feat: SimulationManager.CompositeOutputTexture getter"
```

---

### Task 4: `ExternalTextureReceiver` (replaces `ExternalInputProvider`)

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureReceiver.cs`
- Delete: `Assets/Workspace/11.0 Biomes/src/components/network/ExternalInputProvider.cs` (currently uncommitted move) and the tracked `core/ExternalInputProvider.cs`
- Modify: `Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs`

- [ ] **Step 1: Create `ExternalTextureReceiver.cs`**

```csharp
using UnityEngine;
using UnityEngine.Video;

namespace Biomes
{
    /// <summary>Receives one external texture (Syphon/NDI/Spout) as sim influence.
    /// Replaces ExternalInputProvider. Retains the debug video-clip fallback + blur.</summary>
    public class ExternalTextureReceiver : MonoBehaviour
    {
        [Header("Receive")]
        public bool enableReceive = false;
        public ShareProtocol protocol = ShareProtocol.NDI;
        public string streamName = "";
        public ShareResources resources = new();

        [Header("Debug Input (texture replacement)")]
        [SerializeField] private bool m_DebugUseVideoInput = false;
        [SerializeField] private VideoClip m_DebugVideoClip = null;
        [SerializeField] private bool m_DebugLoopVideo = true;
        [SerializeField, Range(0f, 2f)] private float m_DebugPlaybackSpeed = 1f;
        [SerializeField] private bool m_DebugApplyGaussianBlur = false;
        [SerializeField, Range(1, 31)] private int m_BlurKernelSize = 9;
        [SerializeField, Range(0.1f, 10f)] private float m_BlurStrength = 2.5f;
        [SerializeField] private ComputeShader m_BlurCompute = null;

        private GPUResourceManager gpu;
        private VideoPlayer m_DebugVideoPlayer;
        private RenderTexture m_DebugVideoTexture;
        private RenderTexture m_DebugBlurTemp;
        private RenderTexture _outputTexture;

        private ITextureReceiverBackend _backend;
        private GameObject _backendGO;
        private ShareProtocol _activeProtocol;
        private string _activeName;

        private int m_BlurKernelH = -1;
        private int m_BlurKernelV = -1;
        private static readonly int s_BlurWidthID = Shader.PropertyToID("Width");
        private static readonly int s_BlurHeightID = Shader.PropertyToID("Height");
        private static readonly int s_BlurRadiusID = Shader.PropertyToID("Radius");
        private static readonly int s_BlurSigmaID = Shader.PropertyToID("Sigma");

        public RenderTexture OutputTexture => _outputTexture;

        public void Initialize()
        {
            Release();
            gpu = new GPUResourceManager();
        }

        public void UpdateInput()
        {
            if (m_DebugUseVideoInput) { UpdateDebugVideoInput(); return; }
            if (enableReceive) UpdateReceivedInput();
        }

        private void UpdateReceivedInput()
        {
            EnsureBackend();
            if (_backend == null) return;
            Texture rx = _backend.Received;
            if (rx == null || rx.width < 2 || rx.height < 2) return;
            EnsureOutputTexture(rx.width, rx.height);

            var cmd = new UnityEngine.Rendering.CommandBuffer();
            cmd.name = "Received Texture Copy";
            cmd.Blit(rx, _outputTexture);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        private void EnsureBackend()
        {
            if (_backend != null && _activeProtocol == protocol && _activeName == streamName) return;
            DisposeBackend();
            _backendGO = new GameObject($"Receiver_{protocol}");
            _backendGO.transform.SetParent(transform, false);
            _backendGO.SetActive(false);
            _backend = ExternalTextureShare.CreateReceiver(_backendGO, protocol, streamName, resources);
            _backendGO.SetActive(true);
            _activeProtocol = protocol;
            _activeName = streamName;
        }

        private void DisposeBackend()
        {
            _backend?.Dispose();
            _backend = null;
            if (_backendGO != null) { Destroy(_backendGO); _backendGO = null; }
        }

        private void UpdateDebugVideoInput()
        {
            InitializeDebugVideoIfNeeded();
            if (m_DebugVideoTexture == null) return;

            EnsureOutputTexture(m_DebugVideoTexture.width, m_DebugVideoTexture.height);

            var cmd = new UnityEngine.Rendering.CommandBuffer();
            cmd.name = "Debug Video Copy";
            cmd.Blit(m_DebugVideoTexture, _outputTexture);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();

            if (m_DebugApplyGaussianBlur && m_BlurCompute != null)
                ApplyGaussianBlur();
        }

        private void EnsureOutputTexture(int width, int height)
        {
            if (_outputTexture != null && _outputTexture.IsCreated() &&
                _outputTexture.width == width && _outputTexture.height == height)
                return;

            if (_outputTexture != null)
            {
                _outputTexture.Release();
                Object.Destroy(_outputTexture);
            }

            _outputTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            _outputTexture.name = "ExternalInfluenceOutput";
            _outputTexture.enableRandomWrite = true;
            _outputTexture.useMipMap = false;
            _outputTexture.autoGenerateMips = false;
            _outputTexture.filterMode = FilterMode.Bilinear;
            _outputTexture.wrapMode = TextureWrapMode.Repeat;
            _outputTexture.Create();
            gpu.Track(_outputTexture);
        }

        private void EnsureBlurKernels()
        {
            if (m_BlurCompute == null) return;
            if (m_BlurKernelH < 0) m_BlurKernelH = m_BlurCompute.FindKernel("BlurHorizontal");
            if (m_BlurKernelV < 0) m_BlurKernelV = m_BlurCompute.FindKernel("BlurVertical");
        }

        private void InitializeDebugVideoIfNeeded()
        {
            if (!m_DebugUseVideoInput) return;
            if (m_DebugVideoPlayer == null)
            {
                m_DebugVideoPlayer = gameObject.GetComponent<VideoPlayer>();
                if (m_DebugVideoPlayer == null) m_DebugVideoPlayer = gameObject.AddComponent<VideoPlayer>();

                m_DebugVideoPlayer.playOnAwake = false;
                m_DebugVideoPlayer.renderMode = VideoRenderMode.RenderTexture;
                m_DebugVideoPlayer.source = VideoSource.VideoClip;
                m_DebugVideoPlayer.audioOutputMode = VideoAudioOutputMode.None;
                m_DebugVideoPlayer.isLooping = m_DebugLoopVideo;
                m_DebugVideoPlayer.skipOnDrop = true;
            }

            m_DebugVideoPlayer.clip = m_DebugVideoClip;
            m_DebugVideoPlayer.playbackSpeed = Mathf.Max(0.01f, m_DebugPlaybackSpeed);

            if (m_DebugVideoClip != null)
            {
                int vw = (int)m_DebugVideoClip.width;
                int vh = (int)m_DebugVideoClip.height;

                if (m_DebugVideoTexture == null || !m_DebugVideoTexture.IsCreated() ||
                    m_DebugVideoTexture.width != vw || m_DebugVideoTexture.height != vh)
                {
                    if (m_DebugVideoTexture != null)
                    {
                        m_DebugVideoTexture.Release();
                        Destroy(m_DebugVideoTexture);
                    }

                    m_DebugVideoTexture = new RenderTexture(vw, vh, 0, RenderTextureFormat.ARGB32);
                    m_DebugVideoTexture.name = "DebugVideoInput";
                    m_DebugVideoTexture.enableRandomWrite = false;
                    m_DebugVideoTexture.useMipMap = false;
                    m_DebugVideoTexture.autoGenerateMips = false;
                    m_DebugVideoTexture.filterMode = FilterMode.Bilinear;
                    m_DebugVideoTexture.wrapMode = TextureWrapMode.Clamp;
                    m_DebugVideoTexture.Create();
                    gpu.Track(m_DebugVideoTexture);
                }

                m_DebugVideoPlayer.targetTexture = m_DebugVideoTexture;

                if (!m_DebugVideoPlayer.isPlaying)
                    m_DebugVideoPlayer.Play();
            }
        }

        private void ApplyGaussianBlur()
        {
            EnsureBlurKernels();
            if (_outputTexture == null) return;

            if (m_DebugBlurTemp == null || !m_DebugBlurTemp.IsCreated() ||
                m_DebugBlurTemp.width != _outputTexture.width ||
                m_DebugBlurTemp.height != _outputTexture.height)
            {
                if (m_DebugBlurTemp != null)
                {
                    m_DebugBlurTemp.Release();
                    Destroy(m_DebugBlurTemp);
                }
                m_DebugBlurTemp = new RenderTexture(_outputTexture.width, _outputTexture.height, 0, RenderTextureFormat.ARGB32);
                m_DebugBlurTemp.name = "DebugVideoBlurTemp";
                m_DebugBlurTemp.enableRandomWrite = true;
                m_DebugBlurTemp.useMipMap = false;
                m_DebugBlurTemp.autoGenerateMips = false;
                m_DebugBlurTemp.filterMode = FilterMode.Bilinear;
                m_DebugBlurTemp.wrapMode = TextureWrapMode.Clamp;
                m_DebugBlurTemp.Create();
                gpu.Track(m_DebugBlurTemp);
            }

            int width = _outputTexture.width;
            int height = _outputTexture.height;
            int radius = Mathf.Clamp(m_BlurKernelSize / 2, 0, 32);
            float sigma = Mathf.Max(0.01f, m_BlurStrength);

            m_BlurCompute.SetInt(s_BlurWidthID, width);
            m_BlurCompute.SetInt(s_BlurHeightID, height);
            m_BlurCompute.SetInt(s_BlurRadiusID, radius);
            m_BlurCompute.SetFloat(s_BlurSigmaID, sigma);
            m_BlurCompute.SetTexture(m_BlurKernelH, "Src", _outputTexture);
            m_BlurCompute.SetTexture(m_BlurKernelH, "Dest", m_DebugBlurTemp);
            {
                m_BlurCompute.GetKernelThreadGroupSizes(m_BlurKernelH, out uint tx, out uint ty, out uint _);
                m_BlurCompute.Dispatch(m_BlurKernelH, Mathf.CeilToInt(width / (float)tx), Mathf.CeilToInt(height / (float)ty), 1);
            }

            m_BlurCompute.SetTexture(m_BlurKernelV, "Src", m_DebugBlurTemp);
            m_BlurCompute.SetTexture(m_BlurKernelV, "Dest", _outputTexture);
            {
                m_BlurCompute.GetKernelThreadGroupSizes(m_BlurKernelV, out uint tx, out uint ty, out uint _);
                m_BlurCompute.Dispatch(m_BlurKernelV, Mathf.CeilToInt(width / (float)tx), Mathf.CeilToInt(height / (float)ty), 1);
            }
        }

        public void Release()
        {
            if (m_DebugVideoPlayer != null && m_DebugVideoPlayer.isPlaying)
                m_DebugVideoPlayer.Stop();

            DisposeBackend();
            gpu?.ReleaseAll();
            gpu = null;
            _outputTexture = null;
            m_DebugVideoTexture = null;
            m_DebugBlurTemp = null;
        }

        void OnDestroy() => Release();
    }
}
```

- [ ] **Step 2: Remove the old provider files**

```bash
cd "Assets/Workspace/11.0 Biomes/src/components"
git rm -f --ignore-unmatch "core/ExternalInputProvider.cs" "core/ExternalInputProvider.cs.meta"
rm -f "network/ExternalInputProvider.cs" "network/ExternalInputProvider.cs.meta"
cd -
```

- [ ] **Step 3: Retype the field in `SimulationManager`**

In `SimulationManager.cs`, change:

```csharp
        [SerializeField] private ExternalInputProvider externalInput;
```

to:

```csharp
        [SerializeField] private ExternalTextureReceiver externalInput;
```

(All call sites — `externalInput.Initialize()`, `.UpdateInput()`, `.OutputTexture`, `.Release()` — are unchanged; the public surface is identical.)

- [ ] **Step 4: Verify Unity compiles**

Focus Unity, recompile. Expected: no Console errors. Note: the old `ExternalInputProvider` component instance in `TestScene.unity` will show as "missing script" — that's expected and fixed by re-adding the component in Task 6.

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureReceiver.cs"* \
        "Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/ExternalInputProvider.cs" \
        "Assets/Workspace/11.0 Biomes/src/components/core/ExternalInputProvider.cs.meta"
git commit -m "feat: ExternalTextureReceiver replaces ExternalInputProvider"
```

---

### Task 5: `ExternalTextureSender`

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureSender.cs`

- [ ] **Step 1: Create the sender component**

```csharp
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
```

- [ ] **Step 2: Verify Unity compiles**

Focus Unity, recompile. Expected: no Console errors. `ExternalTextureSender` is addable as a component with a `streams` list and a "Rebuild Streams" button.

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureSender.cs"*
git commit -m "feat: ExternalTextureSender (multi-stream send)"
```

---

### Task 6: In-editor verification + docs

**Files:** `docs/ARCHITECTURE.md` (+ scene wiring in editor)

- [ ] **Step 1: Re-wire the receiver in the scene**

In `TestScene.unity`: the old `ExternalInputProvider` component shows as "missing script". Remove it; add `ExternalTextureReceiver` to the same GameObject. Re-assign it to the `SimulationManager.externalInput` field. Assign the protocol `NdiResources`/`SpoutResources`/`SyphonResources` into its `resources` (the package default assets, e.g. from the KlakNDI/KlakSpout package folders).

- [ ] **Step 2: Verify send (NDI composite)**

Add `ExternalTextureSender` to a GameObject; assign `simManager` and the `resources` (NdiResources at minimum). Add one stream: `source=CompositeOutput, protocol=NDI`, leave `streamName` blank. Click **Rebuild Streams**, enter Play. In a NDI monitor (NDI Tools / TouchDesigner NDI In), confirm a source named `EoC/Composite` appears showing the composite.

- [ ] **Step 3: Verify sim + biome-layer streams**

Add two more streams: `SimOutput index=0` and `BiomeLayer index=0` (Nutrient), both NDI. Rebuild, Play. Confirm `EoC/<SimName>` and `EoC/Nutrient` appear; the biome stream shows the nutrient field. Toggle the biome stream `enabled` off + Rebuild → confirm its source disappears (extract stops).

- [ ] **Step 4: Verify resolution scale**

Set the composite stream `resolutionScale = 0.5`, Rebuild, Play → confirm the received NDI frame is half the source resolution.

- [ ] **Step 5: Verify Syphon + Spout gating (macOS)**

Switch the composite stream to `protocol=Syphon`, Rebuild → confirm `EoC/Composite` appears in a Syphon viewer (e.g. Simple Syphon / TouchDesigner Syphon In). Switch to `protocol=Spout` → confirm a one-time Console warning "Spout unavailable on this platform" and no crash.

- [ ] **Step 6: Verify receive**

On `ExternalTextureReceiver`: set `enableReceive=true`, `protocol=NDI`, `streamName` to a known NDI source (e.g. an NDI test pattern or TD NDI Out). Confirm the source feeds the sim's external influence (visible if a sim's Umwelt uses it / via the composite debug overlay). Confirm the debug video-clip fallback still works when `m_DebugUseVideoInput=true`.

- [ ] **Step 7: Update `docs/ARCHITECTURE.md`**

In `docs/ARCHITECTURE.md` §3.8, replace the `ExternalInputProvider` bullet with:

```markdown
- **`ExternalTextureReceiver`** — receives one external texture (Syphon/NDI/Spout, or a
  debug video clip) into an `OutputTexture` fed to sims as external influence. Replaces
  `ExternalInputProvider`.
- **`ExternalTextureSender`** — sends selected textures (composite, per-sim outputs,
  biome channel layers) out over Syphon/NDI/Spout; per-stream protocol + resolution
  scale. Biome layers extracted via `Biome.RenderChannelTo` only while enabled.
- **`ExternalTextureShare`** — backend isolating all Klak (NDI/Spout/Syphon) API behind
  one interface; `IsAvailable` gates protocols by platform at runtime.
```

Update the §1 topology note: Unity ↔ TD video share is now implemented (Syphon/NDI out, one receive), not aspirational. Commit:

```bash
git add docs/ARCHITECTURE.md
git commit -m "docs: external texture share in ARCHITECTURE"
```

---

## Self-Review

**Spec coverage:**
- Split sender/receiver → Tasks 4, 5 ✓
- Send sources composite/sim/biome → `SendSource` + `ResolveSource` (Task 5) ✓
- Per-stream protocol, platform-gated → `SendStream.protocol` + `IsAvailable` (Tasks 1, 5) ✓
- Biome extract only when enabled → `ResolveSource` runs per enabled stream; disabled streams have no backend (Task 5) ✓
- Per-stream resolution scale → `resolutionScale` + `Downscale` (Task 5) ✓
- Default stream names → `DefaultName` (Task 5) ✓
- KlakSyphon installed, server needs Resources → `ShareResources.syphon`, Syphon backend sets `Resources` (Task 1) ✓
- No `#if` guards; runtime gating → `IsAvailable` via `Application.platform` (Task 1) ✓
- Receive one texture, keep debug fallback → `ExternalTextureReceiver` (Task 4) ✓
- Host edits: `Biome.RenderChannelTo`, `SimulationManager.CompositeOutputTexture`, retype `externalInput` → Tasks 2, 3, 4 ✓
- Error handling (unavailable/missing-resource/null-source/OOR index) → warnings in Tasks 1, 5 ✓

**Placeholder scan:** none — all code complete, no TBD/TODO.

**Type consistency:** `ShareProtocol`, `ShareResources`, `ITextureSenderBackend.SetSource`/`Dispose`, `ITextureReceiverBackend.Received`/`Dispose`, `ExternalTextureShare.CreateSender`/`CreateReceiver`/`IsAvailable`, `SendSource`, `SendStream`, `Biome.RenderChannelTo`, `SimulationManager.CompositeOutputTexture`, `ExternalTextureReceiver` — each defined once and referenced consistently across tasks. `Biome.RezX`/`RezY`, `SimulationBase.GetOutputTexture`, `SimulationManager.simulations`/`biome` are existing public members (verified).

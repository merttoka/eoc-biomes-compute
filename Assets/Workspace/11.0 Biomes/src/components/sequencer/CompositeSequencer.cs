using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    public enum CellBlendMode { Overlay = 0, Replace = 1 }

    /// <summary>
    /// Owns the show output texture (composerOutTex) and composites, every rendered
    /// frame AFTER SimulationManager.Render(): base = sim composite, then biome cells,
    /// then scattered patches, then optional debug outlines. Timeline track mixers push
    /// per-frame draw state via SetBaseWeight/PushCell/PushPatch; state clears each frame.
    /// Clear-in-place rule (ADR-0008): composerOutTex is allocated once and reused so the
    /// ExternalTextureSender's native server never tears down; realloc only on rez change.
    /// </summary>
    [DefaultExecutionOrder(1000)]   // after SimulationManager.LateUpdate → Render()
    public class CompositeSequencer : MonoBehaviour
    {
        private struct RectDraw
        {
            public Texture src;
            public Rect dst;        // normalized
            public Rect srcRect;    // normalized
            public float weight;
            public int mode;        // CellBlendMode, or 1 for patches (alpha lerp)
        }

        [Header("References")]
        public SimulationManager simManager;
        public ComputeShader sequencerCS;
        [Tooltip("Display material re-pointed at composerOutTex (HDRP Unlit _UnlitColorMap).")]
        public Material composerOutMat;
        [Tooltip("Receiver #2: the StreamDiffusion return stream (Spout from TouchDesigner).")]
        public ExternalTextureReceiver diffusionReturn;

        [Header("Composer")]
        [Tooltip("Composer rez = sim composite rez × this. 1 keeps ScreenLayout pixel rects valid.")]
        [Range(0.25f, 1f)] public float composerResScale = 1f;

        [Header("Debug overlay (annotation layer — OFF for show)")]
        public bool debugOutlines = false;
        public Color debugCellColor = new(0f, 1f, 0.6f, 1f);
        public Color debugPatchColor = new(1f, 0.4f, 0f, 1f);

        public const int MaxCells = 4;
        public const int MaxPatchDraws = 128;

        private readonly List<RectDraw> _cells = new(MaxCells);
        private readonly List<RectDraw> _patches = new(MaxPatchDraws);
        private float _baseWeight = 1f;

        private GPUResourceManager gpu;
        private RenderTexture _composerTex;
        private int _baseKernel = -1, _rectKernel = -1, _debugKernel = -1;
        private int _rezX, _rezY;
        private int _allocRezX = -1, _allocRezY = -1;

        private static readonly int s_ComposerRezXID = Shader.PropertyToID("composerRezX");
        private static readonly int s_ComposerRezYID = Shader.PropertyToID("composerRezY");
        private static readonly int s_ComposerOutID = Shader.PropertyToID("composerOut");
        private static readonly int s_BaseTexID = Shader.PropertyToID("baseTex");
        private static readonly int s_BaseWeightID = Shader.PropertyToID("baseWeight");
        private static readonly int s_RectSrcID = Shader.PropertyToID("rectSrc");
        private static readonly int s_DstRectID = Shader.PropertyToID("dstRect");
        private static readonly int s_SrcRectID = Shader.PropertyToID("srcRect");
        private static readonly int s_RectWeightID = Shader.PropertyToID("rectWeight");
        private static readonly int s_BlendModeID = Shader.PropertyToID("blendMode");
        private static readonly int s_DebugColorID = Shader.PropertyToID("debugColor");

        /// <summary>Show output. Null until first play-mode LateUpdate.</summary>
        public RenderTexture ComposerOutputTexture => _composerTex;

        // ── Per-frame state (pushed by Timeline mixers, cleared after render) ──

        /// <summary>0 lets a Replace cell own the frame; default 1 restores each frame.</summary>
        public void SetBaseWeight(float w) => _baseWeight = Mathf.Clamp01(w);

        public void PushCell(Texture src, Rect dstNorm, float weight, CellBlendMode mode)
        {
            if (src == null || weight <= 0f || _cells.Count >= MaxCells) return;
            _cells.Add(new RectDraw
            {
                src = src, dst = dstNorm, srcRect = new Rect(0, 0, 1, 1),
                weight = Mathf.Clamp01(weight), mode = (int)mode,
            });
        }

        public void PushPatch(Texture src, Rect dstNorm, Rect srcNorm, float alpha)
        {
            if (src == null || alpha <= 0f || _patches.Count >= MaxPatchDraws) return;
            _patches.Add(new RectDraw
            {
                src = src, dst = dstNorm, srcRect = srcNorm,
                weight = Mathf.Clamp01(alpha), mode = 1,   // patches always alpha-lerp
            });
        }

        // ── Render ──

        void LateUpdate()
        {
            if (!Application.isPlaying || simManager == null || sequencerCS == null)
            {
                ClearFrameState();
                return;
            }

            var baseTex = simManager.CompositeOutputTexture;
            if (baseTex == null) { ClearFrameState(); return; }   // pre-Reset

            EnsureAllocated();

            sequencerCS.SetInt(s_ComposerRezXID, _rezX);
            sequencerCS.SetInt(s_ComposerRezYID, _rezY);

            // 1. base
            sequencerCS.SetTexture(_baseKernel, s_BaseTexID, baseTex);
            sequencerCS.SetFloat(s_BaseWeightID, _baseWeight);
            sequencerCS.SetTexture(_baseKernel, s_ComposerOutID, _composerTex);
            DispatchFull(_baseKernel);

            // 2. cells, then 3. patches — same kernel, one small dispatch per rect
            for (int i = 0; i < _cells.Count; i++) DispatchRect(_rectKernel, _cells[i]);
            for (int i = 0; i < _patches.Count; i++) DispatchRect(_rectKernel, _patches[i]);

            // 4. optional annotation outlines
            if (debugOutlines)
            {
                for (int i = 0; i < _cells.Count; i++) DispatchDebug(_cells[i].dst, debugCellColor);
                for (int i = 0; i < _patches.Count; i++) DispatchDebug(_patches[i].dst, debugPatchColor);
            }

            if (composerOutMat != null)
                composerOutMat.SetTexture("_UnlitColorMap", _composerTex);

            ClearFrameState();
        }

        private void DispatchFull(int kernel)
        {
            sequencerCS.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out _);
            sequencerCS.Dispatch(kernel,
                Mathf.CeilToInt((float)_rezX / wx), Mathf.CeilToInt((float)_rezY / wy), 1);
        }

        private void DispatchRect(int kernel, in RectDraw d)
        {
            sequencerCS.SetTexture(kernel, s_RectSrcID, d.src);
            sequencerCS.SetTexture(kernel, s_ComposerOutID, _composerTex);
            sequencerCS.SetVector(s_DstRectID, new Vector4(d.dst.x, d.dst.y, d.dst.width, d.dst.height));
            sequencerCS.SetVector(s_SrcRectID, new Vector4(d.srcRect.x, d.srcRect.y, d.srcRect.width, d.srcRect.height));
            sequencerCS.SetFloat(s_RectWeightID, d.weight);
            sequencerCS.SetInt(s_BlendModeID, d.mode);
            int px = Mathf.Max(1, Mathf.CeilToInt(d.dst.width * _rezX));
            int py = Mathf.Max(1, Mathf.CeilToInt(d.dst.height * _rezY));
            sequencerCS.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out _);
            sequencerCS.Dispatch(kernel, Mathf.CeilToInt((float)px / wx), Mathf.CeilToInt((float)py / wy), 1);
        }

        private void DispatchDebug(Rect dst, Color color)
        {
            sequencerCS.SetTexture(_debugKernel, s_ComposerOutID, _composerTex);
            sequencerCS.SetVector(s_DstRectID, new Vector4(dst.x, dst.y, dst.width, dst.height));
            sequencerCS.SetVector(s_DebugColorID, color);
            int px = Mathf.Max(1, Mathf.CeilToInt(dst.width * _rezX));
            int py = Mathf.Max(1, Mathf.CeilToInt(dst.height * _rezY));
            sequencerCS.GetKernelThreadGroupSizes(_debugKernel, out uint wx, out uint wy, out _);
            sequencerCS.Dispatch(_debugKernel, Mathf.CeilToInt((float)px / wx), Mathf.CeilToInt((float)py / wy), 1);
        }

        private void ClearFrameState()
        {
            _cells.Clear();
            _patches.Clear();
            _baseWeight = 1f;
        }

        private void EnsureAllocated()
        {
            _rezX = Mathf.Max(8, Mathf.RoundToInt(simManager.rezX * composerResScale));
            _rezY = Mathf.Max(8, Mathf.RoundToInt(simManager.rezY * composerResScale));
            if (gpu != null && _rezX == _allocRezX && _rezY == _allocRezY) return;

            Release();
            gpu = new GPUResourceManager();
            _composerTex = gpu.CreateTexture2D(_rezX, _rezY, FilterMode.Trilinear,
                RenderTextureFormat.ARGBHalf, name: "composer_out");
            _baseKernel = sequencerCS.FindKernel("BaseKernel");
            _rectKernel = sequencerCS.FindKernel("RectBlendKernel");
            _debugKernel = sequencerCS.FindKernel("DebugRectKernel");
            _allocRezX = _rezX; _allocRezY = _rezY;
        }

        public void Release()
        {
            gpu?.ReleaseAll();
            gpu = null;
            _composerTex = null;
            _allocRezX = _allocRezY = -1;
        }

        void OnDestroy() => Release();
        void OnDisable() => Release();
    }
}

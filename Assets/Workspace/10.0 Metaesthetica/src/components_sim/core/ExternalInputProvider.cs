using UnityEngine;
using UnityEngine.Video;

namespace Metaesthetica
{
    public class ExternalInputProvider : MonoBehaviour
    {
        [Header("Spout")]
        public bool sendSpout = false;
        [SerializeField] private Klak.Spout.SpoutResources _spoutResources = null;
        [SerializeField] private Klak.Spout.SpoutReceiver spoutReceiver = null;

        [Header("Debug Input (Spout Replacement)")]
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
            if (m_DebugUseVideoInput)
                UpdateDebugVideoInput();
            else if (spoutReceiver != null && sendSpout)
                UpdateSpoutInput();
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

        private void UpdateSpoutInput()
        {
            if (_spoutResources == null)
            {
                Debug.LogError("ExternalInputProvider: _spoutResources not assigned.");
                return;
            }

            spoutReceiver.SetResources(_spoutResources);
            Texture receivedTex = spoutReceiver.receivedTexture;
            if (receivedTex == null) return;

            EnsureOutputTexture(receivedTex.width, receivedTex.height);

            var cmd = new UnityEngine.Rendering.CommandBuffer();
            cmd.name = "Spout Texture Copy";
            cmd.Blit(receivedTex, _outputTexture);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
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

            // Horizontal pass
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

            // Vertical pass
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

            gpu?.ReleaseAll();
            gpu = null;
            _outputTexture = null;
            m_DebugVideoTexture = null;
            m_DebugBlurTemp = null;
        }

        void OnDestroy() => Release();
    }
}

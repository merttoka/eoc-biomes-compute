using System;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Single owner of the neuron-firing blob. The OSC frame index (set via SetFrame,
    /// thread-safe) selects which row of the blob is shown; a decay envelope fades
    /// firing to quiet when no new index arrives. Produces a shared float buffer
    /// (one value per neuron, already scaled by the envelope) that SimulationManager
    /// broadcasts to every sim each step.
    /// </summary>
    public class NeuronFiringSource : MonoBehaviour
    {
        [Tooltip("Path under Assets/StreamingAssets, produced by tools/firing_csv_to_f16.py")]
        public string firingBlobFile = "biomes11/termite_firing.f16";

        [Tooltip("Seconds for firing intensity to fade to zero when no /index arrives")]
        public float firingDecaySeconds = 0.5f;

        [Tooltip("Log frame changes to the Console (main thread)")]
        public bool debugLog = false;

        // Blob (loaded once)
        private ushort[] _firingHalf;   // flat float16 bits: frame*_neuronCount + neuron
        private int _neuronCount;
        private int _frameCount;

        // OSC-driven (written on the receive thread)
        private volatile int _targetFrame;
        private volatile bool _dirty;

        // Runtime state (main thread)
        private int _currentFrame = -1;
        private float _intensity;
        private float _lastTime;
        private float[] _row;       // decoded current frame
        private float[] _scaled;    // _row * _intensity, uploaded each step
        private ComputeBuffer _buffer;

        public ComputeBuffer Buffer => _buffer;
        public int NeuronCount => _neuronCount;
        public int FrameCount => _frameCount;
        public int CurrentFrame => _currentFrame;
        public float Intensity => _intensity;

        /// <summary>Thread-safe: called from the OSC receive thread.</summary>
        public void SetFrame(int frame)
        {
            _targetFrame = frame;
            _dirty = true;
        }

        public void Initialize()
        {
            LoadBlob();
            int n = Mathf.Max(1, _neuronCount);
            _row = new float[n];
            _scaled = new float[n];
            ReleaseBuffer();
            _buffer = new ComputeBuffer(n, sizeof(float));
            _buffer.SetData(new float[n]);
            _currentFrame = -1;
            _intensity = 0f;
            _lastTime = Time.unscaledTime;
            _dirty = false;
        }

        /// <summary>Called once per sim step by SimulationManager (main thread).</summary>
        public void UpdateFiring()
        {
            if (_firingHalf == null || _neuronCount <= 0 || _buffer == null) return;

            if (_dirty)
            {
                _dirty = false;
                _currentFrame = Mathf.Clamp(_targetFrame, 0, _frameCount - 1);
                int baseIdx = _currentFrame * _neuronCount;
                for (int i = 0; i < _neuronCount; i++)
                    _row[i] = Mathf.HalfToFloat(_firingHalf[baseIdx + i]);
                _intensity = 1f;
                if (debugLog) Debug.Log($"NeuronFiringSource: frame={_currentFrame}");
            }

            // Decay by real wall-clock time (advances once per rendered frame even if
            // Step() runs multiple times per frame: Time.unscaledTime is constant within a frame).
            float now = Time.unscaledTime;
            float dt = Mathf.Max(0f, now - _lastTime);
            _lastTime = now;
            if (_intensity > 0f && firingDecaySeconds > 0f)
                _intensity = Mathf.Max(0f, _intensity - dt / firingDecaySeconds);

            for (int i = 0; i < _neuronCount; i++)
                _scaled[i] = _row[i] * _intensity;
            _buffer.SetData(_scaled);
        }

        private void LoadBlob()
        {
            _firingHalf = null; _frameCount = 0; _neuronCount = 0;
            if (string.IsNullOrEmpty(firingBlobFile)) return;

            string path = System.IO.Path.Combine(Application.streamingAssetsPath, firingBlobFile);
            if (!System.IO.File.Exists(path))
            {
                Debug.LogWarning($"NeuronFiringSource: blob not found at {path} (run tools/firing_csv_to_f16.py)");
                return;
            }

            using var br = new System.IO.BinaryReader(System.IO.File.OpenRead(path));
            var magic = br.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != (byte)'T' || magic[1] != (byte)'F'
                || magic[2] != (byte)'R' || magic[3] != (byte)'1')
            {
                Debug.LogWarning("NeuronFiringSource: blob has bad magic; ignoring");
                return;
            }
            _neuronCount = (int)br.ReadUInt32();
            _frameCount  = (int)br.ReadUInt32();
            long count = (long)_frameCount * _neuronCount;
            if (count <= 0 || count > int.MaxValue / 2)
            {
                Debug.LogWarning($"NeuronFiringSource: blob size out of range ({_frameCount}x{_neuronCount})");
                _frameCount = 0; _neuronCount = 0;
                return;
            }
            var bytes = br.ReadBytes((int)(count * 2));
            _firingHalf = new ushort[count];
            System.Buffer.BlockCopy(bytes, 0, _firingHalf, 0, bytes.Length);
        }

        private void ReleaseBuffer() { _buffer?.Release(); _buffer = null; }
        void OnDisable() => ReleaseBuffer();
        void OnDestroy() => ReleaseBuffer();

        [Button]
        public void TestLoadAndLog()
        {
            Initialize();
            Debug.Log($"NeuronFiringSource: neurons={_neuronCount} frames={_frameCount}");
        }
    }
}

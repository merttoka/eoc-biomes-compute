using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using EasyButtons;

namespace Metaesthetica
{
    /// <summary>
    /// Reads neuron time series CSV and generates a spatial influence texture
    /// from firing rates. Each neuron splats a gaussian blob at its position,
    /// with the RGBA channel determined by its label (label 0→R, 1→G, 2→B, 3→A).
    /// This texture feeds directly into PhysarumSim's externalInfluenceTex.
    /// </summary>
    public class NeuronFiringDriver : MonoBehaviour
    {
        [Header("Data")]
        [Tooltip("Time series CSV with t_spk_mat, rate_mat, rates_norm columns")]
        public TextAsset timeSeriesCsv;
        [Tooltip("Labels CSV (backbone_labels,xy_norm_0,xy_norm_1)")]
        public TextAsset labelsCsv;

        [Header("Playback")]
        [Tooltip("Original data duration in seconds")]
        public float dataDuration = 180f;
        [Tooltip("Target playback duration in seconds")]
        public float playbackDuration = 750f;
        public bool loop = true;
        public bool playing = true;

        [Header("Simulation")]
        [Tooltip("PhysarumSim to drive — sets externalInfluenceTex")]
        public PhysarumSim targetSim;

        [Header("Splat Settings")]
        public ComputeShader splatCS;
        [Tooltip("Gaussian radius in pixels")]
        [Range(1f, 200f)] public float splatRadius = 60f;
        [Tooltip("Gaussian sigma (spread)")]
        [Range(0.5f, 100f)] public float splatSigma = 25f;
        [Tooltip("Intensity multiplier")]
        [Range(0f, 5f)] public float splatStrength = 1f;
        [Tooltip("Temporal smoothing — blends previous frame's texture (0 = no trail, 1 = infinite trail)")]
        [Range(0f, 0.99f)] public float temporalSmoothing = 0.85f;

        [Header("Neuron Scale")]
        [Tooltip("How much of the canvas neurons fill (matches PhysarumSim.neuronScale)")]
        public Vector2 neuronScale = new Vector2(0.8f, 0.9f);

        [Header("Debug")]
        public bool logActivity = false;
        [SerializeField, Range(0f, 1f)] private float _debugProgress;

        // Parsed data
        private float[][] _ratesNorm; // [timestep][neuronIndex]
        private Vector2[] _neuronPositions; // pixel coords in sim space
        private int[] _neuronLabels;
        private int _neuronCount;
        private int _timestepCount;

        // GPU resources
        private RenderTexture _splatTex;
        private ComputeBuffer _positionsBuffer;
        private ComputeBuffer _ratesBuffer;
        private ComputeBuffer _labelsBuffer;
        private float[] _currentRates;
        private int _clearKernel;
        private int _decayKernel;
        private int _splatKernel;
        private GPUResourceManager _gpu;

        // Runtime
        private float _playbackTime;
        private bool _dataReady;

        // Shader IDs
        private static readonly int s_RezX = Shader.PropertyToID("rezX");
        private static readonly int s_RezY = Shader.PropertyToID("rezY");
        private static readonly int s_SplatTex = Shader.PropertyToID("splatTex");
        private static readonly int s_NeuronPositions = Shader.PropertyToID("neuronPositions");
        private static readonly int s_NeuronRates = Shader.PropertyToID("neuronRates");
        private static readonly int s_NeuronLabels = Shader.PropertyToID("neuronLabels");
        private static readonly int s_NeuronCount = Shader.PropertyToID("neuronCount");
        private static readonly int s_SplatRadius = Shader.PropertyToID("splatRadius");
        private static readonly int s_SplatSigma = Shader.PropertyToID("splatSigma");
        private static readonly int s_SplatStrength = Shader.PropertyToID("splatStrength");

        public RenderTexture OutputTexture => _splatTex;
        public float PlaybackProgress => _dataReady ? _playbackTime / playbackDuration : 0f;

        [Button("Parse Data")]
        public void ParseData()
        {
            _dataReady = false;

            if (timeSeriesCsv == null || labelsCsv == null)
            {
                Debug.LogError("[NeuronFiring] Assign both CSVs.");
                return;
            }

            ParseLabels();
            ParseTimeSeries();

            _currentRates = new float[_neuronCount];
            _playbackTime = 0f;
            _dataReady = true;

            Debug.Log($"[NeuronFiring] Parsed: {_neuronCount} neurons, {_timestepCount} timesteps, stretch {dataDuration}s → {playbackDuration}s");
        }

        void Start()
        {
            if (!_dataReady && timeSeriesCsv != null && labelsCsv != null)
                ParseData();
            InitGPU();
        }

        void OnDestroy()
        {
            _gpu?.ReleaseAll();
            _gpu = null;
        }

        void OnDisable()
        {
            _gpu?.ReleaseAll();
            _gpu = null;
        }

        private void InitGPU()
        {
            if (targetSim == null || splatCS == null || !_dataReady) return;

            _gpu?.ReleaseAll();
            _gpu = new GPUResourceManager();

            int w = targetSim.rezX;
            int h = targetSim.rezY;

            _splatTex = _gpu.CreateTexture2D(w, h, FilterMode.Bilinear,
                RenderTextureFormat.ARGBFloat, "neuron_splat");
            // Upload neuron positions (convert normalized → pixel coords with scale+centering)
            var pixelPositions = new Vector2[_neuronCount];
            for (int i = 0; i < _neuronCount; i++)
            {
                var p = _neuronPositions[i];
                pixelPositions[i] = new Vector2(
                    p.x * neuronScale.x + w * (1f - neuronScale.x) * 0.5f,
                    p.y * neuronScale.y + h * (1f - neuronScale.y) * 0.5f
                );
            }

            _positionsBuffer = _gpu.CreateBuffer(_neuronCount, sizeof(float) * 2);
            _positionsBuffer.SetData(pixelPositions);

            _ratesBuffer = _gpu.CreateBuffer(_neuronCount, sizeof(float));
            _labelsBuffer = _gpu.CreateBuffer(_neuronCount, sizeof(int));
            _labelsBuffer.SetData(_neuronLabels);

            _clearKernel = splatCS.FindKernel("ClearKernel");
            _decayKernel = splatCS.FindKernel("DecayKernel");
            _splatKernel = splatCS.FindKernel("SplatKernel");

            // Clear texture
            splatCS.SetInt(s_RezX, w);
            splatCS.SetInt(s_RezY, h);
            splatCS.SetTexture(_clearKernel, s_SplatTex, _splatTex);
            Dispatch(_clearKernel, w, h);
        }

        void Update()
        {
            if (!_dataReady || !playing || _timestepCount == 0 || _gpu == null) return;

            // Advance time
            _playbackTime += Time.deltaTime;
            if (_playbackTime >= playbackDuration)
            {
                if (loop) _playbackTime %= playbackDuration;
                else { playing = false; return; }
            }

            float progress = _playbackTime / playbackDuration;
            _debugProgress = progress;
            int timestep = Mathf.Clamp(Mathf.FloorToInt(progress * _timestepCount), 0, _timestepCount - 1);

            // Upload current rates
            var row = _ratesNorm[timestep];
            Array.Copy(row, _currentRates, _neuronCount);
            _ratesBuffer.SetData(_currentRates);

            int w = targetSim.rezX;
            int h = targetSim.rezY;
            splatCS.SetInt(s_RezX, w);
            splatCS.SetInt(s_RezY, h);

            // Decay existing texture (trails fade over time)
            splatCS.SetFloat(Shader.PropertyToID("decayFactor"), temporalSmoothing);
            splatCS.SetTexture(_decayKernel, s_SplatTex, _splatTex);
            Dispatch(_decayKernel, w, h);

            // Splat new neuron activity on top
            splatCS.SetTexture(_splatKernel, s_SplatTex, _splatTex);
            splatCS.SetBuffer(_splatKernel, s_NeuronPositions, _positionsBuffer);
            splatCS.SetBuffer(_splatKernel, s_NeuronRates, _ratesBuffer);
            splatCS.SetBuffer(_splatKernel, s_NeuronLabels, _labelsBuffer);
            splatCS.SetInt(s_NeuronCount, _neuronCount);
            splatCS.SetFloat(s_SplatRadius, splatRadius);
            splatCS.SetFloat(s_SplatSigma, splatSigma);
            splatCS.SetFloat(s_SplatStrength, splatStrength);
            Dispatch(_splatKernel, w, h);

            // Assign to sim
            if (targetSim != null)
                targetSim.externalInfluenceTex = _splatTex;

            if (logActivity && timestep % 600 == 0)
                Debug.Log($"[NeuronFiring] t={timestep}/{_timestepCount} progress={progress:P0}");
        }

        private void Dispatch(int kernel, int x, int y)
        {
            splatCS.GetKernelThreadGroupSizes(kernel, out uint wx, out uint wy, out uint _);
            splatCS.Dispatch(kernel,
                Mathf.CeilToInt((float)x / wx),
                Mathf.CeilToInt((float)y / wy), 1);
        }

        // ─── CSV Parsing ───

        private void ParseLabels()
        {
            var lines = labelsCsv.text.Split('\n');
            var labels = new List<int>();
            var positions = new List<Vector2>();
            var inv = CultureInfo.InvariantCulture;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith("backbone")) continue;
                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                if (float.TryParse(parts[0], NumberStyles.Float, inv, out float labelF) &&
                    float.TryParse(parts[1], NumberStyles.Float, inv, out float x) &&
                    float.TryParse(parts[2], NumberStyles.Float, inv, out float y))
                {
                    labels.Add(Mathf.RoundToInt(labelF));
                    // Normalized positions (0-1), Y inverted to match sim coords
                    positions.Add(new Vector2(x, 1f - y));
                }
            }

            _neuronLabels = labels.ToArray();
            _neuronPositions = positions.ToArray();
            _neuronCount = _neuronLabels.Length;
        }

        private void ParseTimeSeries()
        {
            var lines = timeSeriesCsv.text.Split('\n');
            var inv = CultureInfo.InvariantCulture;

            var header = lines[0].Split(',');
            int ratesNormStart = -1;
            for (int i = 0; i < header.Length; i++)
            {
                if (header[i].Trim().StartsWith("rates_norm_0"))
                {
                    ratesNormStart = i;
                    break;
                }
            }

            if (ratesNormStart < 0)
            {
                Debug.LogError("[NeuronFiring] rates_norm columns not found.");
                return;
            }

            var rows = new List<float[]>();
            for (int r = 1; r < lines.Length; r++)
            {
                var line = lines[r].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                var parts = line.Split(',');

                var row = new float[_neuronCount];
                for (int n = 0; n < _neuronCount && (ratesNormStart + n) < parts.Length; n++)
                    float.TryParse(parts[ratesNormStart + n], NumberStyles.Float, inv, out row[n]);
                rows.Add(row);
            }

            _ratesNorm = rows.ToArray();
            _timestepCount = _ratesNorm.Length;
        }

        [Button("Reset Playback")]
        public void ResetPlayback()
        {
            _playbackTime = 0f;
        }
    }
}

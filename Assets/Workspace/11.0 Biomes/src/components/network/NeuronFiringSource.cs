using System;
using System.Collections.Generic;
using System.Globalization;
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
        public string firingBlobFile = "biomes11/organoid_firing.f16";

        [Tooltip("Seconds for firing intensity to fade to zero when no /index arrives")]
        public float firingDecaySeconds = 0.5f;

        [Tooltip("Log frame changes to the Console (main thread)")]
        public bool debugLog = false;

        [Tooltip("Single owner of neuron seed positions (normalized 0..1, contract enforced — see " +
                 "LoadPositions). SimulationManager pushes these to every sim (agent spawn seeding, " +
                 "CA ignition) and to the firing-ring overlay. No sim declares its own copy.")]
        public TextAsset labelsPositionsCsv;

        [Tooltip("How much of the canvas the neuron layout fills (0-1). (1,1) = full canvas. " +
                 "SINGLE SOURCE OF TRUTH: every sim's agent seeding, the composite firing-ring " +
                 "overlay and BiomeInjector's dispersal stamps all read this. Do not re-declare it " +
                 "elsewhere — that invariant was hand-maintained and silently broke in two scenes.")]
        public Vector2 spawnScale = NeuronLayout.DefaultScale;

        /// <summary>How much of the canvas the neuron layout fills. The one authored copy.</summary>
        public Vector2 SpawnScale => spawnScale;

        // Blob (loaded once)
        private ushort[] _firingHalf;   // flat float16 bits: frame*_neuronCount + neuron
        private int _neuronCount;
        private int _frameCount;
        // Strongest per-frame mean anywhere in the loaded recording (computed once at load —
        // see ComputeMaxFrameMean). FrameActivity normalizes against this so
        // burstFiringThreshold reads as "fraction of this recording's peak synchrony", not an
        // absolute mean that depends on how sparse/dense the source recording happens to be.
        private float _maxFrameMean;
        private string _loadedBlobFile;   // which firingBlobFile the buffers were built for
        private TextAsset _loadedPositionsCsv;   // which labelsPositionsCsv PositionsCPU was built for
        private bool _warnedPositionsOutOfContract;
        private bool _warnedDatasetMismatch;

        // OSC-driven (written on the receive thread)
        private volatile int _targetFrame;
        private volatile bool _dirty;

        // Runtime state (main thread)
        private int _currentFrame = -1;
        private float _intensity;
        private float _frameActivity;   // 0..1 aggregate strength of the current frame (see UpdateFiring)
        private float _lastTime;
        private float[] _row;       // decoded current frame
        private float[] _scaled;    // _row * _intensity, uploaded each step
        private ComputeBuffer _buffer;

        // Neuron positions (normalized 0..1, y-flipped) for the firing-ring overlay and,
        // via SimulationManager, every sim's agent spawn seeding + CA ignition.
        private int _posCount;
        private List<Vector2> _posList;

        public ComputeBuffer Buffer => _buffer;
        public int PositionsCount => _posCount;
        /// <summary>Current per-neuron firing values (already intensity-scaled), CPU-side.
        /// Lets the ring overlay compact to active neurons and skip the dispatch when quiet.</summary>
        public float[] ScaledValues => _scaled;
        /// <summary>Normalized neuron positions (same CSV order as ScaledValues), CPU-side.
        /// SINGLE SOURCE OF TRUTH: SimulationManager pushes this to every sim's
        /// neuronPositionsNorm. No sim parses its own CSV.</summary>
        public IReadOnlyList<Vector2> PositionsCPU => _posList;
        public int NeuronCount => _neuronCount;
        public int FrameCount => _frameCount;
        public int CurrentFrame => _currentFrame;
        public float Intensity => _intensity;
        /// <summary>0..1 aggregate firing strength of the current playback frame: the frame's
        /// mean firing value (see UpdateFiring), normalized to the LOADED RECORDING'S strongest
        /// frame (_maxFrameMean, computed once at load). So burstFiringThreshold reads as
        /// "fraction of this recording's peak synchrony" — 0.6 means only the top-tier bursting
        /// events in the recording ignite, regardless of the recording's absolute mean scale.
        /// Distinct from Intensity, which is pure recency (always 1 right after a frame change)
        /// and carries no information about how strong the frame actually was. Gates
        /// burstOnFrameAdvance so a dense /index stream only ignites on synchronous/strong
        /// frames instead of every advance.</summary>
        public float FrameActivity => _frameActivity;

        // ---- Neuron layout mapping -------------------------------------------------
        // The math lives in NeuronLayout (Biomes.Core) so it can be unit-tested against the
        // HLSL; this source owns the scale and binds it. See NeuronLayoutTests.

        /// <summary>Normalized neuron position -> normalized field UV, using this source's scale.</summary>
        public Vector2 NeuronToFieldUV(Vector2 npNorm) => NeuronLayout.ToFieldUV(npNorm, spawnScale);

        /// <summary>Normalized neuron position -> field pixel space, using this source's scale.</summary>
        public Vector2 NeuronToFieldPixels(Vector2 npNorm, float rezX, float rezY)
        {
            var uv = NeuronToFieldUV(npNorm);
            return new Vector2(uv.x * rezX, uv.y * rezY);
        }

        /// <summary>Thread-safe: called from the OSC receive thread.</summary>
        public void SetFrame(int frame)
        {
            _targetFrame = frame;
            _dirty = true;
        }

        public void Initialize()
        {
            // Load the blob from disk + (re)allocate the GPU buffers only on first init or
            // when the source file changes. A normal reset skips the disk read and buffer
            // realloc, just clearing the firing envelope below — so it adds no I/O hitch to
            // the reset frame and keeps the buffer instance stable.
            if (_buffer == null || _loadedBlobFile != firingBlobFile)
            {
                LoadBlob();
                int n = Mathf.Max(1, _neuronCount);
                _row = new float[n];
                _scaled = new float[n];
                ReleaseBuffers();
                _buffer = new ComputeBuffer(n, sizeof(float));
                _loadedBlobFile = firingBlobFile;
            }

            // Positions load independently of the blob: no blob is required (lets a
            // blob-less TestScene instance still seed agent spawn + firing-ring positions),
            // and they (re)load whenever the CSV reference changes, not gated on the
            // blob-reload branch above.
            if (labelsPositionsCsv != _loadedPositionsCsv)
            {
                LoadPositions();
                _loadedPositionsCsv = labelsPositionsCsv;
            }

            // Dataset-pairing check: row k of the CSV IS neuron k of the blob (ADR-0006's
            // spatial-coherence property). A mismatched (blob, CSV) pair should announce
            // itself, not silently wrap/truncate downstream.
            if (!_warnedDatasetMismatch && _neuronCount > 0 && _posCount > 0 && _posCount != _neuronCount)
            {
                Debug.LogWarning($"NeuronFiringSource: positions count ({_posCount}) != blob neuron count " +
                                  $"({_neuronCount}) — labelsPositionsCsv and firingBlobFile look like a mismatched dataset pair");
                _warnedDatasetMismatch = true;
            }

            // Clear the firing envelope (every reset): zero the buffer and reset decay state.
            if (_row != null) System.Array.Clear(_row, 0, _row.Length);
            if (_scaled != null) System.Array.Clear(_scaled, 0, _scaled.Length);
            _buffer.SetData(_scaled ?? new float[_buffer.count]);
            _currentFrame = -1;
            _intensity = 0f;
            _frameActivity = 0f;
            _lastTime = Time.unscaledTime;
            _dirty = false;
        }

        private void LoadPositions()
        {
            _posCount = 0;
            _posList = null;
            if (labelsPositionsCsv == null || string.IsNullOrEmpty(labelsPositionsCsv.text)) return;
            var pts = ParseCsvFloat2(labelsPositionsCsv.text); // normalized 0..1, y-flipped
            if (pts == null || pts.Count == 0) return;
            WarnIfOutOfContract(pts);
            _posList = pts;
            _posCount = pts.Count;
        }

        // Contract: labelsPositionsCsv coordinates MUST be normalized 0..1 (ParseCsvFloat2
        // y-flips but does not rescale). Warns once if any point falls outside a small
        // tolerance band, replacing the old per-sim LooksNormalized01 auto-detect with a
        // validated contract at this single parse site.
        private void WarnIfOutOfContract(List<Vector2> pts)
        {
            if (_warnedPositionsOutOfContract) return;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.x < -0.01f || p.x > 1.01f || p.y < -0.01f || p.y > 1.01f)
                {
                    Debug.LogWarning($"NeuronFiringSource: {labelsPositionsCsv.name} has out-of-range " +
                                      $"point ({p.x:F3},{p.y:F3}) — labelsPositionsCsv must be normalized 0..1");
                    _warnedPositionsOutOfContract = true;
                    break;
                }
            }
        }

        // Moved from SimulationBase (2026-08-12 single-owner refactor) — this is now the
        // only parse site for labels_positions.csv. y-flips (1 - y) so row order matches
        // the blob's neuron order top-to-bottom in screen space.
        private static List<Vector2> ParseCsvFloat2(string csv)
        {
            var list = new List<Vector2>();
            var lines = csv.Split('\n');
            var inv = CultureInfo.InvariantCulture;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (float.TryParse(parts[1], NumberStyles.Float, inv, out float x) &&
                    float.TryParse(parts[2], NumberStyles.Float, inv, out float y))
                    list.Add(new Vector2(x, (1 - y)));
            }
            return list;
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
                float sum = 0f;
                for (int i = 0; i < _neuronCount; i++)
                {
                    float v = Mathf.HalfToFloat(_firingHalf[baseIdx + i]);
                    _row[i] = v;
                    sum += v;
                }
                // Aggregate = MEAN of the row. Measured against organoid_firing.f16: per-neuron
                // values are continuous 0..1 (not bimodal spikes at 0/1), just heavily
                // right-skewed — global median ~0.004, only ~2% of individual values exceed
                // 0.5. A FRACTION-above-0.5 aggregate would rarely clear even a modest threshold
                // on this data (busiest observed frame: only ~18% of neurons > 0.5), starving
                // the frame-advance gate. MEAN stays monotone with synchrony (more/stronger
                // firing -> higher mean) and uses the full continuous range instead of a hard cut.
                float rawMean = _neuronCount > 0 ? sum / _neuronCount : 0f;
                _intensity = 1f;
                // Normalized to the recording's own peak (_maxFrameMean, one-time pass at load)
                // rather than an absolute 0..1 scale: the raw mean on organoid_firing.f16 only
                // ever reaches ~0.21, so an absolute threshold would need per-recording
                // recalibration. Guard <= 0 for an empty/silent/unloaded blob.
                _frameActivity = _maxFrameMean > 0f ? Mathf.Clamp01(rawMean / _maxFrameMean) : 0f;
                if (debugLog) Debug.Log($"NeuronFiringSource: frame={_currentFrame} mean={rawMean:F4} activity={_frameActivity:F3}");
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
            _firingHalf = null; _frameCount = 0; _neuronCount = 0; _maxFrameMean = 0f;
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

            _maxFrameMean = ComputeMaxFrameMean();
            if (debugLog) Debug.Log($"NeuronFiringSource: loaded {_frameCount}x{_neuronCount}, maxFrameMean={_maxFrameMean:F4}");
        }

        // One-time decode pass over the whole recording to find its strongest frame (highest
        // per-frame mean), so FrameActivity can be normalized to 0..1 against this recording's
        // own peak synchrony (see _maxFrameMean). Cost: one HalfToFloat per sample —
        // frameCount * neuronCount, ~23.6M for the committed 131 x 180000 blob — paid once at
        // blob load, not per frame, so it is not on the per-step hot path.
        private float ComputeMaxFrameMean()
        {
            float max = 0f;
            for (int f = 0; f < _frameCount; f++)
            {
                int baseIdx = f * _neuronCount;
                float sum = 0f;
                for (int i = 0; i < _neuronCount; i++)
                    sum += Mathf.HalfToFloat(_firingHalf[baseIdx + i]);
                float mean = sum / _neuronCount;
                if (mean > max) max = mean;
            }
            return max;
        }

        private void ReleaseBuffers()
        {
            _buffer?.Release(); _buffer = null;
        }
        void OnDisable() => ReleaseBuffers();
        void OnDestroy() => ReleaseBuffers();

        [Button]
        public void TestLoadAndLog()
        {
            Initialize();
            Debug.Log($"NeuronFiringSource: neurons={_neuronCount} frames={_frameCount}");
        }
    }
}

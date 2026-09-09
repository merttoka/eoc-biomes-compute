using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Local transport for the organoid firing blob: plays an inclusive frame range over a
    /// wall-clock duration and pushes each frame through <see cref="NeuronFiringSource.SetFrame"/>
    /// — the same entry the OSC <c>/index</c> stream uses, so sims, bursts and the injector's
    /// diurnal phase cannot tell the difference. Use it to rehearse a show segment (e.g. the
    /// Shanghai cut, frames 125000–131000 over 120 s) without TouchDesigner driving.
    ///
    /// <para>Leave <see cref="playing"/> off when TD is sending /index or the two will fight
    /// over the frame. The pure timing lives in <see cref="FramePlayhead"/> (unit-tested).</para>
    /// </summary>
    public class NeuronFiringPlayback : MonoBehaviour
    {
        [Tooltip("The blob owner. Frames are pushed via SetFrame, exactly like OSC /index.")]
        public NeuronFiringSource source;

        [Header("Range")]
        [Tooltip("First blob frame (inclusive).")]
        [Min(0)] public int startFrame = 125000;
        [Tooltip("Last blob frame (inclusive).")]
        [Min(0)] public int endFrame = 131000;
        [Tooltip("Wall-clock seconds for one pass start → end. 6001 frames over 120 s ≈ 50 fps.")]
        [Min(0.01f)] public float durationSeconds = 120f;
        public bool loop = true;

        [Header("Transport")]
        public bool playOnStart = false;
        [Tooltip("Live transport. Toggle in the inspector or via Play/Pause.")]
        public bool playing = false;
        [Tooltip("Advance by unscaled time so Time.timeScale never desyncs the blob from a video " +
                 "playing alongside it (the OSC path is wall-clock too).")]
        public bool unscaledTime = true;
        public bool debugLog = false;

        private readonly FramePlayhead _playhead = new();
        private int _cfgStart = -1, _cfgEnd = -1;
        private float _cfgDuration = -1f;
        private int _lastSent = int.MinValue;
        private bool _warnedRange;

        public int Frame => _playhead.Frame;
        /// <summary>0..1 through the range.</summary>
        public float Progress => _playhead.Progress;
        public bool Finished => _playhead.Finished;

        void Start()
        {
            SyncConfig();
            if (playOnStart) Play();
        }

        void Update()
        {
            if (!playing || source == null) return;
            SyncConfig();
            WarnIfOutOfBlob();

            float dt = unscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            int f = _playhead.Advance(dt);
            Push(f);

            if (_playhead.Finished && !loop)
            {
                playing = false;
                if (debugLog) Debug.Log($"NeuronFiringPlayback: reached {endFrame}, stopped.");
            }
        }

        [Button]
        public void Play()
        {
            SyncConfig();
            playing = true;
            Push(_playhead.Frame);
            if (debugLog) Debug.Log($"NeuronFiringPlayback: play {startFrame}..{endFrame} over {durationSeconds:F1}s (loop={loop})");
        }

        [Button]
        public void Pause() => playing = false;

        [Button]
        public void Restart()
        {
            SyncConfig();
            _playhead.Reset();
            _lastSent = int.MinValue;
            playing = true;
            Push(_playhead.Frame);
        }

        [Button]
        public void Stop()
        {
            playing = false;
            _playhead.Reset();
            _lastSent = int.MinValue;
        }

        /// <summary>Jump to an absolute blob frame (clamped into the range) and push it now.</summary>
        public void Seek(int frame)
        {
            SyncConfig();
            _playhead.Seek(frame);
            Push(_playhead.Frame);
        }

        // Re-Configure only when the inspector fields actually changed, so live edits to the
        // range take effect without rewinding on every Update. Keeps the current frame if it
        // still falls inside the new range.
        private void SyncConfig()
        {
            _playhead.Loop = loop;
            if (startFrame == _cfgStart && endFrame == _cfgEnd &&
                Mathf.Approximately(durationSeconds, _cfgDuration))
                return;

            int keep = _cfgStart >= 0 ? _playhead.Frame : startFrame;
            _playhead.Configure(startFrame, endFrame, durationSeconds, loop);
            _playhead.Seek(keep);
            _cfgStart = startFrame;
            _cfgEnd = endFrame;
            _cfgDuration = durationSeconds;
            _warnedRange = false;
        }

        private void Push(int frame)
        {
            if (frame == _lastSent || source == null) return;
            source.SetFrame(frame);
            _lastSent = frame;
        }

        private void WarnIfOutOfBlob()
        {
            if (_warnedRange || source.FrameCount <= 0) return;
            if (_playhead.End >= source.FrameCount)
            {
                _warnedRange = true;
                Debug.LogWarning($"NeuronFiringPlayback: range {_playhead.Start}..{_playhead.End} exceeds the loaded blob " +
                                 $"({source.FrameCount} frames); frames past the end clamp to the last one.");
            }
        }
    }
}

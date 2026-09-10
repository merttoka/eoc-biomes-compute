using System;
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Minimal step-clock sequencer: fires sim Start/Stop/Reset actions at authored
    /// times. Times are SIM time — converted to fixed sim steps via the manager's
    /// simRate — never wall clock, so under varying fps (or Unity Recorder's constant
    /// capture clock, which slows wall time but keeps game time exact) the cues stay
    /// locked to what the recording shows.
    ///
    /// <para>Keeps its OWN step counter rather than reading
    /// <see cref="SimulationManager.SimStepCount"/>: Reset() zeroes that counter, so an
    /// absolute timeline keyed on it would re-time itself at every ResetAll cue.</para>
    /// </summary>
    public class SimTimeline : MonoBehaviour
    {
        public enum TimelineAction
        {
            StartTermites, StartBoids, StartPhysarum, StartCellular,
            StopTermites, StopBoids, StopPhysarum, StopCellular,
            ResetAll, ResetSimsOnly,
            StartDebugVideo, PauseDebugVideo, StopDebugVideo,
        }

        [Serializable]
        public struct TimelineEvent
        {
            [Tooltip("Sim-time seconds from Play. Absolute — a ResetAll cue does not re-zero the timeline.")]
            public float atSeconds;
            public TimelineAction action;
            [Tooltip("Stop actions only: fade-out duration override in seconds. 0 = keep the sim's own fadeOutSeconds.")]
            public float fadeSecondsOverride;
        }

        public SimulationManager simManager;

        [Header("Frame recording")]
        [Tooltip("Optional: FigureExporter whose frame export is started on Play and stopped at the end, so composite/sim/biome frames cover exactly the playback window.")]
        public FigureExporter figureExporter;
        public bool recordFramesDuringPlayback = false;

        [Header("Firing source (optional)")]
        [Tooltip("Restarted from its start frame on Play and stopped on Stop, so the blob range covers exactly " +
                 "the playback window. Advances on Unity's own clock, so it stays frame-locked to FigureExporter / " +
                 "Recorder captures — use this for recorded takes.")]
        public NeuronFiringPlayback firingPlayback;

        [Header("External input (optional)")]
        [Tooltip("Receiver whose DEBUG clip the timeline cues. On Play the clip is rewound and held transparent " +
                 "(autoplay disabled at runtime); StartDebugVideo / PauseDebugVideo / StopDebugVideo cues drive it; " +
                 "Stop clears it. The live Syphon/NDI path is untouched.")]
        public ExternalTextureReceiver externalReceiver;
        [Tooltip("Start the debug clip at 0 s on Play (otherwise wait for a StartDebugVideo cue).")]
        public bool startDebugVideoOnPlay = false;

        [Header("Playback")]
        [Tooltip("Begin the timeline as soon as play mode starts. Untick startOnPlay on every sim so the opening state is only what the timeline starts.")]
        public bool playOnStart = true;
        [Tooltip("Sim-time seconds at which playback (and frame recording) ends. 0 = run until Stop is pressed.")]
        public float endAtSeconds = 300f;

        [Tooltip("Cues, in sim-time seconds from Play. Order in the list is free — playback sorts a copy.")]
        public List<TimelineEvent> events = new()
        {
            new TimelineEvent { atSeconds = 0,   action = TimelineAction.StartTermites },
            new TimelineEvent { atSeconds = 5,   action = TimelineAction.StartBoids },
            new TimelineEvent { atSeconds = 20,  action = TimelineAction.StartPhysarum },
            new TimelineEvent { atSeconds = 90,  action = TimelineAction.ResetAll },
            new TimelineEvent { atSeconds = 180, action = TimelineAction.ResetAll },
            new TimelineEvent { atSeconds = 270, action = TimelineAction.StopPhysarum, fadeSecondsOverride = 10 },
            new TimelineEvent { atSeconds = 280, action = TimelineAction.StopBoids, fadeSecondsOverride = 10 },
        };

        private bool _playing;
        private int _step;                    // own sim-step clock; survives manager resets
        private List<TimelineEvent> _sorted;  // play-time copy, sorted by atSeconds
        private int _next;                    // next unfired event in _sorted

        /// <summary>Current playback position in sim-time seconds.</summary>
        public float CurrentSeconds =>
            simManager != null ? _step / Mathf.Max(1f, simManager.simRate) : 0f;
        public bool IsPlaying => _playing;

        void Start()
        {
            if (playOnStart) Play();
        }

        [Button]
        public void Play()
        {
            if (simManager == null)
            {
                Debug.LogWarning("[SimTimeline] No SimulationManager assigned.");
                return;
            }
            _step = 0;
            _next = 0;
            _sorted = new List<TimelineEvent>(events);
            _sorted.Sort((a, b) => a.atSeconds.CompareTo(b.atSeconds));
            _playing = true;
            if (recordFramesDuringPlayback && figureExporter != null)
                figureExporter.StartFrameExport();
            if (firingPlayback != null) firingPlayback.Restart();
            if (externalReceiver != null)
            {
                externalReceiver.debugVideoAutoPlay = false; // runtime-only; the timeline owns the clip now
                externalReceiver.StopDebugVideo();           // frame 0, transparent
                if (startDebugVideoOnPlay) externalReceiver.RestartDebugVideo();
            }
            Debug.Log($"[SimTimeline] Play — {_sorted.Count} cues, " +
                      (endAtSeconds > 0 ? $"ends at {endAtSeconds:F0}s." : "manual stop."));
        }

        [Button]
        public void Stop()
        {
            if (!_playing) return;
            _playing = false;
            if (recordFramesDuringPlayback && figureExporter != null)
                figureExporter.StopFrameExport();
            if (firingPlayback != null) firingPlayback.Stop();
            if (externalReceiver != null) externalReceiver.StopDebugVideo();
            Debug.Log($"[SimTimeline] Stopped at {CurrentSeconds:F1}s.");
        }

        void OnDisable() => Stop();

        // Ticks on the same fixed clock the sim steps on. Advancing by stepsPerTick
        // mirrors the manager's FixedUpdate exactly: at stepsPerTick 2 the timeline runs
        // double speed with the sim, at 0 (paused) it holds.
        void FixedUpdate()
        {
            if (!_playing || simManager == null) return;

            float now = CurrentSeconds;
            while (_next < _sorted.Count && _sorted[_next].atSeconds <= now)
                Fire(_sorted[_next++]);

            if (endAtSeconds > 0 && now >= endAtSeconds)
            {
                Stop();
                return;
            }

            _step += Mathf.Max(0, simManager.stepsPerTick);
        }

        private void Fire(TimelineEvent e)
        {
            Debug.Log($"[SimTimeline] {e.atSeconds:F1}s → {e.action}");
            switch (e.action)
            {
                case TimelineAction.StartTermites: StartAll<TermiteSim>(); break;
                case TimelineAction.StartBoids:    StartAll<BoidSim>(); break;
                case TimelineAction.StartPhysarum: StartAll<PhysarumSim>(); break;
                case TimelineAction.StartCellular: StartAll<FieldSimulationBase>(); break;
                case TimelineAction.StopTermites:  StopAll<TermiteSim>(e.fadeSecondsOverride); break;
                case TimelineAction.StopBoids:     StopAll<BoidSim>(e.fadeSecondsOverride); break;
                case TimelineAction.StopPhysarum:  StopAll<PhysarumSim>(e.fadeSecondsOverride); break;
                case TimelineAction.StopCellular:  StopAll<FieldSimulationBase>(e.fadeSecondsOverride); break;
                case TimelineAction.ResetAll:      simManager.Reset(); break;
                case TimelineAction.ResetSimsOnly: simManager.ResetSimsOnly(); break;
                case TimelineAction.StartDebugVideo: VideoCue(r => r.RestartDebugVideo()); break;
                case TimelineAction.PauseDebugVideo: VideoCue(r => r.PauseDebugVideo()); break;
                case TimelineAction.StopDebugVideo:  VideoCue(r => r.StopDebugVideo()); break;
            }
        }

        private void StartAll<T>() where T : SimulationBase
        {
            foreach (var sim in simManager.simulations)
                if (sim is T) simManager.StartSim(sim);
        }

        private void StopAll<T>(float fadeOverride) where T : SimulationBase
        {
            foreach (var sim in simManager.simulations)
            {
                if (sim is not T) continue;
                // Runtime-only override (play-mode field edits don't persist to the scene).
                if (fadeOverride > 0f) sim.fadeOutSeconds = fadeOverride;
                simManager.StopSim(sim);
            }
        }

        private void VideoCue(Action<ExternalTextureReceiver> act)
        {
            if (externalReceiver == null)
            {
                Debug.LogWarning("[SimTimeline] video cue fired but no externalReceiver is assigned.");
                return;
            }
            if (!externalReceiver.DebugUseVideoInput)
            {
                Debug.LogWarning("[SimTimeline] video cue fired but the receiver's debug video input is off.");
                return;
            }
            act(externalReceiver);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Conductor for several <see cref="ParameterInterpolator"/>s (one per sim): one
    /// Play / Pause / Stop / Skip / Refresh drives them all together, instead of pressing
    /// play on each sim's interpolator individually. Monitoring (per-interpolator phase +
    /// progress bars) is drawn by the custom inspector. Assign the per-sim interpolators
    /// to the list below.
    /// </summary>
    public class ParameterInterpolatorGroup : MonoBehaviour
    {
        [Tooltip("The per-sim ParameterInterpolators to drive together.")]
        public List<ParameterInterpolator> interpolators = new();

        [Header("Playback")]
        [Tooltip("Call Play All as soon as play mode starts (SimulationManager resets the sims in OnEnable, so live params exist by Start). " +
                 "Same convention as SimTimeline.playOnStart.")]
        public bool playOnStart = false;
        [Tooltip("Pushed to every interpolator in the list. On: a sim reset (ResetAll / ResetSimsOnly, which zero the sim step counter) " +
                 "rebases the running leg's clock so interpolation continues where it was. Off: each sim reset restarts the queue from waypoint 0.")]
        public bool keepRunningOnSimReset = true;

        [Header("MIDI coexistence")]
        [Tooltip("If assigned, live MFT input pauses all interpolation; after the cooldown " +
                 "without input it resumes from the edited values over the remaining leg time.")]
        public MidiFighterTwister mft;
        [Min(0f), Tooltip("Seconds without MFT input before interpolation resumes.")]
        public float resumeCooldownSeconds = 8f;

        [SerializeField, Tooltip("Read-only: interpolation is currently yielded to live MIDI.")]
        private bool midiOverrideActive;

        private float _lastSeenInputTime;

        public void PlayAll()       { PushResetPolicy(); foreach (var i in interpolators) if (i != null) i.Play(); mft?.InvalidatePickup(); }
        public void PauseAll()      { foreach (var i in interpolators) if (i != null) i.Pause(); }
        public void StopAll()       { foreach (var i in interpolators) if (i != null) i.Stop(); midiOverrideActive = false; }
        public void SkipAllToNext() { foreach (var i in interpolators) if (i != null) i.SkipToNext(); }
        public void RefreshAll()    { foreach (var i in interpolators) if (i != null) i.RefreshParamList(); }

        // The group is the single source of truth for the reset policy of its members.
        private void PushResetPolicy()
        {
            foreach (var i in interpolators) if (i != null) i.keepRunningOnSimReset = keepRunningOnSimReset;
        }

        void Start()
        {
            PushResetPolicy();
            if (playOnStart) PlayAll();
        }

        void Update()
        {
            if (mft == null) return;

            // New MFT input → yield: pause all running interpolators (remembering remaining time).
            if (mft.LastInputTime > _lastSeenInputTime)
            {
                _lastSeenInputTime = mft.LastInputTime;
                if (!midiOverrideActive && AnyRunning())
                {
                    foreach (var i in interpolators) if (i != null) i.PauseForOverride();
                    midiOverrideActive = true;
                }
            }

            // Cooldown elapsed with no input → resume from edited values; knobs need re-pickup.
            if (midiOverrideActive && Time.unscaledTime - mft.LastInputTime >= resumeCooldownSeconds)
            {
                foreach (var i in interpolators) if (i != null) i.ResumeFromOverride();
                mft.InvalidatePickup();
                midiOverrideActive = false;
            }
        }

        private bool AnyRunning()
        {
            foreach (var i in interpolators)
                if (i != null && (i.CurrentPhase == ParameterInterpolator.Phase.Interpolating ||
                                  i.CurrentPhase == ParameterInterpolator.Phase.Holding))
                    return true;
            return false;
        }
    }
}

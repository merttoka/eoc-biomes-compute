using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Slowly interpolates one sim's live parameters from its current state through
    /// an ordered queue of preset assets (waypoints), advancing on simulation steps.
    /// For long-running installations. One component per sim.
    /// </summary>
    public class ParameterInterpolator : MonoBehaviour
    {
        public enum Phase { Idle, Interpolating, Holding, Done }

        [System.Serializable]
        public class ParamToggle
        {
            public string name;
            public bool enabled = true;
        }

        [Header("References")]
        public SimulationManager simManager;
        public int simIndex = 0;

        [Header("Waypoints (target preset assets, played in order)")]
        public List<ScriptableObject> waypoints = new();

        [Tooltip("Optional umwelt targets, index-paired with waypoints (leg i → umweltWaypoints[i]). " +
                 "Empty slot = umwelt untouched that leg. Reads/writes crossfade by channel key: entries " +
                 "only in the target fade in from 0, entries only in the source fade out and are removed at leg end.")]
        public List<UmweltMapping> umweltWaypoints = new();

        [Header("Timing (simulation steps)")]
        [Min(1)] public int durationSteps = 600;
        [Min(0)] public int holdSteps = 0;
        public AnimationCurve easing = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Sim resets")]
        [Tooltip("A sim reset (ResetAll / ResetSimsOnly) zeroes SimulationManager.SimStepCount. On: the running leg's clock is rebased " +
                 "so interpolation continues where it was (the re-cloned live params are re-imposed next step). Off: the queue restarts " +
                 "from waypoint 0. When driven by a ParameterInterpolatorGroup, the group's setting overwrites this one.")]
        public bool keepRunningOnSimReset = true;

        [Header("Per-parameter enable (click Refresh after assigning sim)")]
        public List<ParamToggle> paramToggles = new();

        [Header("Progress (read-only)")]
        [SerializeField] private Phase phase = Phase.Idle;
        [SerializeField] private int currentWaypoint;
        [SerializeField, Range(0f, 1f)] private float progress;

        // Read-only status accessors (for ParameterInterpolatorGroup monitoring).
        public Phase CurrentPhase => phase;
        public float Progress => progress;
        public int CurrentWaypoint => currentWaypoint;
        private int LegCount => Mathf.Max(waypoints != null ? waypoints.Count : 0, umweltWaypoints != null ? umweltWaypoints.Count : 0);
        public int WaypointCount => LegCount;

        // "from" snapshot: paramName -> value per type index, taken at each leg start
        private readonly Dictionary<string, float[]> _from = new();
        // umwelt snapshots: flat key -> value (UmweltKeys), taken at each leg start
        private readonly Dictionary<string, float> _fromUmwelt = new();
        private readonly Dictionary<string, float> _toUmwelt = new();
        private bool _umweltLegActive;   // this leg has an umwelt target
        private bool _umweltLegFinished; // end-of-leg cleanup done (remove faded entries, snap bools)
        private UmweltMapping _umweltInstance; // the clone _fromUmwelt was taken from
        private List<string> _umweltKeys = new();                                 // union of from/to keys, cached per leg
        private readonly Dictionary<string, string> _umweltToggleNames = new();    // key -> toggle name, cached
        private int _legStartStep;
        private int _lastStepSeen;                         // detects the step counter jumping backwards (sim reset)
        private int _legDuration;                          // current leg length (= durationSteps, or remaining on resume)
        private bool _warnedWrongType;

        // Override-pause state (driven by ParameterInterpolatorGroup for MIDI coexistence)
        private Phase _overridePausedPhase = Phase.Idle;   // Idle = not override-paused
        private int _overrideRemaining;                    // leg steps left when paused

        private SimulationBase Sim =>
            (simManager != null && simIndex >= 0 && simIndex < simManager.simulations.Count)
                ? simManager.simulations[simIndex] : null;

        private int StepNow() => simManager != null ? simManager.SimStepCount : 0;

        private UmweltMapping UmweltTargetFor(int i) =>
            (umweltWaypoints != null && i >= 0 && i < umweltWaypoints.Count) ? umweltWaypoints[i] : null;

        private static string UmweltToggleName(string key) =>
            UmweltKeys.IsRead(key)  ? "umwelt.reads" :
            UmweltKeys.IsWrite(key) ? "umwelt.writes" :
            "umwelt." + key;

        private string UmweltToggleNameCached(string key)
        {
            if (!_umweltToggleNames.TryGetValue(key, out var n)) { n = UmweltToggleName(key); _umweltToggleNames[key] = n; }
            return n;
        }

        // ─────────── Param list ───────────

        [Button("Refresh Param List")]
        public void RefreshParamList()
        {
            var sim = Sim;
            if (sim == null) { Debug.LogWarning("ParameterInterpolator: no sim resolved (check simManager/simIndex)"); return; }

            var prev = new Dictionary<string, bool>();
            foreach (var t in paramToggles) prev[t.name] = t.enabled;

            paramToggles.Clear();
            foreach (var name in sim.ModulatableParams)
                paramToggles.Add(new ParamToggle
                {
                    name = name,
                    enabled = prev.TryGetValue(name, out bool e) ? e : true,
                });

            foreach (var s in UmweltKeys.Scalars) AddToggle("umwelt." + s);
            AddToggle("umwelt.reads");
            AddToggle("umwelt.writes");

            void AddToggle(string name) => paramToggles.Add(new ParamToggle
            {
                name = name,
                enabled = prev.TryGetValue(name, out bool e) ? e : true,
            });
        }

        private bool IsEnabled(string name)
        {
            foreach (var t in paramToggles)
                if (t.name == name) return t.enabled;
            return true; // not listed -> default on
        }

        // ─────────── Transport ───────────

        [Button("Play")]
        public void Play()
        {
            var sim = Sim;
            if (sim == null || sim.LiveParamSet == null) { Debug.LogWarning("ParameterInterpolator: no sim/live params (enter Play mode and Reset sims first)"); return; }
            if (LegCount == 0) { Debug.LogWarning("ParameterInterpolator: no waypoints assigned"); return; }

            currentWaypoint = 0;
            _warnedWrongType = false;
            _overridePausedPhase = Phase.Idle;
            SnapshotFrom();
            _legStartStep = StepNow();
            _lastStepSeen = _legStartStep;
            _legDuration = durationSteps;
            phase = Phase.Interpolating;
            progress = 0f;
        }

        [Button("Pause")]
        public void Pause()
        {
            if (phase == Phase.Interpolating || phase == Phase.Holding)
                phase = Phase.Idle;
        }

        [Button("Stop")]
        public void Stop()
        {
            phase = Phase.Idle;
            progress = 0f;
            _overridePausedPhase = Phase.Idle;
        }

        [Button("Skip to Next")]
        public void SkipToNext()
        {
            if (phase == Phase.Interpolating || phase == Phase.Holding)
                Advance();
        }

        // ─────────── Override (MIDI coexistence; driven by ParameterInterpolatorGroup) ───────────

        public bool IsOverridePaused => _overridePausedPhase != Phase.Idle;

        /// <summary>Freeze the current leg, remembering how many steps remain, so
        /// ResumeFromOverride can later continue from edited values over the SAME remaining
        /// time. No-op unless a leg is actively running.</summary>
        public void PauseForOverride()
        {
            if (phase != Phase.Interpolating && phase != Phase.Holding) return;
            int elapsed = StepNow() - _legStartStep;
            _overrideRemaining = phase == Phase.Interpolating
                ? Mathf.Max(0, _legDuration - elapsed)
                : Mathf.Max(0, (_legDuration + holdSteps) - elapsed);
            _overridePausedPhase = phase;
            phase = Phase.Idle;
        }

        /// <summary>Resume an override-paused leg from the CURRENT (possibly MIDI-edited) live
        /// values, continuing toward the same waypoint over the remembered remaining time.</summary>
        public void ResumeFromOverride()
        {
            if (_overridePausedPhase == Phase.Idle) return;
            var sim = Sim;
            if (sim == null || sim.LiveParamSet == null) { _overridePausedPhase = Phase.Idle; return; }

            if (_overridePausedPhase == Phase.Interpolating)
            {
                SnapshotFrom();                    // 'from' = current edited values (no jump)
                _legDuration = _overrideRemaining; // finish in the time that was left
                _legStartStep = StepNow();
                phase = Phase.Interpolating;
            }
            else // resume Holding for the remaining hold time
            {
                _legStartStep = StepNow() - (_legDuration + holdSteps - _overrideRemaining);
                phase = Phase.Holding;
            }
            _lastStepSeen = StepNow(); // a reset during the pause must not read as a fresh reset now
            _overridePausedPhase = Phase.Idle;
        }

        // ─────────── Drive ───────────

        void Update()
        {
            if (phase != Phase.Interpolating && phase != Phase.Holding) return;
            var sim = Sim;
            if (sim == null || sim.LiveParamSet == null) return;

            // Sim reset: SimStepCount went backwards (Reset/ResetSimsOnly zero it). Without this the
            // leg would stall until the counter climbed back past _legStartStep.
            int now = StepNow();
            if (now < _lastStepSeen)
            {
                if (keepRunningOnSimReset)
                {
                    int elapsedBefore = _lastStepSeen - _legStartStep;
                    _legStartStep = now - elapsedBefore;   // same progress, new clock origin
                }
                else
                {
                    _lastStepSeen = now;
                    Play();                                // restart the queue with the sim
                    return;
                }
            }
            _lastStepSeen = now;

            int elapsed = now - _legStartStep;

            if (phase == Phase.Interpolating)
            {
                float t = Mathf.Clamp01(_legDuration > 0 ? (float)elapsed / _legDuration : 1f);
                progress = t;
                ApplyLeg(sim.LiveParamSet, easing.Evaluate(t));

                if (t >= 1f)
                {
                    FinishUmweltLeg();
                    if (holdSteps > 0) phase = Phase.Holding;
                    else Advance();
                }
            }
            else // Holding
            {
                if (elapsed >= _legDuration + holdSteps)
                    Advance();
            }
        }

        private void ApplyLeg(IParamSet live, float te)
        {
            ApplyParamLeg(live, te);
            ApplyUmweltLeg(te);
        }

        private void ApplyParamLeg(IParamSet live, float te)
        {
            var target = waypoints != null && currentWaypoint < waypoints.Count
                ? waypoints[currentWaypoint] as IParamSet : null;
            if (target == null)
            {
                // Umwelt-only leg is legitimate; warn only when the leg has nothing to do.
                if (!_umweltLegActive && !_warnedWrongType)
                {
                    Debug.LogWarning($"ParameterInterpolator: waypoint {currentWaypoint} is neither an IParamSet preset nor paired with an umwelt waypoint; skipping leg");
                    _warnedWrongType = true;
                }
                return;
            }

            int typeCount = Mathf.Min(live.TypeCount, target.TypeCount);
            foreach (var kv in _from)
            {
                string name = kv.Key;
                if (!IsEnabled(name)) continue;
                float[] fromArr = kv.Value;
                for (int i = 0; i < typeCount && i < fromArr.Length; i++)
                {
                    float from = fromArr[i];
                    float to = target.GetValue(name, i);
                    float v = name == "hue" ? LerpHue01(from, to, te) : Mathf.Lerp(from, to, te);
                    live.SetValue(name, i, v);
                }
            }
        }

        private void ApplyUmweltLeg(float te)
        {
            if (!_umweltLegActive) return;
            var liveU = Sim != null ? Sim.liveUmwelt : null;
            if (liveU == null) return;
            if (liveU != _umweltInstance)
            {
                // Reset() re-cloned the umwelt from the asset mid-leg: continue this leg from the
                // fresh clone's values (anything an earlier leg removed is back, and now fades out again).
                liveU.Snapshot(_fromUmwelt);
                _umweltInstance = liveU;
                _umweltKeys = KeyedCrossfade.UnionKeys(_fromUmwelt, _toUmwelt);
            }
            foreach (var key in _umweltKeys)
            {
                if (!IsEnabled(UmweltToggleNameCached(key))) continue;
                liveU.SetValue(key, KeyedCrossfade.Lerp(_fromUmwelt, _toUmwelt, key, te));
            }
        }

        /// <summary>Leg reached t = 1 naturally: drop entries that faded to 0 and snap the
        /// non-lerpable bool. Not called on Skip — a skipped leg re-snapshots from wherever it was.</summary>
        private void FinishUmweltLeg()
        {
            if (!_umweltLegActive || _umweltLegFinished) return;
            _umweltLegFinished = true;
            var liveU = Sim != null ? Sim.liveUmwelt : null;
            var targetU = UmweltTargetFor(currentWaypoint);
            if (liveU == null || targetU == null) return;
            foreach (var key in KeyedCrossfade.KeysToRemove(_fromUmwelt, _toUmwelt))
                if (IsEnabled(UmweltToggleNameCached(key))) liveU.RemoveEntry(key);
            liveU.enableDeath = targetU.enableDeath;
        }

        private void Advance()
        {
            currentWaypoint++;
            if (currentWaypoint >= LegCount)
            {
                currentWaypoint = LegCount - 1;
                phase = Phase.Done;
                progress = 1f;
                return;
            }
            SnapshotFrom();
            _legStartStep = StepNow();
            _legDuration = durationSteps;
            phase = Phase.Interpolating;
            progress = 0f;
        }

        private void SnapshotFrom()
        {
            _from.Clear();
            var sim = Sim;
            var live = sim.LiveParamSet;
            if (live != null)
            {
                int typeCount = live.TypeCount;
                foreach (var name in sim.ModulatableParams)
                {
                    var arr = new float[typeCount];
                    for (int i = 0; i < typeCount; i++)
                        arr[i] = live.GetValue(name, i);
                    _from[name] = arr;
                }
            }

            // Umwelt leg: snapshot both ends now (target asset is read once per leg).
            _fromUmwelt.Clear();
            _toUmwelt.Clear();
            _umweltLegFinished = false;
            // clone only — LiveUmwelt falls back to the asset, which must never be written
            var liveU = sim.liveUmwelt;
            var targetU = UmweltTargetFor(currentWaypoint);
            _umweltLegActive = liveU != null && targetU != null;
            if (_umweltLegActive)
            {
                liveU.Snapshot(_fromUmwelt);
                targetU.Snapshot(_toUmwelt);
                _umweltKeys = KeyedCrossfade.UnionKeys(_fromUmwelt, _toUmwelt);
            }
            _umweltInstance = liveU;
        }

        /// <summary>Shortest-arc hue interpolation on 0..1 (wraps through 1/0).</summary>
        public static float LerpHue01(float a, float b, float t)
        {
            float d = Mathf.Repeat(b - a + 0.5f, 1f) - 0.5f;
            return Mathf.Repeat(a + d * t, 1f);
        }
    }
}

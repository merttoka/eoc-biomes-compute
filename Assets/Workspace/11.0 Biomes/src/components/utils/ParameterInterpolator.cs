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

        [Header("Timing (simulation steps)")]
        [Min(1)] public int durationSteps = 600;
        [Min(0)] public int holdSteps = 0;
        public AnimationCurve easing = AnimationCurve.EaseInOut(0, 0, 1, 1);

        [Header("Per-parameter enable (click Refresh after assigning sim)")]
        public List<ParamToggle> paramToggles = new();

        [Header("Progress (read-only)")]
        [SerializeField] private Phase phase = Phase.Idle;
        [SerializeField] private int currentWaypoint;
        [SerializeField, Range(0f, 1f)] private float progress;

        // "from" snapshot: paramName -> value per type index, taken at each leg start
        private readonly Dictionary<string, float[]> _from = new();
        private int _legStartStep;
        private bool _warnedWrongType;

        private SimulationBase Sim =>
            (simManager != null && simIndex >= 0 && simIndex < simManager.simulations.Count)
                ? simManager.simulations[simIndex] : null;

        private int StepNow() => simManager != null ? simManager.SimStepCount : 0;

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
            if (waypoints == null || waypoints.Count == 0) { Debug.LogWarning("ParameterInterpolator: no waypoints assigned"); return; }

            currentWaypoint = 0;
            _warnedWrongType = false;
            SnapshotFrom();
            _legStartStep = StepNow();
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
        }

        [Button("Skip to Next")]
        public void SkipToNext()
        {
            if (phase == Phase.Interpolating || phase == Phase.Holding)
                Advance();
        }

        // ─────────── Drive ───────────

        void Update()
        {
            if (phase != Phase.Interpolating && phase != Phase.Holding) return;
            var sim = Sim;
            if (sim == null || sim.LiveParamSet == null) return;

            int elapsed = StepNow() - _legStartStep;

            if (phase == Phase.Interpolating)
            {
                float t = Mathf.Clamp01(durationSteps > 0 ? (float)elapsed / durationSteps : 1f);
                progress = t;
                ApplyLeg(sim.LiveParamSet, easing.Evaluate(t));

                if (t >= 1f)
                {
                    if (holdSteps > 0) phase = Phase.Holding;
                    else Advance();
                }
            }
            else // Holding
            {
                if (elapsed >= durationSteps + holdSteps)
                    Advance();
            }
        }

        private void ApplyLeg(IParamSet live, float te)
        {
            var target = waypoints[currentWaypoint] as IParamSet;
            if (target == null)
            {
                if (!_warnedWrongType)
                {
                    Debug.LogWarning($"ParameterInterpolator: waypoint {currentWaypoint} is not an IParamSet preset; skipping leg");
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

        private void Advance()
        {
            currentWaypoint++;
            if (currentWaypoint >= waypoints.Count)
            {
                currentWaypoint = waypoints.Count - 1;
                phase = Phase.Done;
                progress = 1f;
                return;
            }
            SnapshotFrom();
            _legStartStep = StepNow();
            phase = Phase.Interpolating;
            progress = 0f;
        }

        private void SnapshotFrom()
        {
            _from.Clear();
            var sim = Sim;
            var live = sim.LiveParamSet;
            int typeCount = live.TypeCount;
            foreach (var name in sim.ModulatableParams)
            {
                var arr = new float[typeCount];
                for (int i = 0; i < typeCount; i++)
                    arr[i] = live.GetValue(name, i);
                _from[name] = arr;
            }
        }

        /// <summary>Shortest-arc hue interpolation on 0..1 (wraps through 1/0).</summary>
        public static float LerpHue01(float a, float b, float t)
        {
            float d = Mathf.Repeat(b - a + 0.5f, 1f) - 0.5f;
            return Mathf.Repeat(a + d * t, 1f);
        }
    }
}

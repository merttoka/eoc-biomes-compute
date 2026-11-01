using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Morphs sims' live params toward other preset assets once the neuron playback passes a blob
    /// frame, e.g. the ketamine half of a baseline | ketamine blob drifting into another show's
    /// presets. The weight is a pure function of the playhead (0 before <see cref="startFrame"/>,
    /// eased to 1 over <see cref="morphSeconds"/> of data after it), so it lands identically live,
    /// after a Seek, and in a Recorder take. The "from" values are the live params when the weight
    /// leaves 0, and are written back when it returns to 0 (loop wrap into the baseline half).
    /// Targets are cloned and resolution-scaled like the sim's own preset clone, so pixel-unit
    /// params compare like for like.
    /// </summary>
    public class NeuronFiringParamMorph : MonoBehaviour
    {
        [Serializable]
        public class Target
        {
            public SimulationBase sim;
            [Tooltip("Preset asset of the sim's params type (PhysarumParams, TermiteParams, BoidParams, ...).")]
            public ScriptableObject preset;
        }

        public NeuronFiringPlayback playback;
        [Tooltip("Blob frame the morph starts on (e.g. the sidecar's ketamine startFrame).")]
        [Min(0)] public int startFrame = 18000;
        [Tooltip("Data seconds from startFrame to the full target.")]
        [Min(0.01f)] public float morphSeconds = 30f;
        public AnimationCurve easing = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [Tooltip("Also morph hue / saturation / brightness. Off keeps the palette's colors.")]
        public bool includeColor;
        public List<Target> targets = new();

        private static readonly HashSet<string> s_Color = new() { "hue", "saturation", "brightness" };

        private sealed class State
        {
            public IParamSet to;                 // scaled clone of the preset
            public IParamSet live;               // live clone "from" was taken from
            public Dictionary<string, float[]> from;
        }

        private readonly Dictionary<Target, State> _state = new();

        public float Weight
        {
            get
            {
                if (playback == null) return 0f;
                int f = playback.Frame;
                if (f < startFrame) return 0f;
                float dataFps = (playback.endFrame - playback.startFrame) / Mathf.Max(0.01f, playback.durationSeconds);
                return easing.Evaluate(Mathf.Clamp01((f - startFrame) / (morphSeconds * dataFps)));
            }
        }

        void LateUpdate()
        {
            if (playback == null || !playback.playing) return;
            float w = Weight;
            foreach (var t in targets)
            {
                if (t.sim == null || t.preset is not IParamSet || t.sim.LiveParamSet == null) continue;
                if (!_state.TryGetValue(t, out var s)) _state[t] = s = new State();
                var live = t.sim.LiveParamSet;

                if (w <= 0f)
                {
                    // Back before the start: restore once, then leave the live params alone.
                    if (s.from != null && ReferenceEquals(s.live, live)) Apply(t.sim, live, s.from, s.to, 0f);
                    s.from = null;
                    continue;
                }
                // A sim Reset re-clones the live params from its preset: that clone is the new "from".
                if (s.from == null || !ReferenceEquals(s.live, live)) Capture(t.sim, live, s);
                s.to ??= ScaledClone(t.sim, t.preset);
                Apply(t.sim, live, s.from, s.to, w);
            }
        }

        private void Capture(SimulationBase sim, IParamSet live, State s)
        {
            s.live = live;
            s.from = new Dictionary<string, float[]>();
            foreach (var name in sim.ModulatableParams)
            {
                if (!includeColor && s_Color.Contains(name)) continue;
                var arr = new float[live.TypeCount];
                for (int i = 0; i < arr.Length; i++) arr[i] = live.GetValue(name, i);
                s.from[name] = arr;
            }
        }

        // Live types beyond the preset's count reuse its types cyclically (8 live types → a 4-type preset: 0..3, 0..3).
        private static void Apply(SimulationBase sim, IParamSet live, Dictionary<string, float[]> from, IParamSet to, float w)
        {
            int toCount = to != null ? to.TypeCount : 0;
            foreach (var kv in from)
                for (int i = 0; i < live.TypeCount && i < kv.Value.Length; i++)
                {
                    float a = kv.Value[i], b = toCount > 0 ? to.GetValue(kv.Key, i % toCount) : a;
                    live.SetValue(kv.Key, i, kv.Key == "hue" ? ParameterInterpolator.LerpHue01(a, b, w) : Mathf.Lerp(a, b, w));
                }
        }

        // Same scaling SimulationBase.Reset applies to the sim's own clone.
        private IParamSet ScaledClone(SimulationBase sim, ScriptableObject preset)
        {
            var clone = Instantiate(preset);
            clone.name = preset.name + " (morph)";
            var p = (IParamSet)clone;
            float k = sim.referenceHeight > 0f && sim.rezY > 0 ? sim.rezY / sim.referenceHeight : 1f;
            if (sim.scaleSpatialToResolution) p.ScaleSpatial(k);
            if (sim.scaleDensityToResolution) p.ScaleDensity(k);
            return p;
        }

        void OnDestroy()
        {
            foreach (var s in _state.Values)
                if (s.to is ScriptableObject so) Destroy(so);
            _state.Clear();
        }
    }
}

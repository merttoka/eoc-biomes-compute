using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Writes <c>cues.json</c> alongside the render so Max/MSP can compose sound against
    /// picture after lock. Screen 1.2 (Jingyao Hongqiao) carries sound; 2.6 (Xinda Plaza)
    /// does not, so audio is authored once against the same master.
    ///
    /// <para><b>Frames, not seconds, are the contract.</b> The render is offline at 60 fps
    /// against a fixed 60 Hz sim, a 1:1 step-to-frame ratio, so every cue's sim step IS its
    /// frame number and 90 s is exactly 5400 of both. Seconds are exported too, but the frame
    /// index is the value that cannot drift — a non-integer ratio would make it approximate,
    /// which is exactly why the show is authored at 1:1.</para>
    ///
    /// <para>Runs after ShowArc so a cue recorded this step is captured on the same step it
    /// fired.</para>
    /// </summary>
    [DefaultExecutionOrder(-80)]
    public class CueExporter : MonoBehaviour
    {
        [Header("Wiring")]
        public ShowArc arc;
        public SimulationManager simManager;
        public NeuronFiringSource firingSource;

        [Header("Capture")]
        [Tooltip("Record neuron firing onsets while the arc runs. A neuron is logged when it " +
                 "crosses the threshold upward, so a single spike is one event rather than one " +
                 "per frame of its decay tail.")]
        public bool captureFiring = true;
        [Range(0f, 1f)] public float firingOnsetThreshold = 0.35f;
        [Tooltip("Safety cap so a long session cannot grow the log without bound.")]
        [Min(0)] public int maxFiringEvents = 200000;

        [Header("Output")]
        [Tooltip("Written relative to the project root (next to Assets/), matching where the " +
                 "Recorder writes by default.")]
        public string outputPath = "Recordings/cues.json";
        [Tooltip("Frame rate the exported frame indices assume. Must match the Recorder's rate.")]
        public float exportFps = 60f;
        [Tooltip("Export automatically the first time the arc completes a loop.")]
        public bool autoExportOnLoopEnd = true;

        private readonly List<FiringEvent> _firing = new();
        private bool[] _wasAbove;
        private bool _exported;
        private float _lastArcSeconds = -1f;

        private struct FiringEvent
        {
            public int neuron;
            public int frame;
            public float seconds;
            public float intensity;
        }

        public int FiringEventCount => _firing.Count;

        void OnEnable()
        {
            _firing.Clear();
            _wasAbove = null;
            _exported = false;
            _lastArcSeconds = -1f;
        }

        void FixedUpdate()
        {
            if (arc == null) return;

            float now = arc.ArcSeconds;

            // Loop wrap detected by time going backwards — the arc is the only clock here.
            if (_lastArcSeconds >= 0f && now < _lastArcSeconds)
            {
                if (autoExportOnLoopEnd && !_exported) Export();
                _firing.Clear();
                if (_wasAbove != null) System.Array.Clear(_wasAbove, 0, _wasAbove.Length);
            }
            _lastArcSeconds = now;

            if (captureFiring) CaptureFiring(now);
        }

        private void CaptureFiring(float seconds)
        {
            if (firingSource == null) return;
            var scaled = firingSource.ScaledValues;
            if (scaled == null || scaled.Length == 0) return;

            if (_wasAbove == null || _wasAbove.Length != scaled.Length)
                _wasAbove = new bool[scaled.Length];

            int frame = Mathf.RoundToInt(seconds * Mathf.Max(1f, exportFps));

            for (int i = 0; i < scaled.Length; i++)
            {
                bool above = scaled[i] >= firingOnsetThreshold;
                // Rising edge only. Firing decays over ~0.5 s, so a level test would log the
                // same spike on every frame of its tail and bury the actual onsets.
                if (above && !_wasAbove[i] && _firing.Count < maxFiringEvents)
                {
                    _firing.Add(new FiringEvent
                    {
                        neuron = i,
                        frame = frame,
                        seconds = seconds,
                        intensity = scaled[i],
                    });
                }
                _wasAbove[i] = above;
            }
        }

        [Button("Export cues.json")]
        public void Export()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1024 + _firing.Count * 72);

            float fps = Mathf.Max(1f, exportFps);
            float loopSeconds = arc != null ? arc.loopSeconds : 90f;
            float simRate = simManager != null ? simManager.simRate : 60f;

            sb.Append("{\n");
            sb.Append("  \"format\": \"eoc-biomes-cues/1\",\n");
            sb.Append("  \"scene\": \"Scene_DAC\",\n");
            sb.Append("  \"note\": \"Offline render at a 1:1 sim-step to frame ratio; frame index is authoritative.\",\n");
            sb.AppendFormat(inv, "  \"fps\": {0},\n", fps);
            sb.AppendFormat(inv, "  \"simRate\": {0},\n", simRate);
            sb.AppendFormat(inv, "  \"loopSeconds\": {0},\n", loopSeconds);
            sb.AppendFormat(inv, "  \"loopFrames\": {0},\n", Mathf.RoundToInt(loopSeconds * fps));

            // Arc structure: the named keyframes a composer scores against, exported whether
            // or not the arc has run, so cues.json is useful before the first render.
            sb.Append("  \"arc\": [\n");
            if (arc != null)
            {
                AppendArcCue(sb, inv, "start", 0f, fps, false);
                AppendArcCue(sb, inv, "many", arc.manySeconds, fps, false);
                AppendArcCue(sb, inv, "converge", arc.convergeSeconds, fps, false);
                AppendArcCue(sb, inv, "oneBody", arc.oneBodySeconds, fps, false);
                AppendArcCue(sb, inv, "loop", arc.loopSeconds, fps, true);
            }
            sb.Append("  ],\n");

            // Cues actually fired during the run (currently ResetTermites at the loop point).
            sb.Append("  \"fired\": [\n");
            if (arc != null)
            {
                var cues = arc.Cues;
                for (int i = 0; i < cues.Count; i++)
                {
                    var c = cues[i];
                    sb.AppendFormat(inv,
                        "    {{ \"name\": \"{0}\", \"seconds\": {1:0.####}, \"frame\": {2}, \"simStep\": {3} }}{4}\n",
                        c.name, c.seconds, Mathf.RoundToInt(c.seconds * fps), c.simStep,
                        i == cues.Count - 1 ? "" : ",");
                }
            }
            sb.Append("  ],\n");

            sb.AppendFormat(inv, "  \"firingOnsetThreshold\": {0},\n", firingOnsetThreshold);
            sb.Append("  \"firing\": [\n");
            for (int i = 0; i < _firing.Count; i++)
            {
                var f = _firing[i];
                sb.AppendFormat(inv,
                    "    {{ \"neuron\": {0}, \"frame\": {1}, \"seconds\": {2:0.####}, \"intensity\": {3:0.###} }}{4}\n",
                    f.neuron, f.frame, f.seconds, f.intensity, i == _firing.Count - 1 ? "" : ",");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");

            string full = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".", outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            File.WriteAllText(full, sb.ToString());

            _exported = true;
            Debug.Log($"[CueExporter] Wrote {full} — {_firing.Count} firing onsets, " +
                      $"{(arc != null ? arc.Cues.Count : 0)} fired cues @ {fps} fps.", this);
        }

        private static void AppendArcCue(StringBuilder sb, CultureInfo inv,
            string name, float seconds, float fps, bool last)
        {
            sb.AppendFormat(inv,
                "    {{ \"name\": \"{0}\", \"seconds\": {1:0.####}, \"frame\": {2} }}{3}\n",
                name, seconds, Mathf.RoundToInt(seconds * fps), last ? "" : ",");
        }
    }
}

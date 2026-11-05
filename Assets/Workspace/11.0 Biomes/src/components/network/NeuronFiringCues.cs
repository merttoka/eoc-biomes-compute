using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Show actions keyed to the neuron playback's blob frame, e.g. the boundary between two
    /// recordings in one blob (baseline | ketamine). A cue can crossfade the agent palette and/or
    /// fade the output to black, reset, and fade back in, so each condition starts from a fresh
    /// ecosystem. Frames are data frames, so cues land identically live and in a Recorder take.
    /// A jump larger than <see cref="seekFrames"/> (a Seek, not a loop wrap) fires nothing; it snaps
    /// the palette to the condition the playhead landed in.
    /// </summary>
    public class NeuronFiringCues : MonoBehaviour
    {
        [Serializable]
        public class Cue
        {
            public string label = "cue";
            [Tooltip("Blob frame the cue lands on (e.g. the sidecar's segment startFrame).")]
            [Min(0)] public int frame;

            [Header("Palette")]
            public bool setPalette;
            [Tooltip("Palette from the manager's paletteCycle list. Empty = Preset (the authored colors).")]
            public AgentColorPalette palette;
            [Min(0f)] public float paletteFadeSeconds = 6f;

            [Header("Reset")]
            [Tooltip("Fade to black over blackoutSeconds before the frame, reset on it, fade back in.")]
            public bool reset;
            [Tooltip("Full Reset (sims + biome fields) instead of Reset Sims Only.")]
            public bool resetBiome = true;
            [Min(0f)] public float blackoutSeconds = 2.5f;
        }

        public NeuronFiringPlayback playback;
        public SimulationManager manager;
        public List<Cue> cues = new();
        [Tooltip("A frame step larger than this (that is not a loop wrap) is treated as a seek.")]
        [Min(1)] public int seekFrames = 120;

        private int _last = -1;

        void OnEnable() => _last = -1;

        void LateUpdate()
        {
            if (playback == null || manager == null || !playback.playing) return;
            int f = playback.Frame;
            if (_last < 0) { SnapPalette(f); _last = f; return; }
            if (f == _last) return;

            int start = playback.startFrame, end = playback.endFrame;
            bool wrapped = f < _last && _last > end - seekFrames && f < start + seekFrames;
            if (!wrapped && Mathf.Abs(f - _last) > seekFrames) { SnapPalette(f); _last = f; return; }

            float dataFps = (end - start) / Mathf.Max(0.01f, playback.durationSeconds);
            foreach (var c in cues)
            {
                if (c.reset)
                {
                    int lead = Mathf.RoundToInt(c.blackoutSeconds * dataFps);
                    if (Crossed(Wrap(c.frame - lead, start, end), f, wrapped)) manager.SetOutputOn(false, c.blackoutSeconds);
                }
                if (!Crossed(c.frame, f, wrapped)) continue;
                if (c.reset)
                {
                    if (c.resetBiome) manager.Reset(); else manager.ResetSimsOnly();
                    manager.SetOutputOn(true, c.blackoutSeconds);
                }
                if (c.setPalette) SelectPalette(c, instant: false);
            }
            _last = f;
        }

        // Forward step from _last to f, or a wrap through the range end.
        private bool Crossed(int x, int f, bool wrapped) =>
            wrapped ? x > _last || x <= f : _last < x && x <= f;

        private static int Wrap(int x, int start, int end)
        {
            int span = end - start + 1;
            return start + (((x - start) % span) + span) % span;
        }

        // The palette of the last palette cue at or before f (wrapping: before the first cue,
        // the range's last cue still holds).
        private void SnapPalette(int f)
        {
            Cue best = null, latest = null;
            foreach (var c in cues)
            {
                if (!c.setPalette) continue;
                if (c.frame <= f && (best == null || c.frame > best.frame)) best = c;
                if (latest == null || c.frame > latest.frame) latest = c;
            }
            if ((best ?? latest) is Cue cue) SelectPalette(cue, instant: true);
        }

        private void SelectPalette(Cue c, bool instant)
        {
            int index = c.palette == null ? -1 : manager.paletteCycle.palettes.IndexOf(c.palette);
            if (c.palette != null && index < 0)
            {
                Debug.LogWarning($"[NeuronFiringCues] '{c.label}': {c.palette.name} is not in the manager's paletteCycle list.");
                return;
            }
            manager.paletteCycle.fadeSeconds = c.paletteFadeSeconds;
            manager.SelectPalette(index, instant);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Live palette cycling for the agent sims a SimulationManager runs. The cycle is Preset (each
    /// sim's authored colors) then <see cref="palettes"/>; a change fades every type's hue (shortest
    /// arc), saturation and brightness over <see cref="fadeSeconds"/>. The active palette outlives
    /// resets: the manager calls <see cref="Reimpose"/> on each freshly reset sim.
    /// </summary>
    [Serializable]
    public class AgentPaletteCycler
    {
        [Tooltip("Palettes in cycle order. Next/Previous wrap through Preset (the authored colors).")]
        public List<AgentColorPalette> palettes = new();
        [Tooltip("Seconds a palette change fades over (unscaled time). 0 = instant.")]
        [Min(0f)] public float fadeSeconds = 2f;

        [NonSerialized] private int _active = -1;        // −1 = Preset; every Play starts there
        [NonSerialized] private float _progress = 1f;    // 1 = settled
        // Each faded sim's per-type colors when the current change began.
        private readonly Dictionary<SimulationBase, (float h, float s, float b)[]> _from = new();

        public int ActiveIndex => _active;
        public string ActiveName => _active < 0 ? "Preset" : PaletteAt(_active) != null ? PaletteAt(_active).name : "(missing palette)";

        /// <summary>Starts a change to palette <paramref name="index"/> (−1 = Preset; clamped) on
        /// every started agent sim; <paramref name="instant"/> (or fadeSeconds 0) applies at once.</summary>
        public void Select(int index, IReadOnlyList<SimulationBase> sims, bool instant)
        {
            _active = Mathf.Clamp(index, -1, palettes.Count - 1);
            _from.Clear();
            foreach (var sim in sims)
            {
                if (sim == null || sim.runState == SimRunState.Stopped || sim.LiveParamSet is not IAgentColorParams p) continue;
                var from = new (float h, float s, float b)[p.TypeCount];
                for (int i = 0; i < from.Length; i++) from[i] = p.GetHsb(i);
                _from[sim] = from;
            }
            _progress = instant || fadeSeconds <= 0f ? 1f : 0f;
            foreach (var sim in _from.Keys) Write(sim);
        }

        /// <summary>Advances the fade; call once per rendered frame.</summary>
        public void Tick(float deltaTime)
        {
            if (_progress >= 1f) return;
            _progress = fadeSeconds > 0f ? Mathf.Min(1f, _progress + deltaTime / fadeSeconds) : 1f;
            foreach (var sim in _from.Keys) Write(sim);
        }

        /// <summary>Puts the active palette (at the fade's current point) on a sim that was just
        /// reset or started, whose fresh params carry the preset's colors.</summary>
        public void Reimpose(SimulationBase sim)
        {
            if (_active < 0 && _progress >= 1f) return;   // settled on Preset: the reset already restored it
            Write(sim);
        }

        private AgentColorPalette PaletteAt(int i) => i >= 0 && i < palettes.Count ? palettes[i] : null;

        private void Write(SimulationBase sim)
        {
            if (sim == null || sim.LiveParamSet is not IAgentColorParams p) return;
            _from.TryGetValue(sim, out var from);
            float t = Mathf.SmoothStep(0f, 1f, _progress);
            for (int i = 0; i < p.TypeCount; i++)
            {
                // A sim that was not running when the change began has nothing to fade from: it jumps.
                bool fades = from != null && i < from.Length;
                var a = fades ? from[i] : p.GetHsb(i);
                // A settled fade writes the target itself: the hue lerp's wrap can land on 1.0 for 0.
                if (TryTarget(sim, p, i, a, out var to))
                    p.SetHsb(i, fades && _progress < 1f ? Lerp(a, to, t) : to);
            }
        }

        private bool TryTarget(SimulationBase sim, IAgentColorParams p, int type, (float h, float s, float b) current,
            out (float h, float s, float b) target)
        {
            target = default;
            if (_active < 0)
            {
                if (sim.PresetParamSet is not IParamSet preset || type >= preset.TypeCount) return false;
                target = preset.GetHsb(type);
                return true;
            }
            var palette = PaletteAt(_active);
            return palette != null && palette.TryGetTarget(p.Family, type, current.h, current.s, out target);
        }

        private static (float h, float s, float b) Lerp((float h, float s, float b) a, (float h, float s, float b) b, float t) =>
            (ParameterInterpolator.LerpHue01(a.h, b.h, t), Mathf.Lerp(a.s, b.s, t), Mathf.Lerp(a.b, b.b, t));
    }
}

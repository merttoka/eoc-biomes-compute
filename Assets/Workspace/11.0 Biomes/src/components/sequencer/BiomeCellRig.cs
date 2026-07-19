using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// One live biome cell: wraps a nested SimulationManager (own Biome + sims at reduced
    /// rez, own preset assets) and steps it at its own rate, decoupled from the main sim's
    /// fixed clock. The wrapped manager must have ownsGlobalTiming = false and
    /// stepsPerTick = 0 (so its FixedUpdate does nothing; this rig is the only stepper).
    /// The Timeline BiomeCellMixer sets Running while a cell clip is active.
    /// </summary>
    public class BiomeCellRig : MonoBehaviour
    {
        /// <summary>The nested SimulationManager this rig steps. Must have ownsGlobalTiming = false and stepsPerTick = 0.</summary>
        [Tooltip("Nested manager: ownsGlobalTiming OFF, stepsPerTick 0, rez ~1024.")]
        public SimulationManager manager;

        /// <summary>Cell step rate in Hz, clamped 1-60. Independent of the main manager's simRate.</summary>
        [Tooltip("Rig's own step rate (Hz), independent of the main sim's simRate.")]
        [Range(1f, 60f)] public float cellRate = 20f;

        /// <summary>True while a Timeline clip has this cell active. Manager only steps while true.</summary>
        [Tooltip("Set by the Timeline mixer while a cell clip is active. Manual for testing.")]
        public bool Running;

        private float _accum;
        private bool _warnedStepsPerTick;

        /// <summary>Cell source texture for the composer (null until manager Reset).</summary>
        public RenderTexture OutputTexture => manager != null ? manager.CompositeOutputTexture : null;

        void Update()
        {
            if (!Running || manager == null || !Application.isPlaying) return;
            _accum += Time.deltaTime;
            float dt = 1f / Mathf.Max(1f, cellRate);
            int guard = 0;                      // spiral-of-death guard, mirrors main sim
            while (_accum >= dt && guard++ < 4)
            {
                if (!_warnedStepsPerTick && manager != null && manager.stepsPerTick != 0)
                {
                    Debug.LogWarning($"[BiomeCellRig] {name}: nested manager stepsPerTick={manager.stepsPerTick} != 0 — it will ALSO step from FixedUpdate, double-stepping the cell. Set stepsPerTick to 0 on cell-rig managers.", this);
                    _warnedStepsPerTick = true;
                }
                manager.Step();
                _accum -= dt;
            }
            if (_accum >= dt) _accum = 0f;      // dropped time: cell slows, never bursts
        }
    }
}

using UnityEngine;

namespace Biomes
{
    public enum BurstPhase { Idle = 0, Attack = 1, Sustain = 2, Release = 3 }

    /// <summary>
    /// The clock for one event-driven CA burst. Pure and allocation-free so it can be
    /// unit-tested away from Unity — the same reason <see cref="NeuronLayout"/> and
    /// <see cref="CellGrid"/> live in this assembly.
    ///
    /// <para><b>Durations are SIM steps, never rule steps.</b> <c>stepEvery</c> decimates only
    /// the rule, so counting a burst in rule steps would make its wall-clock length change
    /// whenever the author retuned the CA's pace.</para>
    ///
    /// <para><b>Retrigger extends, it does not restart.</b> <see cref="Trigger"/> reports
    /// whether the caller must re-seed, and that is true only from <see cref="BurstPhase.Idle"/>.
    /// Sustained firing therefore sustains one evolving lattice rather than repeatedly wiping
    /// it. The envelope is rate-based rather than looked up from age precisely so a retrigger
    /// mid-sustain continues from the current value instead of dipping to zero.</para>
    /// </summary>
    public struct BurstEnvelope
    {
        public BurstPhase Phase;
        /// <summary>Sim steps since the most recent trigger.</summary>
        public int Age;
        /// <summary>0..1 output multiplier.</summary>
        public float Value;

        public bool IsIdle => Phase == BurstPhase.Idle;

        /// <summary>Start or extend a burst. Returns true when the grid must be re-seeded.</summary>
        public bool Trigger()
        {
            bool reseed = Phase == BurstPhase.Idle;
            Phase = BurstPhase.Attack;
            Age = 0;
            return reseed;
        }

        /// <summary>Advance exactly one sim step.</summary>
        public void Advance(int fadeInSteps, int sustainSteps, int fadeOutSteps)
        {
            int fadeIn  = Mathf.Max(0, fadeInSteps);
            int sustain = Mathf.Max(0, sustainSteps);
            int fadeOut = Mathf.Max(0, fadeOutSteps);

            switch (Phase)
            {
                case BurstPhase.Idle:
                    Value = 0f;
                    Age = 0;
                    break;

                case BurstPhase.Attack:
                    Age++;
                    Value = fadeIn <= 0 ? 1f : Mathf.Min(1f, Value + 1f / fadeIn);
                    if (Age >= fadeIn) { Value = 1f; Phase = BurstPhase.Sustain; }
                    break;

                case BurstPhase.Sustain:
                    Age++;
                    Value = 1f;
                    if (Age >= fadeIn + sustain) Phase = BurstPhase.Release;
                    break;

                case BurstPhase.Release:
                    Age++;
                    Value = fadeOut <= 0 ? 0f : Mathf.Max(0f, Value - 1f / fadeOut);
                    if (Value <= 0f) { Value = 0f; Age = 0; Phase = BurstPhase.Idle; }
                    break;
            }
        }
    }

    /// <summary>
    /// Fires once when a signal crosses a threshold upward, and not again until it has fallen
    /// back below. Without this, any sampling of a firing level above threshold would retrigger
    /// on every single step and the burst would never end.
    /// </summary>
    public struct RisingEdge
    {
        private bool _wasAbove;

        public bool Update(float value, float threshold)
        {
            bool above = value >= threshold;
            bool fired = above && !_wasAbove;
            _wasAbove = above;
            return fired;
        }
    }
}

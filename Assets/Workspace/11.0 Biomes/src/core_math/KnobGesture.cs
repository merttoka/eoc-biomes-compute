using System;

namespace Biomes
{
    /// <summary>What press-and-turn moves on a knob bound to a sim param.</summary>
    public enum PressTurnScope
    {
        Off,
        AllTypesOfSim,
        AllSims,
    }

    public static class PressTurnScopes
    {
        /// <summary>Whether press-and-turn on a knob moves its param on a given sim.</summary>
        public static bool Includes(PressTurnScope scope, bool isKnobsOwnSim, bool simHasParam)
        {
            switch (scope)
            {
                case PressTurnScope.AllTypesOfSim: return isKnobsOwnSim;
                case PressTurnScope.AllSims: return simHasParam;
                default: return false;
            }
        }
    }

    /// <summary>
    /// Tells a tap from a press-and-turn on each knob. The push action has to wait for release, since it can't be known
    /// at the press whether a turn will follow.
    /// </summary>
    public sealed class KnobPressState
    {
        private readonly bool[] _pressed;
        private readonly bool[] _turned;

        public KnobPressState(int knobCount)
        {
            _pressed = new bool[knobCount];
            _turned = new bool[knobCount];
        }

        public bool IsPressed(int knob) => _pressed[knob];

        public void Press(int knob)
        {
            _pressed[knob] = true;
            _turned[knob] = false;
        }

        public void Turned(int knob)
        {
            if (_pressed[knob]) _turned[knob] = true;
        }

        /// <summary>Forgets every press: a release that arrives later is not a tap, and turns are plain turns.</summary>
        public void ReleaseAll() => Array.Clear(_pressed, 0, _pressed.Length);

        /// <summary>Returns true when this release ends a tap: pressed, and not turned while held.</summary>
        public bool Release(int knob)
        {
            bool tapped = _pressed[knob] && !_turned[knob];
            _pressed[knob] = false;
            return tapped;
        }
    }
}

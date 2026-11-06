using System;

namespace Biomes
{
    /// <summary>
    /// One wheel gesture over an inspector field. It decides once which way it set off, and stays that way: a vertical
    /// scroll never moves a field however it drifts, and a sideways swipe ignores its own vertical drift.
    /// </summary>
    public sealed class ScrollGesture
    {
        private enum Way { Undecided, Sideways, Vertical }

        private readonly float _slop;
        private Way _way;
        private float _x, _y;

        /// <param name="slop">How far a gesture travels, in wheel units, before its direction is decided.</param>
        public ScrollGesture(float slop) => _slop = slop;

        /// <summary>
        /// Feeds one wheel event. Returns the sideways travel to apply, 0 while the direction is undecided (that travel
        /// is held, and applied if it turns out sideways), or null once the gesture is vertical.
        /// </summary>
        public float? Feed(float dx, float dy)
        {
            if (_way == Way.Vertical) return null;

            _x += dx;
            _y += dy;
            if (_way == Way.Undecided)
            {
                if (Math.Sqrt(_x * _x + _y * _y) < _slop) return 0f;
                _way = Math.Abs(_x) > Math.Abs(_y) ? Way.Sideways : Way.Vertical;
                if (_way == Way.Vertical) return null;
            }

            float travel = _x;
            _x = 0f;
            _y = 0f;
            return travel;
        }
    }
}

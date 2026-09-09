using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// A wall-clock playhead over an inclusive frame range [Start, End]. Pure and
    /// allocation-free so it can be unit-tested outside play mode; NeuronFiringPlayback
    /// wraps it and pushes the frame into NeuronFiringSource.SetFrame, the same entry the
    /// OSC /index stream uses, so nothing downstream can tell a local pass from a remote one.
    ///
    /// Position is fractional (frames since Start) so a 50 fps range advanced at 60 Hz
    /// lands on every frame exactly once instead of stuttering on integer rounding.
    /// </summary>
    public sealed class FramePlayhead
    {
        public int Start { get; private set; }
        /// <summary>Inclusive.</summary>
        public int End { get; private set; }
        public float DurationSeconds { get; private set; } = 1f;
        public bool Loop { get; set; }

        /// <summary>Fractional frames elapsed since Start, in [0, Length).</summary>
        public float Position { get; private set; }
        /// <summary>True once a non-looping pass has reached End; Advance holds there.</summary>
        public bool Finished { get; private set; }

        public int Length => End - Start + 1;
        public float FramesPerSecond => Length / DurationSeconds;
        public int Frame => Start + Mathf.Min((int)Position, Length - 1);
        /// <summary>0..1 through the range (1 on the last frame).</summary>
        public float Progress => Length > 1 ? (Frame - Start) / (float)(Length - 1) : 1f;

        /// <summary>Set the range and speed. Swaps a reversed range, floors the duration at a
        /// hair above zero, and rewinds to Start.</summary>
        public void Configure(int start, int end, float durationSeconds, bool loop)
        {
            if (end < start) (start, end) = (end, start);
            Start = start;
            End = end;
            DurationSeconds = Mathf.Max(1e-3f, durationSeconds);
            Loop = loop;
            Reset();
        }

        public void Reset()
        {
            Position = 0f;
            Finished = false;
        }

        /// <summary>Jump to an absolute blob frame, clamped into the range.</summary>
        public void Seek(int frame)
        {
            Position = Mathf.Clamp(frame - Start, 0, Length - 1);
            Finished = false;
        }

        /// <summary>Advance by dt seconds and return the frame to display.</summary>
        public int Advance(float dt)
        {
            if (Finished || dt <= 0f) return Frame;

            Position += dt * FramesPerSecond;
            if (Position >= Length)
            {
                if (Loop)
                {
                    Position %= Length;
                }
                else
                {
                    Position = Length - 1;
                    Finished = true;
                }
            }
            return Frame;
        }
    }
}

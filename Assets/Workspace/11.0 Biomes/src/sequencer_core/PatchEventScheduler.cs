using System;
using System.Collections.Generic;

namespace Biomes.Sequencer
{
    /// <summary>Axis-aligned rect in normalized [0,1] texture coords.</summary>
    public struct PatchRect
    {
        public float x, y, w, h;

        public PatchRect(float x, float y, float w, float h)
        {
            this.x = x; this.y = y; this.w = w; this.h = h;
        }

        public bool Overlaps(in PatchRect o) =>
            x < o.x + o.w && o.x < x + w && y < o.y + o.h && o.y < y + h;
    }

    /// <summary>One scheduled patch: where it lands (dst), what it samples (src),
    /// and its lifetime in clip-local frames. crossfadeRoll vs Sigmoid(anchorT)
    /// picks source A or B (per-patch stochastic dissolve).</summary>
    public struct PatchEvent
    {
        public PatchRect dst;
        public PatchRect src;
        public int start;
        public int holdEnd;
        public int fadeEnd;
        public float crossfadeRoll;
        public float anchorT;       // normalized position of this patch's anchor in the clip
    }

    /// <summary>Deterministic inputs for Generate(). One instance per PatchScatterClip.</summary>
    public class PatchScatterConfig
    {
        public int seed = 1234;
        public int count = 64;                 // requested events over the clip (rejections may yield fewer)
        public float minSize = 0.03f;          // dst height, normalized
        public float maxSize = 0.25f;
        public float aspect = 5f;              // composer W/H; dst width = height / aspect → square in pixels
        public int holdMinFrames = 9;          // large patches flash…
        public int holdMaxFrames = 90;         // …small patches linger (size→hold inversion)
        public int fadeFrames = 30;
        public int leadFrames = 150;           // patch may appear this many frames BEFORE its anchor
        public int trailFrames = 90;           // …or this many after (asymmetric spread)
        public int staggerJitterFrames = 12;
        public float crossfadeCenter = 0.5f;
        public float crossfadeWidth = 0.15f;
        public int durationFrames = 1800;
        public int maxRejects = 40;            // rejection-sampling tries per patch
    }

    /// <summary>Anadol-grammar patch scheduling, ported from SimAesthetics
    /// render_overlay_video.py. Pure C# — deterministic from (config.seed), no engine refs.
    /// Determinism assumes System.Random's algorithm (stable within a given Unity runtime,
    /// not guaranteed across .NET versions). Hold/fade windows may intentionally extend
    /// past durationFrames (trailing fade).</summary>
    public static class PatchEventScheduler
    {
        /// <summary>Hard cap on events returned by Generate, regardless of cfg.count.</summary>
        public const int MaxEventsPerClip = 512;

        /// <summary>Reversed size→duration mapping: sizeNorm01=1 (largest) → holdMin
        /// (flash and vanish), sizeNorm01=0 (smallest) → holdMax (linger).</summary>
        public static int SizeToHoldFrames(float sizeNorm01, int holdMin, int holdMax)
        {
            if (sizeNorm01 < 0f) sizeNorm01 = 0f;
            if (sizeNorm01 > 1f) sizeNorm01 = 1f;
            return holdMax - (int)Math.Round((holdMax - holdMin) * (double)sizeNorm01);
        }

        /// <summary>Alpha envelope: 1 during [start, holdEnd), linear fade to 0 over
        /// [holdEnd, fadeEnd), 0 outside.</summary>
        public static float Envelope(in PatchEvent e, int frame)
        {
            if (frame < e.start || frame >= e.fadeEnd) return 0f;
            if (frame < e.holdEnd) return 1f;
            float denom = e.fadeEnd - e.holdEnd;
            return denom <= 0f ? 0f : 1f - (frame - e.holdEnd) / denom;
        }

        /// <summary>Logistic curve over normalized clip time; compare a patch's
        /// crossfadeRoll against this to pick source A (roll ≥ sigmoid) or B (roll &lt; sigmoid).</summary>
        public static float Sigmoid(float t, float center, float width)
        {
            if (width < 1e-5f) width = 1e-5f;
            return 1f / (1f + (float)Math.Exp(-(t - center) / width));
        }

        public static PatchEvent[] Generate(PatchScatterConfig cfg)
        {
            int count = Math.Min(cfg.count, MaxEventsPerClip);
            var rng = new Random(cfg.seed);
            var events = new List<PatchEvent>(count);

            for (int i = 0; i < count; i++)
            {
                // Anchor spreads patches uniformly across the clip; the actual start is
                // offset asymmetrically (lead/trail) + jitter so appearances cascade.
                float anchorT = (i + 0.5f) / count;
                int anchor = (int)(anchorT * cfg.durationFrames);

                float sizeH = Lerp(cfg.minSize, cfg.maxSize, (float)rng.NextDouble());
                float sizeNorm01 = cfg.maxSize > cfg.minSize
                    ? (sizeH - cfg.minSize) / (cfg.maxSize - cfg.minSize) : 0f;
                float sizeW = sizeH / Math.Max(0.01f, cfg.aspect);

                int hold = SizeToHoldFrames(sizeNorm01, cfg.holdMinFrames, cfg.holdMaxFrames);
                int spread = (int)(Lerp(-cfg.leadFrames, cfg.trailFrames, (float)rng.NextDouble()));
                int jitter = rng.Next(-cfg.staggerJitterFrames, cfg.staggerJitterFrames + 1);
                int start = Clamp(anchor + spread + jitter, 0, Math.Max(0, cfg.durationFrames - 1));
                int holdEnd = start + Math.Max(1, hold);
                int fadeEnd = holdEnd + Math.Max(1, cfg.fadeFrames);

                // Rejection sampling: dst must not overlap any event it will be
                // on screen with. Give up after maxRejects (skip the patch).
                bool placed = false;
                for (int attempt = 0; attempt < cfg.maxRejects && !placed; attempt++)
                {
                    var dst = new PatchRect(
                        (float)rng.NextDouble() * (1f - sizeW),
                        (float)rng.NextDouble() * (1f - sizeH),
                        sizeW, sizeH);

                    bool collides = false;
                    for (int k = 0; k < events.Count; k++)
                    {
                        var other = events[k];
                        bool timeOverlap = start < other.fadeEnd && other.start < fadeEnd;
                        if (timeOverlap && dst.Overlaps(other.dst)) { collides = true; break; }
                    }
                    if (collides) continue;

                    // src: random sub-rect of the source texture, same normalized size.
                    var src = new PatchRect(
                        (float)rng.NextDouble() * (1f - sizeW),
                        (float)rng.NextDouble() * (1f - sizeH),
                        sizeW, sizeH);

                    events.Add(new PatchEvent
                    {
                        dst = dst,
                        src = src,
                        start = start,
                        holdEnd = holdEnd,
                        fadeEnd = fadeEnd,
                        crossfadeRoll = (float)rng.NextDouble(),
                        anchorT = anchorT,
                    });
                    placed = true;
                }
            }

            events.Sort((a, b) => a.start.CompareTo(b.start));
            return events.ToArray();
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    /// <summary>Sorted-cursor sweep over PatchEvents: O(active + newly-started) per
    /// forward frame. A backward frame (scrub) rewinds and replays — still deterministic.</summary>
    public class PatchSweep
    {
        /// <summary>Hard cap on simultaneously active (drawn) events. Once reached,
        /// newly-activated events are dropped permanently (not deferred/queued) — since
        /// activation is processed in ascending start order off the sorted cursor, the
        /// drop set is a deterministic function of the event array and frame sequence.
        /// This bound also fixes _active's capacity so it never regrows past its initial
        /// allocation, keeping the Collect hot path allocation-free.</summary>
        public const int MaxActive = 128;

        private readonly PatchEvent[] _sorted;   // by start ascending
        private readonly List<int> _active = new List<int>(MaxActive);
        private int _cursor;
        private int _lastFrame = int.MinValue;

        public PatchSweep(PatchEvent[] events)
        {
            _sorted = (PatchEvent[])events.Clone();
            Array.Sort(_sorted, (a, b) => a.start.CompareTo(b.start));
        }

        /// <summary>Copies events active at <paramref name="frame"/> into
        /// <paramref name="outBuf"/>; returns the count. outBuf must be at least
        /// as long as the event array. Active count is capped at <see cref="MaxActive"/>;
        /// events activated beyond the cap are permanently skipped (deterministic drop,
        /// see MaxActive doc).</summary>
        public int Collect(int frame, PatchEvent[] outBuf)
        {
            if (frame < _lastFrame) { _cursor = 0; _active.Clear(); }  // backward scrub → rewind
            _lastFrame = frame;

            // Expire before admitting: under cap saturation, admitting new activations
            // before removing entries that have already expired as of `frame` lets those
            // stale entries occupy MaxActive slots that should have freed up first. That
            // makes the drop-set depend on whether `frame` was reached stepwise (expiring
            // along the way) or via jump/rewind-then-replay (batching expiry with the new
            // frame's activations) — expiring first keeps the two paths identical.
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var e = _sorted[_active[i]];
                if (frame >= e.fadeEnd) _active.RemoveAt(i);
            }

            // Admit newly-crossed events, but skip anything already dead-on-arrival
            // (fadeEnd <= frame) — a large forward jump can cross many events whose
            // entire lifetime already precedes `frame`. Counting those toward the cap
            // (as the old admit-then-expire order did) burns MaxActive slots on events
            // contributing nothing, permanently starving later, still-live events that
            // stepwise per-frame play would have let in as earlier ones expired along
            // the way.
            while (_cursor < _sorted.Length && _sorted[_cursor].start <= frame)
            {
                if (frame < _sorted[_cursor].fadeEnd && _active.Count < MaxActive)
                    _active.Add(_cursor);
                _cursor++;
            }

            int n = 0;
            for (int i = 0; i < _active.Count; i++)
            {
                var e = _sorted[_active[i]];
                if (frame >= e.start) outBuf[n++] = e;
            }
            return n;
        }
    }
}

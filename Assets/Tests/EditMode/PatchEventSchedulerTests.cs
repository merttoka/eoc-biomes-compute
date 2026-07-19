using System;
using NUnit.Framework;
using Biomes.Sequencer;

namespace Biomes.Sequencer.Tests
{
    public class PatchEventSchedulerTests
    {
        private static PatchScatterConfig Cfg(int seed = 42) => new PatchScatterConfig
        {
            seed = seed,
            count = 64,
            minSize = 0.03f,
            maxSize = 0.25f,
            aspect = 5f,
            holdMinFrames = 9,
            holdMaxFrames = 90,
            fadeFrames = 30,
            leadFrames = 150,
            trailFrames = 90,
            staggerJitterFrames = 12,
            crossfadeCenter = 0.5f,
            crossfadeWidth = 0.15f,
            durationFrames = 1800,
            maxRejects = 40,
        };

        [Test]
        public void Generate_SameSeed_IsDeterministic()
        {
            var a = PatchEventScheduler.Generate(Cfg());
            var b = PatchEventScheduler.Generate(Cfg());
            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].start, b[i].start);
                Assert.AreEqual(a[i].dst.x, b[i].dst.x);
                Assert.AreEqual(a[i].crossfadeRoll, b[i].crossfadeRoll);
            }
        }

        [Test]
        public void Generate_DifferentSeed_Differs()
        {
            var a = PatchEventScheduler.Generate(Cfg(1));
            var b = PatchEventScheduler.Generate(Cfg(2));
            bool anyDiff = a.Length != b.Length;
            for (int i = 0; !anyDiff && i < a.Length; i++)
                anyDiff = a[i].start != b[i].start || a[i].dst.x != b[i].dst.x;
            Assert.IsTrue(anyDiff);
        }

        [Test]
        public void Generate_TimeCoactivePatches_NeverOverlapSpatially()
        {
            var events = PatchEventScheduler.Generate(Cfg());
            for (int i = 0; i < events.Length; i++)
                for (int j = i + 1; j < events.Length; j++)
                {
                    bool timeOverlap = events[i].start < events[j].fadeEnd &&
                                       events[j].start < events[i].fadeEnd;
                    if (timeOverlap)
                        Assert.IsFalse(events[i].dst.Overlaps(events[j].dst),
                            $"events {i} and {j} are co-active and overlap spatially");
                }
        }

        [Test]
        public void Generate_EventsWithinDurationAndUnitSquare()
        {
            var events = PatchEventScheduler.Generate(Cfg());
            Assert.Greater(events.Length, 0);
            foreach (var e in events)
            {
                Assert.GreaterOrEqual(e.start, 0);
                Assert.Greater(e.holdEnd, e.start);
                Assert.Greater(e.fadeEnd, e.holdEnd);
                Assert.GreaterOrEqual(e.dst.x, 0f);
                Assert.GreaterOrEqual(e.dst.y, 0f);
                Assert.LessOrEqual(e.dst.x + e.dst.w, 1f + 1e-4f);
                Assert.LessOrEqual(e.dst.y + e.dst.h, 1f + 1e-4f);
            }
        }

        [Test]
        public void SizeToHold_LargerPatch_ShorterHold()
        {
            int small = PatchEventScheduler.SizeToHoldFrames(0f, 9, 90);
            int mid = PatchEventScheduler.SizeToHoldFrames(0.5f, 9, 90);
            int large = PatchEventScheduler.SizeToHoldFrames(1f, 9, 90);
            Assert.AreEqual(90, small);
            Assert.AreEqual(9, large);
            Assert.Greater(small, mid);
            Assert.Greater(mid, large);
        }

        [Test]
        public void Envelope_HoldsAtOne_ThenFadesToZero()
        {
            var e = new PatchEvent
            {
                start = 100, holdEnd = 130, fadeEnd = 160,
                dst = new PatchRect(0, 0, 0.1f, 0.1f),
                src = new PatchRect(0, 0, 0.1f, 0.1f),
            };
            Assert.AreEqual(0f, PatchEventScheduler.Envelope(in e, 99));
            Assert.AreEqual(1f, PatchEventScheduler.Envelope(in e, 100));
            Assert.AreEqual(1f, PatchEventScheduler.Envelope(in e, 129));
            float mid = PatchEventScheduler.Envelope(in e, 145);
            Assert.Greater(mid, 0f);
            Assert.Less(mid, 1f);
            Assert.AreEqual(0f, PatchEventScheduler.Envelope(in e, 160));
            Assert.AreEqual(0f, PatchEventScheduler.Envelope(in e, 500));
        }

        [Test]
        public void Sigmoid_CenteredAndMonotonic()
        {
            Assert.AreEqual(0.5f, PatchEventScheduler.Sigmoid(0.5f, 0.5f, 0.15f), 1e-4f);
            float prev = -1f;
            for (float t = 0f; t <= 1f; t += 0.05f)
            {
                float v = PatchEventScheduler.Sigmoid(t, 0.5f, 0.15f);
                Assert.Greater(v, prev);
                prev = v;
            }
            Assert.Less(PatchEventScheduler.Sigmoid(0f, 0.5f, 0.15f), 0.05f);
            Assert.Greater(PatchEventScheduler.Sigmoid(1f, 0.5f, 0.15f), 0.95f);
        }

        [Test]
        public void Sweep_MatchesBruteForce_IncludingBackwardScrub()
        {
            var events = PatchEventScheduler.Generate(Cfg());
            var sweep = new PatchSweep(events);
            var buf = new PatchEvent[events.Length];
            // forward, backward, random jumps
            int[] frames = { 0, 200, 900, 901, 300, 1799, 50, 1800 };
            foreach (int frame in frames)
            {
                int n = sweep.Collect(frame, buf);
                int expected = 0;
                foreach (var e in events)
                    if (e.start <= frame && frame < e.fadeEnd) expected++;
                Assert.AreEqual(expected, n, $"frame {frame}");
                for (int i = 0; i < n; i++)
                    Assert.IsTrue(buf[i].start <= frame && frame < buf[i].fadeEnd);
            }
        }
    }
}

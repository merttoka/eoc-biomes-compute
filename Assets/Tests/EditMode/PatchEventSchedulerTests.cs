using System;
using System.Collections.Generic;
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
        public void Generate_CountClampedToMaxEventsPerClip()
        {
            var cfg = Cfg();
            cfg.count = 600;
            cfg.minSize = 0.01f;
            cfg.maxSize = 0.02f;
            cfg.durationFrames = 20000;
            cfg.holdMinFrames = 5;
            cfg.holdMaxFrames = 20;
            cfg.fadeFrames = 5;
            cfg.leadFrames = 10;
            cfg.trailFrames = 10;
            cfg.staggerJitterFrames = 2;

            var events = PatchEventScheduler.Generate(cfg);
            Assert.LessOrEqual(events.Length, PatchEventScheduler.MaxEventsPerClip);
        }

        [Test]
        public void Generate_DenseConfig_RejectsSomePatchesButStaysValid()
        {
            var cfg = new PatchScatterConfig
            {
                seed = 7,
                count = 64,
                minSize = 0.4f,
                maxSize = 0.6f,
                aspect = 1f,
                holdMinFrames = 80,
                holdMaxFrames = 95,
                fadeFrames = 5,
                leadFrames = 0,
                trailFrames = 0,
                staggerJitterFrames = 0,
                crossfadeCenter = 0.5f,
                crossfadeWidth = 0.15f,
                durationFrames = 100,
                maxRejects = 40,
            };
            var events = PatchEventScheduler.Generate(cfg);

            Assert.Less(events.Length, cfg.count);
            Assert.GreaterOrEqual(events.Length, 1);

            for (int i = 0; i < events.Length; i++)
            {
                Assert.GreaterOrEqual(events[i].dst.x, 0f);
                Assert.GreaterOrEqual(events[i].dst.y, 0f);
                Assert.LessOrEqual(events[i].dst.x + events[i].dst.w, 1f + 1e-4f);
                Assert.LessOrEqual(events[i].dst.y + events[i].dst.h, 1f + 1e-4f);

                for (int j = i + 1; j < events.Length; j++)
                {
                    bool timeOverlap = events[i].start < events[j].fadeEnd &&
                                       events[j].start < events[i].fadeEnd;
                    if (timeOverlap)
                        Assert.IsFalse(events[i].dst.Overlaps(events[j].dst),
                            $"events {i} and {j} are co-active and overlap spatially");
                }
            }
        }

        [Test]
        public void Sweep_ActiveCap_LimitsTo128AndIsDeterministic()
        {
            const int total = 200;
            var events = new PatchEvent[total];
            for (int i = 0; i < total; i++)
            {
                events[i] = new PatchEvent
                {
                    dst = new PatchRect(i * 0.001f, 0f, 0.0005f, 0.0005f),
                    src = new PatchRect(0f, 0f, 0.0005f, 0.0005f),
                    start = 0,
                    holdEnd = 100,
                    fadeEnd = 200,
                    crossfadeRoll = 0f,
                    anchorT = 0f,
                };
            }
            var sweep = new PatchSweep(events);

            var buf1 = new PatchEvent[total];
            int n1 = sweep.Collect(50, buf1);
            Assert.AreEqual(PatchSweep.MaxActive, n1);

            var buf2 = new PatchEvent[total];
            int n2 = sweep.Collect(50, buf2);
            Assert.AreEqual(PatchSweep.MaxActive, n2);

            for (int i = 0; i < n1; i++)
                Assert.AreEqual(buf1[i].dst.x, buf2[i].dst.x, $"index {i} differs between calls");
        }

        [Test]
        public void Sweep_RewindUnderCapSaturation_IsIdentical()
        {
            // Two non-overlapping "generations" of hand-built events:
            //  - gen A: exactly MaxActive (128) events, staggered starts 0..127, all sharing
            //    fadeEnd=300 so all 128 are simultaneously co-active (saturating the cap)
            //    and expire together, well before gen B ever starts.
            //  - gen B: 72 events, staggered starts 400..542 (long hold — fadeEnd=5000),
            //    starting only after every gen A event has already expired.
            // Total = 200 > MaxActive. At a frame F chosen after gen B has fully started but
            // long before any gen B event fades, the only correct active set is all of gen B
            // (gen A is entirely dead by F). A jump straight to F from a rewound cursor must
            // not let gen A's already-dead events burn cap slots ahead of gen B.
            const int genACount = PatchSweep.MaxActive; // 128
            const int genBCount = 72;
            const int total = genACount + genBCount;    // 200
            const int F = 1000;

            var events = new PatchEvent[total];
            for (int i = 0; i < genACount; i++)
            {
                events[i] = new PatchEvent
                {
                    dst = new PatchRect(i, 0f, 0.001f, 0.001f),
                    src = new PatchRect(0f, 0f, 0.001f, 0.001f),
                    start = i,
                    holdEnd = 290,
                    fadeEnd = 300,
                    crossfadeRoll = 0f,
                    anchorT = 0f,
                };
            }
            for (int j = 0; j < genBCount; j++)
            {
                int i = genACount + j;
                events[i] = new PatchEvent
                {
                    dst = new PatchRect(i, 0f, 0.001f, 0.001f),
                    src = new PatchRect(0f, 0f, 0.001f, 0.001f),
                    start = 400 + j * 2,
                    holdEnd = 4950,
                    fadeEnd = 5000,
                    crossfadeRoll = 0f,
                    anchorT = 0f,
                };
            }

            // Stepwise: walk every single frame from 0 up to F, recording the active set
            // (identified by dst.x, unique per event) from the final Collect(F) call.
            var stepwiseSweep = new PatchSweep(events);
            var buf = new PatchEvent[total];
            int nStepwise = 0;
            for (int f = 0; f <= F; f++)
                nStepwise = stepwiseSweep.Collect(f, buf);
            var stepwiseIds = new HashSet<float>();
            for (int i = 0; i < nStepwise; i++) stepwiseIds.Add(buf[i].dst.x);

            // Scrub-then-play: Collect far ahead, rewind to 0, then jump directly to F in
            // a single Collect call (skipping every intermediate frame).
            var jumpSweep = new PatchSweep(events);
            var jumpBuf = new PatchEvent[total];
            jumpSweep.Collect(4000, jumpBuf);
            jumpSweep.Collect(0, jumpBuf);
            int nJump = jumpSweep.Collect(F, jumpBuf);
            var jumpIds = new HashSet<float>();
            for (int i = 0; i < nJump; i++) jumpIds.Add(jumpBuf[i].dst.x);

            Assert.AreEqual(genBCount, nStepwise, "stepwise should land on exactly gen B");
            Assert.AreEqual(nStepwise, nJump, "jump-to-F active count differs from stepwise");
            Assert.IsTrue(stepwiseIds.SetEquals(jumpIds),
                "jump-to-F active set differs from stepwise active set at the same frame");
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

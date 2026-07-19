using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Biomes.Sequencer;

namespace Biomes
{
    /// <summary>Anadol-grammar patch scatter over the composer: deterministic from
    /// (params, seed). SourceB + crossfade gives the per-patch stochastic dissolve
    /// (e.g. A = main composite, B = StreamDiffusion return).</summary>
    public class PatchScatterClip : PlayableAsset, ITimelineClipAsset
    {
        /// <summary>Schedule clock, in frames/sec — independent of render fps so the
        /// clip-local patch schedule stays scrub-deterministic.</summary>
        public const float FrameRate = 60f;

        /// <summary>Source A: the texture kind a patch shows when its crossfadeRoll
        /// lands on the "A" side of the sigmoid.</summary>
        [Header("Sources")]
        public CellSource sourceA = CellSource.MainComposite;
        /// <summary>Source B: the texture kind a patch dissolves into on the "B" side
        /// of the sigmoid (e.g. the StreamDiffusion return).</summary>
        public CellSource sourceB = CellSource.DiffusionReturn;

        /// <summary>RNG seed for PatchEventScheduler.Generate — same seed + params on
        /// the same clip duration/aspect yields an identical schedule.</summary>
        [Header("Scatter (deterministic per seed)")]
        public int seed = 1234;
        /// <summary>Requested patch count over the clip. Generate may return fewer
        /// (rejection sampling gives up per-patch after maxRejects), and is hard-capped
        /// at PatchEventScheduler.MaxEventsPerClip.</summary>
        [Range(1, 512)] public int count = 64;
        /// <summary>Smallest patch height, normalized composer UV (0-1).</summary>
        [Range(0.01f, 0.5f)] public float minSize = 0.03f;
        /// <summary>Largest patch height, normalized composer UV (0-1).</summary>
        [Range(0.02f, 0.9f)] public float maxSize = 0.25f;

        /// <summary>Shortest hold duration, in frames @ FrameRate — used by the
        /// largest patches (flash and vanish).</summary>
        [Header("Timing (frames @ 60)")]
        [Range(1, 60)] public int holdMinFrames = 9;
        /// <summary>Longest hold duration, in frames @ FrameRate — used by the
        /// smallest patches (linger).</summary>
        [Range(10, 300)] public int holdMaxFrames = 90;
        /// <summary>Fade-out duration after hold ends, in frames @ FrameRate.</summary>
        [Range(1, 120)] public int fadeFrames = 30;
        /// <summary>Frames a patch may appear before its evenly-spread anchor time
        /// (asymmetric lead/trail spread cascades appearances instead of bursting).</summary>
        [Range(0, 600)] public int leadFrames = 150;
        /// <summary>Frames a patch may appear after its anchor time.</summary>
        [Range(0, 600)] public int trailFrames = 90;
        /// <summary>Extra random jitter (+/-) added to each patch's start frame.</summary>
        [Range(0, 60)] public int staggerJitterFrames = 12;

        /// <summary>Normalized clip-time (0-1) center of the per-patch A→B logistic
        /// crossfade — compared against each event's crossfadeRoll.</summary>
        [Header("A→B crossfade")]
        [Range(0f, 1f)] public float crossfadeCenter = 0.5f;
        /// <summary>Width of the A→B logistic crossfade curve; smaller = sharper
        /// transition around crossfadeCenter.</summary>
        [Range(0.01f, 0.5f)] public float crossfadeWidth = 0.15f;

        /// <summary>Clip supports Timeline's built-in ease-in/ease-out blend curves.</summary>
        public ClipCaps clipCaps => ClipCaps.Blending;

        /// <inheritdoc/>
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<PatchScatterBehaviour>.Create(graph);
            playable.GetBehaviour().clip = this;
            return playable;
        }

        /// <summary>Builds the PatchEventScheduler config for this clip at a given
        /// timeline duration and composer aspect ratio. Called once per playable
        /// instance (by PatchScatterBehaviour.EnsureBuilt), never per frame.</summary>
        public PatchScatterConfig BuildConfig(double clipDuration, float composerAspect) => new()
        {
            seed = seed,
            count = count,
            minSize = minSize,
            maxSize = maxSize,
            aspect = composerAspect,
            holdMinFrames = holdMinFrames,
            holdMaxFrames = holdMaxFrames,
            fadeFrames = fadeFrames,
            leadFrames = leadFrames,
            trailFrames = trailFrames,
            staggerJitterFrames = staggerJitterFrames,
            crossfadeCenter = crossfadeCenter,
            crossfadeWidth = crossfadeWidth,
            durationFrames = Mathf.Max(1, (int)(clipDuration * FrameRate)),
            maxRejects = 40,
        };
    }

    /// <summary>Runtime playable behaviour: lazily builds the deterministic patch
    /// schedule + sweep on first ProcessFrame (clip duration and composer aspect
    /// aren't known at CreatePlayable time), then exposes them to
    /// <see cref="PatchScatterMixer"/>.</summary>
    public class PatchScatterBehaviour : PlayableBehaviour
    {
        /// <summary>Source clip asset for this playable instance.</summary>
        public PatchScatterClip clip;

        /// <summary>Deterministic patch schedule for this playable instance, built
        /// lazily by <see cref="EnsureBuilt"/>. Null until then.</summary>
        public PatchEvent[] events;
        /// <summary>Sorted-cursor sweep over <see cref="events"/>; rewind-safe so
        /// scrubbing the Timeline reproduces the same active set.</summary>
        public PatchSweep sweep;
        /// <summary>Preallocated buffer for PatchSweep.Collect, sized to events.Length —
        /// reused every frame so Collect never allocates.</summary>
        public PatchEvent[] activeBuf;
        /// <summary>Composer aspect used to build <see cref="events"/> — compared against
        /// the incoming aspect each frame to detect the RT-not-yet-created fallback.</summary>
        private float _builtAspect;

        /// <summary>Builds <see cref="events"/>/<see cref="sweep"/>/<see cref="activeBuf"/>
        /// on first call, and rebuilds if the composer aspect has materially changed since
        /// the last build (in practice at most once, when the composer RT first materializes
        /// after the sequencer's first LateUpdate — a clip active at frame 0 otherwise locks
        /// in the pre-RT fallback aspect for its whole run). Determinism is preserved because
        /// the schedule is still a pure function of (params, seed, duration, aspect).</summary>
        public void EnsureBuilt(double duration, float aspect)
        {
            if (events != null && Mathf.Abs(aspect - _builtAspect) <= 0.01f) return;
            events = PatchEventScheduler.Generate(clip.BuildConfig(duration, aspect));
            sweep = new PatchSweep(events);
            activeBuf = new PatchEvent[events.Length];
            _builtAspect = aspect;
        }
    }
}

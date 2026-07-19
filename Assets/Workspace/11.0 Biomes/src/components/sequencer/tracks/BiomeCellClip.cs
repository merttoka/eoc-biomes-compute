using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>Which live texture a <see cref="BiomeCellClip"/> or patch clip draws from.</summary>
    public enum CellSource
    {
        /// <summary>A <see cref="BiomeCellRig"/>'s own output.</summary>
        Rig = 0,
        /// <summary>The main SimulationManager's composite output.</summary>
        MainComposite = 1,
        /// <summary>The general external input receiver.</summary>
        InputReceiver = 2,
        /// <summary>The StreamDiffusion return receiver.</summary>
        DiffusionReturn = 3,
    }

    /// <summary>One biome cell on the timeline: which texture, where on the composer,
    /// overlay or replace. Clip ease-in/out curves drive the blend weight.</summary>
    public class BiomeCellClip : PlayableAsset, ITimelineClipAsset
    {
        /// <summary>Which live texture this clip draws from.</summary>
        public CellSource source = CellSource.Rig;
        /// <summary>Rig to sample when <see cref="source"/> is <see cref="CellSource.Rig"/>. Resolved from the director's bindings.</summary>
        public ExposedReference<BiomeCellRig> rig;
        /// <summary>Destination rect on the composer, normalized 0-1 (x, y, width, height).</summary>
        [Tooltip("Normalized composer rect: x, y, width, height in 0..1.")]
        public Rect dstRect = new(0.25f, 0.25f, 0.5f, 0.5f);
        /// <summary>Overlay (additive) or Replace (alpha lerp) blend against the composer.</summary>
        public CellBlendMode mode = CellBlendMode.Overlay;
        /// <summary>When true and <see cref="mode"/> is Replace, also calls SetBaseWeight(1-weight) while this clip is active.</summary>
        [Tooltip("Replace only: also duck the base sim composite to 1-weight while active.")]
        public bool duckBase = false;

        /// <summary>Clip supports Timeline's built-in ease-in/ease-out blend curves.</summary>
        public ClipCaps clipCaps => ClipCaps.Blending;

        /// <inheritdoc/>
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<BiomeCellBehaviour>.Create(graph);
            var b = playable.GetBehaviour();
            b.clip = this;
            b.rig = rig.Resolve(graph.GetResolver());
            return playable;
        }
    }

    /// <summary>Runtime playable behaviour carrying a resolved <see cref="BiomeCellClip"/> and its rig binding to the mixer.</summary>
    public class BiomeCellBehaviour : PlayableBehaviour
    {
        /// <summary>Source clip asset for this playable instance.</summary>
        public BiomeCellClip clip;
        /// <summary>Rig resolved from the clip's ExposedReference, or null if unbound / source isn't Rig.</summary>
        public BiomeCellRig rig;
    }
}

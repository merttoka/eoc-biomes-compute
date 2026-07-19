using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>Timeline waypoint: ease one sim's live params from their state at clip
    /// start toward a snapshot preset asset. Clip ease-in curve = interpolation curve;
    /// hold the clip at weight 1 to hold the look.</summary>
    public class ParamSnapshotClip : PlayableAsset, ITimelineClipAsset
    {
        /// <summary>A params preset/snapshot asset implementing IParamSet (PhysarumParams, BoidParams, TermiteParams instance).</summary>
        [Tooltip("A params preset/snapshot asset implementing IParamSet (PhysarumParams, BoidParams, TermiteParams instance).")]
        public ScriptableObject snapshot;
        /// <summary>Index into SimulationManager.simulations.</summary>
        [Tooltip("Index into SimulationManager.simulations.")]
        public int simIndex = 0;

        /// <summary>Clip supports Timeline's built-in ease-in/ease-out blend curves.</summary>
        public ClipCaps clipCaps => ClipCaps.Blending;

        /// <inheritdoc/>
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<ParamSnapshotBehaviour>.Create(graph);
            playable.GetBehaviour().clip = this;
            return playable;
        }
    }

    /// <summary>Runtime playable behaviour carrying a resolved <see cref="ParamSnapshotClip"/>
    /// and its captured "from" state to the mixer.</summary>
    public class ParamSnapshotBehaviour : PlayableBehaviour
    {
        /// <summary>Source clip asset for this playable instance.</summary>
        public ParamSnapshotClip clip;

        /// <summary>"from" values captured the first frame the clip has weight; name → per-type values.
        /// Null between weighted spans, so re-entering the clip re-captures from wherever the show drifted to.</summary>
        public Dictionary<string, float[]> from;
        /// <summary>Whether the "snapshot is not an IParamSet" warning has already been logged for this playable instance.</summary>
        public bool warned;
    }
}

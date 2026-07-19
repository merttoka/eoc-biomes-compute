using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>Timeline track of <see cref="ParamSnapshotClip"/>s, bound to a
    /// <see cref="SimulationManager"/>. Its mixer eases each clip's target sim's live
    /// params toward the clip's snapshot preset by the clip's blend weight.</summary>
    [TrackColor(0.55f, 0.35f, 0.9f)]
    [TrackClipType(typeof(ParamSnapshotClip))]
    [TrackBindingType(typeof(SimulationManager))]
    public class ParamSnapshotTrack : TrackAsset
    {
        /// <inheritdoc/>
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
            => ScriptPlayable<ParamSnapshotMixer>.Create(graph, inputCount);
    }
}

using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>Timeline track of <see cref="PatchScatterClip"/>s, bound to a
    /// <see cref="CompositeSequencer"/>. Its mixer sweeps each clip's deterministic
    /// patch schedule and pushes active patches into the sequencer every frame.</summary>
    [TrackColor(1f, 0.5f, 0.1f)]
    [TrackClipType(typeof(PatchScatterClip))]
    [TrackBindingType(typeof(CompositeSequencer))]
    public class PatchScatterTrack : TrackAsset
    {
        /// <inheritdoc/>
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
            => ScriptPlayable<PatchScatterMixer>.Create(graph, inputCount);
    }
}

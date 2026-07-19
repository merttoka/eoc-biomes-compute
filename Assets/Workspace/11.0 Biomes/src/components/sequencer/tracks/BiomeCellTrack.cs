using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>Timeline track of <see cref="BiomeCellClip"/>s, bound to a <see cref="CompositeSequencer"/>.
    /// Its mixer pushes every active clip into the sequencer each frame.</summary>
    [TrackColor(0.2f, 0.8f, 0.5f)]
    [TrackClipType(typeof(BiomeCellClip))]
    [TrackBindingType(typeof(CompositeSequencer))]
    public class BiomeCellTrack : TrackAsset
    {
        /// <inheritdoc/>
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
            => ScriptPlayable<BiomeCellMixer>.Create(graph, inputCount);
    }
}

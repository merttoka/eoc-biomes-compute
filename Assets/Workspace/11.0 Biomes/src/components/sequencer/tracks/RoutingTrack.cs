using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>Timeline track of <see cref="RoutingClip"/>s, bound to a
    /// <see cref="CompositeSequencer"/>. Its mixer sets/clears
    /// <see cref="SimulationManager.influenceOverride"/> from the active clip so a timed
    /// section can route e.g. the StreamDiffusion return into every sim's external
    /// influence input.</summary>
    [TrackColor(0.9f, 0.8f, 0.2f)]
    [TrackClipType(typeof(RoutingClip))]
    [TrackBindingType(typeof(CompositeSequencer))]
    public class RoutingTrack : TrackAsset
    {
        /// <inheritdoc/>
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount)
            => ScriptPlayable<RoutingMixer>.Create(graph, inputCount);
    }
}

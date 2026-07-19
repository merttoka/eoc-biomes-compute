using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>While active, feeds the chosen texture into every sim's
    /// externalInfluenceTex (via SimulationManager.influenceOverride) so e.g. the
    /// StreamDiffusion return perturbs sim behavior for a timed section.</summary>
    public class RoutingClip : PlayableAsset, ITimelineClipAsset
    {
        /// <summary>Which live texture overrides the sims' external influence while this clip is active.</summary>
        public CellSource influenceSource = CellSource.DiffusionReturn;

        /// <summary>No built-in blend support — the mixer applies a hard first-active-clip-wins override, not a weighted blend.</summary>
        public ClipCaps clipCaps => ClipCaps.None;

        /// <inheritdoc/>
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<RoutingBehaviour>.Create(graph);
            playable.GetBehaviour().clip = this;
            return playable;
        }
    }

    /// <summary>Runtime playable behaviour carrying a resolved <see cref="RoutingClip"/> to the mixer.</summary>
    public class RoutingBehaviour : PlayableBehaviour
    {
        /// <summary>Source clip asset for this playable instance.</summary>
        public RoutingClip clip;
    }
}

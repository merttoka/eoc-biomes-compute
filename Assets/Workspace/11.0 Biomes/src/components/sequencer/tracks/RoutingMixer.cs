using UnityEngine;
using UnityEngine.Playables;

namespace Biomes
{
    /// <summary>Sets/clears SimulationManager.influenceOverride from the active routing
    /// clip. Cleared every frame first, so no clip = normal externalInput path.
    /// influenceOverride is an external stateful resource (same class as
    /// <see cref="BiomeCellRig.Running"/> — see <see cref="BiomeCellMixer"/>): if the
    /// graph tears down or pauses while a clip is still overriding, the override would
    /// otherwise stay stuck non-null and the sims would never fall back to the normal
    /// receiver path. OnPlayableDestroy/OnBehaviourPause clear it explicitly.</summary>
    public class RoutingMixer : PlayableBehaviour
    {
        // Cached from the last ProcessFrame's playerData — OnPlayableDestroy/
        // OnBehaviourPause don't receive playerData, so this is the only way to reach
        // the bound CompositeSequencer for teardown.
        private CompositeSequencer _seq;

        /// <inheritdoc/>
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var seq = playerData as CompositeSequencer;
            if (seq == null || seq.simManager == null) return;
            _seq = seq;

            seq.simManager.influenceOverride = null;

            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                if (playable.GetInputWeight(i) <= 0f) continue;
                var b = ((ScriptPlayable<RoutingBehaviour>)playable.GetInput(i)).GetBehaviour();
                if (b.clip == null) continue;
                seq.simManager.influenceOverride = seq.ResolveSource(b.clip.influenceSource, null);
                break;   // first active clip wins
            }
        }

        /// <summary>Clears influenceOverride when the graph tears down this mixer (e.g. director Stop).</summary>
        public override void OnPlayableDestroy(Playable playable) => ClearOverride();

        /// <summary>Clears influenceOverride when the graph pauses evaluation of this mixer.</summary>
        public override void OnBehaviourPause(Playable playable, FrameData info) => ClearOverride();

        private void ClearOverride()
        {
            if (_seq != null && _seq.simManager != null)
                _seq.simManager.influenceOverride = null;
        }
    }
}

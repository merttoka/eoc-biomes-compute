using UnityEngine;
using UnityEngine.Playables;

namespace Biomes
{
    /// <summary>Pushes every active cell clip into the CompositeSequencer each frame,
    /// weight = Timeline input weight (clip ease curves). Rigs run while their clip has
    /// any weight (they keep evolving through the blend). Never disables a rig GameObject —
    /// only toggles <see cref="BiomeCellRig.Running"/>, so the rig's manager never tears
    /// down the RenderTexture the composer may still be sampling.</summary>
    public class BiomeCellMixer : PlayableBehaviour
    {
        /// <inheritdoc/>
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var seq = playerData as CompositeSequencer;
            if (seq == null) return;

            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                float w = playable.GetInputWeight(i);
                var input = (ScriptPlayable<BiomeCellBehaviour>)playable.GetInput(i);
                var b = input.GetBehaviour();
                if (b.clip == null) continue;

                if (b.rig != null) b.rig.Running = w > 0f;
                if (w <= 0f) continue;

                Texture src = seq.ResolveSource(b.clip.source, b.rig);
                if (src == null) continue;   // rig not reset yet / receiver silent → skip

                seq.PushCell(src, b.clip.dstRect, w, b.clip.mode);
                if (b.clip.mode == CellBlendMode.Replace && b.clip.duckBase)
                    seq.SetBaseWeight(1f - w);
            }
        }

        /// <summary>Forces every reachable rig to stop when the graph tears down this mixer
        /// (e.g. director Stop), so a rig mid-clip doesn't stay Running forever.</summary>
        public override void OnPlayableDestroy(Playable playable)
        {
            StopAllRigs(playable);
        }

        /// <summary>Forces every reachable rig to stop when the graph pauses evaluation of
        /// this mixer. Also fires when the mixer's overall effective weight hits 0 mid-timeline
        /// (e.g. between clips) — that's acceptable and matches ProcessFrame's own
        /// w &lt;= 0 → Running=false semantics.</summary>
        public override void OnBehaviourPause(Playable playable, FrameData info)
        {
            StopAllRigs(playable);
        }

        private static void StopAllRigs(Playable playable)
        {
            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                var input = (ScriptPlayable<BiomeCellBehaviour>)playable.GetInput(i);
                var b = input.GetBehaviour();
                if (b == null || b.rig == null) continue;
                b.rig.Running = false;
            }
        }
    }
}

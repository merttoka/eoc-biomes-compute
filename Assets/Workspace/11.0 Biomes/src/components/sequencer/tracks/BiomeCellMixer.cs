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
    }
}

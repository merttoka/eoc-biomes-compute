using UnityEngine;
using UnityEngine.Playables;
using Biomes.Sequencer;

namespace Biomes
{
    /// <summary>Each frame: sweep the clip's deterministic events at the clip-local
    /// frame, compute envelope alpha × clip weight, pick source A/B via the sigmoid
    /// stochastic crossfade, and push draws to the CompositeSequencer.
    /// Unlike <see cref="BiomeCellMixer"/>, a patch clip drives no external
    /// always-simulating resource (no rig to stop) — its schedule state
    /// (events/sweep/activeBuf) lives entirely on the PlayableBehaviour and is
    /// garbage-collected with the playable, so no OnPlayableDestroy/OnBehaviourPause
    /// teardown is required here. With multiple simultaneous patch clips, note that
    /// CompositeSequencer.PushPatch's global cap (<see cref="CompositeSequencer.MaxPatchDraws"/>)
    /// wins in track evaluation order — later clips degrade to fewer patches, never black.</summary>
    public class PatchScatterMixer : PlayableBehaviour
    {
        /// <inheritdoc/>
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var seq = playerData as CompositeSequencer;
            if (seq == null) return;

            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                float clipWeight = playable.GetInputWeight(i);
                if (clipWeight <= 0f) continue;

                var input = (ScriptPlayable<PatchScatterBehaviour>)playable.GetInput(i);
                var b = input.GetBehaviour();
                if (b.clip == null) continue;

                var composer = seq.ComposerOutputTexture;
                // Fallback only lives until the composer RT exists (before the sequencer's
                // first LateUpdate); EnsureBuilt rebuilds with the true aspect on that frame.
                float aspect = composer != null ? (float)composer.width / composer.height : 5f;
                b.EnsureBuilt(input.GetDuration(), aspect);

                Texture texA = seq.ResolveSource(b.clip.sourceA, null);
                Texture texB = seq.ResolveSource(b.clip.sourceB, null);
                if (texB == null) texB = texA;   // diffusion stream down → degrade to A
                if (texA == null) texA = texB;   // (never black)
                if (texA == null) continue;

                int frame = (int)(input.GetTime() * PatchScatterClip.FrameRate);
                int active = b.sweep.Collect(frame, b.activeBuf);

                for (int p = 0; p < active && p < CompositeSequencer.MaxPatchDraws; p++)
                {
                    ref var e = ref b.activeBuf[p];
                    float alpha = PatchEventScheduler.Envelope(in e, frame) * clipWeight;
                    if (alpha <= 0f) continue;
                    float sig = PatchEventScheduler.Sigmoid(e.anchorT, b.clip.crossfadeCenter, b.clip.crossfadeWidth);
                    Texture src = e.crossfadeRoll < sig ? texB : texA;
                    seq.PushPatch(src,
                        new Rect(e.dst.x, e.dst.y, e.dst.w, e.dst.h),
                        new Rect(e.src.x, e.src.y, e.src.w, e.src.h),
                        alpha);
                }
            }
        }
    }
}

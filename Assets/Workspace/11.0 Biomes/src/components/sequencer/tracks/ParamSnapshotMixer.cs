using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

namespace Biomes
{
    /// <summary>Applies snapshot clips: live = lerp(from, snapshot, clipWeight) for every
    /// modulatable param (hue via shortest-arc). "from" is captured when the clip first
    /// gains weight, so easing starts from whatever the show drifted to.
    /// Unlike <see cref="BiomeCellMixer"/>, a snapshot clip drives no external
    /// always-simulating resource (no rig to stop) — the only state it owns is the
    /// per-playable "from" dictionary, which lives on the PlayableBehaviour and is
    /// garbage-collected with it. If the graph tears down or pauses mid-clip, the live
    /// param values simply stay at their last-written state — the same "no restore on
    /// stop" convention <see cref="ParameterInterpolator"/> already uses — so no
    /// OnPlayableDestroy/OnBehaviourPause teardown is required here.</summary>
    public class ParamSnapshotMixer : PlayableBehaviour
    {
        /// <inheritdoc/>
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var manager = playerData as SimulationManager;
            if (manager == null || !Application.isPlaying) return;

            int n = playable.GetInputCount();
            for (int i = 0; i < n; i++)
            {
                float w = playable.GetInputWeight(i);
                var input = (ScriptPlayable<ParamSnapshotBehaviour>)playable.GetInput(i);
                var b = input.GetBehaviour();
                if (b.clip == null) continue;

                if (w <= 0f) { b.from = null; continue; }   // re-capture on next entry

                var target = b.clip.snapshot as IParamSet;
                if (target == null)
                {
                    if (!b.warned && b.clip.snapshot != null)
                    {
                        Debug.LogWarning($"ParamSnapshotClip: '{b.clip.snapshot.name}' is not an IParamSet preset; clip is a no-op");
                        b.warned = true;
                    }
                    continue;
                }

                var sim = (b.clip.simIndex >= 0 && b.clip.simIndex < manager.simulations.Count)
                    ? manager.simulations[b.clip.simIndex] : null;
                if (sim == null || sim.LiveParamSet == null) continue;
                var live = sim.LiveParamSet;

                if (b.from == null)   // first weighted frame → capture "from"
                {
                    b.from = new Dictionary<string, float[]>();
                    foreach (var name in sim.ModulatableParams)
                    {
                        var arr = new float[live.TypeCount];
                        for (int t = 0; t < live.TypeCount; t++)
                            arr[t] = live.GetValue(name, t);
                        b.from[name] = arr;
                    }
                }

                int typeCount = Mathf.Min(live.TypeCount, target.TypeCount);
                foreach (var kv in b.from)
                {
                    float[] fromArr = kv.Value;
                    for (int t = 0; t < typeCount && t < fromArr.Length; t++)
                    {
                        float to = target.GetValue(kv.Key, t);
                        float v = kv.Key == "hue"
                            ? ParameterInterpolator.LerpHue01(fromArr[t], to, w)
                            : Mathf.Lerp(fromArr[t], to, w);
                        live.SetValue(kv.Key, t, v);
                    }
                }
            }
        }
    }
}

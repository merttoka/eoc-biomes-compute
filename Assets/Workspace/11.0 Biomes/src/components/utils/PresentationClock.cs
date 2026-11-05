using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Clock for presentation-paced effects (data playback, firing decay, backbone scaffold,
    /// palette cycle, output fades): wall time live, Unity's capture clock while one is set.
    /// Unity Recorder and FigureExporter set Time.captureDeltaTime, which makes game time
    /// advance one fixed step per rendered frame while unscaled time keeps following the
    /// wall; anything on unscaled time then runs ahead of the recording by the ratio of
    /// capture rate to render rate. The sims step on game time already (FixedUpdate).
    /// </summary>
    public static class PresentationClock
    {
        public static bool Capturing => Time.captureDeltaTime > 0f;
        public static float DeltaTime => Capturing ? Time.deltaTime : Time.unscaledDeltaTime;
        /// <summary>Jumps when a capture starts or stops; consumers that difference it clamp at 0.</summary>
        public static float Now => Capturing ? Time.time : Time.unscaledTime;
    }
}

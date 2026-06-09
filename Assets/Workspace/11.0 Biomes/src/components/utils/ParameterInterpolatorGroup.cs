using System.Collections.Generic;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Conductor for several <see cref="ParameterInterpolator"/>s (one per sim): one
    /// Play / Pause / Stop / Skip / Refresh drives them all together, instead of pressing
    /// play on each sim's interpolator individually. Monitoring (per-interpolator phase +
    /// progress bars) is drawn by the custom inspector. Assign the per-sim interpolators
    /// to the list below.
    /// </summary>
    public class ParameterInterpolatorGroup : MonoBehaviour
    {
        [Tooltip("The per-sim ParameterInterpolators to drive together.")]
        public List<ParameterInterpolator> interpolators = new();

        public void PlayAll()       { foreach (var i in interpolators) if (i != null) i.Play(); }
        public void PauseAll()      { foreach (var i in interpolators) if (i != null) i.Pause(); }
        public void StopAll()       { foreach (var i in interpolators) if (i != null) i.Stop(); }
        public void SkipAllToNext() { foreach (var i in interpolators) if (i != null) i.SkipToNext(); }
        public void RefreshAll()    { foreach (var i in interpolators) if (i != null) i.RefreshParamList(); }
    }
}

using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>Inspector for ParameterInterpolatorGroup: All-transport buttons + a live
    /// progress bar per interpolator (repaints during Play).</summary>
    [CustomEditor(typeof(ParameterInterpolatorGroup))]
    public class ParameterInterpolatorGroupEditor : Editor
    {
        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            var group = (ParameterInterpolatorGroup)target;

            DrawDefaultInspector(); // the interpolators list

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("▶ Play All"))  group.PlayAll();
                if (GUILayout.Button("⏸ Pause All")) group.PauseAll();
                if (GUILayout.Button("⏹ Stop All"))  group.StopAll();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("⏭ Skip All"))    group.SkipAllToNext();
                if (GUILayout.Button("↻ Refresh All")) group.RefreshAll();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);

            if (group.interpolators == null || group.interpolators.Count == 0)
            {
                EditorGUILayout.HelpBox("Assign the per-sim ParameterInterpolators above.", MessageType.Info);
                return;
            }

            foreach (var itp in group.interpolators)
            {
                if (itp == null) { EditorGUILayout.LabelField("— (missing reference)"); continue; }
                var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight + 2f);
                string label = $"{itp.name}   {itp.CurrentPhase}   wp {itp.CurrentWaypoint + 1}/{Mathf.Max(itp.WaypointCount, 1)}";
                EditorGUI.ProgressBar(rect, itp.Progress, label);
            }
        }
    }
}

using UnityEditor;
using UnityEngine;

namespace Biomes
{
    [CustomEditor(typeof(ExternalTextureReceiver))]
    public class ExternalTextureReceiverEditor : Editor
    {
        // Repaint continuously so the discovered-source list and live preview stay
        // current (NDI/Syphon discovery updates over time, even in edit mode).
        public override bool RequiresConstantRepaint() => true;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var recv = (ExternalTextureReceiver)target;

            // ── Discovered source picker ──
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Discover Source", EditorStyles.boldLabel);

            string[] sources = ExternalTextureShare.EnumerateSources(recv.protocol);
            if (sources.Length > 0)
            {
                int cur = System.Array.IndexOf(sources, recv.streamName);
                int sel = EditorGUILayout.Popup($"{recv.protocol} sources", cur, sources);
                if (sel >= 0 && sel != cur)
                {
                    Undo.RecordObject(recv, "Set Stream Name");
                    recv.streamName = sources[sel];
                    EditorUtility.SetDirty(recv);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"No {recv.protocol} sources found yet. Ensure the sender is publishing. " +
                    "Exact names required: NDI = \"<MACHINE> (Name)\", Syphon = \"App/Name\".",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Received Preview", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play mode to preview. Tip: enable 'Self Drive' + 'Enable Receive' " +
                    "to preview without wiring this into the SimulationManager.",
                    MessageType.Info);
                return;
            }

            var tex = recv.OutputTexture;
            if (tex == null)
            {
                EditorGUILayout.HelpBox(
                    "No texture yet. Check: 'Enable Receive' on, a matching stream name, " +
                    "the right protocol, resources assigned, and a live source publishing.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField($"{tex.width} x {tex.height}", EditorStyles.miniLabel);

            // Aspect-correct preview box.
            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1f;
            float w = EditorGUIUtility.currentViewWidth - 40f;
            float h = aspect > 0 ? w / aspect : w;
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(r, tex, null, ScaleMode.ScaleToFit);
        }
    }
}

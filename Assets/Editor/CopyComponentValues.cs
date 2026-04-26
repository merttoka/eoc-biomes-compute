using UnityEngine;
using UnityEditor;

public class CopyComponentValues : EditorWindow
{
    GameObject source;
    GameObject target;

    [MenuItem("Tools/Copy Component Values Between Types")]
    static void Open() => GetWindow<CopyComponentValues>("Copy Component Values");

    void OnGUI()
    {
        source = (GameObject)EditorGUILayout.ObjectField("Source GO", source, typeof(GameObject), true);
        target = (GameObject)EditorGUILayout.ObjectField("Target GO", target, typeof(GameObject), true);

        if (GUILayout.Button("Copy All Matching Components") && source != null && target != null)
        {
            var srcComponents = source.GetComponents<MonoBehaviour>();
            var tgtComponents = target.GetComponents<MonoBehaviour>();

            int copied = 0;
            foreach (var src in srcComponents)
            {
                if (src == null) continue;
                foreach (var tgt in tgtComponents)
                {
                    if (tgt == null) continue;
                    if (src.GetType().Name != tgt.GetType().Name) continue;

                    string json = EditorJsonUtility.ToJson(src);
                    Undo.RecordObject(tgt, "Copy Component Values");
                    EditorJsonUtility.FromJsonOverwrite(json, tgt);
                    EditorUtility.SetDirty(tgt);
                    Debug.Log($"Copied {src.GetType().Name} -> {tgt.GetType().Name}");
                    copied++;
                }
            }
            Debug.Log($"Done. Copied {copied} component(s).");
        }
    }
}

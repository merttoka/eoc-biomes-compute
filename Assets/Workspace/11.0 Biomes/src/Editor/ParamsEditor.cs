using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>Shared palette generation settings for the param editors.</summary>
    internal static class PaletteEditorState
    {
        public static float lightnessMin = 35f;
        public static float lightnessMax = 80f;
        public static float hueMin = 0f;
        public static float hueMax = 360f;
        public static bool showPaletteSettings = false;
    }

    /// <summary>Shared inspector body for any IParamSet ScriptableObject (Physarum /
    /// Boid / Termite). Draws typeCount (with type-list sync), the types/ranges arrays,
    /// a palette preview, and the edit-mode tool buttons. Per-type access goes through
    /// IParamSet so there is a single implementation for all param assets.</summary>
    internal static class ParamsEditorGUI
    {
        public static void DrawInspector(Editor editor, IParamSet p)
        {
            var so = editor.serializedObject;
            so.Update();

            var typeCountProp = so.FindProperty("typeCount");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(typeCountProp);
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
                Undo.RecordObject(editor.target, "Sync Types");
                p.SyncTypesList();
                EditorUtility.SetDirty(editor.target);
                so.Update();
            }

            EditorGUILayout.PropertyField(so.FindProperty("types"), true);
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(so.FindProperty("ranges"), true);
            so.ApplyModifiedProperties();

            DrawColorSwatches(p);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Edit-Mode Tools", EditorStyles.boldLabel);
            DrawPaletteSettings();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Randomize Params"))
            {
                Undo.RecordObject(editor.target, "Randomize Params");
                p.RandomizeParams();
                EditorUtility.SetDirty(editor.target);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Randomize Colors"))
            {
                Undo.RecordObject(editor.target, "Randomize Colors");
                ApplyPalette(p);
                EditorUtility.SetDirty(editor.target);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset to Defaults"))
            {
                Undo.RecordObject(editor.target, "Reset Defaults");
                p.ResetToDefaults();
                EditorUtility.SetDirty(editor.target);
                GUIUtility.ExitGUI();
            }
        }

        private static void ApplyPalette(IParamSet p)
        {
            var palette = ColorPalette.GenerateHS(p.TypeCount,
                PaletteEditorState.lightnessMin, PaletteEditorState.lightnessMax,
                PaletteEditorState.hueMin, PaletteEditorState.hueMax);
            for (int i = 0; i < p.TypeCount && i < palette.Count; i++)
            {
                p.SetValue("hue", i, palette[i].hue);
                p.SetValue("saturation", i, palette[i].saturation);
            }
        }

        private static void DrawColorSwatches(IParamSet p)
        {
            int n = p.TypeCount;
            if (n == 0) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Palette Preview", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            float w = (EditorGUIUtility.currentViewWidth - 40) / n;
            for (int i = 0; i < n; i++)
            {
                var c = Color.HSVToRGB(p.GetValue("hue", i), p.GetValue("saturation", i), 0.85f);
                var rect = GUILayoutUtility.GetRect(w, 20);
                EditorGUI.DrawRect(rect, c);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawPaletteSettings()
        {
            PaletteEditorState.showPaletteSettings = EditorGUILayout.Foldout(
                PaletteEditorState.showPaletteSettings, "Palette Generation Settings");
            if (!PaletteEditorState.showPaletteSettings) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.MinMaxSlider("Lightness", ref PaletteEditorState.lightnessMin,
                ref PaletteEditorState.lightnessMax, 0f, 100f);
            EditorGUILayout.BeginHorizontal();
            PaletteEditorState.lightnessMin = EditorGUILayout.FloatField(PaletteEditorState.lightnessMin, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            PaletteEditorState.lightnessMax = EditorGUILayout.FloatField(PaletteEditorState.lightnessMax, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.MinMaxSlider("Hue Range", ref PaletteEditorState.hueMin,
                ref PaletteEditorState.hueMax, 0f, 360f);
            EditorGUILayout.BeginHorizontal();
            PaletteEditorState.hueMin = EditorGUILayout.FloatField(PaletteEditorState.hueMin, GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            PaletteEditorState.hueMax = EditorGUILayout.FloatField(PaletteEditorState.hueMax, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }
    }

    [CustomEditor(typeof(PhysarumParams))]
    public class PhysarumParamsEditor : Editor
    {
        public override void OnInspectorGUI() => ParamsEditorGUI.DrawInspector(this, (IParamSet)target);
    }

    [CustomEditor(typeof(BoidParams))]
    public class BoidParamsEditor : Editor
    {
        public override void OnInspectorGUI() => ParamsEditorGUI.DrawInspector(this, (IParamSet)target);
    }

    [CustomEditor(typeof(TermiteParams))]
    public class TermiteParamsEditor : Editor
    {
        public override void OnInspectorGUI() => ParamsEditorGUI.DrawInspector(this, (IParamSet)target);
    }
}

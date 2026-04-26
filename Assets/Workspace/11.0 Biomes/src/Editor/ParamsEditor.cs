using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>Shared palette generation settings for both param editors.</summary>
    internal static class PaletteEditorState
    {
        public static float lightnessMin = 35f;
        public static float lightnessMax = 80f;
        public static float hueMin = 0f;
        public static float hueMax = 360f;
        public static bool showPaletteSettings = false;
    }

    [CustomEditor(typeof(PhysarumParams))]
    public class PhysarumParamsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var typeCountProp = serializedObject.FindProperty("typeCount");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(typeCountProp);
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                Undo.RecordObject(target, "Sync Physarum Types");
                ((PhysarumParams)target).SyncTypesList();
                EditorUtility.SetDirty(target);
                serializedObject.Update();
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("types"), true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ranges"), true);
            serializedObject.ApplyModifiedProperties();

            // Color swatches
            var p = (PhysarumParams)target;
            DrawColorSwatches(p.types.ConvertAll(t => (t.hue, t.saturation)));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Edit-Mode Tools", EditorStyles.boldLabel);

            DrawPaletteSettings();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Randomize Params"))
            {
                Undo.RecordObject(target, "Randomize Physarum Params");
                p.RandomizeParams();
                EditorUtility.SetDirty(target);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Randomize Colors"))
            {
                Undo.RecordObject(target, "Randomize Physarum Colors");
                RandomizeWithSettings(p);
                EditorUtility.SetDirty(target);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset to Defaults"))
            {
                Undo.RecordObject(target, "Reset Physarum Defaults");
                p.ResetToDefaults();
                EditorUtility.SetDirty(target);
                GUIUtility.ExitGUI();
            }
        }

        private void RandomizeWithSettings(PhysarumParams p)
        {
            var s = typeof(PaletteEditorState);
            var palette = ColorPalette.GenerateHS(p.types.Count,
                PaletteEditorState.lightnessMin, PaletteEditorState.lightnessMax,
                PaletteEditorState.hueMin, PaletteEditorState.hueMax);
            for (int i = 0; i < p.types.Count && i < palette.Count; i++)
            {
                p.types[i].hue = palette[i].hue;
                p.types[i].saturation = palette[i].saturation;
            }
        }

        private static void DrawColorSwatches(List<(float h, float s)> colors)
        {
            if (colors.Count == 0) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Palette Preview", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            float w = (EditorGUIUtility.currentViewWidth - 40) / colors.Count;
            foreach (var (h, s) in colors)
            {
                var c = Color.HSVToRGB(h, s, 0.85f);
                var rect = GUILayoutUtility.GetRect(w, 20);
                EditorGUI.DrawRect(rect, c);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawPaletteSettings()
        {
            PaletteEditorState.showPaletteSettings = EditorGUILayout.Foldout(
                PaletteEditorState.showPaletteSettings, "Palette Generation Settings");
            if (PaletteEditorState.showPaletteSettings)
            {
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
    }

    [CustomEditor(typeof(BoidParams))]
    public class BoidParamsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var typeCountProp = serializedObject.FindProperty("typeCount");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(typeCountProp);
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                Undo.RecordObject(target, "Sync Boid Types");
                ((BoidParams)target).SyncTypesList();
                EditorUtility.SetDirty(target);
                serializedObject.Update();
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("types"), true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ranges"), true);
            serializedObject.ApplyModifiedProperties();

            // Color swatches
            var p = (BoidParams)target;
            DrawColorSwatches(p.types.ConvertAll(t => (t.hue, t.saturation)));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Edit-Mode Tools", EditorStyles.boldLabel);

            DrawPaletteSettings();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Randomize Params"))
            {
                Undo.RecordObject(target, "Randomize Boid Params");
                p.RandomizeParams();
                EditorUtility.SetDirty(target);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button("Randomize Colors"))
            {
                Undo.RecordObject(target, "Randomize Boid Colors");
                RandomizeWithSettings(p);
                EditorUtility.SetDirty(target);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset to Defaults"))
            {
                Undo.RecordObject(target, "Reset Boid Defaults");
                p.ResetToDefaults();
                EditorUtility.SetDirty(target);
                GUIUtility.ExitGUI();
            }
        }

        private void RandomizeWithSettings(BoidParams p)
        {
            var palette = ColorPalette.GenerateHS(p.types.Count,
                PaletteEditorState.lightnessMin, PaletteEditorState.lightnessMax,
                PaletteEditorState.hueMin, PaletteEditorState.hueMax);
            for (int i = 0; i < p.types.Count && i < palette.Count; i++)
            {
                p.types[i].hue = palette[i].hue;
                p.types[i].saturation = palette[i].saturation;
            }
        }

        private static void DrawColorSwatches(List<(float h, float s)> colors)
        {
            if (colors.Count == 0) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Palette Preview", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            float w = (EditorGUIUtility.currentViewWidth - 40) / colors.Count;
            foreach (var (h, s) in colors)
            {
                var c = Color.HSVToRGB(h, s, 0.85f);
                var rect = GUILayoutUtility.GetRect(w, 20);
                EditorGUI.DrawRect(rect, c);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawPaletteSettings()
        {
            PaletteEditorState.showPaletteSettings = EditorGUILayout.Foldout(
                PaletteEditorState.showPaletteSettings, "Palette Generation Settings");
            if (PaletteEditorState.showPaletteSettings)
            {
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
    }
}

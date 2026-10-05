using System;
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// "Colors" section of the agent params inspector: a color field per type that shows (and
    /// takes) the exact rendered color, H/S/B sliders, palette apply / per-type pick, and saving
    /// the current colors into a palette.
    /// </summary>
    internal static class AgentColorsGUI
    {
        private const string PalettePrefKey = "Biomes.AgentColors.PaletteGuid";
        private const string PaletteFolder = "Assets/Workspace/11.0 Biomes/assets/Palettes";

        // One selection for every agent params inspector: pick a show once, apply it sim by sim.
        private static AgentColorPalette SelectedPalette
        {
            get
            {
                var path = AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(PalettePrefKey, ""));
                return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<AgentColorPalette>(path);
            }
            set => EditorPrefs.SetString(PalettePrefKey,
                value != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long _) ? guid : "");
        }

        public static void Draw(Editor editor, IAgentColorParams p)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Colors", EditorStyles.boldLabel);
            var palette = DrawPaletteRow(editor.target, p);
            for (int i = 0; i < p.TypeCount; i++)
                DrawTypeRow(editor, p, i, palette);
            editor.serializedObject.Update();   // rows write the object directly; refresh before the types list draws
        }

        private static AgentColorPalette DrawPaletteRow(UnityEngine.Object target, IAgentColorParams p)
        {
            EditorGUI.BeginChangeCheck();
            var palette = (AgentColorPalette)EditorGUILayout.ObjectField("Palette", SelectedPalette, typeof(AgentColorPalette), false);
            if (EditorGUI.EndChangeCheck()) SelectedPalette = palette;

            if (palette != null)
            {
                DrawSwatchStrip(palette, p.Family);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button($"Apply to {p.TypeCount} types"))
                    {
                        Undo.RecordObject(target, "Apply Palette");
                        palette.ApplyTo(p);
                        EditorUtility.SetDirty(target);
                    }
                    if (GUILayout.Button(new GUIContent("Store in palette", $"Replace the palette's {p.Family} swatches with these colors")))
                    {
                        Undo.RecordObject(palette, "Store in Palette");
                        palette.StoreFrom(p);
                        EditorUtility.SetDirty(palette);
                    }
                }
            }
            if (GUILayout.Button("Save as new palette…"))
                SaveAsNewPalette(target, p);
            return palette;
        }

        // Tall swatches are the ones Apply draws from for this sim; short ones belong to other sims.
        private static void DrawSwatchStrip(AgentColorPalette palette, AgentFamily family)
        {
            int n = palette.swatches.Count;
            if (n == 0) return;
            var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(false, 18f));
            float w = rect.width / n;
            for (int i = 0; i < n; i++)
            {
                float h = PaletteAssign.IsCandidate(palette.swatches, family, i) ? rect.height : 6f;
                EditorGUI.DrawRect(new Rect(rect.x + i * w, rect.yMax - h, w - 1f, h), palette.swatches[i].color);
            }
        }

        private static void DrawTypeRow(Editor editor, IAgentColorParams p, int i, AgentColorPalette palette)
        {
            var target = editor.target;
            var (h, s, b) = p.GetHsb(i);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var picked = EditorGUILayout.ColorField(new GUIContent($"Type {i}"), AgentColor.ToRgb(h, s, b),
                    showEyedropper: true, showAlpha: false, hdr: false);
                if (EditorGUI.EndChangeCheck())
                    Write(target, p, i, AgentColor.FromRgb(picked, h, s));

                using (new EditorGUI.DisabledScope(palette == null || palette.swatches.Count == 0))
                {
                    var content = new GUIContent("▾", "Pick a palette color for this type");
                    var rect = GUILayoutUtility.GetRect(content, EditorStyles.miniButton, GUILayout.Width(22f));
                    if (GUI.Button(rect, content, EditorStyles.miniButton))
                        PopupWindow.Show(rect, new SwatchPopup(palette, p.Family, color =>
                        {
                            Write(target, p, i, AgentColor.FromRgb(color, h, s));
                            editor.Repaint();
                        }));
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                float labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 14f;
                EditorGUI.BeginChangeCheck();
                float nh = EditorGUILayout.Slider("H", h, 0f, 1f);
                float ns = EditorGUILayout.Slider("S", s, 0f, 1f);
                float nb = EditorGUILayout.Slider("B", b, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                    Write(target, p, i, (nh, ns, nb));
                EditorGUIUtility.labelWidth = labelWidth;
            }
        }

        private static void Write(UnityEngine.Object target, IAgentColorParams p, int i, (float h, float s, float b) c)
        {
            Undo.RecordObject(target, "Edit Agent Color");
            p.SetHsb(i, c);
            EditorUtility.SetDirty(target);
        }

        private static void SaveAsNewPalette(UnityEngine.Object target, IAgentColorParams p)
        {
            string source = target.name.Replace("(Clone)", "").Trim();
            string path = EditorUtility.SaveFilePanelInProject("Save Agent Color Palette", $"{source} Palette", "asset",
                "Save these type colors as a palette", PaletteFolder);
            if (!string.IsNullOrEmpty(path))
            {
                var palette = ScriptableObject.CreateInstance<AgentColorPalette>();
                palette.notes = $"Saved from {source} on {DateTime.Now:yyyy-MM-dd}.";
                palette.StoreFrom(p);
                AssetDatabase.CreateAsset(palette, path);
                AssetDatabase.SaveAssets();
                SelectedPalette = palette;
            }
            GUIUtility.ExitGUI();   // the save panel ran a nested event loop; end this layout pass
        }

        private sealed class SwatchPopup : PopupWindowContent
        {
            private const float Cell = 26f, Pad = 4f;
            private const int Columns = 8;
            private readonly AgentColorPalette _palette;
            private readonly AgentFamily _family;
            private readonly Action<Color> _pick;

            public SwatchPopup(AgentColorPalette palette, AgentFamily family, Action<Color> pick)
            {
                _palette = palette; _family = family; _pick = pick;
            }

            public override Vector2 GetWindowSize()
            {
                int n = _palette.swatches.Count;
                return new Vector2(Mathf.Min(n, Columns) * Cell + 2f * Pad, ((n + Columns - 1) / Columns) * Cell + 2f * Pad);
            }

            public override void OnGUI(Rect rect)
            {
                var swatches = _palette.swatches;
                for (int i = 0; i < swatches.Count; i++)
                {
                    var r = new Rect(Pad + (i % Columns) * Cell, Pad + (i / Columns) * Cell, Cell - 2f, Cell - 2f);
                    EditorGUI.DrawRect(r, swatches[i].color);
                    if (!PaletteAssign.IsCandidate(swatches, _family, i))   // tagged for another sim
                        EditorGUI.DrawRect(new Rect(r.x, r.yMax - 4f, r.width, 4f), new Color(0f, 0f, 0f, 0.6f));
                    string tip = "#" + ColorUtility.ToHtmlStringRGB(swatches[i].color)
                               + (swatches[i].family == AgentFamily.Any ? "" : " · " + swatches[i].family);
                    if (GUI.Button(r, new GUIContent("", tip), GUIStyle.none))
                    {
                        _pick(swatches[i].color);
                        editorWindow.Close();
                    }
                }
            }
        }
    }
}

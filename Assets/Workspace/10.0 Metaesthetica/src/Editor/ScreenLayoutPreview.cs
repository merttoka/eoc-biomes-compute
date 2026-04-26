using UnityEditor;
using UnityEngine;

namespace Metaesthetica
{
    public class ScreenLayoutPreview : EditorWindow
    {
        private ScreenLayout _layout;
        private SimulationManager _simManager;
        private float _padding = 20f;
        private bool _showLabels = true;
        private bool _showPixelDims = true;
        private float _labelAlpha = 0.85f;

        private static readonly Color COL_ENTRANCE = new(0.7f, 0.4f, 1f, 0.6f);
        private static readonly Color COL_FRONT    = new(0.2f, 0.8f, 0.4f, 0.6f);
        private static readonly Color COL_SIDE     = new(1f, 0.5f, 0.2f, 0.6f);
        private static readonly Color COL_ROUND    = new(0.3f, 0.7f, 1f, 0.6f);

        [MenuItem("Tools/Screen Layout Preview (10.0)")]
        static void Open()
        {
            var win = GetWindow<ScreenLayoutPreview>("Screen Layout");
            win.minSize = new Vector2(400, 300);
        }

        void OnEnable()
        {
            EditorApplication.update += Repaint;
        }

        void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            _layout = (ScreenLayout)EditorGUILayout.ObjectField("Screen Layout", _layout, typeof(ScreenLayout), true);
            if (_layout == null)
                _layout = FindFirstObjectByType<ScreenLayout>();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _simManager = (SimulationManager)EditorGUILayout.ObjectField("Sim Manager", _simManager, typeof(SimulationManager), true);
            if (_simManager == null)
                _simManager = FindFirstObjectByType<SimulationManager>();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _showLabels = EditorGUILayout.Toggle("Labels", _showLabels, GUILayout.Width(100));
            _showPixelDims = EditorGUILayout.Toggle("Pixel Dims", _showPixelDims, GUILayout.Width(120));
            _labelAlpha = EditorGUILayout.Slider("Opacity", _labelAlpha, 0.1f, 1f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            if (_layout == null || _layout.screens.Count == 0)
            {
                EditorGUILayout.HelpBox("No ScreenLayout found or no screens configured.", MessageType.Info);
                return;
            }

            float availW = position.width - _padding * 2;
            float availH = position.height - GUILayoutUtility.GetLastRect().yMax - _padding * 2;
            if (availH < 100) availH = 100;

            Texture compositeTex = GetCompositeTexture();
            float texAspect = compositeTex != null
                ? (float)compositeTex.width / compositeTex.height
                : 1f;

            float previewW, previewH;
            if (availW / availH > texAspect)
            {
                previewH = availH;
                previewW = previewH * texAspect;
            }
            else
            {
                previewW = availW;
                previewH = previewW / texAspect;
            }

            float originX = _padding + (availW - previewW) * 0.5f;
            float originY = GUILayoutUtility.GetLastRect().yMax + _padding;
            var previewRect = new Rect(originX, originY, previewW, previewH);

            if (compositeTex != null)
            {
                GUI.DrawTexture(previewRect, compositeTex, ScaleMode.StretchToFill);
            }
            else
            {
                EditorGUI.DrawRect(previewRect, new Color(0.1f, 0.1f, 0.1f, 1f));
                var noTexStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    fontSize = 12,
                    normal = { textColor = Color.gray }
                };
                GUI.Label(previewRect, "No composite texture\n(enter Play mode)", noTexStyle);
            }

            DrawRectOutline(previewRect, Color.gray * 0.5f, 1f);

            for (int i = 0; i < _layout.screens.Count; i++)
            {
                var cfg = _layout.screens[i];
                Color col = GetWallGroupColor(cfg.name);
                col.a = _labelAlpha;

                // Convert pixel source rect to preview coordinates
                Rect uv = _layout.PixelsToUV(cfg.sourcePixels);
                float rx = previewRect.x + uv.x * previewW;
                float ry = previewRect.y + (1f - uv.y - uv.height) * previewH;
                float rw = uv.width * previewW;
                float rh = uv.height * previewH;
                var screenRect = new Rect(rx, ry, rw, rh);

                Color fillCol = col;
                fillCol.a *= 0.25f;
                EditorGUI.DrawRect(screenRect, fillCol);
                DrawRectOutline(screenRect, col, 2f);

                if (_showLabels)
                {
                    string label = cfg.name.Replace("_", "\n");
                    if (_showPixelDims)
                        label += $"\n{cfg.pixelWidth}x{cfg.pixelHeight}"
                              +  $"\nsrc: {cfg.sourcePixels.width}x{cfg.sourcePixels.height}";

                    var labelStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 9,
                        wordWrap = true,
                        normal = { textColor = Color.white },
                        fontStyle = FontStyle.Bold,
                    };

                    var shadowRect = new Rect(screenRect.x + 1, screenRect.y + 1, screenRect.width, screenRect.height);
                    var shadowStyle = new GUIStyle(labelStyle) { normal = { textColor = Color.black } };
                    GUI.Label(shadowRect, label, shadowStyle);
                    GUI.Label(screenRect, label, labelStyle);
                }
            }

            float infoY = previewRect.yMax + 8;
            if (compositeTex != null)
            {
                EditorGUI.LabelField(new Rect(_padding, infoY, position.width - _padding * 2, 16),
                    $"Composite: {compositeTex.width}x{compositeTex.height}  |  {_layout.screens.Count} screens",
                    EditorStyles.miniLabel);
            }

            float legendY = infoY + 18;
            float legendX = _padding;
            DrawLegend(ref legendX, legendY, COL_ENTRANCE, "Entrance");
            DrawLegend(ref legendX, legendY, COL_FRONT, "Front");
            DrawLegend(ref legendX, legendY, COL_SIDE, "Side");
            DrawLegend(ref legendX, legendY, COL_ROUND, "Round");
        }

        private Texture GetCompositeTexture()
        {
            if (_layout != null && _layout.screenMaterial != null)
            {
                var tex = _layout.screenMaterial.GetTexture("_UnlitColorMap");
                if (tex != null) return tex;
            }
            if (_simManager != null && _simManager.m_compositeOutMat != null)
            {
                var tex = _simManager.m_compositeOutMat.GetTexture("_UnlitColorMap");
                if (tex != null) return tex;
            }
            return null;
        }

        private Color GetWallGroupColor(string name)
        {
            if (name.StartsWith("Entrance")) return COL_ENTRANCE;
            if (name.StartsWith("FrontWall")) return COL_FRONT;
            if (name.StartsWith("SideWall")) return COL_SIDE;
            if (name.StartsWith("Round")) return COL_ROUND;
            return Color.white * 0.7f;
        }

        private void DrawRectOutline(Rect r, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, thickness, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), color);
        }

        private void DrawLegend(ref float x, float y, Color color, string label)
        {
            EditorGUI.DrawRect(new Rect(x, y, 12, 12), color);
            GUI.Label(new Rect(x + 15, y - 1, 60, 16), label, EditorStyles.miniLabel);
            x += 75;
        }
    }
}

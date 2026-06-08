using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    [CustomEditor(typeof(MidiFighterTwister))]
    public class MidiFighterTwisterEditor : Editor
    {
        private static readonly Color COL_SIM_PHYSARUM = new(0.3f, 0.6f, 1f, 0.35f);
        private static readonly Color COL_SIM_BOID     = new(1f, 0.5f, 0.2f, 0.35f);
        private static readonly Color COL_SIM_TERMITE  = new(0.9f, 0.85f, 0.2f, 0.35f);
        private static readonly Color COL_BIOME        = new(0.3f, 0.8f, 0.3f, 0.35f);
        private static readonly Color COL_UMWELT       = new(0.3f, 0.8f, 0.8f, 0.35f);
        private static readonly Color COL_GLOBAL       = new(0.7f, 0.4f, 0.9f, 0.35f);
        private static readonly Color COL_EMPTY        = new(0.3f, 0.3f, 0.3f, 0.15f);

        private static readonly string[] BANK_NAMES = {
            "Bank 0: Core Params",
            "Bank 1: Secondary Params",
            "Bank 2: Visual + Biome",
            "Bank 3: Umwelt + Global"
        };

        private int _previewBank = 0;
        private int _previewHwBank = 0;
        private bool _showSideCCs = false;

        private void OnEnable()
        {
            var mft = (MidiFighterTwister)target;
            mft.onBankChanged += OnBankChanged;
        }

        private void OnDisable()
        {
            var mft = (MidiFighterTwister)target;
            if (mft != null) mft.onBankChanged -= OnBankChanged;
        }

        private void OnBankChanged()
        {
            EditorApplication.delayCall += () =>
            {
                if (target == null) return;
                var mft = (MidiFighterTwister)target;
                _previewBank = mft.SoftBank;
                _previewHwBank = mft.HwBank;
                Repaint();
            };
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var mft = (MidiFighterTwister)target;

            EditorGUILayout.Space(10);

            // ─── Rebuild button ───
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Rebuild All Banks", GUILayout.Height(24)))
                mft.BuildAllBanks();
            EditorGUILayout.EndHorizontal();

            // ─── Soft bank tabs ───
            EditorGUILayout.Space(6);
            _previewBank = GUILayout.Toolbar(_previewBank,
                new[] { "0: Core", "1: Secondary", "2: Visual+Biome", "3: Umwelt+Global" });

            // ─── HW bank tabs ───
            int hwBank = Application.isPlaying ? mft.HwBank : _previewHwBank;
            _previewHwBank = GUILayout.Toolbar(hwBank,
                new[] { "HW 1", "HW 2", "HW 3", "HW 4" });

            EditorGUILayout.LabelField($"{BANK_NAMES[_previewBank]}  |  HW Bank {_previewHwBank + 1}", EditorStyles.boldLabel);

            // ─── 4x4 grid ───
            var bankBindings = mft.GetBankBindings();
            var sims = mft.GetSimulations();
            if (bankBindings == null || bankBindings.Length <= _previewBank) return;
            var bindings = bankBindings[_previewBank];
            if (bindings == null) return;

            float cellW = (EditorGUIUtility.currentViewWidth - 40) / 4f;
            float cellH = 42;

            for (int row = 0; row < 4; row++)
            {
                EditorGUILayout.BeginHorizontal();
                for (int col = 0; col < 4; col++)
                {
                    int idx = _previewHwBank * MidiFighterTwister.ENCODERS_PER_HW_BANK + row * 4 + col;
                    var b = bindings[idx];
                    string label;
                    Color bgColor;

                    switch (b.target)
                    {
                        case MidiFighterTwister.BindingTarget.SimParam
                            when b.simIndex >= 0 && sims != null && b.simIndex < sims.Count && sims[b.simIndex] != null:
                        {
                            var sim = sims[b.simIndex];
                            string sn = sim.SimName;
                            string shortParam = b.paramName ?? "?";
                            if (shortParam.Length > 12) shortParam = shortParam.Substring(0, 12);
                            label = $"{sn[0]}{b.typeIndex}\n{shortParam}";
                            bgColor = sim is PhysarumSim ? COL_SIM_PHYSARUM
                                    : sim is TermiteSim ? COL_SIM_TERMITE
                                    : COL_SIM_BOID;
                            break;
                        }
                        case MidiFighterTwister.BindingTarget.BiomeCrossField:
                            label = $"Biome\n{b.label ?? "?"}";
                            bgColor = COL_BIOME;
                            break;
                        case MidiFighterTwister.BindingTarget.Umwelt:
                            label = $"Umwelt\n{b.label ?? "?"}";
                            bgColor = COL_UMWELT;
                            break;
                        case MidiFighterTwister.BindingTarget.Global:
                            label = $"Global\n{b.label ?? "?"}";
                            bgColor = COL_GLOBAL;
                            break;
                        default:
                            label = $"CC {idx}\n--";
                            bgColor = COL_EMPTY;
                            break;
                    }

                    var rect = GUILayoutUtility.GetRect(cellW, cellH);
                    EditorGUI.DrawRect(rect, bgColor);
                    // Border
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1), Color.gray * 0.6f);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height - 1, rect.width, 1), Color.gray * 0.6f);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1, rect.height), Color.gray * 0.6f);
                    EditorGUI.DrawRect(new Rect(rect.x + rect.width - 1, rect.y, 1, rect.height), Color.gray * 0.6f);

                    var style = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 9,
                        wordWrap = true,
                        normal = { textColor = Color.white },
                    };
                    EditorGUI.LabelField(rect, label, style);
                }
                EditorGUILayout.EndHorizontal();
            }

            // ─── Side button summary ───
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Side Buttons", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical("box", GUILayout.Width(EditorGUIUtility.currentViewWidth / 2 - 24));
            EditorGUILayout.LabelField("LEFT", EditorStyles.centeredGreyMiniLabel);
            DrawSideBtn("Top", "leftTop");
            DrawSideBtn("Mid", "leftMid");
            DrawSideBtn("Bot", "leftBot");
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("RIGHT", EditorStyles.centeredGreyMiniLabel);
            DrawSideBtn("Top", "rightTop");
            DrawSideBtn("Mid", "rightMid");
            DrawSideBtn("Bot", "rightBot");
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // ─── Side Button CC Numbers (collapsible) ───
            _showSideCCs = EditorGUILayout.Foldout(_showSideCCs, "Side Button CC Numbers (HW bank 1)");
            if (_showSideCCs)
            {
                EditorGUI.indentLevel++;
                serializedObject.Update();
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.BeginVertical();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ccSideL1"), new GUIContent("L1 Top"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ccSideL2"), new GUIContent("L2 Mid"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ccSideL3"), new GUIContent("L3 Bot"));
                EditorGUILayout.EndVertical();
                EditorGUILayout.BeginVertical();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ccSideR1"), new GUIContent("R1 Top"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ccSideR2"), new GUIContent("R2 Mid"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("ccSideR3"), new GUIContent("R3 Bot"));
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                serializedObject.ApplyModifiedProperties();
                EditorGUI.indentLevel--;
            }

            // ─── Legend ───
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            DrawLegendSwatch(COL_SIM_PHYSARUM, "Physarum");
            DrawLegendSwatch(COL_SIM_BOID, "Boid");
            DrawLegendSwatch(COL_SIM_TERMITE, "Termite");
            DrawLegendSwatch(COL_BIOME, "Biome");
            DrawLegendSwatch(COL_UMWELT, "Umwelt");
            DrawLegendSwatch(COL_GLOBAL, "Global");
            EditorGUILayout.EndHorizontal();

            // ─── Layout Mode Explanation ───
            EditorGUILayout.Space(4);
            var layoutProp = serializedObject.FindProperty("layoutMode");
            string layoutHelp = layoutProp.enumValueIndex switch
            {
                0 => // ColumnPerType
                    "COLUMN PER TYPE — Best for few types (1-4 total across all sims)\n" +
                    "Each column = one agent type. Rows = params.\n" +
                    "Types are flattened left-to-right: Physarum type0, type1, then Boid type0, etc.\n\n" +
                    "Example (2 Physarum types + 1 Boid type):\n" +
                    "  Col 0: P0    Col 1: P1    Col 2: B0    Col 3: --\n" +
                    "  Row 0: moveSpeed / moveSpeed / maxSpeed\n" +
                    "  Row 1: senseAngle / senseAngle / maxForce\n" +
                    "  Row 2: turnAngle / turnAngle / separateRange\n" +
                    "  Row 3: senseDist / senseDist / alignRange",
                1 => // ColumnPerSim
                    "COLUMN PER SIM — Best for many sims, fewer types\n" +
                    "Each column = one sim (type 0 only). Rows = params.\n" +
                    "Up to 4 sims side by side.\n\n" +
                    "Example (Physarum + Boid):\n" +
                    "  Col 0: Physarum    Col 1: Boid\n" +
                    "  Row 0: moveSpeed   / maxSpeed\n" +
                    "  Row 1: senseAngle  / maxForce\n" +
                    "  Row 2: turnAngle   / separateRange\n" +
                    "  Row 3: senseDist   / alignRange",
                2 => // HalfAndHalf
                    "HALF AND HALF — Best for exactly 2 sims with multiple types\n" +
                    "Top 2 rows (enc 0-7) = sim 0, bottom 2 rows (enc 8-15) = sim 1.\n" +
                    "Columns = types within each sim (up to 4).\n\n" +
                    "Example (Physarum 2 types + Boid 2 types):\n" +
                    "  Row 0: P0.move  P1.move  --  --\n" +
                    "  Row 1: P0.sense P1.sense --  --\n" +
                    "  Row 2: B0.speed B1.speed --  --\n" +
                    "  Row 3: B0.force B1.force --  --",
                _ => ""
            };
            EditorGUILayout.HelpBox(layoutHelp, MessageType.None);

            // ─── Help ───
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Encoder push: " + serializedObject.FindProperty("pushMode").enumDisplayNames[
                    serializedObject.FindProperty("pushMode").enumValueIndex] + "\n" +
                "Shift layer (hold MFT shift): fine-tune (10x precision)\n" +
                "Side L-mid / R-mid: cycle software banks\n" +
                "LED feedback sends ring positions + RGB colors (needs MIDI output plugin)\n" +
                "Sim LED colors: Physarum = blue, Boid = orange, Termite = yellow\n\n" +
                "All sims support 1-8 agent types (set on the params ScriptableObject).\n" +
                "Banks 0-1 map params by layout mode. Bank 2 maps visual + biome. Bank 3 maps umwelt + globals.",
                MessageType.Info);

            // Runtime info
            if (Application.isPlaying)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField($"Active: Soft Bank {mft.SoftBank} | HW Bank {mft.HwBank + 1}", EditorStyles.boldLabel);
            }
        }

        private void DrawSideBtn(string pos, string propName)
        {
            var prop = serializedObject.FindProperty(propName);
            if (prop == null) return;
            EditorGUILayout.LabelField($"  {pos}: {prop.enumDisplayNames[prop.enumValueIndex]}",
                EditorStyles.miniLabel);
        }

        private void DrawLegendSwatch(Color color, string label)
        {
            var rect = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
            EditorGUI.DrawRect(rect, color);
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(55));
        }
    }
}

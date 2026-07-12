#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    [CustomEditor(typeof(MidiPianoMixer))]
    public class MidiPianoMixerEditor : Editor
    {
        static readonly Color[] Palette = {
            new Color(0.20f,0.60f,1.00f), new Color(1.00f,0.55f,0.20f), new Color(0.40f,0.85f,0.45f),
            new Color(0.85f,0.40f,0.85f), new Color(0.90f,0.80f,0.30f), new Color(0.40f,0.80f,0.85f),
            new Color(0.85f,0.35f,0.40f), new Color(0.65f,0.65f,0.70f),
        };
        static readonly string[] Pitch = { "C","C#","D","D#","E","F","F#","G","G#","A","A#","B" };
        static string NoteName(int n) => n < 0 ? "--" : $"{Pitch[((n % 12) + 12) % 12]}{n / 12 - 1}";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var mixer = (MidiPianoMixer)target;
            var sm = serializedObject.FindProperty("m_SimManager").objectReferenceValue as SimulationManager;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Composition Mixer Map", EditorStyles.boldLabel);

            if (sm == null || sm.simulations == null || sm.simulations.Count == 0)
            {
                EditorGUILayout.HelpBox("Assign a SimulationManager with sims to see the key map.", MessageType.Info);
            }
            else
            {
                var notes = PianoMixerLayout.AssignSimNotes(mixer.mixerBaseNote, mixer.commandLowNote, sm.simulations.Count);
                for (int i = 0; i < sm.simulations.Count; i++)
                {
                    var sim = sm.simulations[i];
                    string simName = sim != null ? sim.SimName : $"sim{i}";
                    float w = sim != null ? sim.compositeWeight : 0f;

                    Rect row = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
                    Rect bar = new Rect(row.x, row.y + 2, row.width, row.height - 4);
                    EditorGUI.DrawRect(bar, new Color(0f, 0f, 0f, 0.15f));
                    Rect fill = new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(w / 4f), bar.height);
                    EditorGUI.DrawRect(fill, Palette[i % Palette.Length]);
                    EditorGUI.LabelField(row, $"   {NoteName(notes[i]),-4} →  {simName}   (w {w:0.00})");
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Command keys (hold sustain to arm resets)", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"   {NoteName(PianoMixerLayout.ResetFullNote)}   full Reset");
            EditorGUILayout.LabelField($"   {NoteName(PianoMixerLayout.ResetSimsOnlyNote)}   Reset sims only");
            EditorGUILayout.LabelField($"   {NoteName(PianoMixerLayout.TogglePauseNote)}   Pause");

            if (Application.isPlaying) Repaint(); // live weight bars during play
        }
    }
}
#endif

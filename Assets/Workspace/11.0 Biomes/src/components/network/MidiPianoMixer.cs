using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>Plays a MIDI piano (Clavinova) as a live composition mixer over sim
    /// compositeWeight. Subscribes to MIDIMapping note/pedal events; owns all
    /// compositeWeight writes. Coexists with MidiFighterTwister (disjoint write set).</summary>
    public class MidiPianoMixer : MonoBehaviour
    {
        [SerializeField] private MIDIMapping m_Midi;
        [SerializeField] private SimulationManager m_SimManager;

        [Header("Layout")]
        [Tooltip("First MIDI note of the mixer zone; white keys from here up auto-assign to sims in order. 21 = A0.")]
        public int mixerBaseNote = 21;
        [Tooltip("Notes at or above this are the command zone (resets/pause). 96 = C7.")]
        public int commandLowNote = 96;

        [Header("Response")]
        [Tooltip("velocity(0..1) x weightMax = target composite weight. >1 boosts a layer past its default 1.")]
        [Range(0f, 4f)] public float weightMax = 2f;
        [Tooltip("Seconds for a layer's weight to ease toward its played level.")]
        [Min(0.0001f)] public float smoothingSeconds = 0.08f;

        // Filled by RebuildLayout() in Task 3. simNotes[i] = MIDI note driving sim i.
        private int[] _simNotes = new int[0];
        private float[] _targetWeight = new float[0];

        [Button("Run Layout Self-Test")]
        public void RunLayoutSelfTest()
        {
            int pass = 0, fail = 0;
            void Check(bool cond, string label)
            { if (cond) pass++; else { fail++; Debug.LogError($"[PianoMixer:TEST] FAIL {label}"); } }

            // White keys: A0(21) white, A#0(22) black, C4(60) white, C#4(61) black.
            Check(PianoMixerLayout.IsWhiteKey(21), "A0 is white");
            Check(!PianoMixerLayout.IsWhiteKey(22), "A#0 is black");
            Check(PianoMixerLayout.IsWhiteKey(60), "C4 is white");
            Check(!PianoMixerLayout.IsWhiteKey(61), "C#4 is black");

            // Auto-assign 3 sims from A0 → first three white keys: A0(21), B0(23), C1(24).
            var notes = PianoMixerLayout.AssignSimNotes(21, 96, 3);
            Check(notes.Length == 3, "3 sims assigned");
            Check(notes.Length == 3 && notes[0] == 21 && notes[1] == 23 && notes[2] == 24,
                  $"white keys A0(21),B0(23),C1(24) got [{string.Join(",", notes)}]");

            // Classify.
            Check(PianoMixerLayout.Classify(23, 96, notes, out int si) == PianoMixerLayout.NoteRole.MixerSim && si == 1, "B0 → sim 1");
            Check(PianoMixerLayout.Classify(108, 96, notes, out _) == PianoMixerLayout.NoteRole.ResetFull, "C8 → ResetFull");
            Check(PianoMixerLayout.Classify(107, 96, notes, out _) == PianoMixerLayout.NoteRole.ResetSimsOnly, "B7 → ResetSimsOnly");
            Check(PianoMixerLayout.Classify(96, 96, notes, out _) == PianoMixerLayout.NoteRole.TogglePause, "C7 → TogglePause");
            Check(PianoMixerLayout.Classify(22, 96, notes, out _) == PianoMixerLayout.NoteRole.Ignored, "A#0 ignored");

            // Velocity → weight, clamped to 0..4.
            Check(Mathf.Approximately(PianoMixerLayout.VelocityToWeight(1f, 2f), 2f), "vel 1.0 x2 = 2.0");
            Check(Mathf.Approximately(PianoMixerLayout.VelocityToWeight(0.5f, 2f), 1f), "vel 0.5 x2 = 1.0");
            Check(PianoMixerLayout.VelocityToWeight(1f, 10f) == 4f, "clamped to 4");

            Debug.Log($"[PianoMixer:TEST] {pass} passed, {fail} failed");
        }
    }
}

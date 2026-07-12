using UnityEngine;

namespace Biomes
{
    /// <summary>Pure, testable mapping logic for the Clavinova composition mixer. No Unity
    /// runtime state, so it is exercised by MidiPianoMixer.RunLayoutSelfTest().</summary>
    public static class PianoMixerLayout
    {
        public const int ResetFullNote     = 108; // C8, highest key on an 88-key board
        public const int ResetSimsOnlyNote = 107; // B7, next to it
        public const int TogglePauseNote   = 96;  // C7

        public enum NoteRole { Ignored, MixerSim, ResetFull, ResetSimsOnly, TogglePause }

        // MIDI note is a white key when its pitch class is a natural (C D E F G A B).
        public static bool IsWhiteKey(int note)
        {
            switch (((note % 12) + 12) % 12)
            {
                case 0: case 2: case 4: case 5: case 7: case 9: case 11: return true;
                default: return false;
            }
        }

        // Assign sims to consecutive WHITE keys from baseNote upward, staying below the
        // command zone. simNotes[i] = MIDI note driving sim i, or -1 if the keys run out.
        public static int[] AssignSimNotes(int baseNote, int commandLowNote, int simCount)
        {
            var simNotes = new int[Mathf.Max(0, simCount)];
            for (int i = 0; i < simNotes.Length; i++) simNotes[i] = -1;
            int assigned = 0, note = baseNote;
            while (assigned < simCount && note < commandLowNote)
            {
                if (IsWhiteKey(note)) simNotes[assigned++] = note;
                note++;
            }
            return simNotes;
        }

        // Classify an incoming note. simIndex is set only when role == MixerSim.
        public static NoteRole Classify(int note, int commandLowNote, int[] simNotes, out int simIndex)
        {
            simIndex = -1;
            if (note >= commandLowNote)
            {
                if (note == ResetFullNote)     return NoteRole.ResetFull;
                if (note == ResetSimsOnlyNote) return NoteRole.ResetSimsOnly;
                if (note == TogglePauseNote)   return NoteRole.TogglePause;
                return NoteRole.Ignored;
            }
            if (simNotes != null)
                for (int i = 0; i < simNotes.Length; i++)
                    if (simNotes[i] == note) { simIndex = i; return NoteRole.MixerSim; }
            return NoteRole.Ignored;
        }

        // velocity01 (0..1) → composite-weight target, scaled by weightMax and clamped to the
        // SimulationBase.compositeWeight range (0..4).
        public static float VelocityToWeight(float velocity01, float weightMax)
            => Mathf.Clamp(velocity01 * weightMax, 0f, 4f);
    }
}

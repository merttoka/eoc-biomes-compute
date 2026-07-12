using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using EasyButtons;

namespace Biomes
{
    /// <summary>Plays a MIDI piano (Clavinova) as a live composition mixer over sim
    /// compositeWeight. Self-contained: opens its own Minis device connection (same pattern as
    /// MidiFighterTwister), so it needs no MIDIMapping component. Owns all compositeWeight
    /// writes and coexists with MidiFighterTwister (disjoint write set).</summary>
    public class MidiPianoMixer : MonoBehaviour
    {
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

        [Header("Debug")]
        [Tooltip("Log every raw MIDI note/CC this component receives (noisy). Action logs ([PianoMixer] ...) are always on.")]
        public bool logMidi = false;

        private const int CC_SUSTAIN = 64;
        private bool _sustainHeld;

        // simNotes[i] = MIDI note driving sim i.
        private int[] _simNotes = new int[0];
        private float[] _targetWeight = new float[0];

        private readonly List<Minis.MidiDevice> _devices = new();

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

        #region MIDI connection (self-contained, mirrors MidiFighterTwister)

        void OnEnable()
        {
            InputSystem.onDeviceChange += OnDeviceChange;
            ConnectAllDevices();
            RebuildLayout();
        }

        void OnDisable()
        {
            DisconnectAllDevices();
            InputSystem.onDeviceChange -= OnDeviceChange;
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device is not Minis.MidiDevice) return;
            // Reconnect the whole set (disconnect-then-connect) so add/remove never double-subscribes.
            DisconnectAllDevices();
            ConnectAllDevices();
        }

        void ConnectAllDevices()
        {
            foreach (var device in InputSystem.devices)
            {
                if (device is not Minis.MidiDevice midiDevice) continue;
                midiDevice.onWillNoteOn += OnWillNoteOn;
                midiDevice.onWillControlChange += OnWillControlChange;
                _devices.Add(midiDevice);
                if (logMidi) Debug.Log($"[PianoMixer] Connected: {device.description.product}");
            }
        }

        void DisconnectAllDevices()
        {
            foreach (var d in _devices)
            {
                d.onWillNoteOn -= OnWillNoteOn;
                d.onWillControlChange -= OnWillControlChange;
            }
            _devices.Clear();
        }

        void OnWillNoteOn(Minis.MidiNoteControl note, float velocity)
        {
            if (logMidi) Debug.Log($"[PianoMixer] NoteOn {note.noteNumber} vel {velocity:0.00}");
            HandleNoteOn(note.channel, note.noteNumber, velocity);
        }

        void OnWillControlChange(Minis.MidiValueControl cc, float value)
        {
            if (cc.controlNumber == CC_SUSTAIN)
            {
                bool held = value >= 0.5f;
                if (held != _sustainHeld)
                {
                    _sustainHeld = held;
                    if (logMidi) Debug.Log($"[PianoMixer] Sustain {(held ? "DOWN" : "UP")}");
                }
            }
        }

        #endregion

        /// <summary>Rebuild the key→sim map and seed targets from the sims' current weights
        /// (so an untouched layer holds its level). Call when the sim list changes.</summary>
        public void RebuildLayout()
        {
            int n = (m_SimManager != null && m_SimManager.simulations != null)
                ? m_SimManager.simulations.Count : 0;
            _simNotes = PianoMixerLayout.AssignSimNotes(mixerBaseNote, commandLowNote, n);
            _targetWeight = new float[n];
            for (int i = 0; i < n; i++)
            {
                var sim = m_SimManager.simulations[i];
                _targetWeight[i] = sim != null ? sim.compositeWeight : 1f;
            }
        }

        private void HandleNoteOn(int channel, int note, float velocity01)
        {
            var role = PianoMixerLayout.Classify(note, commandLowNote, _simNotes, out int simIndex);
            switch (role)
            {
                case PianoMixerLayout.NoteRole.MixerSim:
                    if (simIndex >= 0 && simIndex < _targetWeight.Length)
                    {
                        _targetWeight[simIndex] = PianoMixerLayout.VelocityToWeight(velocity01, weightMax);
                        Debug.Log($"[PianoMixer] sim {simIndex} weight → {_targetWeight[simIndex]:0.00}");
                    }
                    break;
                case PianoMixerLayout.NoteRole.ResetFull:
                    if (_sustainHeld && m_SimManager != null)
                    { m_SimManager.Reset(); Debug.Log("[PianoMixer] Reset()"); }
                    break;
                case PianoMixerLayout.NoteRole.ResetSimsOnly:
                    if (_sustainHeld && m_SimManager != null)
                    { m_SimManager.ResetSimsOnly(); Debug.Log("[PianoMixer] ResetSimsOnly()"); }
                    break;
                case PianoMixerLayout.NoteRole.TogglePause:
                    if (m_SimManager != null)
                    {
                        m_SimManager.stepsPerTick = m_SimManager.stepsPerTick > 0 ? 0 : 1;
                        Debug.Log($"[PianoMixer] Paused = {m_SimManager.stepsPerTick == 0}");
                    }
                    break;
            }
        }

        void Update()
        {
            if (m_SimManager == null || m_SimManager.simulations == null) return;
            if (_targetWeight.Length != m_SimManager.simulations.Count) { RebuildLayout(); return; }

            // Frame-rate-independent exponential ease toward each layer's played level.
            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0001f, smoothingSeconds));
            for (int i = 0; i < _targetWeight.Length; i++)
            {
                var sim = m_SimManager.simulations[i];
                if (sim == null) continue;
                sim.compositeWeight += (_targetWeight[i] - sim.compositeWeight) * k;
            }
        }
    }
}

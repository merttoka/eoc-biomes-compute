using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Metaesthetica
{
    sealed public class MIDIMapping : MonoBehaviour
    {
        #region Private Fields
        [SerializeField] private List<SimulationBase> m_Simulations = new List<SimulationBase>();

        private Dictionary<(int, int), float> m_lastMidiValues = new Dictionary<(int, int), float>();
        #endregion

        #region Public Classes
        [Serializable]
        public class MIDIControlMapping
        {
            public int m_MIDIChannel;
            public int m_ControlNumber;
            public int m_SimIndex;
            public string m_ParamName;
            public int m_ParamIndex;
            public bool m_UseDelta = false;
        }

        public List<MIDIControlMapping> m_MIDIMappings = new List<MIDIControlMapping>();
        #endregion

        #region Callback implementation
        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            var midiDevice = device as Minis.MidiDevice;
            if (midiDevice == null) return;

            AddLog($"MIDI Device {change} ({device.description.product})");

            DisconnectAllDevices();
            ConnectAllDevices();
        }

        void OnWillNoteOn(Minis.MidiNoteControl note, float velocity)
          => AddLog($"Ch.{note.channel,-2} " +
                    $"{note.shortDisplayName,3} ({note.noteNumber:000}) " +
                    $"Note On  {velocity * 100,3:0}%");

        void OnWillNoteOff(Minis.MidiNoteControl note)
          => AddLog($"Ch.{note.channel,-2} " +
                    $"{note.shortDisplayName,3} ({note.noteNumber:000}) " +
                     "Note Off");

        void OnWillAftertouch(Minis.MidiNoteControl note, float pressure)
          => AddLog($"Ch.{note.channel,-2} " +
                    $"{note.shortDisplayName,3} ({note.noteNumber:000}) " +
                    $"Pressure {pressure * 100,3:0}%");

        void OnWillControlChange(Minis.MidiValueControl cc, float value)
        {
            var controlKey = (cc.channel, cc.controlNumber);

            m_lastMidiValues.TryGetValue(controlKey, out float lastValue);
            float delta = value - lastValue;
            m_lastMidiValues[controlKey] = value;

            AddLog($"Ch.{cc.channel,-2} CC ({cc.controlNumber:000}) {value * 100,3:0}% (Delta: {delta:F2})");

            foreach (var mapping in m_MIDIMappings)
            {
                if (mapping.m_MIDIChannel == cc.channel && mapping.m_ControlNumber == cc.controlNumber)
                {
                    ApplyMapping(mapping, delta, value);
                }
            }
        }

        void OnWillChannelPressure(AxisControl axis, float value)
          => AddLog($"Ch.{((Minis.MidiDevice)axis.device).channel,-2} " +
                    $"Pressure {value * 100,3:0}%");

        void OnWillPitchBend(AxisControl axis, float value)
          => AddLog($"Ch.{((Minis.MidiDevice)axis.device).channel,-2} " +
                    $"PitchBend {value * 100,3:0}%");
        #endregion

        #region Private Methods
        private void ApplyMapping(MIDIControlMapping mapping, float delta, float value)
        {
            if (mapping.m_SimIndex < 0 || mapping.m_SimIndex >= m_Simulations.Count) return;
            var sim = m_Simulations[mapping.m_SimIndex];
            if (sim == null) return;

            if (mapping.m_UseDelta)
                sim.SetParameterDelta(mapping.m_ParamName, mapping.m_ParamIndex, delta);
            else
                sim.SetParameter(mapping.m_ParamName, mapping.m_ParamIndex, value);
        }
        #endregion

        #region Device detection

        List<Minis.MidiDevice> _devices = new();

        void ConnectAllDevices()
        {
            foreach (var device in InputSystem.devices)
            {
                var midiDevice = device as Minis.MidiDevice;
                if (midiDevice == null) continue;

                midiDevice.onWillNoteOn += OnWillNoteOn;
                midiDevice.onWillNoteOff += OnWillNoteOff;
                midiDevice.onWillAftertouch += OnWillAftertouch;
                midiDevice.onWillControlChange += OnWillControlChange;
                midiDevice.onWillChannelPressure += OnWillChannelPressure;
                midiDevice.onWillPitchBend += OnWillPitchBend;

                _devices.Add(midiDevice);
            }
        }

        void DisconnectAllDevices()
        {
            foreach (var midiDevice in _devices)
            {
                midiDevice.onWillNoteOn -= OnWillNoteOn;
                midiDevice.onWillNoteOff -= OnWillNoteOff;
                midiDevice.onWillAftertouch -= OnWillAftertouch;
                midiDevice.onWillControlChange -= OnWillControlChange;
                midiDevice.onWillChannelPressure -= OnWillChannelPressure;
                midiDevice.onWillPitchBend -= OnWillPitchBend;
            }
            _devices.Clear();
        }

        #endregion

        #region Logging

        void AddLog(string line)
        {
            Debug.Log(line);
        }

        #endregion

        #region MonoBehaviour implementation

        void Start()
        {
            InputSystem.onDeviceChange += OnDeviceChange;
            ConnectAllDevices();
        }

        void OnDestroy()
        {
            DisconnectAllDevices();
            InputSystem.onDeviceChange -= OnDeviceChange;
        }

        #endregion
    }
}

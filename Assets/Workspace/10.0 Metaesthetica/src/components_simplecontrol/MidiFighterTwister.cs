using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using RtMidi;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Metaesthetica
{
    /// <summary>
    /// Full Midi Fighter Twister integration for Metaesthetica.
    ///
    /// MFT hardware layout (0-indexed MIDI channels):
    ///   Encoders:      CC 0-15  Ch 0  (rotation, absolute 0-127)
    ///   Encoder push:  CC 0-15  Ch 1  (127=press, 0=release)
    ///   LED ring:      CC 0-15  Ch 0  (send TO device to set ring position)
    ///   RGB color:     CC 0-15  Ch 1  (send TO device, value = color index)
    ///   RGB animation: CC 0-15  Ch 2  (send TO device)
    ///   Side buttons:  CC 8-13  Ch 3  (L1=8,L2=9,L3=10,R1=11,R2=12,R3=13)
    ///   Shift layer:   Ch 4    (shift encoder values)
    ///
    /// 4x4 grid (encoder indices):
    ///   [ 0][ 1][ 2][ 3]    row 0
    ///   [ 4][ 5][ 6][ 7]    row 1
    ///   [ 8][ 9][10][11]    row 2
    ///   [12][13][14][15]    row 3
    ///
    /// Software banks (independent of MFT hardware banks):
    ///   Bank 0: Core sim params (moveSpeed, senseAngle, turnAngle, senseDistance / separationRange, alignmentRange, attractionRange, maxSpeed)
    ///   Bank 1: Secondary sim params (depositAmount, eatAmount, externalInfluenceStrength, diffuseRate / maxForce, foodSensorDistance, ...)
    ///   Bank 2: Visual (hue, saturation per type)
    ///   Bank 3: Global (stepsPerFrame, stepMod)
    /// </summary>
    [ExecuteInEditMode]
    public class MidiFighterTwister : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SimulationManager m_SimManager;
        [SerializeField] private List<SimulationBase> m_Simulations = new();
        [SerializeField] private ScreenLayout m_ScreenLayout;

        [Header("Layout")]
        public LayoutMode layoutMode = LayoutMode.ColumnPerType;

        [Header("Encoder Push")]
        public EncoderPushMode pushMode = EncoderPushMode.RandomizeParam;

        [Header("Side Buttons")]
        public SideButtonAction leftTop    = SideButtonAction.Reset;
        public SideButtonAction leftMid    = SideButtonAction.None;
        public SideButtonAction leftBot    = SideButtonAction.RandomizeParams;
        public SideButtonAction rightTop   = SideButtonAction.None;
        public SideButtonAction rightMid   = SideButtonAction.None;
        public SideButtonAction rightBot   = SideButtonAction.RandomizeColors;

        [Header("LED Feedback")]
        public bool sendLEDFeedback = true;
        [Range(0f, 2f)] public float ledUpdateInterval = 0.1f;

        [Header("Debug")]
        public bool logMidi = true;

        // ─── MFT MIDI constants (0-indexed channels) ───
        private const int CH_ENCODER = 0;
        private const int CH_PUSH    = 1;
        private const int CH_RGB     = 1;  // same channel, send TO device
        private const int CH_ANIM    = 2;
        private const int CH_SIDE    = 3;
        private const int CH_SHIFT   = 4;

        [HideInInspector] public int ccSideL1 = 8;
        [HideInInspector] public int ccSideL2 = 9;
        [HideInInspector] public int ccSideL3 = 10;
        [HideInInspector] public int ccSideR1 = 11;
        [HideInInspector] public int ccSideR2 = 12;
        [HideInInspector] public int ccSideR3 = 13;
        // HW banks offset by 6: bank1=8-13, bank2=14-19, bank3=20-25, bank4=26-31
        private const int SIDE_CC_BANK_OFFSET = 6;

        // MFT RGB color values (sent as CC value on Ch 1)
        private const int RGB_OFF      = 0;
        private const int RGB_BLUE     = 33;  // Physarum
        private const int RGB_ORANGE   = 83;  // Boid
        private const int RGB_GREEN    = 43;
        private const int RGB_PURPLE   = 110; // Global
        private const int RGB_WHITE    = 127;
        private const int RGB_RED      = 1;
        private const int RGB_CYAN     = 40;
        private const int RGB_YELLOW   = 65;

        // MFT LED animation values (sent as CC value on Ch 2)
        private const int ANIM_NONE    = 0;
        private const int ANIM_STROBE  = 47;
        private const int ANIM_PULSE   = 55;
        private const int ANIM_RAINBOW = 127;

        // ─── MIDI Output ───
        private MidiOut _midiOut;
        private bool _midiOutReady;

        // ─── State ───
        private int _softBank = 0;
        private int _hwBank = 0;
        public int SoftBank => _softBank;
        public int HwBank => _hwBank;
        public const int SOFT_BANK_COUNT = 4;
        public const int HW_BANK_COUNT = 4;
        public const int ENCODERS_PER_HW_BANK = 16;
        public const int TOTAL_ENCODERS = ENCODERS_PER_HW_BANK * HW_BANK_COUNT; // 64

        private Dictionary<(int, int), float> _lastValues = new();
        private List<Minis.MidiDevice> _devices = new();
        private float _lastLEDUpdate;

        // After bank switch, ignore absolute encoder values until the knob moves.
        private bool[] _encoderPickedUp = new bool[ENCODERS_PER_HW_BANK];

        // Event for editor repaint
        public event Action onBankChanged;

        // Per-bank binding tables: [softBank][encoder 0-63]
        // Encoder index = hwBank * 16 + localEncoder
        private EncoderBinding[][] _bankBindings;
        private int[] _bankColors; // RGB color per bank

        // ─── Binding struct ───
        public struct EncoderBinding
        {
            public BindingTarget target;
            public int simIndex;
            public string paramName;
            public int typeIndex;
            public string label;
            public float min, max;
            public Action<float> setter;
            public Func<float> getter;
        }

        public enum BindingTarget { None, SimParam, Global }

        public enum LayoutMode
        {
            ColumnPerType,
            ColumnPerSim,
            HalfAndHalf,
        }

        public enum EncoderPushMode
        {
            RandomizeParam,
            ResetParamToDefault,
            ToggleFineTune,
        }

        public enum SideButtonAction
        {
            None,
            Reset,
            RandomizeParams,
            RandomizeColors,
            RandomizeAll,
            PrevSoftBank,
            NextSoftBank,
            ExportPNG,
            TogglePause,
            SaveParams,
        }

        // Fine-tune state per encoder
        private bool[] _fineTune = new bool[16];

        // ─── Physarum param definitions (Vector4-based, 4 types per sim) ───
        private static readonly string[] PhysarumCoreParams = { "moveSpeed", "senseAngle", "turnAngle", "senseDistance" };
        private static readonly string[] PhysarumSecondaryParams = { "depositAmount", "eatAmount", "externalInfluenceStrength" };
        private static readonly string[] PhysarumVisualParams = { "hue", "saturation" };

        // ─── Boid param definitions (scalar, index ignored) ───
        private static readonly string[] BoidCoreParams = { "separationRange", "alignmentRange", "attractionRange", "maxSpeed" };
        private static readonly string[] BoidSecondaryParams = { "maxForce", "depositAmount", "eatAmount", "externalInfluenceStrength" };
        private static readonly string[] BoidVisualParams = { "hue", "saturation" };

        #region Lifecycle

        private bool _initialized;

        void OnEnable()
        {
            if (_initialized) return;
            _initialized = true;
            BuildAllBanks();
            for (int i = 0; i < 16; i++) _encoderPickedUp[i] = true;
            InputSystem.onDeviceChange += OnDeviceChange;
            ConnectAllDevices();
            OpenMidiOutput();
            SendAllLEDs();
        }

        void OnDisable()
        {
            if (!_initialized) return;
            _initialized = false;
            DisconnectAllDevices();
            CloseMidiOutput();
            InputSystem.onDeviceChange -= OnDeviceChange;
        }

        void Update()
        {
            if (sendLEDFeedback && Time.realtimeSinceStartup - _lastLEDUpdate > ledUpdateInterval)
            {
                _lastLEDUpdate = Time.realtimeSinceStartup;
                SendEncoderRingPositions();
            }
        }

        #endregion

        #region Bank Building

        public void BuildAllBanks()
        {
            _bankBindings = new EncoderBinding[SOFT_BANK_COUNT][];
            _bankColors = new int[SOFT_BANK_COUNT];
            for (int b = 0; b < SOFT_BANK_COUNT; b++)
                _bankBindings[b] = new EncoderBinding[TOTAL_ENCODERS];

            BuildBank0_CoreParams();
            BuildBank1_SecondaryParams();
            BuildBank2_Visual();
            BuildBank3_Global();

            if (logMidi)
                LogBindingTable(_softBank);
        }

        // Alias for editor
        public void BuildBindings() => BuildAllBanks();

        /// <summary>Bank 0: Core sim params.</summary>
        private void BuildBank0_CoreParams()
        {
            _bankColors[0] = RGB_BLUE;
            var bindings = _bankBindings[0];

            switch (layoutMode)
            {
                case LayoutMode.ColumnPerType: BuildSimParamsColumnPerType(bindings, 0); break;
                case LayoutMode.ColumnPerSim:  BuildSimParamsColumnPerSim(bindings, 0); break;
                case LayoutMode.HalfAndHalf:   BuildSimParamsHalfAndHalf(bindings, 0); break;
            }
        }

        /// <summary>Bank 1: Secondary sim params.</summary>
        private void BuildBank1_SecondaryParams()
        {
            _bankColors[1] = RGB_ORANGE;
            var bindings = _bankBindings[1];

            switch (layoutMode)
            {
                case LayoutMode.ColumnPerType: BuildSimParamsColumnPerType(bindings, 4); break;
                case LayoutMode.ColumnPerSim:  BuildSimParamsColumnPerSim(bindings, 4); break;
                case LayoutMode.HalfAndHalf:   BuildSimParamsHalfAndHalf(bindings, 4); break;
            }
        }

        /// <summary>Bank 2: Visual (hue, saturation per type).</summary>
        private void BuildBank2_Visual()
        {
            _bankColors[2] = RGB_GREEN;
            var bindings = _bankBindings[2];

            var columns = CollectTypeColumns();
            int cols = Mathf.Min(columns.Count, 4 * HW_BANK_COUNT);
            for (int col = 0; col < cols; col++)
            {
                var (simIdx, typeIdx) = columns[col];
                int e0 = ColRowToEncoderIdx(col, 0);
                int e1 = ColRowToEncoderIdx(col, 1);
                if (e0 < TOTAL_ENCODERS) bindings[e0] = MakeSimParamBinding(simIdx, "hue", typeIdx);
                if (e1 < TOTAL_ENCODERS) bindings[e1] = MakeSimParamBinding(simIdx, "saturation", typeIdx);
                // Row 2: diffuseRate if Physarum (Vector4), else scalar diffuseRate
                int e2 = ColRowToEncoderIdx(col, 2);
                if (e2 < TOTAL_ENCODERS) bindings[e2] = MakeSimParamBinding(simIdx, "diffuseRate", typeIdx);
            }
        }

        /// <summary>Bank 3: SimManager globals.</summary>
        private void BuildBank3_Global()
        {
            _bankColors[3] = RGB_PURPLE;
            var bindings = _bankBindings[3];

            // Column 0: SimManager globals
            var mgr = m_SimManager;
            if (mgr != null)
            {
                bindings[ColRowToEncoderIdx(0, 0)] = MakeGlobalBinding("stepsPerFrame",
                    v => mgr.stepsPerFrame = Mathf.RoundToInt(v), () => mgr.stepsPerFrame, 0f, 10f);
                bindings[ColRowToEncoderIdx(0, 1)] = MakeGlobalBinding("stepMod",
                    v => mgr.stepMod = Mathf.Max(1, Mathf.RoundToInt(v)), () => mgr.stepMod, 1f, 50f);
            }
        }

        // ─── Param layout builders (reused by bank 0 and 1) ───

        /// <summary>
        /// Maps global column index to encoder index across HW banks.
        /// Column 0-3 -> HW bank 0 (enc 0-15), columns 4-7 -> HW bank 1 (enc 16-31), etc.
        /// </summary>
        private int ColRowToEncoderIdx(int globalCol, int row)
        {
            int hwPage = globalCol / 4;
            int colInPage = globalCol % 4;
            return hwPage * ENCODERS_PER_HW_BANK + row * 4 + colInPage;
        }

        private string[] GetParamList(SimulationBase sim, int paramOffset)
        {
            // paramOffset 0 = core (rows 0-3), paramOffset 4 = secondary (rows 0-3)
            if (sim is PhysarumSim)
                return paramOffset == 0 ? PhysarumCoreParams : PhysarumSecondaryParams;
            if (sim is BoidSim)
                return paramOffset == 0 ? BoidCoreParams : BoidSecondaryParams;
            return Array.Empty<string>();
        }

        private void BuildSimParamsColumnPerType(EncoderBinding[] bindings, int paramOffset)
        {
            var columns = CollectTypeColumns();
            int cols = Mathf.Min(columns.Count, 4 * HW_BANK_COUNT);
            for (int col = 0; col < cols; col++)
            {
                var (simIdx, typeIdx) = columns[col];
                var sim = m_Simulations[simIdx];
                var pars = GetParamList(sim, paramOffset);
                for (int row = 0; row < 4; row++)
                {
                    if (row >= pars.Length) break;
                    int encIdx = ColRowToEncoderIdx(col, row);
                    if (encIdx < TOTAL_ENCODERS)
                        bindings[encIdx] = MakeSimParamBinding(simIdx, pars[row], typeIdx);
                }
            }
        }

        private void BuildSimParamsColumnPerSim(EncoderBinding[] bindings, int paramOffset)
        {
            int cols = Mathf.Min(m_Simulations.Count, 4 * HW_BANK_COUNT);
            for (int col = 0; col < cols; col++)
            {
                var sim = m_Simulations[col];
                if (sim == null) continue;
                var pars = GetParamList(sim, paramOffset);
                for (int row = 0; row < 4; row++)
                {
                    if (row >= pars.Length) break;
                    int encIdx = ColRowToEncoderIdx(col, row);
                    if (encIdx < TOTAL_ENCODERS)
                        bindings[encIdx] = MakeSimParamBinding(col, pars[row], 0);
                }
            }
        }

        private void BuildSimParamsHalfAndHalf(EncoderBinding[] bindings, int paramOffset)
        {
            for (int half = 0; half < 2; half++)
            {
                if (half >= m_Simulations.Count) break;
                var sim = m_Simulations[half];
                if (sim == null) continue;

                var pars = GetParamList(sim, paramOffset);
                int typeCount = Mathf.Max(1, GetTypeCount(sim));
                int colCount = Mathf.Min(typeCount, 4 * HW_BANK_COUNT);
                int startRow = half * 2;

                for (int col = 0; col < colCount; col++)
                {
                    for (int row = 0; row < 2; row++)
                    {
                        if (row >= pars.Length) break;
                        int encIdx = ColRowToEncoderIdx(col, startRow + row);
                        if (encIdx < TOTAL_ENCODERS)
                            bindings[encIdx] = MakeSimParamBinding(half, pars[row], col);
                    }
                }
            }
        }

        // ─── Binding factories ───

        private EncoderBinding MakeSimParamBinding(int simIdx, string paramName, int typeIdx)
        {
            return new EncoderBinding
            {
                target = BindingTarget.SimParam,
                simIndex = simIdx,
                paramName = paramName,
                typeIndex = typeIdx,
                label = paramName,
            };
        }

        private EncoderBinding MakeGlobalBinding(string label, Action<float> setter, Func<float> getter, float min, float max)
        {
            return new EncoderBinding
            {
                target = BindingTarget.Global,
                simIndex = -1, label = label,
                setter = setter, getter = getter,
                min = min, max = max,
            };
        }

        // ─── Helpers ───

        /// <summary>
        /// Collects (simIndex, typeIndex) pairs. Physarum has 4 types (Vector4 components).
        /// Boid has 1 type (scalar params).
        /// </summary>
        private List<(int simIdx, int typeIdx)> CollectTypeColumns()
        {
            var columns = new List<(int, int)>();
            for (int s = 0; s < m_Simulations.Count; s++)
            {
                var sim = m_Simulations[s];
                if (sim == null) continue;
                int tc = Mathf.Max(1, GetTypeCount(sim));
                for (int t = 0; t < tc; t++)
                    columns.Add((s, t));
            }
            return columns;
        }

        private int GetTypeCount(SimulationBase sim)
        {
            // Physarum uses Vector4 params = 4 types; Boid uses scalar params = 1 type
            if (sim is PhysarumSim) return 4;
            if (sim is BoidSim) return 1;
            return 1;
        }

        #endregion

        #region MIDI Input

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device is not Minis.MidiDevice) return;
            if (logMidi) Debug.Log($"[MFT] Device {change} ({device.description.product})");
            DisconnectAllDevices();
            ConnectAllDevices();
            SendAllLEDs();
        }

        void OnWillControlChange(Minis.MidiValueControl cc, float value)
        {
            int ch = cc.channel;
            int num = cc.controlNumber;

            var key = (ch, num);
            _lastValues.TryGetValue(key, out float lastValue);
            float delta = value - lastValue;
            _lastValues[key] = value;

            // ─── Encoder rotation (Ch 0, CC 0-63) ───
            if (ch == CH_ENCODER && num >= 0 && num < 64)
            {
                int hwBank = num / 16;
                int encoderIdx = num % 16;
                if (hwBank != _hwBank)
                    SetHwBank(hwBank);
                ApplyEncoder(encoderIdx, value, delta);
                return;
            }

            // ─── Shift encoder rotation (Ch 4, CC 0-63) ───
            if (ch == CH_SHIFT && num >= 0 && num < 64)
            {
                int hwBank = num / 16;
                int encoderIdx = num % 16;
                if (hwBank != _hwBank)
                    SetHwBank(hwBank);
                ApplyEncoder(encoderIdx, value, delta * 0.1f);
                return;
            }

            // ─── Encoder push (Ch 1, CC 0-63) ───
            if (ch == CH_PUSH && num >= 0 && num < 64)
            {
                if (value > 0.5f)
                    HandleEncoderPush(num % 16);
                return;
            }

            // ─── HW bank change (Ch 3, CC 0-3) ───
            if (ch == CH_SIDE && num >= 0 && num < HW_BANK_COUNT && value > 0.5f)
            {
                SetHwBank(num);
                if (logMidi)
                    Debug.Log($"[MFT] HW Bank -> {num + 1}");
                return;
            }

            // ─── Side buttons (normalize CC across HW banks) ───
            if (value > 0.5f)
            {
                int normCC = num;
                for (int b = 1; b < HW_BANK_COUNT; b++)
                {
                    if (num >= ccSideL1 + b * SIDE_CC_BANK_OFFSET &&
                        num <= ccSideR3 + b * SIDE_CC_BANK_OFFSET)
                    {
                        normCC = num - b * SIDE_CC_BANK_OFFSET;
                        break;
                    }
                }

                SideButtonAction action = SideButtonAction.None;
                if      (normCC == ccSideL1) action = leftTop;
                else if (normCC == ccSideL2) action = leftMid;
                else if (normCC == ccSideL3) action = leftBot;
                else if (normCC == ccSideR1) action = rightTop;
                else if (normCC == ccSideR2) action = rightMid;
                else if (normCC == ccSideR3) action = rightBot;
                if (action != SideButtonAction.None)
                {
                    ExecuteAction(action);
                    return;
                }
            }

            // ─── Unhandled CC ───
            if (logMidi)
                Debug.Log($"[MFT] Unhandled CC: Ch.{ch} CC#{num} = {value:F2}");
        }

        void OnWillNoteOn(Minis.MidiNoteControl note, float velocity)
        {
            if (logMidi)
                Debug.Log($"[MFT] Note {note.noteNumber} Ch.{note.channel} vel={velocity:F2}");

            if (note.noteNumber >= 0 && note.noteNumber < HW_BANK_COUNT)
            {
                SetHwBank(note.noteNumber);
                return;
            }
        }

        void OnWillNoteOff(Minis.MidiNoteControl note) { }

        #endregion

        #region Encoder Apply

        private void ApplyEncoder(int encoderIdx, float value, float delta)
        {
            if (_bankBindings == null) return;
            int globalIdx = _hwBank * ENCODERS_PER_HW_BANK + encoderIdx;
            if (globalIdx >= TOTAL_ENCODERS) return;
            var b = _bankBindings[_softBank][globalIdx];
            if (b.target == BindingTarget.None) return;

            // Pickup logic
            if (!_encoderPickedUp[encoderIdx])
            {
                float currentNorm = GetNormalizedValue(b);
                if (currentNorm >= 0f && Mathf.Abs(value - currentNorm) > 0.02f)
                    return;
                _encoderPickedUp[encoderIdx] = true;
            }

            switch (b.target)
            {
                case BindingTarget.SimParam:
                    if (b.simIndex >= 0 && b.simIndex < m_Simulations.Count && SimReady(m_Simulations[b.simIndex]))
                    {
                        var sim = m_Simulations[b.simIndex];
                        sim.SetParameter(b.paramName, b.typeIndex, value);
                        if (logMidi)
                            Debug.Log($"[MFT] B{_softBank} Enc{encoderIdx} -> {sim.SimName}.{b.paramName}[{b.typeIndex}] = {value:F2}");
                    }
                    break;

                case BindingTarget.Global:
                    if (b.setter != null)
                    {
                        float mapped = Mathf.Lerp(b.min, b.max, value);
                        b.setter(mapped);
                        if (logMidi)
                            Debug.Log($"[MFT] B{_softBank} Enc{encoderIdx} -> {b.label} = {mapped:F4}");
                    }
                    break;
            }
        }

        #endregion

        #region Encoder Push

        private void HandleEncoderPush(int encoderIdx)
        {
            if (_bankBindings == null) return;
            int globalIdx = _hwBank * ENCODERS_PER_HW_BANK + encoderIdx;
            if (globalIdx >= TOTAL_ENCODERS) return;
            var b = _bankBindings[_softBank][globalIdx];
            if (b.target == BindingTarget.None) return;

            switch (pushMode)
            {
                case EncoderPushMode.RandomizeParam:
                    RandomizeSingleBinding(b, encoderIdx);
                    break;

                case EncoderPushMode.ResetParamToDefault:
                    ResetSingleBinding(b, encoderIdx);
                    break;

                case EncoderPushMode.ToggleFineTune:
                    _fineTune[encoderIdx] = !_fineTune[encoderIdx];
                    if (logMidi) Debug.Log($"[MFT] Push {encoderIdx} fine-tune = {_fineTune[encoderIdx]}");
                    break;
            }
        }

        private void RandomizeSingleBinding(EncoderBinding b, int idx)
        {
            float rand = UnityEngine.Random.value;
            switch (b.target)
            {
                case BindingTarget.SimParam:
                    if (b.simIndex >= 0 && b.simIndex < m_Simulations.Count && SimReady(m_Simulations[b.simIndex]))
                    {
                        m_Simulations[b.simIndex].SetParameter(b.paramName, b.typeIndex, rand);
                        if (logMidi) Debug.Log($"[MFT] Push {idx} randomize {b.paramName}[{b.typeIndex}] = {rand:F2}");
                    }
                    break;
                case BindingTarget.Global:
                    if (b.setter != null)
                    {
                        float v = Mathf.Lerp(b.min, b.max, rand);
                        b.setter(v);
                        if (logMidi) Debug.Log($"[MFT] Push {idx} randomize {b.label} = {v:F4}");
                    }
                    break;
            }
        }

        private void ResetSingleBinding(EncoderBinding b, int idx)
        {
            switch (b.target)
            {
                case BindingTarget.SimParam:
                    if (b.simIndex >= 0 && b.simIndex < m_Simulations.Count && SimReady(m_Simulations[b.simIndex]))
                    {
                        m_Simulations[b.simIndex].SetParameter(b.paramName, b.typeIndex, 0.5f);
                        if (logMidi) Debug.Log($"[MFT] Push {idx} reset {b.paramName}[{b.typeIndex}]");
                    }
                    break;
                case BindingTarget.Global:
                    if (b.setter != null)
                    {
                        float mid = (b.min + b.max) * 0.5f;
                        b.setter(mid);
                        if (logMidi) Debug.Log($"[MFT] Push {idx} reset {b.label} = {mid:F4}");
                    }
                    break;
            }
        }

        #endregion

        #region Side Button Actions

        private void ExecuteAction(SideButtonAction action)
        {
            if (logMidi) Debug.Log($"[MFT] Action: {action}");

            switch (action)
            {
                case SideButtonAction.Reset:
                    m_SimManager?.Reset();
                    BuildAllBanks(); // rebind after reset (runtime clones change)
                    SendAllLEDs();
                    break;

                case SideButtonAction.RandomizeParams:
                    foreach (var sim in m_Simulations)
                    {
                        if (sim is PhysarumSim ps) ps.RandomizeParams();
                        else if (sim is BoidSim bs) bs.RandomizeParams();
                    }
                    break;

                case SideButtonAction.RandomizeColors:
                    foreach (var sim in m_Simulations)
                    {
                        if (sim is PhysarumSim ps) ps.RandomizeColors();
                        else if (sim is BoidSim bs) bs.RandomizeColors();
                    }
                    break;

                case SideButtonAction.RandomizeAll:
                    foreach (var sim in m_Simulations)
                    {
                        if (sim is PhysarumSim ps) { ps.RandomizeParams(); ps.RandomizeColors(); }
                        else if (sim is BoidSim bs) { bs.RandomizeParams(); bs.RandomizeColors(); }
                    }
                    break;

                case SideButtonAction.ExportPNG:
                    m_SimManager?.ExportPNG();
                    break;

                case SideButtonAction.PrevSoftBank:
                    SetSoftBank((_softBank - 1 + SOFT_BANK_COUNT) % SOFT_BANK_COUNT);
                    break;

                case SideButtonAction.NextSoftBank:
                    SetSoftBank((_softBank + 1) % SOFT_BANK_COUNT);
                    break;

                case SideButtonAction.TogglePause:
                    if (m_SimManager != null)
                    {
                        m_SimManager.stepsPerFrame = m_SimManager.stepsPerFrame > 0 ? 0 : 1;
                        if (logMidi) Debug.Log($"[MFT] Paused = {m_SimManager.stepsPerFrame == 0}");
                    }
                    break;

                case SideButtonAction.SaveParams:
                    SaveCurrentParams();
                    break;
            }
        }

        private void SaveCurrentParams()
        {
#if UNITY_EDITOR
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string folder = "Assets/Workspace/10.0 Metaesthetica/assets";

            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets/Workspace/10.0 Metaesthetica", "assets");

            foreach (var sim in m_Simulations)
            {
                if (sim is PhysarumSim ps && ps.agentParams != null)
                {
                    var copy = ScriptableObject.Instantiate(ps.agentParams);
                    copy.name = $"PhysarumParams_{timestamp}";
                    string path = $"{folder}/{copy.name}.asset";
                    AssetDatabase.CreateAsset(copy, path);
                    Debug.Log($"[MFT] Saved: {path}");
                }
                else if (sim is BoidSim bs && bs.agentParams != null)
                {
                    var copy = ScriptableObject.Instantiate(bs.agentParams);
                    copy.name = $"BoidParams_{timestamp}";
                    string path = $"{folder}/{copy.name}.asset";
                    AssetDatabase.CreateAsset(copy, path);
                    Debug.Log($"[MFT] Saved: {path}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[MFT] All params saved to {folder}");
#else
            Debug.Log("[MFT] SaveParams is editor-only.");
#endif
        }

        private void SetSoftBank(int bank)
        {
            _softBank = bank;
            for (int i = 0; i < ENCODERS_PER_HW_BANK; i++) _encoderPickedUp[i] = false;
            if (logMidi)
            {
                string[] bankNames = { "Core Params", "Secondary Params", "Visual", "Global" };
                Debug.Log($"[MFT] === Soft Bank {bank}: {bankNames[bank]} | HW Bank {_hwBank} ===");
                LogBindingTable(_softBank);
            }
            SendAllLEDs();
            onBankChanged?.Invoke();
        }

        private void SetHwBank(int bank)
        {
            _hwBank = bank;
            for (int i = 0; i < ENCODERS_PER_HW_BANK; i++) _encoderPickedUp[i] = false;
            if (logMidi)
                Debug.Log($"[MFT] HW Bank {bank} (Soft Bank {_softBank})");
            SendAllLEDs();
            onBankChanged?.Invoke();
        }

        #endregion

        #region LED Feedback (send MIDI TO device)

        private void SendAllLEDs()
        {
            if (!sendLEDFeedback || _bankBindings == null || !_midiOutReady) return;

            for (int i = 0; i < ENCODERS_PER_HW_BANK; i++)
            {
                int cc = _hwBank * ENCODERS_PER_HW_BANK + i; // CC includes HW bank offset
                int globalIdx = cc; // same mapping for binding lookup
                var b = _bankBindings[_softBank][globalIdx];

                if (b.target == BindingTarget.None)
                {
                    SendCC(CH_RGB, cc, RGB_OFF);
                    SendCC(CH_ENCODER, cc, 0);
                    SendCC(CH_ANIM, cc, ANIM_NONE);
                    continue;
                }

                int color = b.target switch
                {
                    BindingTarget.SimParam when b.simIndex >= 0 && b.simIndex < m_Simulations.Count
                        => m_Simulations[b.simIndex] is PhysarumSim ? RGB_BLUE : RGB_ORANGE,
                    BindingTarget.Global => RGB_PURPLE,
                    _ => RGB_OFF,
                };
                SendCC(CH_RGB, cc, color);

                float norm = GetNormalizedValue(b);
                if (norm >= 0f)
                    SendCC(CH_ENCODER, cc, Mathf.RoundToInt(norm * 127f));
            }
        }

        private void SendEncoderRingPositions()
        {
            if (!sendLEDFeedback || !_midiOutReady || _bankBindings == null) return;

            for (int i = 0; i < ENCODERS_PER_HW_BANK; i++)
            {
                int cc = _hwBank * ENCODERS_PER_HW_BANK + i;
                var b = _bankBindings[_softBank][cc];
                if (b.target == BindingTarget.None) continue;
                float norm = GetNormalizedValue(b);
                if (norm >= 0f)
                    SendCC(CH_ENCODER, cc, Mathf.RoundToInt(norm * 127f));
            }
        }

        private float GetNormalizedValue(EncoderBinding b)
        {
            switch (b.target)
            {
                case BindingTarget.SimParam:
                    if (b.simIndex >= 0 && b.simIndex < m_Simulations.Count)
                    {
                        var sim = m_Simulations[b.simIndex];
                        if (!SimReady(sim)) return -1f;
                        float raw = sim.GetParameter(b.paramName, b.typeIndex);
                        var (mn, mx) = GetParamRange(sim, b.paramName);
                        return Mathf.InverseLerp(mn, mx, raw);
                    }
                    break;
                case BindingTarget.Global:
                    if (b.getter != null)
                        return Mathf.InverseLerp(b.min, b.max, b.getter());
                    break;
            }
            return -1f;
        }

        /// <summary>True if the sim has runtime params (agentParams).</summary>
        private bool SimReady(SimulationBase sim)
        {
            if (sim == null) return false;
            if (sim is PhysarumSim ps) return ps.agentParams != null;
            if (sim is BoidSim bs) return bs.agentParams != null;
            return false;
        }

        /// <summary>Returns (min, max) ranges matching SetParameter MapAndClamp calls.</summary>
        private (float min, float max) GetParamRange(SimulationBase sim, string paramName)
        {
            if (sim is PhysarumSim)
            {
                return paramName switch
                {
                    "moveSpeed" => (0.01f, 5f),
                    "senseAngle" => (0.1f, 360f),
                    "senseDistance" => (0.1f, 200f),
                    "turnAngle" => (0.1f, 360f),
                    "externalInfluenceStrength" => (-1f, 20f),
                    "depositAmount" => (0.01f, 1f),
                    "eatAmount" => (0.01f, 2f),
                    "hue" => (0f, 1f),
                    "saturation" => (0f, 1f),
                    "diffuseRate" => (0f, 1f),
                    _ => (0f, 1f),
                };
            }
            if (sim is BoidSim)
            {
                return paramName switch
                {
                    "separationRange" => (1f, 500f),
                    "alignmentRange" => (1f, 500f),
                    "attractionRange" => (1f, 500f),
                    "maxSpeed" => (0.1f, 5f),
                    "maxForce" => (0.1f, 5f),
                    "depositAmount" => (0f, 1f),
                    "eatAmount" => (0f, 1f),
                    "externalInfluenceStrength" => (0f, 5f),
                    "hue" => (0f, 1f),
                    "saturation" => (0f, 1f),
                    "diffuseRate" => (0f, 1f),
                    _ => (0f, 1f),
                };
            }
            return (0f, 1f);
        }

        private void SendCC(int channel, int ccNumber, int value)
        {
            if (!_midiOutReady || _midiOut == null) return;
            byte status = (byte)(0xB0 | (channel & 0x0F));
            byte cc = (byte)(ccNumber & 0x7F);
            byte val = (byte)Mathf.Clamp(value, 0, 127);
            try
            {
                _midiOut.SendMessage(new byte[] { status, cc, val });
            }
            catch (Exception e)
            {
                if (logMidi) Debug.LogWarning($"[MFT] MIDI send error: {e.Message}");
                _midiOutReady = false;
            }
        }

        private void OpenMidiOutput()
        {
            CloseMidiOutput();
            try
            {
                _midiOut = MidiOut.Create();
                if (_midiOut == null || !_midiOut.IsOk) return;

                int portCount = _midiOut.PortCount;
                int mftPort = -1;
                for (int i = 0; i < portCount; i++)
                {
                    string name = _midiOut.GetPortName(i);
                    if (logMidi) Debug.Log($"[MFT] MIDI Out port {i}: {name}");
                    if (name.IndexOf("Midi Fighter Twister", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("MFT", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        mftPort = i;
                    }
                }

                if (mftPort >= 0)
                {
                    _midiOut.OpenPort(mftPort, "EoC_MFT_Out");
                    _midiOutReady = true;
                    Debug.Log($"[MFT] MIDI output opened: port {mftPort} ({_midiOut.GetPortName(mftPort)})");
                }
                else
                {
                    if (logMidi) Debug.Log("[MFT] No MFT output port found. LED feedback disabled.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MFT] Failed to open MIDI output: {e.Message}");
            }
        }

        private void CloseMidiOutput()
        {
            _midiOutReady = false;
            if (_midiOut != null)
            {
                try { _midiOut.ClosePort(); } catch { }
                _midiOut.Dispose();
                _midiOut = null;
            }
        }

        #endregion

        #region Device Management

        void ConnectAllDevices()
        {
            foreach (var device in InputSystem.devices)
            {
                if (device is not Minis.MidiDevice midiDevice) continue;
                midiDevice.onWillNoteOn += OnWillNoteOn;
                midiDevice.onWillNoteOff += OnWillNoteOff;
                midiDevice.onWillControlChange += OnWillControlChange;
                _devices.Add(midiDevice);
                if (logMidi) Debug.Log($"[MFT] Connected: {device.description.product}");
            }
        }

        void DisconnectAllDevices()
        {
            foreach (var d in _devices)
            {
                d.onWillNoteOn -= OnWillNoteOn;
                d.onWillNoteOff -= OnWillNoteOff;
                d.onWillControlChange -= OnWillControlChange;
            }
            _devices.Clear();
        }

        #endregion

        #region Logging

        public void LogBindingTable(int bank)
        {
            if (_bankBindings == null || bank >= _bankBindings.Length) return;
            var bindings = _bankBindings[bank];

            string[] bankNames = { "Core Params", "Secondary Params", "Visual", "Global" };
            string table = $"[MFT] Soft Bank {bank}: {bankNames[bank]} | HW Bank {_hwBank}\n";

            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    int idx = _hwBank * ENCODERS_PER_HW_BANK + row * 4 + col;
                    var b = bindings[idx];
                    string cell;

                    switch (b.target)
                    {
                        case BindingTarget.SimParam when b.simIndex >= 0 && b.simIndex < m_Simulations.Count:
                            string sn = m_Simulations[b.simIndex]?.SimName ?? "?";
                            cell = $"{sn[0]}{b.typeIndex}.{b.paramName}";
                            break;
                        case BindingTarget.Global:
                            cell = b.label ?? "--";
                            break;
                        default:
                            cell = "--";
                            break;
                    }

                    if (cell.Length > 16) cell = cell.Substring(0, 16);
                    table += $"[{cell,-16}] ";
                }
                table += "\n";
            }
            Debug.Log(table);
        }

        // Access for editor
        public EncoderBinding[][] GetBankBindings() => _bankBindings;
        public List<SimulationBase> GetSimulations() => m_Simulations;

        #endregion
    }
}

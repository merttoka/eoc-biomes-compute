using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using RtMidi;

namespace Biomes
{
    /// <summary>
    /// Full Midi Fighter Twister integration for Biomes.
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
    ///   Bank 0: Core sim params (moveSpeed, senseAngle, turnAngle, senseDistance / maxSpeed, maxForce, ranges)
    ///   Bank 1: Secondary sim params (depositAmount, eatAmount, diffuseRate, foodSensor...)
    ///   Bank 2: Visual + Biome (hue, saturation, biome cross-field interactions)
    ///   Bank 3: Umwelt + Global (metabolicHeat, oxygenConsumption, stepsPerTick, simRate)
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
        public SideButtonAction rightTop   = SideButtonAction.ResetSimsOnly;
        public SideButtonAction rightMid   = SideButtonAction.None;
        public SideButtonAction rightBot   = SideButtonAction.RandomizeColors;

        [Header("LED Feedback")]
        public bool sendLEDFeedback = true;
        [Range(0f, 2f)] public float ledUpdateInterval = 0.1f;

        [Header("LED Colors")]
        [Tooltip("MFT hue-wheel CC values (1-125). Column color = Lerp(start, end, typeIndex/(typeCount-1)).")]
        [Range(1, 125)] public int physarumHueStart = 20;
        [Range(1, 125)] public int physarumHueEnd   = 40;
        [Range(1, 125)] public int boidHueStart     = 78;
        [Range(1, 125)] public int boidHueEnd       = 98;
        [Range(1, 125)] public int termiteHueStart  = 57;
        [Range(1, 125)] public int termiteHueEnd    = 70;
        [Tooltip("Bank-switch flash: top row = soft bank, bottom row = HW bank. 0 disables.")]
        [Range(0f, 2f)] public float bankFlashDuration = 0.7f;

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
        // These are approximate; MFT uses a 0-127 color wheel
        private const int RGB_OFF      = 0;
        private const int RGB_BLUE     = 33;  // Physarum
        private const int RGB_ORANGE   = 83;  // Boid
        private const int RGB_GREEN    = 43;  // Biome
        private const int RGB_PURPLE   = 110; // Umwelt/Global
        private const int RGB_WHITE    = 127;
        private const int RGB_RED      = 1;
        private const int RGB_CYAN     = 40;
        private const int RGB_YELLOW   = 65;

        // MFT LED animation values (sent as CC value on Ch 2)
        // Per DJTT manual: 1-8 strobe, 9-16 pulse, 17-47 RGB brightness (47 = 100%), 127 rainbow
        private const int ANIM_NONE           = 0;
        private const int ANIM_RGB_BRIGHT_MAX = 47;

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

        // Bank-switch flash overlay: >0 while active; Update() restores LEDs on expiry.
        private float _flashUntil = -1f;
        private bool FlashActive => _flashUntil > 0f && Time.unscaledTime < _flashUntil;

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
            // For biome/umwelt/global bindings
            public string label;
            public float min, max;
            public Action<float> setter;
            public Func<float> getter;
        }

        public enum BindingTarget { None, SimParam, BiomeChannel, BiomeCrossField, Umwelt, Global }

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
            ResetSimsOnly,
            RandomizeParams,
            RandomizeColors,
            RandomizeAll,
            PrevSoftBank,
            NextSoftBank,
            ExportPNG,
            TogglePause,
            CycleDebugChannel,
            ClearBiome,
            ToggleDebugGrid,
            CycleScreen,
            ToggleScreen,
            SaveSnapshot,
            SaveToCurrentParams,
        }

        // Fine-tune state per encoder
        private bool[] _fineTune = new bool[16];

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
            if (_flashUntil > 0f && !FlashActive)
            {
                _flashUntil = -1f;
                SendAllLEDs();
            }
            if (FlashActive) return;
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
            BuildBank2_VisualAndBiome();
            BuildBank3_UmweltAndGlobal();

            if (logMidi)
                LogBindingTable(_softBank);
        }

        // Alias for editor
        public void BuildBindings() => BuildAllBanks();

        /// <summary>Bank 0: Core sim params — same layout logic as before.</summary>
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

        /// <summary>Bank 1: Secondary sim params (offset into ModulatableParams).</summary>
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

        /// <summary>Bank 2: Visual (hue, saturation per type) + Biome cross-field interactions.</summary>
        private void BuildBank2_VisualAndBiome()
        {
            _bankColors[2] = RGB_GREEN;
            var bindings = _bankBindings[2];

            // Types span across HW banks, last column of HW bank 0 = biome cross-field
            var columns = CollectTypeColumns();
            int maxTypeCols = 4 * HW_BANK_COUNT - 1; // reserve last col of bank 0 for biome
            int cols = Mathf.Min(columns.Count, maxTypeCols);
            for (int col = 0; col < cols; col++)
            {
                var (simIdx, typeIdx) = columns[col];
                // Skip col 3 of HW bank 0 (reserved for biome)
                int globalCol = col < 3 ? col : col + 1;
                int e0 = ColRowToEncoderIdx(globalCol, 0);
                int e1 = ColRowToEncoderIdx(globalCol, 1);
                int e2 = ColRowToEncoderIdx(globalCol, 2);
                int e3 = ColRowToEncoderIdx(globalCol, 3);
                if (e0 < TOTAL_ENCODERS) bindings[e0] = MakeSimParamBinding(simIdx, "hue", typeIdx);
                if (e1 < TOTAL_ENCODERS) bindings[e1] = MakeSimParamBinding(simIdx, "saturation", typeIdx);
                if (e2 < TOTAL_ENCODERS) bindings[e2] = MakeSimParamBinding(simIdx, "diffuseRate", typeIdx);
                if (e3 < TOTAL_ENCODERS) bindings[e3] = MakeSimParamBinding(simIdx, "depositAmount", typeIdx);
            }

            // Column 3 of HW bank 0: Biome cross-field interactions
            var biome = m_SimManager?.biome;
            var config = biome?.fieldConfig;
            if (config != null)
            {
                bindings[ColRowToEncoderIdx(3, 0)] = MakeBiomeCrossFieldBinding("wasteToNutrient",
                    v => config.wasteToNutrientRate = v, () => config.wasteToNutrientRate, 0f, 0.1f);
                bindings[ColRowToEncoderIdx(3, 1)] = MakeBiomeCrossFieldBinding("temp→Flow",
                    v => config.temperatureToFlowStrength = v, () => config.temperatureToFlowStrength, 0f, 1f);
                bindings[ColRowToEncoderIdx(3, 2)] = MakeBiomeCrossFieldBinding("temp→Perm",
                    v => config.temperatureToPermeability = v, () => config.temperatureToPermeability, 0f, 1f);
                bindings[ColRowToEncoderIdx(3, 3)] = MakeBiomeCrossFieldBinding("noiseScale",
                    v => config.noiseScale = v, () => config.noiseScale, 0f, 10f);
            }
        }

        /// <summary>Bank 3: Umwelt params + SimManager globals.</summary>
        private void BuildBank3_UmweltAndGlobal()
        {
            _bankColors[3] = RGB_PURPLE;
            var bindings = _bankBindings[3];

            // Columns 0+: Umwelt params per sim (spans HW banks, last col of bank 0 = globals)
            int maxSimCols = 4 * HW_BANK_COUNT - 1;
            for (int col = 0; col < Mathf.Min(m_Simulations.Count, maxSimCols); col++)
            {
                var sim = m_Simulations[col];
                if (sim == null || sim.umwelt == null) continue;
                var u = sim.umwelt;
                int globalCol = col < 3 ? col : col + 1; // skip col 3 (globals)

                bindings[ColRowToEncoderIdx(globalCol, 0)] = MakeUmweltBinding($"{sim.SimName[0]}.metHeat",
                    v => u.metabolicHeat = v, () => u.metabolicHeat, 0f, 0.1f);
                bindings[ColRowToEncoderIdx(globalCol, 1)] = MakeUmweltBinding($"{sim.SimName[0]}.O2cons",
                    v => u.oxygenConsumption = v, () => u.oxygenConsumption, 0f, 0.1f);
                bindings[ColRowToEncoderIdx(globalCol, 2)] = MakeUmweltBinding($"{sim.SimName[0]}.permMin",
                    v => u.preferredPermeabilityMin = v, () => u.preferredPermeabilityMin, 0f, 1f);
                bindings[ColRowToEncoderIdx(globalCol, 3)] = MakeUmweltBinding($"{sim.SimName[0]}.permMax",
                    v => u.preferredPermeabilityMax = v, () => u.preferredPermeabilityMax, 0f, 1f);
            }

            // Column 3 of HW bank 0: SimManager globals
            var mgr = m_SimManager;
            if (mgr != null)
            {
                bindings[ColRowToEncoderIdx(3, 0)] = MakeGlobalBinding("stepsPerTick",
                    v => mgr.stepsPerTick = Mathf.RoundToInt(v), () => mgr.stepsPerTick, 0f, 10f);
                bindings[ColRowToEncoderIdx(3, 1)] = MakeGlobalBinding("simRate",
                    v => { mgr.simRate = v; mgr.ApplySimRate(); }, () => mgr.simRate, 15f, 120f);

                var biome = mgr.biome;
                if (biome != null)
                {
                    bindings[ColRowToEncoderIdx(3, 2)] = MakeGlobalBinding("debugChannel",
                        v => biome.debugChannel = Mathf.RoundToInt(v), () => biome.debugChannel, 0f, BiomeChannel.Count - 1);
                }
            }
        }

        // ─── Param layout builders (reused by bank 0 and 1) ───

        /// <summary>
        /// Maps global column index to encoder index across HW banks.
        /// Column 0-3 → HW bank 0 (enc 0-15), columns 4-7 → HW bank 1 (enc 16-31), etc.
        /// </summary>
        private int ColRowToEncoderIdx(int globalCol, int row)
        {
            int hwPage = globalCol / 4;
            int colInPage = globalCol % 4;
            return hwPage * ENCODERS_PER_HW_BANK + row * 4 + colInPage;
        }

        private void BuildSimParamsColumnPerType(EncoderBinding[] bindings, int paramOffset)
        {
            var columns = CollectTypeColumns();
            // Up to 16 columns across 4 HW banks (4 cols per bank)
            int cols = Mathf.Min(columns.Count, 4 * HW_BANK_COUNT);
            for (int col = 0; col < cols; col++)
            {
                var (simIdx, typeIdx) = columns[col];
                var sim = m_Simulations[simIdx];
                var pars = sim.ModulatableParams;
                for (int row = 0; row < 4; row++)
                {
                    int pIdx = paramOffset + row;
                    if (pIdx >= pars.Count) break;
                    int encIdx = ColRowToEncoderIdx(col, row);
                    if (encIdx < TOTAL_ENCODERS)
                        bindings[encIdx] = MakeSimParamBinding(simIdx, pars[pIdx], typeIdx);
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
                var pars = sim.ModulatableParams;
                for (int row = 0; row < 4; row++)
                {
                    int pIdx = paramOffset + row;
                    if (pIdx >= pars.Count) break;
                    int encIdx = ColRowToEncoderIdx(col, row);
                    if (encIdx < TOTAL_ENCODERS)
                        bindings[encIdx] = MakeSimParamBinding(col, pars[pIdx], 0);
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

                var pars = sim.ModulatableParams;
                int typeCount = Mathf.Max(1, GetTypeCount(sim));
                int cols = Mathf.Min(typeCount, 4 * HW_BANK_COUNT);
                int startRow = half * 2;

                for (int col = 0; col < cols; col++)
                {
                    for (int row = 0; row < 2; row++)
                    {
                        int pIdx = paramOffset + row;
                        if (pIdx >= pars.Count) break;
                        int encIdx = ColRowToEncoderIdx(col, startRow + row);
                        if (encIdx < TOTAL_ENCODERS)
                            bindings[encIdx] = MakeSimParamBinding(half, pars[pIdx], col);
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

        private EncoderBinding MakeBiomeCrossFieldBinding(string label, Action<float> setter, Func<float> getter, float min, float max)
        {
            return new EncoderBinding
            {
                target = BindingTarget.BiomeCrossField,
                simIndex = -1, label = label,
                setter = setter, getter = getter,
                min = min, max = max,
            };
        }

        private EncoderBinding MakeUmweltBinding(string label, Action<float> setter, Func<float> getter, float min, float max)
        {
            return new EncoderBinding
            {
                target = BindingTarget.Umwelt,
                simIndex = -1, label = label,
                setter = setter, getter = getter,
                min = min, max = max,
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
            if (sim is PhysarumSim ps)
                return ps.agentParams != null ? ps.agentParams.types.Count
                     : ps.paramsSO != null ? ps.paramsSO.types.Count : 1;
            if (sim is BoidSim bs)
                return bs.agentParams != null ? bs.agentParams.types.Count
                     : bs.paramsSO != null ? bs.paramsSO.types.Count : 1;
            if (sim is TermiteSim ts)
                return ts.agentParams != null ? ts.agentParams.types.Count
                     : ts.paramsSO != null ? ts.paramsSO.types.Count : 1;
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
            // HW banks extend columns: CC 0-15 = page 0, 16-31 = page 1, 32-47 = page 2, 48-63 = page 3
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
                // Normalize: subtract HW bank offset to get bank-1 equivalent CC
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

            // MFT bank change: Note On on any channel, notes 0-3 = bank 1-4
            // (spec says Ch 3 notes 0-3, but some configs vary)
            if (note.noteNumber >= 0 && note.noteNumber < HW_BANK_COUNT)
            {
                SetHwBank(note.noteNumber);
                return;
            }
        }

        void OnWillNoteOff(Minis.MidiNoteControl note) { }

        #endregion

        #region Encoder Apply

        /// <summary>Wall-clock time (unscaled) of the last MFT input that changed a parameter.
        /// ParameterInterpolatorGroup watches this to yield automation to a live performer.</summary>
        public float LastInputTime { get; private set; }
        private void MarkInput() => LastInputTime = Time.unscaledTime;

        /// <summary>Force all encoders back to "needs pickup" so the next physical turn must
        /// re-cross the current value (no jump). Called after automation moves params.</summary>
        public void InvalidatePickup()
        {
            if (_encoderPickedUp == null) return;
            for (int i = 0; i < _encoderPickedUp.Length; i++) _encoderPickedUp[i] = false;
        }

        private void ApplyEncoder(int encoderIdx, float value, float delta)
        {
            if (_bankBindings == null) return;
            int globalIdx = _hwBank * ENCODERS_PER_HW_BANK + encoderIdx;
            if (globalIdx >= TOTAL_ENCODERS) return;
            var b = _bankBindings[_softBank][globalIdx];
            if (b.target == BindingTarget.None) return;

            MarkInput(); // any bound-encoder turn yields automation to the performer

            // Pickup logic: after bank switch, ignore absolute values until
            // the knob crosses through the current param value.
            // This prevents slamming a param when you switch banks.
            if (!_encoderPickedUp[encoderIdx])
            {
                float currentNorm = GetNormalizedValue(b);
                if (currentNorm >= 0f && Mathf.Abs(value - currentNorm) > 0.02f)
                {
                    // Knob hasn't reached the current value yet — skip
                    return;
                }
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

                case BindingTarget.BiomeCrossField:
                case BindingTarget.Umwelt:
                case BindingTarget.Global:
                    if (b.setter != null)
                    {
                        float mapped = Mathf.Lerp(b.min, b.max, value);
                        b.setter(mapped);

                        // Re-upload biome settings if we changed them
                        if (b.target == BindingTarget.BiomeCrossField)
                            m_SimManager?.biome?.UploadChannelSettings();

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

            MarkInput();

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
                case BindingTarget.BiomeCrossField:
                case BindingTarget.Umwelt:
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
                case BindingTarget.BiomeCrossField:
                case BindingTarget.Umwelt:
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

                case SideButtonAction.ResetSimsOnly:
                    m_SimManager?.ResetSimsOnly();
                    BuildAllBanks();
                    SendAllLEDs();
                    break;

                case SideButtonAction.RandomizeParams:
                    foreach (var sim in m_Simulations)
                        sim?.LiveParamSet?.RandomizeParams();
                    break;

                case SideButtonAction.RandomizeColors:
                    foreach (var sim in m_Simulations)
                        sim?.LiveParamSet?.RandomizeColors();
                    break;

                case SideButtonAction.RandomizeAll:
                    foreach (var sim in m_Simulations)
                    {
                        sim?.LiveParamSet?.RandomizeParams();
                        sim?.LiveParamSet?.RandomizeColors();
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
                        m_SimManager.stepsPerTick = m_SimManager.stepsPerTick > 0 ? 0 : 1;
                        if (logMidi) Debug.Log($"[MFT] Paused = {m_SimManager.stepsPerTick == 0}");
                    }
                    break;

                case SideButtonAction.CycleDebugChannel:
                    if (m_SimManager?.biome != null)
                    {
                        m_SimManager.biome.debugChannel = (m_SimManager.biome.debugChannel + 1) % BiomeChannel.Count;
                        if (logMidi) Debug.Log($"[MFT] Debug channel = {m_SimManager.biome.debugChannel}");
                    }
                    break;

                case SideButtonAction.ClearBiome:
                    m_SimManager?.biome?.ClearAll();
                    break;

                case SideButtonAction.ToggleDebugGrid:
                    if (m_SimManager?.biome != null)
                    {
                        m_SimManager.biome.showDebugGrid = !m_SimManager.biome.showDebugGrid;
                        if (logMidi) Debug.Log($"[MFT] Debug grid = {m_SimManager.biome.showDebugGrid}");
                    }
                    break;

                case SideButtonAction.CycleScreen:
                    m_ScreenLayout?.CycleActiveScreen();
                    break;

                case SideButtonAction.ToggleScreen:
                    m_ScreenLayout?.ToggleCurrentScreen();
                    break;

                case SideButtonAction.SaveSnapshot:
                    SaveSnapshot();
                    break;

                case SideButtonAction.SaveToCurrentParams:
                    SaveToCurrentParams();
                    break;
            }
        }

        // Clone every sim's live params to timestamped .asset files so a "moment" can be
        // recalled later. Editor-only (AssetDatabase); no-op in standalone builds.
        private void SaveSnapshot()
        {
#if UNITY_EDITOR
            const string dir = "Assets/Workspace/11.0 Biomes/assets/Snapshots";
            System.IO.Directory.CreateDirectory(dir);
            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            int saved = 0;
            foreach (var sim in m_Simulations)
            {
                var live = sim?.LiveParamSet as ScriptableObject;
                if (live == null) continue;
                var clone = Instantiate(live);
                string path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{dir}/{sim.SimName}_{stamp}.asset");
                UnityEditor.AssetDatabase.CreateAsset(clone, path);
                saved++;
            }
            if (saved > 0) UnityEditor.AssetDatabase.SaveAssets();
            Debug.Log($"[MFT] Saved {saved} param snapshot(s) to {dir} ({stamp})");
#else
            Debug.LogWarning("[MFT] SaveSnapshot is editor-only (AssetDatabase); no-op in builds.");
#endif
        }

        // Overwrite each sim's CURRENT preset asset (paramsSO) with its live params — saves
        // "where you are now" into the assigned asset, no new snapshot file. Editor-only.
        private void SaveToCurrentParams()
        {
#if UNITY_EDITOR
            int saved = 0;
            foreach (var sim in m_Simulations)
                if (sim != null && sim.SaveLiveParamsToPreset()) saved++;
            if (saved > 0) UnityEditor.AssetDatabase.SaveAssets();
            Debug.Log($"[MFT] Saved live params into {saved} current preset asset(s)");
#else
            Debug.LogWarning("[MFT] SaveToCurrentParams is editor-only (AssetDatabase); no-op in builds.");
#endif
        }

        private void SetSoftBank(int bank)
        {
            _softBank = bank;
            for (int i = 0; i < ENCODERS_PER_HW_BANK; i++) _encoderPickedUp[i] = false;
            if (logMidi)
            {
                string[] bankNames = { "Core Params", "Secondary Params", "Visual + Biome", "Umwelt + Global" };
                Debug.Log($"[MFT] === Soft Bank {bank}: {bankNames[bank]} | HW Bank {_hwBank} ===");
                LogBindingTable(_softBank);
            }
            StartBankFlash();
            onBankChanged?.Invoke();
        }

        private void SetHwBank(int bank)
        {
            _hwBank = bank;
            for (int i = 0; i < ENCODERS_PER_HW_BANK; i++) _encoderPickedUp[i] = false;
            if (logMidi)
                Debug.Log($"[MFT] HW Bank {bank} (Soft Bank {_softBank})");
            StartBankFlash();
            onBankChanged?.Invoke();
        }

        #endregion

        #region LED Feedback (send MIDI TO device)

        /// <summary>
        /// Sends RGB colors and ring positions for all 16 encoders on the current bank.
        /// Requires MFT to be in a mode where it receives CC feedback.
        /// </summary>
        private void SendAllLEDs()
        {
            if (!sendLEDFeedback || _bankBindings == null || !_midiOutReady) return;

            for (int i = 0; i < ENCODERS_PER_HW_BANK; i++)
            {
                int cc = _hwBank * ENCODERS_PER_HW_BANK + i;
                var b = _bankBindings[_softBank][cc];

                if (b.target == BindingTarget.None)
                {
                    SendCC(CH_RGB, cc, RGB_OFF);
                    SendCC(CH_ENCODER, cc, 0);
                    SendCC(CH_ANIM, cc, ANIM_NONE);
                    continue;
                }

                int color = b.target switch
                {
                    BindingTarget.SimParam        => GetSimParamColor(b),
                    BindingTarget.BiomeCrossField => RGB_GREEN,
                    BindingTarget.Umwelt          => RGB_CYAN,
                    BindingTarget.Global          => RGB_PURPLE,
                    _ => RGB_OFF,
                };
                SendCC(CH_RGB, cc, color);
                SendCC(CH_ANIM, cc, ANIM_RGB_BRIGHT_MAX);

                float norm = GetNormalizedValue(b);
                if (norm >= 0f)
                    SendCC(CH_ENCODER, cc, Mathf.RoundToInt(norm * 127f));
            }
        }

        /// <summary>Flash bank identity: top row = softBank+1 knobs in bank color,
        /// bottom row = hwBank+1 knobs in white. Update() restores after bankFlashDuration.</summary>
        private void StartBankFlash()
        {
            if (!sendLEDFeedback || !_midiOutReady || _bankColors == null || bankFlashDuration <= 0f)
            {
                SendAllLEDs();
                return;
            }
            _flashUntil = Time.unscaledTime + bankFlashDuration;
            int ccBase = _hwBank * ENCODERS_PER_HW_BANK;
            for (int i = 0; i < ENCODERS_PER_HW_BANK; i++)
            {
                int row = i / 4, col = i % 4;
                int color = RGB_OFF;
                if (row == 0 && col <= _softBank) color = _bankColors[_softBank];
                else if (row == 3 && col <= _hwBank) color = RGB_WHITE;
                SendCC(CH_RGB, ccBase + i, color);
                SendCC(CH_ANIM, ccBase + i, color == RGB_OFF ? ANIM_NONE : ANIM_RGB_BRIGHT_MAX);
                SendCC(CH_ENCODER, ccBase + i, 0); // ring off during flash
            }
        }

        /// <summary>Updates ring positions only (called periodically from Update).</summary>
        private void SendEncoderRingPositions()
        {
            if (FlashActive) return;
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

        /// <summary>Hue-wheel CC for a SimParam binding: family range interpolated by type index.
        /// Single-type sims land on the range midpoint (≈ legacy family anchor).</summary>
        private int GetSimParamColor(EncoderBinding b)
        {
            if (b.simIndex < 0 || b.simIndex >= m_Simulations.Count) return RGB_OFF;
            var sim = m_Simulations[b.simIndex];
            (int start, int end) = sim switch
            {
                PhysarumSim => (physarumHueStart, physarumHueEnd),
                TermiteSim  => (termiteHueStart, termiteHueEnd),
                _           => (boidHueStart, boidHueEnd),
            };
            int typeCount = Mathf.Max(1, GetTypeCount(sim));
            float t = typeCount <= 1 ? 0.5f : (float)b.typeIndex / (typeCount - 1);
            return Mathf.RoundToInt(Mathf.Lerp(start, end, t));
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
                case BindingTarget.BiomeCrossField:
                case BindingTarget.Umwelt:
                case BindingTarget.Global:
                    if (b.getter != null)
                        return Mathf.InverseLerp(b.min, b.max, b.getter());
                    break;
            }
            return -1f;
        }

        /// <summary>True if the sim has runtime params (agentParams). False in edit mode.</summary>
        // Any sim with a live param set is controllable (covers Physarum/Boid/Termite/…).
        private bool SimReady(SimulationBase sim) => sim != null && sim.LiveParamSet != null;

        private (float min, float max) GetParamRange(SimulationBase sim, string paramName)
        {
            var p = sim != null ? sim.LiveParamSet : null;
            return p != null ? p.GetRange(paramName) : (0f, 1f);
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

            string[] bankNames = { "Core Params", "Secondary Params", "Visual + Biome", "Umwelt + Global" };
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
                            cell = $"{GetSimParamColor(b),3}|{sn[0]}{b.typeIndex}.{b.paramName}";
                            break;
                        case BindingTarget.BiomeCrossField:
                        case BindingTarget.Umwelt:
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

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    [Serializable]
    public class ParameterEvent
    {
        public int step;
        public int simIndex;
        public string paramName;
        public int typeIndex;
        public float value;
    }

    [Serializable]
    public class ParameterRecording
    {
        public int rezX;
        public int rezY;
        public List<string> simNames = new();
        public int totalSteps;
        public string recordedAt;
        public List<ParameterEvent> events = new();
    }

    public class ParameterRecorder : MonoBehaviour
    {
        [Header("References")]
        public SimulationManager simManager;

        [Header("Settings")]
        [Range(0.001f, 1f)] public float changeThreshold = 0.001f;
        public string savePath = "Recordings/params";

        [Header("State")]
        [SerializeField] private bool isRecording;
        [SerializeField] private bool isPlaying;
        [SerializeField] private int playbackStep;

        private ParameterRecording currentRecording;
        private ParameterRecording loadedRecording;

        // One sim's parameters at one frame: values[t * names.Count + p] = GetParameter(names[p], t)
        // for t < typeCount. Two sets per sim (previous / current frame) swap every frame, so
        // recording allocates nothing per frame beyond the change events themselves.
        private sealed class SimSnapshot
        {
            public IReadOnlyList<string> names;   // the sim's ModulatableParams; null = not started, nothing read
            public int typeCount;
            public float[] values = Array.Empty<float>();
        }

        private const int MaxProbedTypes = 8;
        private SimSnapshot[] _prevSnapshot = Array.Empty<SimSnapshot>();
        private SimSnapshot[] _currSnapshot = Array.Empty<SimSnapshot>();

        private int _playbackEventIndex;

        public bool IsRecording => isRecording;
        public bool IsPlaying => isPlaying;

        void Update()
        {
            if (simManager == null) return;

            if (isRecording)
                RecordFrame();

            if (isPlaying)
                PlaybackFrame();
        }

        // ═══════════════ RECORDING ═══════════════

        [Button("Start Recording")]
        public void StartRecording()
        {
            if (simManager == null) return;

            currentRecording = new ParameterRecording
            {
                rezX = simManager.rezX,
                rezY = simManager.rezY,
                recordedAt = DateTime.Now.ToString("o"),
            };

            for (int i = 0; i < simManager.simulations.Count; i++)
            {
                var sim = simManager.simulations[i];
                currentRecording.simNames.Add(sim != null ? sim.SimName : "null");
            }

            _prevSnapshot = Array.Empty<SimSnapshot>();
            _currSnapshot = Array.Empty<SimSnapshot>();
            MatchSimCount();
            TakeSnapshot(_prevSnapshot);
            isRecording = true;
            isPlaying = false;
            Debug.Log("ParameterRecorder: recording started");
        }

        [Button("Stop Recording")]
        public void StopRecording()
        {
            if (!isRecording) return;
            isRecording = false;
            currentRecording.totalSteps = simManager.SimStepCount;
            Debug.Log($"ParameterRecorder: stopped, {currentRecording.events.Count} events captured");
        }

        private void RecordFrame()
        {
            MatchSimCount();
            TakeSnapshot(_currSnapshot);
            int step = simManager.SimStepCount;

            for (int simIdx = 0; simIdx < _currSnapshot.Length; simIdx++)
            {
                if (simManager.simulations[simIdx] == null) continue;

                var prev = _prevSnapshot[simIdx];
                var curr = _currSnapshot[simIdx];
                if (prev.names == null || curr.names == null) continue;

                // Type-major, param-minor: the order the events have always been written in.
                int n = curr.names.Count;
                for (int t = 0; t < curr.typeCount; t++)
                {
                    for (int p = 0; p < n; p++)
                    {
                        if (!TryGetPrevious(prev, curr.names, p, t, out float prevVal)) continue;
                        float value = curr.values[t * n + p];
                        if (Mathf.Abs(value - prevVal) > changeThreshold)
                        {
                            currentRecording.events.Add(new ParameterEvent
                            {
                                step = step,
                                simIndex = simIdx,
                                paramName = curr.names[p],
                                typeIndex = t,
                                value = value,
                            });
                        }
                    }
                }
            }

            (_prevSnapshot, _currSnapshot) = (_currSnapshot, _prevSnapshot);
        }

        // The previous frame's value of (names[p], t). Absent when that frame didn't read it:
        // the sim had not started, or probed fewer types. A slot whose param list changed
        // (another sim assigned) matches by name.
        private static bool TryGetPrevious(SimSnapshot prev, IReadOnlyList<string> names, int p, int t, out float value)
        {
            value = 0f;
            if (t >= prev.typeCount) return false;
            int prevP = p;
            if (!ReferenceEquals(prev.names, names))
            {
                prevP = -1;
                for (int i = 0; i < prev.names.Count; i++)
                    if (prev.names[i] == names[p]) { prevP = i; break; }
                if (prevP < 0) return false;
            }
            value = prev.values[t * prev.names.Count + prevP];
            return true;
        }

        // Events hold RAW values (captured via GetParameter); SetParameter maps a 0..1 knob
        // into the param range, so replay writes raw into the live clone instead. No clone yet
        // (sim not started) = nothing to write to.
        private static void ApplyRecorded(SimulationBase sim, ParameterEvent evt) =>
            sim.LiveParamSet?.SetValue(evt.paramName, evt.typeIndex, evt.value);

        // Both snapshot sets get one slot per sim, sized for the sim's param list, so steady-state
        // frames never allocate. New slots start unread: no events on their first frame.
        private void MatchSimCount()
        {
            var sims = simManager.simulations;
            if (_currSnapshot.Length != sims.Count)
            {
                Array.Resize(ref _prevSnapshot, sims.Count);
                Array.Resize(ref _currSnapshot, sims.Count);
            }
            for (int i = 0; i < sims.Count; i++)
            {
                int size = sims[i] != null ? sims[i].ModulatableParams.Count * MaxProbedTypes : 0;
                _prevSnapshot[i] = Fit(_prevSnapshot[i], size);
                _currSnapshot[i] = Fit(_currSnapshot[i], size);
            }

            static SimSnapshot Fit(SimSnapshot s, int size)
            {
                s ??= new SimSnapshot();
                if (s.values.Length < size) Array.Resize(ref s.values, size);   // keeps the read values
                return s;
            }
        }

        // Type indices are probed rather than read from LiveParamSet.TypeCount, which keeps the
        // event stream identical to existing recordings: up to 8, stopping after the first index
        // > 0 whose params all read 0 (that row is kept). Agent sims read 0 past their type
        // count; the CA sims ignore the index and repeat type 0 in all 8 rows.
        private void TakeSnapshot(SimSnapshot[] into)
        {
            for (int i = 0; i < into.Length; i++)
            {
                var snap = into[i];
                snap.names = null;
                snap.typeCount = 0;
                var sim = simManager.simulations[i];
                if (sim == null || sim.LiveParamSet == null) continue;   // not started: nothing to record yet

                var names = sim.ModulatableParams;
                int n = names.Count;
                snap.names = names;
                for (int t = 0; t < MaxProbedTypes; t++)
                {
                    bool anyValid = false;
                    for (int p = 0; p < n; p++)
                    {
                        float val = sim.GetParameter(names[p], t);
                        snap.values[t * n + p] = val;
                        if (val != 0f) anyValid = true;
                    }
                    snap.typeCount = t + 1;
                    if (!anyValid && t > 0) break;
                }
            }
        }

        // ═══════════════ SAVE / LOAD ═══════════════

        [Button("Save Recording")]
        public void SaveRecording()
        {
            if (currentRecording == null || currentRecording.events.Count == 0)
            {
                Debug.LogWarning("ParameterRecorder: nothing to save");
                return;
            }

            string dir = Path.Combine(Application.dataPath, "..", savePath);
            Directory.CreateDirectory(dir);
            string filename = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string path = Path.Combine(dir, filename);

            string json = JsonUtility.ToJson(currentRecording, true);
            File.WriteAllText(path, json);
            Debug.Log($"ParameterRecorder: saved to {path}");
        }

        public void LoadRecording(string path)
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"ParameterRecorder: file not found: {path}");
                return;
            }

            string json = File.ReadAllText(path);
            loadedRecording = JsonUtility.FromJson<ParameterRecording>(json);
            Debug.Log($"ParameterRecorder: loaded {loadedRecording.events.Count} events from {path}");
        }

        [Button("Load Latest")]
        public void LoadLatest()
        {
            string dir = Path.Combine(Application.dataPath, "..", savePath);
            if (!Directory.Exists(dir))
            {
                Debug.LogWarning("ParameterRecorder: no recordings directory");
                return;
            }

            var files = Directory.GetFiles(dir, "recording_*.json");
            if (files.Length == 0)
            {
                Debug.LogWarning("ParameterRecorder: no recordings found");
                return;
            }

            Array.Sort(files);
            LoadRecording(files[files.Length - 1]);
        }

        // ═══════════════ PLAYBACK ═══════════════

        [Button("Start Playback")]
        public void StartPlayback()
        {
            if (loadedRecording == null)
            {
                Debug.LogWarning("ParameterRecorder: no recording loaded");
                return;
            }

            isPlaying = true;
            isRecording = false;
            playbackStep = 0;
            _playbackEventIndex = 0;
            Debug.Log($"ParameterRecorder: playback started ({loadedRecording.events.Count} events)");
        }

        [Button("Stop Playback")]
        public void StopPlayback()
        {
            isPlaying = false;
            Debug.Log("ParameterRecorder: playback stopped");
        }

        private void PlaybackFrame()
        {
            int currentStep = simManager.SimStepCount;

            while (_playbackEventIndex < loadedRecording.events.Count)
            {
                var evt = loadedRecording.events[_playbackEventIndex];
                if (evt.step > currentStep) break;

                // Apply event
                if (evt.simIndex >= 0 && evt.simIndex < simManager.simulations.Count)
                {
                    var sim = simManager.simulations[evt.simIndex];
                    if (sim != null)
                    {
                        ApplyRecorded(sim, evt);
                    }
                }
                _playbackEventIndex++;
            }

            // End of recording
            if (_playbackEventIndex >= loadedRecording.events.Count)
            {
                isPlaying = false;
                Debug.Log("ParameterRecorder: playback complete");
            }

            playbackStep = currentStep;
        }

        [Button("Seek to Step")]
        public void SeekToStep(int targetStep)
        {
            if (loadedRecording == null) return;

            // Apply all events up to targetStep
            for (int i = 0; i < loadedRecording.events.Count; i++)
            {
                var evt = loadedRecording.events[i];
                if (evt.step > targetStep) break;

                if (evt.simIndex >= 0 && evt.simIndex < simManager.simulations.Count)
                {
                    var sim = simManager.simulations[evt.simIndex];
                    if (sim != null) ApplyRecorded(sim, evt);
                }
                _playbackEventIndex = i + 1;
            }

            playbackStep = targetStep;
        }
    }
}

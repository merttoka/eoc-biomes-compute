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

        // Snapshot: [simIndex][paramName][typeIndex] = value
        private Dictionary<string, float>[] _prevSnapshot;

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

            _prevSnapshot = TakeSnapshot();
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
            var snapshot = TakeSnapshot();
            int step = simManager.SimStepCount;

            for (int simIdx = 0; simIdx < simManager.simulations.Count; simIdx++)
            {
                var sim = simManager.simulations[simIdx];
                if (sim == null) continue;

                var prev = _prevSnapshot[simIdx];
                var curr = snapshot[simIdx];

                foreach (var kvp in curr)
                {
                    if (prev.TryGetValue(kvp.Key, out float prevVal))
                    {
                        if (Mathf.Abs(kvp.Value - prevVal) > changeThreshold)
                        {
                            // Parse key: "paramName:typeIndex"
                            ParseKey(kvp.Key, out string paramName, out int typeIndex);
                            currentRecording.events.Add(new ParameterEvent
                            {
                                step = step,
                                simIndex = simIdx,
                                paramName = paramName,
                                typeIndex = typeIndex,
                                value = kvp.Value,
                            });
                        }
                    }
                }
            }

            _prevSnapshot = snapshot;
        }

        private Dictionary<string, float>[] TakeSnapshot()
        {
            var snapshot = new Dictionary<string, float>[simManager.simulations.Count];
            for (int i = 0; i < simManager.simulations.Count; i++)
            {
                snapshot[i] = new Dictionary<string, float>();
                var sim = simManager.simulations[i];
                if (sim == null) continue;

                var paramNames = sim.ModulatableParams;
                // Query each param for each type
                // We need to know type count — use GetParameter with increasing indices until it returns 0
                // Heuristic: try up to 8 types
                for (int typeIdx = 0; typeIdx < 8; typeIdx++)
                {
                    bool anyValid = false;
                    foreach (var pName in paramNames)
                    {
                        float val = sim.GetParameter(pName, typeIdx);
                        string key = $"{pName}:{typeIdx}";
                        snapshot[i][key] = val;
                        if (val != 0f) anyValid = true;
                    }
                    if (!anyValid && typeIdx > 0) break;
                }
            }
            return snapshot;
        }

        private static void ParseKey(string key, out string paramName, out int typeIndex)
        {
            int sep = key.LastIndexOf(':');
            paramName = key.Substring(0, sep);
            typeIndex = int.Parse(key.Substring(sep + 1));
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
                        sim.SetParameter(evt.paramName, evt.typeIndex, evt.value);
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
                    sim?.SetParameter(evt.paramName, evt.typeIndex, evt.value);
                }
                _playbackEventIndex = i + 1;
            }

            playbackStep = targetStep;
        }
    }
}

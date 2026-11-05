using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using UnityEditor.Recorder.Encoder;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// Records one pass of the active scene's <see cref="NeuronFiringPlayback"/> range as a seamless,
    /// frame-locked loop (ProRes, from <see cref="SimulationManager.recorderTarget"/> at rezX×rezY).
    /// Enters Play, pre-rolls the tail of the range so the sims carry end-of-pass state into the
    /// first recorded frame, records exactly durationSeconds × fps frames, then exits Play.
    /// The Recorder's constant frame rate sets the capture clock; the sims (FixedUpdate) and every
    /// <see cref="PresentationClock"/> consumer follow it, so the video matches the data at any
    /// render speed. Entering Play reloads the domain, so the request rides in SessionState.
    /// </summary>
    [InitializeOnLoad]
    public static class LoopTakeRecorder
    {
        private const string Key = "Biomes.LoopTake.";
        private static RecorderController _controller;
        private static bool _sawRecording;

        static LoopTakeRecorder()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        /// <param name="outputPath">Absolute path without extension.</param>
        /// <param name="passSeconds">Seconds to record; 0 = the playback's durationSeconds (one full loop).</param>
        /// <param name="fromDataFrame">Blob frame on the first recorded frame; -1 = the range start (loop take).
        /// Set it with a short passSeconds for an excerpt.</param>
        public static void Record(string outputPath, int fps = 60, float preRollSeconds = 120f,
                                  ProResEncoderSettings.OutputFormat format = ProResEncoderSettings.OutputFormat.ProRes422Proxy,
                                  float passSeconds = 0f, int fromDataFrame = -1)
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[LoopTake] Start from Edit mode."); return; }
            var manager = Object.FindFirstObjectByType<SimulationManager>();
            var playback = Object.FindFirstObjectByType<NeuronFiringPlayback>();
            if (manager == null || playback == null) { Debug.LogError("[LoopTake] Scene needs a SimulationManager and a NeuronFiringPlayback."); return; }
            // The manager blits into recorderTarget every frame while it is assigned; hand it back
            // unassigned after the take so live/standalone cost is unchanged.
            bool assigned = manager.recorderTarget == null;
            if (assigned) manager.CreateRecorderTargetAsset();
            SessionState.SetBool(Key + "Unassign", assigned);
            UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();

            SessionState.SetBool(Key + "Pending", true);
            SessionState.SetString(Key + "Out", outputPath);
            SessionState.SetInt(Key + "Fps", fps);
            SessionState.SetFloat(Key + "PreRoll", preRollSeconds);
            SessionState.SetInt(Key + "Format", (int)format);
            SessionState.SetFloat(Key + "Pass", passSeconds > 0f ? passSeconds : playback.durationSeconds);
            SessionState.SetInt(Key + "From", fromDataFrame >= 0 ? fromDataFrame : playback.startFrame);
            EditorApplication.EnterPlaymode();
        }

        public static string Status() => _controller != null && _controller.IsRecording()
            ? $"recording, frame {Time.frameCount}, data frame {Object.FindFirstObjectByType<NeuronFiringPlayback>()?.Frame}"
            : SessionState.GetBool(Key + "Pending", false) ? "pending" : "idle";

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Key + "Pending", false))
                Begin();
            else if (change == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Unassign", false))
            {
                SessionState.SetBool(Key + "Unassign", false);
                var manager = Object.FindFirstObjectByType<SimulationManager>();
                if (manager == null) return;
                manager.recorderTarget = null;
                EditorUtility.SetDirty(manager);
                UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
            }
            else if (change == PlayModeStateChange.ExitingPlayMode && _controller != null)
            {
                if (_controller.IsRecording()) _controller.StopRecording();
                _controller = null;
                EditorApplication.update -= Watch;
            }
        }

        private static void Begin()
        {
            SessionState.SetBool(Key + "Pending", false);
            int fps = SessionState.GetInt(Key + "Fps", 60);
            float pass = SessionState.GetFloat(Key + "Pass", 600f);
            float preRoll = SessionState.GetFloat(Key + "PreRoll", 120f);
            int preRollFrames = Mathf.RoundToInt(preRoll * fps);
            int passFrames = Mathf.RoundToInt(pass * fps);

            var manager = Object.FindFirstObjectByType<SimulationManager>();
            var playback = Object.FindFirstObjectByType<NeuronFiringPlayback>();
            var rt = manager.recorderTarget;

            var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            movie.name = "LoopTake";
            movie.Enabled = true;
            movie.EncoderSettings = new ProResEncoderSettings
                { Format = (ProResEncoderSettings.OutputFormat)SessionState.GetInt(Key + "Format", 5) };
            movie.ImageInputSettings = new RenderTextureInputSettings
                { RenderTexture = rt, OutputWidth = rt.width, OutputHeight = rt.height };
            movie.AudioInputSettings.PreserveAudio = false;
            movie.OutputFile = SessionState.GetString(Key + "Out", "Recordings/loop_take");

            var settings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            settings.AddRecorderSettings(movie);
            settings.SetRecordModeToFrameInterval(preRollFrames, preRollFrames + passFrames - 1);
            settings.FrameRatePlayback = FrameRatePlayback.Constant;
            settings.FrameRate = fps;
            settings.CapFrameRate = true;

            _controller = new RecorderController(settings);
            _controller.PrepareRecording();
            _controller.StartRecording();

            // Start preRoll seconds of playback before fromDataFrame (wrapping through the loop),
            // so that frame lands on the first recorded one. Playback advances on
            // PresentationClock, i.e. 1/fps per captured frame.
            int span = playback.endFrame - playback.startFrame;
            int back = Mathf.RoundToInt(preRoll / Mathf.Max(0.01f, playback.durationSeconds) * span);
            int from = SessionState.GetInt(Key + "From", playback.startFrame) - playback.startFrame;
            playback.Play();
            playback.Seek(playback.startFrame + (((from - back) % span) + span) % span);

            _sawRecording = false;
            EditorApplication.update += Watch;
            Debug.Log($"[LoopTake] {rt.width}×{rt.height} @ {fps} fps: pre-roll {preRollFrames} frames, " +
                      $"record {passFrames} frames → {movie.OutputFile}.mov");
        }

        // Exit Play once the Recorder has run past its frame interval.
        private static void Watch()
        {
            if (_controller == null) { EditorApplication.update -= Watch; return; }
            bool rec = _controller.IsRecording();
            if (rec) _sawRecording = true;
            if (_sawRecording && !rec)
            {
                EditorApplication.update -= Watch;
                _controller = null;
                Debug.Log("[LoopTake] done.");
                EditorApplication.ExitPlaymode();
            }
        }
    }
}

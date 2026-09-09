using UnityEngine;
using OscJack;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// Arms <c>tools/osc_index_tester.py --wait</c>: tells the idle streamer to start (<c>/stream/start</c>)
    /// when play mode begins and/or when <see cref="SimTimeline"/> plays, and to stop (<c>/stream/stop</c>)
    /// on Stop / disable — so one Play press starts the OSC firing stream and the take together.
    ///
    /// <para><b>Realtime only.</b> The external streamer paces itself by wall clock; under a capture
    /// clock (FigureExporter <c>captureFramerate</c> / Unity Recorder) Unity runs slower than realtime
    /// and the stream drifts ahead of the recording. For recorded takes drive the blob with
    /// <see cref="NeuronFiringPlayback"/> (advances on Unity's own clock) via <see cref="SimTimeline"/>.</para>
    /// </summary>
    public class OscStreamTrigger : MonoBehaviour
    {
        [Header("Target — osc_index_tester.py --wait PORT")]
        public string host = "127.0.0.1";
        public int port = 9101;

        [Header("When")]
        [Tooltip("Send /stream/start as soon as play mode starts (independent of SimTimeline).")]
        public bool startOnPlayMode = false;
        [Tooltip("Send /stream/stop when this component is disabled / play mode exits.")]
        public bool stopOnDisable = true;

        [Header("Range override (optional)")]
        [Tooltip("Send START END FPS with /stream/start so the streamer uses these instead of its own " +
                 "command-line range. Off = no arguments, the streamer keeps its CLI settings.")]
        public bool sendRange = false;
        [Min(0)] public int startFrame = 125000;
        [Min(0)] public int endFrame = 131000;
        [Min(1)] public int fps = 50;

        private OscClient _client;
        private bool _started;
        private OscClient Client => _client ??= new OscClient(host, port);

        void Start()
        {
            if (startOnPlayMode) SendStart();
        }

        void OnDisable()
        {
            if (stopOnDisable && _started) SendStop();
            _client?.Dispose();
            _client = null;
        }

        [Button("Send /stream/start")]
        public void SendStart()
        {
            if (sendRange) Client.Send("/stream/start", startFrame, endFrame, fps);
            else Client.Send("/stream/start");
            _started = true;
            Debug.Log($"[OscStreamTrigger] /stream/start → {host}:{port}" +
                      (sendRange ? $" ({startFrame}..{endFrame} @ {fps} fps)" : " (streamer's CLI settings)"));
        }

        [Button("Send /stream/stop")]
        public void SendStop()
        {
            Client.Send("/stream/stop");
            _started = false;
            Debug.Log($"[OscStreamTrigger] /stream/stop → {host}:{port}");
        }

        [Button("Send /stream/quit")]
        public void SendQuit() => Client.Send("/stream/quit");
    }
}

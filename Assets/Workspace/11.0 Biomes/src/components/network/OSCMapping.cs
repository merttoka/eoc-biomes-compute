using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using OscJack;

namespace Biomes
{
    public class OSCMapping : MonoBehaviour
    {
        private OscServer m_OscServer;
        public int m_Port = 9000;

        // OscJack parses on a background socket thread and invokes callbacks there directly
        // (no main-thread marshalling). Reset()/ResetSimsOnly() call Unity GPU + GameObject
        // APIs, which throw if run off the main thread (and the OscServer loop would then
        // break, killing OSC). Queue those actions and drain them in Update() on the main
        // thread. Param/injector/index callbacks only touch CPU state, so they stay inline.
        private readonly ConcurrentQueue<Action> m_MainThreadActions = new();

        [SerializeField] public SimulationManager m_SimulationManager;
        [SerializeField] public List<SimulationBase> m_Simulations = new List<SimulationBase>();
        [SerializeField] public BiomeInjector m_BiomeInjector;
        [SerializeField] public NeuronFiringSource m_NeuronFiringSource;

        void Start()
        {
            m_OscServer = new OscServer(m_Port);

            // Neuron firing: external frame index (0..frameCount-1) scrubs the blob.
            // GetElementAsInt handles both int ('i') and float ('f') OSC type tags.
            On(
                "/index",
                (string address, OscDataHandle data) => {
                    if (m_NeuronFiringSource == null) return;
                    m_NeuronFiringSource.SetFrame(data.GetElementAsInt(0));
                }
            );

            // Reset commands — marshalled to the main thread (they touch GPU + GameObjects).
            On(
                "/sim_reset",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.Reset());
                }
            );
            On(
                "/sim_resetSimsOnly",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.ResetSimsOnly());
                }
            );
            // Per-type sim resets (respawn one family, others keep running).
            On(
                "/sim_resetPhysarum",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.ResetPhysarum());
                }
            );
            On(
                "/sim_resetBoids",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.ResetBoids());
                }
            );
            On(
                "/sim_resetTermites",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.ResetTermites());
                }
            );

            // Show blackout (organoid-analysis show mode): /sim_off [fadeSeconds] fades the
            // whole composite to black, /sim_on [fadeSeconds] fades it back. No argument uses
            // SimulationManager.outputFadeSeconds.
            On(
                "/sim_off",
                (string address, OscDataHandle data) => {
                    float fade = data.GetElementCount() > 0 ? data.GetElementAsFloat(0) : -1f;   // read now: the handle is only valid in this callback
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.SetOutputOn(false, fade));
                }
            );
            On(
                "/sim_on",
                (string address, OscDataHandle data) => {
                    float fade = data.GetElementCount() > 0 ? data.GetElementAsFloat(0) : -1f;
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.SetOutputOn(true, fade));
                }
            );

            // Agent palettes (SimulationManager.paletteCycle) — main thread: the fade ticks there.
            On(
                "/palette_next",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.NextPalette());
                }
            );
            On(
                "/palette_prev",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.PreviousPalette());
                }
            );
            // /palette <index>: select directly (−1 = Preset, the authored colors).
            On(
                "/palette",
                (string address, OscDataHandle data) => {
                    int index = data.GetElementAsInt(0);   // read now: the handle is only valid in this callback
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.SelectPalette(index));
                }
            );

            // Register param callbacks per sim using ModulatableParams
            // Convention: /<simPrefix>_<paramName>_<index>
            for (int simIdx = 0; simIdx < m_Simulations.Count; simIdx++)
            {
                var sim = m_Simulations[simIdx];
                if (sim == null) continue;

                string prefix = sim.SimName.Substring(0, 1).ToLower();
                int capturedIdx = simIdx;

                foreach (string paramName in sim.ModulatableParams)
                {
                    RegisterVec4Param(prefix, paramName, capturedIdx);
                }
            }

            // Biome injector source drivers: /inject/<name> <value>, /inject/<name>/pos <u> <v>
            RegisterInjectorSources();

            Debug.Log($"[OSC] Server listening on port {m_Port}");
        }

        private void RegisterVec4Param(string prefix, string paramName, int simIdx)
        {
            for (int i = 0; i < 4; i++)
            {
                int paramIndex = i;
                string address = $"/{prefix}_{paramName}_{paramIndex}";
                On(
                    address,
                    (string addr, OscDataHandle data) => {
                        // Live params exist only once the sim has started (timeline-started
                        // sims are null until their cue); drop messages that arrive before.
                        var sim = m_Simulations[simIdx];
                        if (sim == null || sim.LiveParamSet == null) return;
                        sim.SetParameter(paramName, paramIndex, data.GetElementAsFloat(0));
                    }
                );
            }
        }

        // Register OSC drivers for each BiomeInjector source (by source name):
        //   /inject/<name>         <value 0..1>                      → SetValue   (intensity; e.g. CO2/light, arm activity)
        //   /inject/<name>/pos     <u> <v>                           → SetPosition(move emitter; e.g. kinetic-arm pose)
        //   /inject/<name>/shape   <radius> <falloff>                → SetShape   (resize only)
        //   /inject/<name>/stamp   <u> <v> <radius> <falloff> <val>  → SetStamp   (full hit; e.g. audio → Dispersal)
        // Use the BiomeInjector's "Add Example Dispersal Sources" button to create arm1/arm2/arm3 + audio.
        private void RegisterInjectorSources()
        {
            if (m_BiomeInjector == null || m_BiomeInjector.sources == null) return;
            foreach (var src in m_BiomeInjector.sources)
            {
                if (src == null || string.IsNullOrEmpty(src.name)) continue;
                string srcName = src.name;                       // SetValue/SetPosition/SetStamp key
                string baseAddr = BiomeInjector.OscAddressFor(src); // explicit override or /inject/<name>
                if (string.IsNullOrEmpty(baseAddr)) continue;

                On(
                    baseAddr,
                    (string addr, OscDataHandle data) => {
                        m_BiomeInjector.SetValue(srcName, data.GetElementAsFloat(0));
                    }
                );

                On(
                    $"{baseAddr}/pos",
                    (string addr, OscDataHandle data) => {
                        m_BiomeInjector.SetPosition(srcName, data.GetElementAsFloat(0), data.GetElementAsFloat(1));
                    }
                );

                On(
                    $"{baseAddr}/shape",
                    (string addr, OscDataHandle data) => {
                        m_BiomeInjector.SetShape(srcName,
                            data.GetElementAsFloat(0), data.GetElementAsFloat(1));
                    }
                );

                // Full stamp in one message: u v radius falloff value (e.g. audio → sized Dispersal hit).
                On(
                    $"{baseAddr}/stamp",
                    (string addr, OscDataHandle data) => {
                        m_BiomeInjector.SetStamp(srcName,
                            data.GetElementAsFloat(0), data.GetElementAsFloat(1),
                            data.GetElementAsFloat(2), data.GetElementAsFloat(3),
                            data.GetElementAsFloat(4));
                    }
                );
            }
            Debug.Log($"[OSC] Registered {m_BiomeInjector.sources.Count} injector source(s) under /inject/<name> (value · /pos · /shape · /stamp)");
        }

        // OscJack ends its receive thread for good on the first exception a callback throws,
        // which would silence every address for the rest of the session. Contain it here and
        // log on the main thread instead.
        private void On(string address, OscMessageDispatcher.MessageCallback callback)
        {
            m_OscServer.MessageDispatcher.AddCallback(address, (addr, data) =>
            {
                try { callback(addr, data); }
                catch (Exception e) { m_MainThreadActions.Enqueue(() => Debug.LogException(e)); }
            });
        }

        void Update()
        {
            // Drain OSC actions that must run on the main thread (sim resets).
            while (m_MainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        void OnDestroy()
        {
            m_OscServer?.Dispose();
            m_OscServer = null;
        }
    }
}

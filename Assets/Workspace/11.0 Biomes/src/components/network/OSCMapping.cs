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

        // ── media-agent → biome bridge (topology B: Unity listens to /sn/<entity>/… direct) ──
        // Maps each media-agent entity to a sim (behavior/* multipliers) and, optionally, a
        // BiomeInjector source (warmth → Temperature stamp). Kept data-driven so the same build
        // works when a maestra gateway later remaps the prefix/entity addresses.
        [Serializable]
        public class EntityBinding
        {
            [Tooltip("media-agent entity id in the OSC address (/sn/<entityId>/…), e.g. termite, physarum, boid.")]
            public string entityId;
            [Tooltip("Sim that consumes this entity's behavior/* multipliers (termite→TermiteSim, physarum→PhysarumSim, boid→BoidSim).")]
            public SimulationBase sim;
            [Tooltip("BiomeInjector source name that /sn/<entityId>/warmth drives (Temperature stamp). Blank = no warmth route.")]
            public string warmthSourceName;
        }
        [Tooltip("OSC address prefix the media-agent OSCSink emits under (default /sn).")]
        public string m_SnPrefix = "/sn";
        [Tooltip("Per-entity routing for the media-agent bridge: warmth → BiomeInjector Temperature stamp, behavior/* → sim multipliers.")]
        public List<EntityBinding> m_EntityBindings = new List<EntityBinding>();

        void Start()
        {
            m_OscServer = new OscServer(m_Port);

            // Neuron firing: external frame index (0..frameCount-1) scrubs the blob.
            // GetElementAsInt handles both int ('i') and float ('f') OSC type tags.
            m_OscServer.MessageDispatcher.AddCallback(
                "/index",
                (string address, OscDataHandle data) => {
                    if (m_NeuronFiringSource == null) return;
                    m_NeuronFiringSource.SetFrame(data.GetElementAsInt(0));
                }
            );

            // Reset commands — marshalled to the main thread (they touch GPU + GameObjects).
            m_OscServer.MessageDispatcher.AddCallback(
                "/sim_reset",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.Reset());
                }
            );
            m_OscServer.MessageDispatcher.AddCallback(
                "/sim_resetSimsOnly",
                (string address, OscDataHandle data) => {
                    m_MainThreadActions.Enqueue(() => m_SimulationManager.ResetSimsOnly());
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

            // media-agent bridge: /sn/<entity>/warmth + /sn/<entity>/behavior/{speed,trail,sensor,cohesion}
            RegisterMediaAgentBridge();

            // Catch-all debug
            m_OscServer.MessageDispatcher.AddCallback(
                "*",
                (string address, OscDataHandle data) => {
                    string msg = "(" + address + ": ";
                    for (int i = 0; i < data.GetElementCount(); i++)
                    {
                        msg += data.GetElementAsFloat(i).ToString();
                        if (i < data.GetElementCount() - 1) msg += ", ";
                    }
                    msg += ")";
                    Debug.Log($"[OSC] {msg}");
                }
            );

            Debug.Log($"[OSC] Server listening on port {m_Port}");
        }

        private void RegisterVec4Param(string prefix, string paramName, int simIdx)
        {
            for (int i = 0; i < 4; i++)
            {
                int paramIndex = i;
                string address = $"/{prefix}_{paramName}_{paramIndex}";
                m_OscServer.MessageDispatcher.AddCallback(
                    address,
                    (string addr, OscDataHandle data) => {
                        m_Simulations[simIdx].SetParameter(paramName, paramIndex, data.GetElementAsFloat(0));
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

                m_OscServer.MessageDispatcher.AddCallback(
                    baseAddr,
                    (string addr, OscDataHandle data) => {
                        m_BiomeInjector.SetValue(srcName, data.GetElementAsFloat(0));
                    }
                );

                m_OscServer.MessageDispatcher.AddCallback(
                    $"{baseAddr}/pos",
                    (string addr, OscDataHandle data) => {
                        m_BiomeInjector.SetPosition(srcName, data.GetElementAsFloat(0), data.GetElementAsFloat(1));
                    }
                );

                m_OscServer.MessageDispatcher.AddCallback(
                    $"{baseAddr}/shape",
                    (string addr, OscDataHandle data) => {
                        m_BiomeInjector.SetShape(srcName,
                            data.GetElementAsFloat(0), data.GetElementAsFloat(1));
                    }
                );

                // Full stamp in one message: u v radius falloff value (e.g. audio → sized Dispersal hit).
                m_OscServer.MessageDispatcher.AddCallback(
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

        // media-agent → biome bridge (topology B). Per entity binding, subscribe to the SHORT
        // leaf names the media-agent OSCSink emits (media_agent/osc.py): /sn/<entity>/warmth and
        // /sn/<entity>/behavior/{speed,trail,sensor,cohesion}. warmth drives a BiomeInjector
        // Temperature stamp (reusing the thread-safe SetValue path); the four behavior leaves
        // drive non-destructive per-sim global multipliers. All callbacks touch CPU state only and
        // are thread-safe, so they run inline on the socket thread (no main-thread enqueue) — same
        // pattern as the /inject/<name> callbacks above.
        private void RegisterMediaAgentBridge()
        {
            if (m_EntityBindings == null) return;
            string prefix = string.IsNullOrEmpty(m_SnPrefix) ? "/sn" : m_SnPrefix.TrimEnd('/');
            int registered = 0;
            foreach (var b in m_EntityBindings)
            {
                if (b == null || string.IsNullOrEmpty(b.entityId)) continue;
                string entityId = b.entityId;

                // warmth → Temperature stamp via BiomeInjector (thread-safe SetValue).
                if (m_BiomeInjector != null && !string.IsNullOrEmpty(b.warmthSourceName))
                {
                    string warmthSource = b.warmthSourceName;
                    m_OscServer.MessageDispatcher.AddCallback(
                        $"{prefix}/{entityId}/warmth",
                        (string addr, OscDataHandle data) => {
                            m_BiomeInjector.SetValue(warmthSource, data.GetElementAsFloat(0));
                        }
                    );
                }

                // behavior/{speed,trail,sensor,cohesion} → non-destructive per-sim multipliers.
                var sim = b.sim;
                if (sim != null)
                {
                    RegisterBehaviorLeaf(prefix, entityId, "speed",    sim, SimulationBase.BehaviorLeaf.Speed);
                    RegisterBehaviorLeaf(prefix, entityId, "trail",    sim, SimulationBase.BehaviorLeaf.Trail);
                    RegisterBehaviorLeaf(prefix, entityId, "sensor",   sim, SimulationBase.BehaviorLeaf.Sensor);
                    RegisterBehaviorLeaf(prefix, entityId, "cohesion", sim, SimulationBase.BehaviorLeaf.Cohesion);
                }
                registered++;
            }
            Debug.Log($"[OSC] Registered media-agent bridge for {registered} entity binding(s) under {prefix}/<entity>/ (warmth · behavior/{{speed,trail,sensor,cohesion}})");
        }

        // Register one media-agent behavior leaf → sim.SetBehaviorMultiplier. Inline + thread-safe
        // (SetBehaviorMultiplier only writes a volatile float; consumed on the main thread).
        private void RegisterBehaviorLeaf(string prefix, string entityId, string leafName,
                                          SimulationBase sim, SimulationBase.BehaviorLeaf leaf)
        {
            m_OscServer.MessageDispatcher.AddCallback(
                $"{prefix}/{entityId}/behavior/{leafName}",
                (string addr, OscDataHandle data) => {
                    sim.SetBehaviorMultiplier(leaf, data.GetElementAsFloat(0));
                }
            );
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

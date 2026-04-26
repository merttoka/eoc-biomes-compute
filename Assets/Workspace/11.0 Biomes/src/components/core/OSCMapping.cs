using System.Collections.Generic;
using UnityEngine;
using OscJack;

namespace Biomes
{
    public class OSCMapping : MonoBehaviour
    {
        private OscServer m_OscServer;
        public int m_Port = 9000;

        [SerializeField] public SimulationManager m_SimulationManager;
        [SerializeField] public List<SimulationBase> m_Simulations = new List<SimulationBase>();

        void Start()
        {
            m_OscServer = new OscServer(m_Port);

            // Reset commands
            m_OscServer.MessageDispatcher.AddCallback(
                "/sim_reset",
                (string address, OscDataHandle data) => {
                    m_SimulationManager.Reset();
                }
            );
            m_OscServer.MessageDispatcher.AddCallback(
                "/sim_resetSimsOnly",
                (string address, OscDataHandle data) => {
                    m_SimulationManager.ResetSimsOnly();
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

        void OnDestroy()
        {
            m_OscServer?.Dispose();
            m_OscServer = null;
        }
    }
}

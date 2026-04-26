using System.Collections.Generic;
using UnityEngine;
using OscJack;

namespace Metaesthetica
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

            // Reset
            m_OscServer.MessageDispatcher.AddCallback(
                "/sim_reset",
                (string address, OscDataHandle data) => {
                    m_SimulationManager.Reset();
                }
            );
            m_OscServer.MessageDispatcher.AddCallback(
                "/sim_resetAndRandomize",
                (string address, OscDataHandle data) => {
                    m_SimulationManager.ResetAndRandomizePhysarum();
                }
            );

            // Register param callbacks for each sim using string-based dispatch
            // Convention: /<simPrefix>_<paramName>_<index>
            // e.g. /p_moveSpeed_0, /b_separationRange_0
            for (int simIdx = 0; simIdx < m_Simulations.Count; simIdx++)
            {
                var sim = m_Simulations[simIdx];
                if (sim == null) continue;

                string prefix = sim.SimName.Substring(0, 1).ToLower();
                int capturedIdx = simIdx;

                // Physarum params
                if (sim is PhysarumSim)
                {
                    RegisterVec4Param(prefix, "moveSpeed", capturedIdx);
                    RegisterVec4Param(prefix, "senseAngle", capturedIdx);
                    RegisterVec4Param(prefix, "senseDistance", capturedIdx);
                    RegisterVec4Param(prefix, "turnAngle", capturedIdx);
                }
            }

            // Catch-all for debug
            m_OscServer.MessageDispatcher.AddCallback(
                "*",
                (string address, OscDataHandle data) => {
                    string debugString = "(" + address + ": ";
                    for (int i = 0; i < data.GetElementCount(); i++)
                    {
                        debugString += data.GetElementAsFloat(i).ToString();
                        if (i < data.GetElementCount() - 1) debugString += ", ";
                    }
                    debugString += ")";
                    Debug.Log(debugString);
                }
            );
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

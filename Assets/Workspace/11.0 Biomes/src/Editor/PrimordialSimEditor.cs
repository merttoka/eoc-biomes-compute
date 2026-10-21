using System.Text;
using EasyButtons;
using UnityEditor;
using UnityEngine;

namespace Biomes
{
    /// <summary>
    /// PrimordialSim's inspector plus a population readout: the particle count is derived (density ×
    /// world area) and split by species share, and whether a species can grow cells depends on the
    /// density it perceives — its own plus the others weighted by otherTypeWeight. Edit mode shows
    /// what the next Reset builds under the parent SimulationManager; Play shows the running sim.
    /// </summary>
    [CustomEditor(typeof(PrimordialSim))]
    public class PrimordialSimEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            var sim = (PrimordialSim)target;
            string report = Report(sim, out bool warn);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(report, warn ? MessageType.Warning : MessageType.Info);
            this.DrawEasyButtons();
            if (Application.isPlaying) Repaint();   // follows live param edits
        }

        private static string Report(PrimordialSim sim, out bool warn)
        {
            warn = false;
            bool live = Application.isPlaying && sim.agentParams != null && sim.GetAgentCount() > 0;
            var p = live ? sim.agentParams : sim.paramsSO;
            if (p == null || p.types.Count == 0) return "Assign a PrimordialParams preset.";

            int rezX, rezY, count;
            float unit;
            if (live)
            {
                rezX = sim.rezX; rezY = sim.rezY; unit = sim.AllocatedUnitPx; count = sim.GetAgentCount();
            }
            else
            {
                var m = sim.GetComponentInParent<SimulationManager>();
                if (m == null) return "Not under a SimulationManager: the population is set on Reset.";
                rezX = Mathf.Max(8, Mathf.RoundToInt(m.rezX * m.simResolutionScale));
                rezY = Mathf.Max(8, Mathf.RoundToInt(m.rezY * m.simResolutionScale));
                unit = sim.spaceUnitPx * (m.scaleSpatialToResolution && m.referenceHeight > 0f ? rezY / m.referenceHeight : 1f);
                count = PrimordialPopulation.Count(sim.density, PrimordialPopulation.WorldSu(rezX, rezY, unit));
            }

            var world = PrimordialPopulation.WorldSu(rezX, rezY, unit);
            float area = world.x * world.y;
            var ends = PrimordialSim.TypeEnds(count, p);
            var own = new float[ends.Length];
            for (int t = 0; t < ends.Length; t++)
                own[t] = (ends[t] - (t > 0 ? ends[t - 1] : 0)) / area;

            var sb = new StringBuilder();
            sb.Append($"{(live ? "Running" : "Next Reset")}: {count:N0} particles · world {world.x:F0}×{world.y:F0} su " +
                      $"at {unit:F1} px/su ({rezX}×{rezY})");
            for (int t = 0; t < ends.Length; t++)
            {
                int n = ends[t] - (t > 0 ? ends[t - 1] : 0);
                float felt = PrimordialPopulation.FeltDensity(t, own, p.types[t].otherTypeWeight);
                sb.Append($"\ntype {t}: {n:N0} · own {own[t]:F3}/su² · feels {felt:F3}");
                if (felt < PrimordialPopulation.SurviveMin)
                {
                    warn = true;
                    sb.Append("  ⚠ too sparse: no cells — raise density, share or otherTypeWeight");
                }
                else if (felt < PrimordialPopulation.EmergeMin && sim.spawnMode != SpawnMode.NeuronPositions)
                {
                    warn = true;
                    sb.Append("  ⚠ cells survive here but won't nucleate from scatter — use Neuron Positions spawn, or raise it");
                }
                else if (felt < PrimordialPopulation.EmergeMin)
                {
                    sb.Append("  · sparse: lives on the neuron-seeded spores");
                }
                else if (felt > PrimordialPopulation.TissueAbove)
                {
                    sb.Append("  · dense: cells crowd into tissue");
                }
            }
            if (ends.Length > 1)
                sb.Append($"\nCells emerge from ~{PrimordialPopulation.EmergeMin:F2} felt (paper: 0.08). otherTypeWeight 1 = " +
                          "one physics in many colours; 0 = separate species, each needs that density alone.");
            return sb.ToString();
        }
    }
}

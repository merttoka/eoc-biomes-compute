using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Biomes
{
    /// <summary>
    /// Builds the 11.4 PPS show workspace (ADR-0009): <c>Scene_PPS.unity</c>, copied from the 11.3
    /// rig, keeps Physarum, replaces Boids + Termites with a PrimordialSim, and points every reference
    /// (manager, MFT, OSC, injector, interpolator group, output quads) at the new pair. Assets are
    /// scene-local copies so the show tunes its own. Find-or-create throughout: re-running re-asserts
    /// the wiring but never overwrites an asset that already exists, so tuning survives.
    /// </summary>
    public static class PrimordialSceneSetup
    {
        private const string SourceDir = "Assets/Workspace/11.3 Brave New Work Scene";
        private const string SourceScene = SourceDir + "/Scene_BraveNewWork.unity";
        private const string Root = "Assets/Workspace/11.4 PPS Scene";
        private const string AssetsDir = Root + "/assets";
        private const string MaterialsDir = Root + "/materials";
        private const string ScenePath = Root + "/Scene_PPS.unity";
        private const string ComputePath = "Assets/Workspace/11.0 Biomes/src/computes/PrimordialSim.compute";

        [MenuItem("Biomes/Setup 11.4 PPS Scene")]
        public static void Run()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[PrimordialSceneSetup] Stop Play mode first.");
                return;
            }

            EnsureFolder(Root);
            EnsureFolder(AssetsDir);
            EnsureFolder(MaterialsDir);

            var physParams = CopyOnce<PhysarumParams>(SourceDir + "/assets/PhysarumParams.asset", AssetsDir + "/PhysarumParams.asset");
            var physUmwelt = CopyOnce<UmweltMapping>(SourceDir + "/assets/UmweltPhysarum_Alt.asset", AssetsDir + "/UmweltPhysarum_PPS.asset");
            var fieldConfig = CopyOnce<BiomeFieldConfig>(SourceDir + "/assets/BiomeFieldConfig_Homeostatic.asset", AssetsDir + "/BiomeFieldConfig_PPS.asset");
            var physMat = CopyOnce<Material>(SourceDir + "/materials/m_physarum.mat", MaterialsDir + "/m_physarum.mat");
            var ppsMat = CopyOnce<Material>(SourceDir + "/materials/m_physarum.mat", MaterialsDir + "/m_primordial.mat");
            var compositeMat = CopyOnce<Material>(SourceDir + "/materials/m_composite.mat", MaterialsDir + "/m_composite.mat");
            var ppsParams = CreateOnce<PrimordialParams>(AssetsDir + "/PrimordialParams.asset", _ => { });
            var ppsUmwelt = CreateOnce<UmweltMapping>(AssetsDir + "/UmweltPrimordial.asset", DefaultPrimordialUmwelt);
            var ppsCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);

            if (!File.Exists(ScenePath) && !AssetDatabase.CopyAsset(SourceScene, ScenePath))
            {
                Debug.LogError($"[PrimordialSceneSetup] Could not copy {SourceScene} → {ScenePath}");
                return;
            }
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var manager = UnityEngine.Object.FindFirstObjectByType<SimulationManager>(FindObjectsInactive.Include);
            if (manager == null)
            {
                Debug.LogError("[PrimordialSceneSetup] No SimulationManager in " + ScenePath);
                return;
            }

            // Sims: keep Physarum, drop the others, find-or-create Primordial.
            var physarum = manager.GetComponentInChildren<PhysarumSim>(true);
            foreach (var sim in manager.GetComponentsInChildren<SimulationBase>(true))
                if (sim is BoidSim || sim is TermiteSim)
                    UnityEngine.Object.DestroyImmediate(sim.gameObject);

            var pps = manager.GetComponentInChildren<PrimordialSim>(true);
            if (pps == null)
            {
                var go = new GameObject("PrimordialSim");
                go.transform.SetParent(manager.transform, false);
                pps = go.AddComponent<PrimordialSim>();
                pps.compositeWeight = 1f;
                pps.renderPersistence = 0.85f;
                pps.fadeOutSeconds = 10f;
                pps.dispersalSpeedMode = SimulationBase.DispersalSpeedMode.Multiplier;
                pps.dispersalSpeedMult = 3f;
                go.AddComponent<ParameterInterpolator>();
            }
            pps.cs = ppsCompute;
            pps.outputMat = ppsMat;
            pps.paramsSO = ppsParams;
            pps.umwelt = ppsUmwelt;
            pps.agentParams = null;   // runtime slot: Play clones paramsSO into it

            if (physarum != null)
            {
                physarum.paramsSO = physParams;
                physarum.umwelt = physUmwelt;
                physarum.outputMat = physMat;
            }

            var sims = new List<SimulationBase>();
            if (physarum != null) sims.Add(physarum);
            sims.Add(pps);
            manager.simulations = sims;
            manager.compositeOutMat = compositeMat;
            manager.rezX = 1920;
            manager.rezY = 1080;
            if (manager.biome != null)
            {
                manager.biome.fieldConfig = fieldConfig;
                manager.biome.biomeRezX = 480;
                manager.biome.biomeRezY = 270;
                EditorUtility.SetDirty(manager.biome);
            }
            EditorUtility.SetDirty(manager);

            // Interpolators: index-paired with the manager's list.
            var interpolators = new List<ParameterInterpolator>();
            for (int i = 0; i < sims.Count; i++)
            {
                var pi = sims[i].GetComponent<ParameterInterpolator>();
                if (pi == null) continue;
                pi.simManager = manager;
                pi.simIndex = i;
                EditorUtility.SetDirty(pi);
                interpolators.Add(pi);
            }
            var group = manager.GetComponent<ParameterInterpolatorGroup>();
            if (group != null)
            {
                group.interpolators = interpolators;
                EditorUtility.SetDirty(group);
            }

            // Control surfaces keep their own sim lists (private serialized fields).
            foreach (var mft in UnityEngine.Object.FindObjectsByType<MidiFighterTwister>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                SetObjectList(mft, "m_Simulations", sims);
            foreach (var osc in UnityEngine.Object.FindObjectsByType<OSCMapping>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                SetObjectList(osc, "m_Simulations", sims);

            // Firing pulses erupt at the fixed neuron positions — where the creatures are born —
            // instead of at the (removed) termites.
            var injector = manager.injector;
            if (injector != null)
            {
                injector.firingDispersalSource = BiomeInjector.FiringDispersalSource.NeuronPositions;
                injector.firingAgentSim = null;
                EditorUtility.SetDirty(injector);
            }

            // Output quads under the composite: physarum keeps its own, boids → primordial, termite goes.
            if (manager.compositeOutputQuad != null)
            {
                var quadRoot = manager.compositeOutputQuad;
                var compositeRenderer = quadRoot.GetComponent<MeshRenderer>();
                if (compositeRenderer != null) compositeRenderer.sharedMaterial = compositeMat;
                foreach (Transform child in ChildrenSnapshot(quadRoot))
                {
                    var r = child.GetComponent<MeshRenderer>();
                    switch (child.name)
                    {
                        case "physarum": if (r != null) r.sharedMaterial = physMat; break;
                        case "boids":
                        case "primordial":
                            child.name = "primordial";
                            if (r != null) r.sharedMaterial = ppsMat;
                            break;
                        case "termite": UnityEngine.Object.DestroyImmediate(child.gameObject); break;
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PrimordialSceneSetup] {ScenePath}: {string.Join(" + ", sims.ConvertAll(s => s.SimName))}");
        }

        // The ecosystem side of the 11.4 rig (spec: "The ecosystem loop"). PPS deposits Nutrient —
        // its free particles are the paper's nutrients, and Physarum already hunts Nutrient — and
        // feels Physarum's scent (Pheromone0) as agitation (SpeedBoost → turn noise), so thick veins
        // dissolve the cells they reach. Dispersal pulses scatter and agitate. No SpeedPenalty read:
        // constant speed is load-bearing for the life cycle.
        private static void DefaultPrimordialUmwelt(UmweltMapping u)
        {
            u.reads = new List<UmweltReadEntry>
            {
                new() { channel = BiomeChannel.Pheromone0, weight = 0.6f, effect = UmweltEffect.SpeedBoost },
                new() { channel = BiomeChannel.Pheromone0, weight = -0.5f, effect = UmweltEffect.Chemotaxis },
                new() { channel = BiomeChannel.Dispersal, weight = -2f, effect = UmweltEffect.Chemotaxis },
                new() { channel = BiomeChannel.Dispersal, weight = 1f, effect = UmweltEffect.SpeedBoost },
            };
            u.writes = new List<UmweltWriteEntry>
            {
                new() { channel = BiomeChannel.Nutrient, amount = 0.02f },
            };
            u.preferredPermeabilityMin = 0f;
            u.preferredPermeabilityMax = 1f;
            u.metabolicHeat = 0f;
            u.oxygenConsumption = 0f;
        }

        private static void SetObjectList(UnityEngine.Object target, string field, List<SimulationBase> sims)
        {
            var so = new SerializedObject(target);
            var list = so.FindProperty(field);
            if (list == null || !list.isArray) return;
            list.arraySize = sims.Count;
            for (int i = 0; i < sims.Count; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = sims[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // A snapshot, because the quad loop destroys children while iterating.
        private static List<Transform> ChildrenSnapshot(Transform t)
        {
            var children = new List<Transform>();
            foreach (Transform c in t) children.Add(c);
            return children;
        }

        private static T CopyOnce<T>(string from, string to) where T : UnityEngine.Object
        {
            if (AssetDatabase.LoadAssetAtPath<T>(to) == null && !AssetDatabase.CopyAsset(from, to))
                throw new InvalidOperationException($"[PrimordialSceneSetup] Could not copy {from} → {to}");
            return AssetDatabase.LoadAssetAtPath<T>(to);
        }

        private static T CreateOnce<T>(string path, Action<T> init) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;
            var asset = ScriptableObject.CreateInstance<T>();
            init(asset);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}

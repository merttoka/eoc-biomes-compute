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
    /// rig with its sims and agent counts (Physarum 1 M, Boids 20 k, Termites 131), adds a
    /// PrimordialSim as the fourth, and points every reference (manager, MFT, OSC, interpolator
    /// group, output quads) at the four. Assets are scene-local copies so the show tunes its own.
    /// Find-or-create throughout: re-running never overwrites an asset that already exists, so
    /// params/umwelt tuning survives — but it re-asserts the scene objects' wiring AND the settings
    /// below (resolutions, neuron layout), so tune those in the scene after the last run. Delete
    /// Scene_PPS.unity to re-copy the 11.3 rig.
    /// </summary>
    public static class PrimordialSceneSetup
    {
        private const string SourceDir = "Assets/Workspace/11.3 Brave New Work Scene";
        private const string SourceScene = SourceDir + "/Scene_BraveNewWork.unity";
        private const string Root = "Assets/Workspace/11.4 PPS Scene";
        private const string AssetsDir = Root + "/assets";
        private const string MaterialsDir = Root + "/materials";
        private const string PalettesDir = AssetsDir + "/Palettes";
        private const string ScenePath = Root + "/Scene_PPS.unity";
        private const string ComputePath = "Assets/Workspace/11.0 Biomes/src/computes/PrimordialSim.compute";

        /// <summary>Rewrites the 11.4 palette assets from <see cref="Schemes"/> (palettes only: no scene,
        /// params or umwelt is touched), for when the table changes after the scene was built.</summary>
        [MenuItem("Biomes/Refresh 11.4 PPS Palettes")]
        public static void RefreshPalettes()
        {
            foreach (var scheme in Schemes)
            {
                var pal = AssetDatabase.LoadAssetAtPath<AgentColorPalette>($"{PalettesDir}/PPS {scheme.name}.asset");
                if (pal == null) continue;
                scheme.Fill(pal);
                EditorUtility.SetDirty(pal);
            }
            AssetDatabase.SaveAssets();
        }

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
            EnsureFolder(PalettesDir);

            // Colour schemes for the four sims. The first is the authored look, applied to each preset
            // when it is made; all of them go in the live cycle (Next/Previous Palette, MFT, OSC).
            var palettes = new List<AgentColorPalette>();
            foreach (var scheme in Schemes)
                palettes.Add(CreateOnce<AgentColorPalette>($"{PalettesDir}/PPS {scheme.name}.asset", scheme.Fill));
            var palette = palettes[0];
            var physParams = CreateOnce<PhysarumParams>(AssetsDir + "/PhysarumParams.asset",
                p => { HuntingPhysarum(p); palette.ApplyTo(p); });
            var physUmwelt = CopyOnce<UmweltMapping>(SourceDir + "/assets/UmweltPhysarum_Alt.asset", AssetsDir + "/UmweltPhysarum_PPS.asset", QuietScent);
            var boidParams = CopyOnce<BoidParams>(SourceDir + "/assets/BoidParams.asset", AssetsDir + "/BoidParams.asset", palette.ApplyTo);
            var boidUmwelt = CopyOnce<UmweltMapping>(SourceDir + "/assets/UmweltBoid_Alt.asset", AssetsDir + "/UmweltBoid_PPS.asset");
            var termiteParams = CopyOnce<TermiteParams>(SourceDir + "/assets/TermiteParams.asset", AssetsDir + "/TermiteParams.asset", palette.ApplyTo);
            var termiteUmwelt = CopyOnce<UmweltMapping>(SourceDir + "/assets/UmweltTermite_Alt.asset", AssetsDir + "/UmweltTermite_PPS.asset");
            var fieldConfig = CopyOnce<BiomeFieldConfig>(SourceDir + "/assets/BiomeFieldConfig_Homeostatic.asset", AssetsDir + "/BiomeFieldConfig_PPS.asset");
            var physMat = CopyOnce<Material>(SourceDir + "/materials/m_physarum.mat", MaterialsDir + "/m_physarum.mat");
            var boidMat = CopyOnce<Material>(SourceDir + "/materials/m_boid.mat", MaterialsDir + "/m_boid.mat");
            var termiteMat = CopyOnce<Material>(SourceDir + "/materials/m_termite.mat", MaterialsDir + "/m_termite.mat");
            var ppsMat = CopyOnce<Material>(SourceDir + "/materials/m_physarum.mat", MaterialsDir + "/m_primordial.mat");
            var compositeMat = CopyOnce<Material>(SourceDir + "/materials/m_composite.mat", MaterialsDir + "/m_composite.mat");
            var ppsParams = CreateOnce<PrimordialParams>(AssetsDir + "/PrimordialParams.asset", p =>
            {
                palette.ApplyTo(p);   // indigo soup specks...
                var t = p.types[0];
                t.densityHueShift = 0.27f;   // ...through violet to rose membranes,
                t.densityGlow = 0.75f;       // pale glowing cores (SIGGRAPH's pink-white glints)
                t.sparseDim = 0.8f;
                t.fieldDrift = 1.5f;         // dispersal pulses push cells outward (a transient field)
            });
            var ppsUmwelt = CreateOnce<UmweltMapping>(AssetsDir + "/UmweltPrimordial.asset", DefaultPrimordialUmwelt);
            var ppsCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);

            // Opening Scene_PPS replaces whatever is open: let the user save it first (or cancel).
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
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

            // Sims: the 11.3 three keep their objects and counts; Primordial is found or created.
            var physarum = manager.GetComponentInChildren<PhysarumSim>(true);
            var boids = manager.GetComponentInChildren<BoidSim>(true);
            var termites = manager.GetComponentInChildren<TermiteSim>(true);

            var pps = manager.GetComponentInChildren<PrimordialSim>(true);
            if (pps == null)
            {
                var go = new GameObject("PrimordialSim");
                go.transform.SetParent(manager.transform, false);
                pps = go.AddComponent<PrimordialSim>();
                pps.compositeWeight = 1f;
                pps.renderPersistence = 0.75f;
                pps.fadeOutSeconds = 10f;
                // Speed is load-bearing for the life cycle: a pulse should agitate (turn noise), not
                // multiply v the way it flings the other sims.
                pps.dispersalSpeedMode = SimulationBase.DispersalSpeedMode.Multiplier;
                pps.dispersalSpeedMult = 0.5f;
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
                EditorUtility.SetDirty(physarum);
            }
            if (boids != null)
            {
                boids.paramsSO = boidParams;
                boids.umwelt = boidUmwelt;
                boids.outputMat = boidMat;
                EditorUtility.SetDirty(boids);
            }
            if (termites != null)
            {
                termites.paramsSO = termiteParams;
                termites.umwelt = termiteUmwelt;
                termites.outputMat = termiteMat;
                EditorUtility.SetDirty(termites);
            }
            EditorUtility.SetDirty(pps);

            // The 11.3 neuron layout fills a 4800-wide strip; spread it over the 16:9 canvas. And play
            // the firing blob locally (looping the Shanghai cut) so neurons fire — dispersal pulses,
            // faster creatures — without TouchDesigner sending /index. Untick playOnStart when TD drives it.
            var firing = manager.GetComponent<NeuronFiringSource>();
            if (firing != null)
            {
                var so = new SerializedObject(firing);
                so.FindProperty("spawnScale").vector2Value = new Vector2(0.6f, 0.75f);
                so.ApplyModifiedPropertiesWithoutUndo();
                var playback = manager.GetComponent<NeuronFiringPlayback>();
                if (playback == null)
                {
                    playback = manager.gameObject.AddComponent<NeuronFiringPlayback>();
                    playback.loop = true;
                    playback.playOnStart = true;
                }
                playback.source = firing;
                EditorUtility.SetDirty(playback);
            }

            // 11.3's order first, so MFT columns, OSC indices and piano keys stay where they were.
            var sims = new List<SimulationBase>();
            foreach (var sim in new SimulationBase[] { physarum, boids, termites, pps })
                if (sim != null) sims.Add(sim);
            manager.simulations = sims;
            manager.compositeOutMat = compositeMat;
            manager.rezX = 1920;
            manager.rezY = 1080;
            manager.moundColor = palette.moundColor;   // the authored scheme's walls
            manager.moundOverlayStrength = 0.45f;
            manager.paletteCycle.palettes = palettes;
            manager.paletteCycle.fadeSeconds = 4f;
            if (physarum != null) physarum.compositeWeight = 0.7f;   // 1 M at 1080p: a network, not a glare
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

            // Per-sim preview quads beside the composite: 11.3's column of three, plus primordial
            // (a copy of the termite quad), re-spaced to fit four.
            if (manager.compositeOutputQuad != null)
            {
                var quadRoot = manager.compositeOutputQuad;
                var compositeRenderer = quadRoot.GetComponent<MeshRenderer>();
                if (compositeRenderer != null) compositeRenderer.sharedMaterial = compositeMat;
                var termiteQuad = quadRoot.Find("termite");
                if (quadRoot.Find("primordial") == null && termiteQuad != null)
                    UnityEngine.Object.Instantiate(termiteQuad.gameObject, quadRoot).name = "primordial";
                var previews = new (string name, Material mat)[]
                    { ("physarum", physMat), ("boids", boidMat), ("termite", termiteMat), ("primordial", ppsMat) };
                for (int i = 0; i < previews.Length; i++)
                {
                    var quad = quadRoot.Find(previews[i].name);
                    if (quad == null) continue;
                    var r = quad.GetComponent<MeshRenderer>();
                    if (r != null) r.sharedMaterial = previews[i].mat;
                    quad.localPosition = new Vector3(0.7f, 0.375f - 0.25f * i, quad.localPosition.z);
                    quad.localScale = new Vector3(0.24f, 0.24f, 1f);
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PrimordialSceneSetup] {ScenePath}: {string.Join(" + ", sims.ConvertAll(s => s.SimName))}");
        }

        // The ecosystem side of the 11.4 rig (spec: "The ecosystem loop"). PPS deposits Nutrient —
        // its free particles are the paper's nutrients, and Physarum already hunts Nutrient — and
        // feels Physarum's scent (Pheromone0) as agitation (SpeedBoost → turn noise), so where veins
        // thicken they dissolve the cells they reach. Dispersal pulses agitate and (drift) push. No
        // SpeedPenalty read: constant speed is load-bearing for the life cycle. No chemotaxis on the
        // standing scent: drift up a field with maxima collapses the soup onto them. Habitat is open
        // ground (the boids' band): inside a termite wall the habitat gate slows particles (speed is
        // what holds a cell together, so it dissolves) and its avoidance drifts them back out.
        private static void DefaultPrimordialUmwelt(UmweltMapping u)
        {
            u.reads = new List<UmweltReadEntry>
            {
                new() { channel = BiomeChannel.Pheromone0, weight = 2f, effect = UmweltEffect.SpeedBoost },
                new() { channel = BiomeChannel.Dispersal, weight = -2f, effect = UmweltEffect.Chemotaxis },
                new() { channel = BiomeChannel.Dispersal, weight = 1f, effect = UmweltEffect.SpeedBoost },
            };
            u.writes = new List<UmweltWriteEntry>
            {
                new() { channel = BiomeChannel.Nutrient, amount = 0.02f },
            };
            u.preferredPermeabilityMin = 0.6f;
            u.preferredPermeabilityMax = 1f;
            u.metabolicHeat = 0f;
            u.oxygenConsumption = 0f;
        }

        // Two hunters for the creatures, authored at the 2160 reference: a vein builder (rotation ≈
        // 2× sensor angle → a reticulated network, Jones 2010) and a finer mesh. Low mutual eating,
        // so they interleave instead of banding into parallel lanes. Colours come from the palette.
        private static void HuntingPhysarum(PhysarumParams p)
        {
            p.typeCount = 2;
            p.SyncTypesList();
            Set(p.types[0], speed: 1.2f, senseAngle: 22.5f, turnAngle: 45f, senseDistance: 18f, deposit: 0.03f);
            Set(p.types[1], speed: 1.0f, senseAngle: 30f, turnAngle: 65f, senseDistance: 26f, deposit: 0.02f);

            static void Set(PhysarumAgentType t, float speed, float senseAngle, float turnAngle, float senseDistance,
                            float deposit)
            {
                t.moveSpeed = speed; t.senseAngle = senseAngle; t.turnAngle = turnAngle; t.senseDistance = senseDistance;
                t.depositAmount = deposit; t.eatAmount = 0.005f; t.diffuseRate = 0.93f;
                t.firingSpeedMul = 2f; t.firingDepositAmount = 0.5f;
            }
        }

        // Four-sim colour schemes, authored as kernel hue/saturation/brightness: Physarum veins + mesh,
        // the four boid types, the termites, up to four creature species (bases; PaletteAssign gives
        // species i the i-th, wrapping), and the wall colour. Kept low in saturation with one accent
        // each, so the shows read as one body of work. The creatures' hue ramps +0.27
        // (densityHueShift) from their base, so a cell lands at base + 0.13…0.27: each base is picked
        // for where it lands. The first is the SIGGRAPH show's scheme, softened.
        private readonly struct Scheme
        {
            public readonly string name, notes;
            public readonly Vector3[] physarum, boids, primordial;
            public readonly Vector3 termite;
            public readonly Color walls;

            public Scheme(string name, string notes, Vector3[] physarum, Vector3[] boids, Vector3 termite,
                          Vector3[] primordial, Color walls)
            {
                this.name = name; this.notes = notes; this.physarum = physarum; this.boids = boids;
                this.termite = termite; this.primordial = primordial; this.walls = walls;
            }

            public void Fill(AgentColorPalette pal)
            {
                pal.notes = notes;
                pal.swatches = new List<PaletteSwatch>();
                foreach (var c in physarum) Add(pal, c, AgentFamily.Physarum);
                foreach (var c in boids) Add(pal, c, AgentFamily.Boid);
                Add(pal, termite, AgentFamily.Termite);
                foreach (var c in primordial) Add(pal, c, AgentFamily.Primordial);
                pal.tintsMounds = true;
                pal.moundColor = walls;

                static void Add(AgentColorPalette pal, Vector3 hsb, AgentFamily f) =>
                    pal.swatches.Add(new PaletteSwatch(AgentColor.ToDisplay(hsb.x, hsb.y, hsb.z), f));
            }
        }

        private static readonly Scheme[] Schemes =
        {
            new("Siggraph Soft", "SIGGRAPH's scheme, softened: coral-rose veins over a dim crimson mesh, " +
                    "its pale silver-green / periwinkle boids, icy termites on silver-blue walls, indigo " +
                    "soup ramping to pale rose creatures.",
                new[] { new Vector3(0f, .55f, .72f), new Vector3(.985f, .6f, .42f) },
                new[] { new Vector3(.28f, .15f, .7f), new Vector3(.65f, .47f, .7f), new Vector3(.28f, .16f, .7f), new Vector3(.21f, .41f, .7f) },
                new Vector3(.57f, .35f, .85f),
                new[] { new Vector3(.72f, .45f, .85f), new Vector3(.58f, .35f, .8f), new Vector3(.88f, .4f, .85f), new Vector3(.45f, .3f, .8f) },
                new Color(.45f, .62f, .72f)),
            new("Abyss", "Deep sea: teal veins over a deep-blue mesh, pale aqua / lilac boids, gold termites " +
                    "on slate walls, teal soup ramping to violet creatures.",
                new[] { new Vector3(.48f, .55f, .65f), new Vector3(.6f, .6f, .42f) },
                new[] { new Vector3(.5f, .15f, .75f), new Vector3(.66f, .35f, .7f), new Vector3(.42f, .25f, .72f), new Vector3(.75f, .2f, .72f) },
                new Vector3(.12f, .3f, .8f),
                new[] { new Vector3(.45f, .45f, .85f), new Vector3(.33f, .4f, .8f), new Vector3(.6f, .4f, .85f), new Vector3(0f, .35f, .85f) },
                new Color(.22f, .3f, .4f)),
            new("Ember Ash", "Embers: amber veins over a dim red mesh, ash-grey boids, bone termites on " +
                    "charcoal walls, rose soup ramping through orange to gold creatures.",
                new[] { new Vector3(.07f, .7f, .72f), new Vector3(.02f, .75f, .42f) },
                new[] { new Vector3(.08f, .08f, .72f), new Vector3(.6f, .07f, .6f), new Vector3(.1f, .12f, .68f), new Vector3(.05f, .2f, .62f) },
                new Vector3(.1f, .1f, .85f),
                new[] { new Vector3(.93f, .5f, .85f), new Vector3(.85f, .45f, .8f), new Vector3(.08f, .1f, .85f), new Vector3(.55f, .3f, .75f) },
                new Color(.36f, .33f, .31f)),
            new("Lichen", "Lichen on stone: sage veins over an olive mesh, pale sage / bone / ochre boids, " +
                    "rust termites on warm stone walls, rose-peach creatures.",
                new[] { new Vector3(.25f, .45f, .55f), new Vector3(.16f, .5f, .38f) },
                new[] { new Vector3(.3f, .12f, .75f), new Vector3(.12f, .1f, .8f), new Vector3(.11f, .45f, .65f), new Vector3(.2f, .25f, .75f) },
                new Vector3(.05f, .45f, .65f),
                new[] { new Vector3(.85f, .55f, .85f), new Vector3(.95f, .45f, .8f), new Vector3(.4f, .3f, .8f), new Vector3(.7f, .35f, .8f) },
                new Color(.52f, .48f, .4f)),
            new("Bone", "Near-monochrome: bone and grey veins, grey boids and termites on pale grey walls; " +
                    "the creatures (rose) are the only colour.",
                new[] { new Vector3(.1f, .07f, .6f), new Vector3(.6f, .05f, .38f) },
                new[] { new Vector3(.6f, .03f, .7f), new Vector3(.1f, .04f, .65f), new Vector3(.6f, .06f, .6f), new Vector3(.1f, .06f, .72f) },
                new Vector3(.58f, .1f, .8f),
                new[] { new Vector3(.82f, .65f, .85f), new Vector3(.5f, .5f, .8f), new Vector3(0f, .55f, .8f), new Vector3(.9f, .08f, .85f) },
                new Color(.55f, .55f, .57f)),
        };

        // 11.3's write rate saturates Pheromone0 (mean 0.38 measured with 250 k agents), so the
        // creatures would be agitated everywhere instead of where the veins are. 0.004 at 250 k held
        // a mean ≈ 0.08; at 11.3's 1 M agents the same flux is 0.001.
        private static void QuietScent(UmweltMapping u)
        {
            foreach (var w in u.writes)
                if (w.channel == BiomeChannel.Pheromone0) w.amount = 0.001f;
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

        // onCopy runs only when the copy is made, so later runs never clobber tuning.
        private static T CopyOnce<T>(string from, string to, Action<T> onCopy = null) where T : UnityEngine.Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(to);
            if (existing != null) return existing;
            if (!AssetDatabase.CopyAsset(from, to))
                throw new InvalidOperationException($"[PrimordialSceneSetup] Could not copy {from} → {to}");
            var copy = AssetDatabase.LoadAssetAtPath<T>(to);
            if (onCopy != null)
            {
                onCopy(copy);
                EditorUtility.SetDirty(copy);
            }
            return copy;
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

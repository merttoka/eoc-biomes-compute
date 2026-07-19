using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>
    /// One-click editor setup for the Temporal Composer demo rig, mirroring
    /// docs/superpowers/2026-07-19-temporal-composer-manual-setup.md against the
    /// CURRENTLY OPEN scene. Every step is find-or-create: safe to re-run against an
    /// already-wired scene without duplicating GameObjects, components, tracks, clips,
    /// or assets. Hard-gated to Scene_SIGGRAPH_2 so the original Scene_SIGGRAPH can
    /// never be touched.
    /// </summary>
    public static class TemporalComposerSceneSetup
    {
        private const string TargetSceneName = "Scene_SIGGRAPH_2";

        private const string SceneAssetsDir = "Assets/Workspace/11.2 SIGGRAPH Scene/assets";
        private const string SceneMaterialsDir = "Assets/Workspace/11.2 SIGGRAPH Scene/materials";
        private const string SnapshotsDir = SceneAssetsDir + "/Snapshots";
        private const string SignalsDir = SceneAssetsDir + "/signals";
        private const string TimelinePath = SceneAssetsDir + "/ShowSequence.playable";
        private const string ComposerOutMatPath = SceneMaterialsDir + "/m_temporal_out.mat";
        private const string SequencerComputePath =
            "Assets/Workspace/11.0 Biomes/src/computes/SequencerComposite.compute";
        private const string CellRigAPresetPath = SnapshotsDir + "/Physarum_20260711_201424.asset";
        private const string ParamMorphSnapshotPath = SnapshotsDir + "/Physarum_20260711_195316.asset";

        private static readonly List<string> s_Log = new();

        /// <summary>
        /// Runs the full Temporal Composer wiring pass against the active scene. Aborts
        /// (dialog + error, no changes) unless the active scene is Scene_SIGGRAPH_2 and
        /// the editor is not in Play mode. Safe to invoke repeatedly, including via
        /// -executeMethod.
        /// </summary>
        [MenuItem("Biomes/Setup Temporal Composer Scene")]
        public static void Run()
        {
            s_Log.Clear();

            if (EditorApplication.isPlaying)
            {
                Abort("Cannot run in Play mode. Stop Play mode and try again.");
                return;
            }

            Scene scene = EditorSceneManager.GetActiveScene();
            if (scene.name != TargetSceneName)
            {
                Abort($"Active scene is '{scene.name}', expected '{TargetSceneName}'. " +
                      "Aborting so the original Scene_SIGGRAPH can never be touched by this command.");
                return;
            }

            SimulationManager mainManager = FindMainManager();
            if (mainManager == null)
            {
                Abort("No SimulationManager with ownsGlobalTiming = true found in the active scene.");
                return;
            }

            CompositeSequencer sequencer = SetupComposer(mainManager);
            ClearMainCompositeOutMat(mainManager);

            Transform simRoot = mainManager.transform;
            while (simRoot.parent != null) simRoot = simRoot.parent;

            BiomeCellRig cellRigA = SetupCellRig("CellRig_A", simRoot.gameObject);
            AssignCellRigAPreset(cellRigA != null ? cellRigA.gameObject : null);

            BiomeCellRig cellRigB = SetupCellRig("CellRig_B", simRoot.gameObject);

            (PlayableDirector director, TimelineAsset timeline) = SetupDirector();

            SetupBiomeCellTracks(timeline, director, sequencer, cellRigA, cellRigB);
            SetupPatchScatterTrack(timeline, director, sequencer);
            SetupParamSnapshotTrack(timeline, director, mainManager);
            SetupRoutingTrack(timeline, director, sequencer);
            SetupSignals(timeline, director, mainManager);

            SetupTextureIO(mainManager, sequencer);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            string summary = "Temporal Composer setup complete.\n\n" + string.Join("\n", s_Log);
            Debug.Log("[TemporalComposerSceneSetup] " + summary);
            EditorUtility.DisplayDialog("Temporal Composer Setup", summary, "OK");
        }

        private static void Abort(string reason)
        {
            Debug.LogError("[TemporalComposerSceneSetup] " + reason);
            EditorUtility.DisplayDialog("Temporal Composer Setup — Aborted", reason, "OK");
        }

        private static void Log(string line)
        {
            s_Log.Add(line);
            Debug.Log("[TemporalComposerSceneSetup] " + line);
        }

        // ── Discovery helpers ──────────────────────────────────────────────

        private static SimulationManager FindMainManager()
        {
            var all = Object.FindObjectsByType<SimulationManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            return all.FirstOrDefault(m => m.ownsGlobalTiming);
        }

        private static GameObject FindRootGO(string goName)
        {
            foreach (var go in EditorSceneManager.GetActiveScene().GetRootGameObjects())
                if (go.name == goName) return go;
            return null;
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = System.IO.Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(folderPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        // ── 1. CompositeSequencer ──────────────────────────────────────────

        private static CompositeSequencer SetupComposer(SimulationManager mainManager)
        {
            GameObject go = FindRootGO("TemporalComposer");
            bool goCreated = go == null;
            if (goCreated)
            {
                go = new GameObject("TemporalComposer");
                Undo.RegisterCreatedObjectUndo(go, "Create TemporalComposer");
            }

            var seq = go.GetComponent<CompositeSequencer>();
            bool compCreated = seq == null;
            if (compCreated) seq = Undo.AddComponent<CompositeSequencer>(go);

            var so = new SerializedObject(seq);
            SetObjectRef(so, "simManager", mainManager, "CompositeSequencer");
            SetObjectRef(so, "sequencerCS",
                AssetDatabase.LoadAssetAtPath<ComputeShader>(SequencerComputePath), "CompositeSequencer");
            SetObjectRef(so, "composerOutMat", GetOrCreateComposerOutMat(), "CompositeSequencer");
            SetFloat(so, "composerResScale", 1f, "CompositeSequencer");
            SetBool(so, "debugOutlines", false, "CompositeSequencer");
            so.ApplyModifiedProperties();

            Log($"TemporalComposer: {(goCreated ? "created" : "found")}; " +
                $"CompositeSequencer: {(compCreated ? "created" : "found")}, wired to main SimulationManager");
            return seq;
        }

        private static Material GetOrCreateComposerOutMat()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(ComposerOutMatPath);
            if (mat != null) return mat;

            Shader shader = Shader.Find("HDRP/Unlit");
            if (shader == null)
            {
                Debug.LogError("[TemporalComposerSceneSetup] Shader 'HDRP/Unlit' not found — " +
                                "cannot create m_temporal_out.mat. Assign composerOutMat manually.");
                return null;
            }

            mat = new Material(shader) { name = "m_temporal_out" };
            EnsureFolder(SceneMaterialsDir);
            AssetDatabase.CreateAsset(mat, ComposerOutMatPath);
            Log($"m_temporal_out.mat: created at {ComposerOutMatPath}");
            return mat;
        }

        private static void ClearMainCompositeOutMat(SimulationManager mainManager)
        {
            var so = new SerializedObject(mainManager);
            SerializedProperty prop = so.FindProperty("compositeOutMat");
            if (prop == null)
            {
                Debug.LogWarning("[TemporalComposerSceneSetup] SimulationManager.compositeOutMat field " +
                                  "not found — skipped clearing (two-writer risk if it's still assigned).");
                return;
            }

            if (prop.objectReferenceValue == null)
            {
                Log("Main SimulationManager.compositeOutMat: already clear");
                return;
            }

            Undo.RecordObject(mainManager, "Clear main compositeOutMat");
            prop.objectReferenceValue = null;
            so.ApplyModifiedProperties();
            Log("Main SimulationManager.compositeOutMat: cleared (CompositeSequencer is now the sole writer)");
        }

        // ── 2. Cell rigs ───────────────────────────────────────────────────

        private static readonly string[] s_ForbiddenComponentTypeNames =
        {
            nameof(ExternalTextureReceiver), nameof(ExternalTextureSender),
            nameof(MidiPianoMixer), nameof(OSCMapping), nameof(MidiFighterTwister), nameof(MIDIMapping),
        };

        private static BiomeCellRig SetupCellRig(string rigName, GameObject simRootTemplate)
        {
            GameObject rigGO = FindRootGO(rigName);
            bool created = rigGO == null;
            if (created)
            {
                rigGO = Object.Instantiate(simRootTemplate);
                rigGO.name = rigName;
                Undo.RegisterCreatedObjectUndo(rigGO, $"Create {rigName}");

                DestroyForbiddenComponents(rigGO, rigName);
                foreach (var cam in rigGO.GetComponentsInChildren<Camera>(true))
                {
                    Undo.RecordObject(cam.gameObject, $"Deactivate camera in {rigName}");
                    cam.gameObject.SetActive(false);
                }
            }

            var nestedManager = rigGO.GetComponentInChildren<SimulationManager>(true);
            if (nestedManager == null)
            {
                Debug.LogError($"[TemporalComposerSceneSetup] {rigName}: no SimulationManager found in " +
                                "duplicated hierarchy — cannot configure rig.");
                return null;
            }

            ConfigureCellRigManager(nestedManager, rigName);

            var rig = rigGO.GetComponent<BiomeCellRig>();
            bool rigCreated = rig == null;
            if (rigCreated) rig = Undo.AddComponent<BiomeCellRig>(rigGO);
            Undo.RecordObject(rig, $"Configure {rigName}");
            rig.manager = nestedManager;
            rig.cellRate = 30f;
            rig.Running = false;
            EditorUtility.SetDirty(rig);

            Log($"{rigName}: {(created ? "created (duplicated main sim hierarchy)" : "found")}; " +
                $"BiomeCellRig: {(rigCreated ? "created" : "found")}, manager wired, cellRate=30, Running=false");
            return rig;
        }

        private static void DestroyForbiddenComponents(GameObject root, string rigName)
        {
            int destroyed = 0;
            destroyed += DestroyAllOfType<ExternalTextureReceiver>(root);
            destroyed += DestroyAllOfType<ExternalTextureSender>(root);
            destroyed += DestroyAllOfType<MidiPianoMixer>(root);
            destroyed += DestroyAllOfType<OSCMapping>(root);
            destroyed += DestroyAllOfType<MidiFighterTwister>(root);
            destroyed += DestroyAllOfType<MIDIMapping>(root);

            if (destroyed > 0)
                Log($"{rigName}: destroyed {destroyed} network/MIDI/OSC component(s) copied by duplication " +
                    $"({string.Join(", ", s_ForbiddenComponentTypeNames)})");
        }

        private static int DestroyAllOfType<T>(GameObject root) where T : Component
        {
            var found = root.GetComponentsInChildren<T>(true);
            foreach (var c in found)
                Undo.DestroyObjectImmediate(c);
            return found.Length;
        }

        private static void ConfigureCellRigManager(SimulationManager mgr, string rigName)
        {
            Undo.RecordObject(mgr, $"Configure {rigName} manager");
            var so = new SerializedObject(mgr);

            SetInt(so, "rezX", 1024, rigName);
            SetInt(so, "rezY", 1024, rigName);
            SetBool(so, "ownsGlobalTiming", false, rigName);
            SetInt(so, "stepsPerTick", 0, rigName);
            SetBool(so, "limitFPS", false, rigName);
            ClearObjectRef(so, "compositeOutMat", rigName);
            ClearObjectRef(so, "compositeOutputQuad", rigName);
            ClearObjectRef(so, "recordingCamera", rigName);
            ClearObjectRef(so, "externalInput", rigName);
            ClearObjectRef(so, "neuronFiring", rigName);
            ClearObjectRef(so, "injector", rigName);

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(mgr);
        }

        private static void AssignCellRigAPreset(GameObject cellRigAGO)
        {
            if (cellRigAGO == null) return;

            var physarum = cellRigAGO.GetComponentInChildren<PhysarumSim>(true);
            if (physarum == null)
            {
                Debug.LogWarning("[TemporalComposerSceneSetup] CellRig_A: no PhysarumSim found in " +
                                  "duplicated hierarchy — preset not assigned.");
                return;
            }

            var preset = AssetDatabase.LoadAssetAtPath<PhysarumParams>(CellRigAPresetPath);
            if (preset == null)
            {
                Debug.LogWarning($"[TemporalComposerSceneSetup] CellRig_A: preset asset not found at " +
                                  $"{CellRigAPresetPath} — paramsSO left as duplicated.");
                return;
            }

            var so = new SerializedObject(physarum);
            SerializedProperty prop = so.FindProperty("paramsSO");
            if (prop == null)
            {
                Debug.LogWarning("[TemporalComposerSceneSetup] CellRig_A: PhysarumSim.paramsSO field not " +
                                  "found — preset not assigned.");
                return;
            }

            bool changed = prop.objectReferenceValue != preset;
            prop.objectReferenceValue = preset;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(physarum);
            Log($"CellRig_A PhysarumSim.paramsSO: {(changed ? "set to" : "already")} {preset.name} " +
                "(manual doc's named example — distinct rig preset)");
        }

        // ── SerializedProperty write helpers (verify-then-log) ──────────────

        private static void SetObjectRef(SerializedObject so, string field, Object value, string ownerLabel)
        {
            SerializedProperty prop = so.FindProperty(field);
            if (prop == null) { WarnMissingField(field, ownerLabel); return; }
            prop.objectReferenceValue = value;
        }

        private static void ClearObjectRef(SerializedObject so, string field, string ownerLabel) =>
            SetObjectRef(so, field, null, ownerLabel);

        private static void SetInt(SerializedObject so, string field, int value, string ownerLabel)
        {
            SerializedProperty prop = so.FindProperty(field);
            if (prop == null) { WarnMissingField(field, ownerLabel); return; }
            prop.intValue = value;
        }

        private static void SetFloat(SerializedObject so, string field, float value, string ownerLabel)
        {
            SerializedProperty prop = so.FindProperty(field);
            if (prop == null) { WarnMissingField(field, ownerLabel); return; }
            prop.floatValue = value;
        }

        private static void SetBool(SerializedObject so, string field, bool value, string ownerLabel)
        {
            SerializedProperty prop = so.FindProperty(field);
            if (prop == null) { WarnMissingField(field, ownerLabel); return; }
            prop.boolValue = value;
        }

        private static void WarnMissingField(string field, string ownerLabel) =>
            Debug.LogWarning($"[TemporalComposerSceneSetup] {ownerLabel}: field '{field}' not found " +
                              "on the component — skipped. Verify the field name hasn't changed.");

        // ── 3. PlayableDirector + ShowSequence ───────────────────────────────

        private static (PlayableDirector, TimelineAsset) SetupDirector()
        {
            GameObject go = FindRootGO("ShowDirector");
            bool goCreated = go == null;
            if (goCreated)
            {
                go = new GameObject("ShowDirector");
                Undo.RegisterCreatedObjectUndo(go, "Create ShowDirector");
            }

            var director = go.GetComponent<PlayableDirector>();
            bool dirCreated = director == null;
            if (dirCreated) director = Undo.AddComponent<PlayableDirector>(go);

            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelinePath);
            bool timelineCreated = timeline == null;
            if (timelineCreated)
            {
                timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                timeline.name = "ShowSequence";
                EnsureFolder(SceneAssetsDir);
                AssetDatabase.CreateAsset(timeline, TimelinePath);
            }

            if (director.playableAsset != timeline)
            {
                Undo.RecordObject(director, "Assign ShowSequence");
                director.playableAsset = timeline;
                EditorUtility.SetDirty(director);
            }

            Log($"ShowDirector: {(goCreated ? "created" : "found")}; PlayableDirector: " +
                $"{(dirCreated ? "created" : "found")}; ShowSequence.playable: " +
                $"{(timelineCreated ? "created" : "found")}");
            return (director, timeline);
        }

        // ── Track / clip helpers ─────────────────────────────────────────

        private static T GetOrCreateTrack<T>(TimelineAsset timeline, string trackName) where T : TrackAsset, new()
        {
            foreach (var t in timeline.GetRootTracks())
            {
                if (t is T match && t.name == trackName)
                {
                    Log($"{trackName}: found ({typeof(T).Name})");
                    return match;
                }
            }

            var track = timeline.CreateTrack<T>(null, trackName);
            Log($"{trackName}: created ({typeof(T).Name})");
            return track;
        }

        private static void BindTrack(PlayableDirector director, TrackAsset track, Object bound)
        {
            if (director.GetGenericBinding(track) == bound)
            {
                Log($"{track.name}: binding already set");
                return;
            }

            Undo.RecordObject(director, $"Bind {track.name}");
            director.SetGenericBinding(track, bound);
            EditorUtility.SetDirty(director);
            Log($"{track.name}: bound to {(bound != null ? bound.ToString() : "null")}");
        }

        private static TimelineClip GetOrCreateClip<T>(TrackAsset track, string clipAssetName)
            where T : ScriptableObject, IPlayableAsset
        {
            foreach (var c in track.GetClips())
            {
                if (c.asset is T && c.asset.name == clipAssetName)
                {
                    Log($"{clipAssetName}: found on {track.name}");
                    return c;
                }
            }

            TimelineClip clip = track.CreateClip<T>();
            ((ScriptableObject)clip.asset).name = clipAssetName;
            clip.displayName = clipAssetName;
            Log($"{clipAssetName}: created on {track.name}");
            return clip;
        }

        /// <summary>Binds a clip's ExposedReference&lt;BiomeCellRig&gt; field to a rig via a
        /// deterministic PropertyName (derived from the clip asset's name), so re-running
        /// the setup reuses the same director binding key instead of leaking a fresh GUID
        /// every pass.</summary>
        private static void BindRigReference(PlayableDirector director, BiomeCellClip clip, BiomeCellRig rig)
        {
            var key = new PropertyName(clip.name + ".rig");
            clip.rig.exposedName = key;
            Undo.RecordObject(director, $"Bind {clip.name} rig reference");
            director.SetReferenceValue(key, rig);
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(director);
        }

        // ── 4. BiomeCellTrack ×2 (rig A gets the overlay + full-frame replace,
        //       rig B gets the overlay — kept on separate tracks so the two rig-A
        //       clips at 15-45s/70-90s and the rig-B clip never share a track) ──

        private static void SetupBiomeCellTracks(TimelineAsset timeline, PlayableDirector director,
            CompositeSequencer sequencer, BiomeCellRig rigA, BiomeCellRig rigB)
        {
            var trackA = GetOrCreateTrack<BiomeCellTrack>(timeline, "BiomeCellTrack A");
            BindTrack(director, trackA, sequencer);

            TimelineClip cellAOverlay = GetOrCreateClip<BiomeCellClip>(trackA, "CellA_Overlay");
            cellAOverlay.start = 15;
            cellAOverlay.duration = 30; // 15-45s
            cellAOverlay.easeInDuration = 3;
            cellAOverlay.easeOutDuration = 3;
            var cellAOverlayAsset = (BiomeCellClip)cellAOverlay.asset;
            cellAOverlayAsset.source = CellSource.Rig;
            cellAOverlayAsset.dstRect = new Rect(0f, 0f, 0.5f, 1f); // left band
            cellAOverlayAsset.mode = CellBlendMode.Overlay;
            cellAOverlayAsset.duckBase = false;
            BindRigReference(director, cellAOverlayAsset, rigA);
            EditorUtility.SetDirty(cellAOverlayAsset);

            TimelineClip cellAReplace = GetOrCreateClip<BiomeCellClip>(trackA, "CellA_ReplaceFull");
            cellAReplace.start = 70;
            cellAReplace.duration = 20; // 70-90s
            cellAReplace.easeInDuration = 3;
            cellAReplace.easeOutDuration = 5; // "then fades back" — no exact value in doc
            var cellAReplaceAsset = (BiomeCellClip)cellAReplace.asset;
            cellAReplaceAsset.source = CellSource.Rig;
            cellAReplaceAsset.dstRect = new Rect(0f, 0f, 1f, 1f); // full-frame
            cellAReplaceAsset.mode = CellBlendMode.Replace;
            cellAReplaceAsset.duckBase = true;
            BindRigReference(director, cellAReplaceAsset, rigA);
            EditorUtility.SetDirty(cellAReplaceAsset);

            var trackB = GetOrCreateTrack<BiomeCellTrack>(timeline, "BiomeCellTrack B");
            BindTrack(director, trackB, sequencer);

            TimelineClip cellBOverlay = GetOrCreateClip<BiomeCellClip>(trackB, "CellB_Overlay");
            cellBOverlay.start = 15;
            cellBOverlay.duration = 30; // 15-45s
            cellBOverlay.easeInDuration = 3;
            cellBOverlay.easeOutDuration = 3;
            var cellBOverlayAsset = (BiomeCellClip)cellBOverlay.asset;
            cellBOverlayAsset.source = CellSource.Rig;
            cellBOverlayAsset.dstRect = new Rect(0.5f, 0f, 0.5f, 1f); // right band
            cellBOverlayAsset.mode = CellBlendMode.Overlay;
            cellBOverlayAsset.duckBase = false;
            BindRigReference(director, cellBOverlayAsset, rigB);
            EditorUtility.SetDirty(cellBOverlayAsset);
        }

        // ── 5. PatchScatterTrack ─────────────────────────────────────────

        private static void SetupPatchScatterTrack(TimelineAsset timeline, PlayableDirector director,
            CompositeSequencer sequencer)
        {
            var track = GetOrCreateTrack<PatchScatterTrack>(timeline, "PatchScatterTrack");
            BindTrack(director, track, sequencer);

            TimelineClip clip = GetOrCreateClip<PatchScatterClip>(track, "PatchScatter_MainToDiffusion");
            clip.start = 40;
            clip.duration = 30; // 40-70s
            var asset = (PatchScatterClip)clip.asset;
            asset.sourceA = CellSource.MainComposite;
            asset.sourceB = CellSource.DiffusionReturn;
            asset.crossfadeCenter = 0.5f;
            EditorUtility.SetDirty(asset);
        }

        // ── 6. ParamSnapshotTrack ────────────────────────────────────────

        private static void SetupParamSnapshotTrack(TimelineAsset timeline, PlayableDirector director,
            SimulationManager mainManager)
        {
            var track = GetOrCreateTrack<ParamSnapshotTrack>(timeline, "ParamSnapshotTrack");
            BindTrack(director, track, mainManager);

            int simIndex = mainManager.simulations.FindIndex(s => s is PhysarumSim);
            if (simIndex < 0)
            {
                Debug.LogWarning("[TemporalComposerSceneSetup] Main manager has no PhysarumSim in " +
                                  "simulations — ParamSnapshotClip.simIndex left at 0.");
                simIndex = 0;
            }

            var snapshot = AssetDatabase.LoadAssetAtPath<ScriptableObject>(ParamMorphSnapshotPath);
            if (snapshot == null)
                Debug.LogWarning($"[TemporalComposerSceneSetup] Snapshot asset not found at " +
                                  $"{ParamMorphSnapshotPath} — ParamSnapshotClip.snapshot left unassigned.");

            TimelineClip clip = GetOrCreateClip<ParamSnapshotClip>(track, "ParamSnapshot_PhysarumMorph");
            clip.start = 0;
            clip.duration = 20; // 0-20s
            clip.easeInDuration = 10; // 10s ease per doc
            var asset = (ParamSnapshotClip)clip.asset;
            asset.snapshot = snapshot;
            asset.simIndex = simIndex;
            EditorUtility.SetDirty(asset);
        }

        // ── 7. RoutingTrack ──────────────────────────────────────────────

        private static void SetupRoutingTrack(TimelineAsset timeline, PlayableDirector director,
            CompositeSequencer sequencer)
        {
            var track = GetOrCreateTrack<RoutingTrack>(timeline, "RoutingTrack");
            BindTrack(director, track, sequencer);

            TimelineClip clip = GetOrCreateClip<RoutingClip>(track, "Routing_DiffusionReturn");
            clip.start = 55;
            clip.duration = 10; // 55-65s
            var asset = (RoutingClip)clip.asset;
            asset.influenceSource = CellSource.DiffusionReturn;
            EditorUtility.SetDirty(asset);
        }

        // ── 8. Reset signals ─────────────────────────────────────────────

        private static readonly (string signalName, string methodLabel)[] s_SignalReactions =
        {
            ("Sig_ResetSims", nameof(SimulationManager.ResetSimsOnly)),
            ("Sig_ResetPhysarum", nameof(SimulationManager.ResetPhysarum)),
            ("Sig_ResetBoids", nameof(SimulationManager.ResetBoids)),
            ("Sig_ResetTermites", nameof(SimulationManager.ResetTermites)),
        };

        private static void SetupSignals(TimelineAsset timeline, PlayableDirector director,
            SimulationManager mainManager)
        {
            var signals = new Dictionary<string, SignalAsset>();
            foreach (var (signalName, _) in s_SignalReactions)
                signals[signalName] = EnsureSignalAsset(signalName);

            var receiver = mainManager.GetComponent<SignalReceiver>();
            bool receiverCreated = receiver == null;
            if (receiverCreated) receiver = Undo.AddComponent<SignalReceiver>(mainManager.gameObject);
            Log($"SignalReceiver: {(receiverCreated ? "created" : "found")} on {mainManager.name}");

            AddReactionIfMissing(receiver, signals["Sig_ResetSims"], mainManager.ResetSimsOnly,
                nameof(SimulationManager.ResetSimsOnly));
            AddReactionIfMissing(receiver, signals["Sig_ResetPhysarum"], mainManager.ResetPhysarum,
                nameof(SimulationManager.ResetPhysarum));
            AddReactionIfMissing(receiver, signals["Sig_ResetBoids"], mainManager.ResetBoids,
                nameof(SimulationManager.ResetBoids));
            AddReactionIfMissing(receiver, signals["Sig_ResetTermites"], mainManager.ResetTermites,
                nameof(SimulationManager.ResetTermites));

            var track = GetOrCreateTrack<SignalTrack>(timeline, "Reset Signals");
            BindTrack(director, track, receiver);

            // Only the 90s demo pass table places an emitter (Sig_ResetTermites @ 60s).
            // The other three signals are wired and ready but left for the user to drop
            // additional SignalEmitters where the show calls for them.
            EnsureSignalEmitter(track, "Sig_ResetTermites Emitter", 60d, signals["Sig_ResetTermites"]);
        }

        private static SignalAsset EnsureSignalAsset(string signalName)
        {
            string path = $"{SignalsDir}/{signalName}.signal";
            var asset = AssetDatabase.LoadAssetAtPath<SignalAsset>(path);
            if (asset != null)
            {
                Log($"{signalName}.signal: found");
                return asset;
            }

            EnsureFolder(SignalsDir);
            asset = ScriptableObject.CreateInstance<SignalAsset>();
            AssetDatabase.CreateAsset(asset, path);
            Log($"{signalName}.signal: created at {path}");
            return asset;
        }

        private static void AddReactionIfMissing(SignalReceiver receiver, SignalAsset asset,
            UnityAction action, string methodLabel)
        {
            if (asset == null)
            {
                Debug.LogWarning($"[TemporalComposerSceneSetup] SignalReceiver: signal asset null, " +
                                  $"skipping reaction for {methodLabel}.");
                return;
            }

            if (receiver.GetRegisteredSignals().Contains(asset))
            {
                Log($"SignalReceiver: {asset.name} -> {methodLabel} already registered");
                return;
            }

            var evt = new UnityEvent();
            UnityEventTools.AddPersistentListener(evt, action);
            receiver.AddReaction(asset, evt);
            EditorUtility.SetDirty(receiver);
            Log($"SignalReceiver: {asset.name} -> {methodLabel} registered");
        }

        private static void EnsureSignalEmitter(SignalTrack track, string markerName, double time,
            SignalAsset asset)
        {
            foreach (var m in track.GetMarkers())
            {
                if (m is SignalEmitter existing && existing.name == markerName)
                {
                    Log($"{markerName}: already present at t={existing.time}");
                    return;
                }
            }

            var emitter = track.CreateMarker<SignalEmitter>(time);
            emitter.name = markerName;
            emitter.asset = asset;
            EditorUtility.SetDirty(track);
            Log($"{markerName}: created at t={time}s (asset={asset?.name})");
        }

        // ── 9. TextureIO: second receiver + sender ────────────────────────

        private static void SetupTextureIO(SimulationManager mainManager, CompositeSequencer sequencer)
        {
            GameObject textureIO = FindRootGO("TextureIO");
            if (textureIO == null)
            {
                Debug.LogWarning("[TemporalComposerSceneSetup] No 'TextureIO' GameObject found in the " +
                                  "scene — skipping receiver/sender wiring. Create it manually per the " +
                                  "manual setup doc §1 step 12.");
                return;
            }

            var receivers = textureIO.GetComponents<ExternalTextureReceiver>();
            var diffusion = receivers.FirstOrDefault(r => r.streamName == "TD_Diffusion");
            bool diffusionCreated = diffusion == null;
            if (diffusionCreated) diffusion = Undo.AddComponent<ExternalTextureReceiver>(textureIO);

            Undo.RecordObject(diffusion, "Configure TD_Diffusion receiver");
            diffusion.streamName = "TD_Diffusion";
            diffusion.enableReceive = true;
            diffusion.selfDrive = true;
            EditorUtility.SetDirty(diffusion);
            Log($"TextureIO: TD_Diffusion ExternalTextureReceiver {(diffusionCreated ? "created" : "found")}, " +
                "enableReceive/selfDrive ON");

            var primary = receivers.FirstOrDefault(r => r != diffusion);
            if (primary == null)
                Debug.LogWarning("[TemporalComposerSceneSetup] TextureIO: no primary ExternalTextureReceiver " +
                                  "found besides TD_Diffusion — CompositeSequencer.inputReceiver left unassigned.");

            var seqSO = new SerializedObject(sequencer);
            SetObjectRef(seqSO, "diffusionReturn", diffusion, "CompositeSequencer");
            SetObjectRef(seqSO, "inputReceiver", primary, "CompositeSequencer");
            seqSO.ApplyModifiedProperties();
            Log("CompositeSequencer: diffusionReturn -> TD_Diffusion, inputReceiver -> primary receiver");

            var sender = textureIO.GetComponent<ExternalTextureSender>();
            if (sender == null)
            {
                Debug.LogWarning("[TemporalComposerSceneSetup] TextureIO: no ExternalTextureSender found — " +
                                  "skipping EoC/Composer sender wiring.");
                return;
            }

            Undo.RecordObject(sender, "Wire ExternalTextureSender");
            sender.sequencer = sequencer;
            var composerStream = sender.streams.FirstOrDefault(s => s.source == SendSource.ComposerOutput);
            bool streamCreated = composerStream == null;
            if (streamCreated)
                sender.streams.Add(new SendStream { source = SendSource.ComposerOutput });
            EditorUtility.SetDirty(sender);
            Log($"TextureIO: ExternalTextureSender.sequencer wired; ComposerOutput stream " +
                $"{(streamCreated ? "added" : "already present")}");
        }
    }
}

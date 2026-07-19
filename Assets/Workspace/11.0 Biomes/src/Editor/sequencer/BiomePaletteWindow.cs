using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace Biomes
{
    /// <summary>Grid of all IParamSet preset/snapshot assets with cached thumbnails.
    /// "Capture" saves a thumb from the live composer; "Insert" drops a ParamSnapshotClip
    /// at the inspected Timeline's playhead. Assets can also be dragged from here.</summary>
    public class BiomePaletteWindow : EditorWindow
    {
        private const int ThumbH = 96;
        private Vector2 _scroll;
        private readonly List<ScriptableObject> _assets = new();
        private int _simIndex;

        /// <summary>Opens (or focuses) the Biome Palette window.</summary>
        [MenuItem("Biomes/Biome Palette")]
        public static void Open() => GetWindow<BiomePaletteWindow>("Biome Palette");

        void OnEnable() => Refresh();

        private void Refresh()
        {
            _assets.Clear();
            foreach (string guid in AssetDatabase.FindAssets("t:ScriptableObject",
                new[] { "Assets/Workspace/11.2 SIGGRAPH Scene/assets",
                        "Assets/Workspace/11.1 CURRENTS Scene/assets" }))
            {
                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (so is IParamSet) _assets.Add(so);
            }
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton)) Refresh();
                GUILayout.FlexibleSpace();
                GUILayout.Label("simIndex");
                _simIndex = EditorGUILayout.IntField(_simIndex, GUILayout.Width(32));
            }

            var seq = FindFirstObjectByType<CompositeSequencer>();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            int cols = Mathf.Max(1, (int)(position.width / (ThumbH * 2f)));
            int col = 0;
            EditorGUILayout.BeginHorizontal();
            foreach (var asset in _assets)
            {
                if (col++ >= cols) { EditorGUILayout.EndHorizontal(); EditorGUILayout.BeginHorizontal(); col = 1; }
                DrawTile(asset, seq);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private void DrawTile(ScriptableObject asset, CompositeSequencer seq)
        {
            using (new EditorGUILayout.VerticalScope(GUI.skin.box, GUILayout.Width(ThumbH * 2f - 12)))
            {
                var thumb = SnapshotThumbnailCache.Get(asset);
                var rect = GUILayoutUtility.GetRect(ThumbH * 2f - 20, ThumbH * 0.6f);
                if (thumb != null) GUI.DrawTexture(rect, thumb, ScaleMode.ScaleAndCrop);
                else EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));

                // Drag support: start a normal object drag from the tile.
                var e = Event.current;
                if (e.type == EventType.MouseDrag && rect.Contains(e.mousePosition))
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[] { asset };
                    DragAndDrop.StartDrag(asset.name);
                    e.Use();
                }

                GUILayout.Label(asset.name, EditorStyles.miniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(seq == null || seq.ComposerOutputTexture == null))
                        if (GUILayout.Button("Capture", EditorStyles.miniButton))
                            SnapshotThumbnailCache.Capture(asset, seq.ComposerOutputTexture);
                    using (new EditorGUI.DisabledScope(TimelineEditor.inspectedDirector == null))
                        if (GUILayout.Button("Insert", EditorStyles.miniButton))
                            InsertClip(asset);
                }
            }
        }

        /// <summary>Finds the scene's global-timing SimulationManager for auto-binding a new
        /// track, not just any SimulationManager — a naive FindFirstObjectByType can pick a
        /// cell rig's nested manager instead of the composite scene's driving one. Prefers the
        /// manager wired to a CompositeSequencer; falls back to scanning for one flagged
        /// ownsGlobalTiming; falls back to any manager if neither is found.</summary>
        private static SimulationManager FindGlobalSimManager()
        {
            var composer = FindFirstObjectByType<CompositeSequencer>();
            if (composer != null && composer.simManager != null) return composer.simManager;

            foreach (var mgr in FindObjectsByType<SimulationManager>(FindObjectsSortMode.None))
                if (mgr.ownsGlobalTiming) return mgr;

            return FindFirstObjectByType<SimulationManager>();
        }

        private void InsertClip(ScriptableObject asset)
        {
            var director = TimelineEditor.inspectedDirector;
            var timeline = director.playableAsset as TimelineAsset;
            if (timeline == null) return;

            ParamSnapshotTrack track = null;
            foreach (var t in timeline.GetOutputTracks())
                if (t is ParamSnapshotTrack pst) { track = pst; break; }
            if (track == null)
            {
                track = timeline.CreateTrack<ParamSnapshotTrack>(null, "Param Snapshots");
                director.SetGenericBinding(track, FindGlobalSimManager());
            }

            var clip = track.CreateClip<ParamSnapshotClip>();
            clip.start = director.time;
            clip.duration = 10;
            clip.displayName = asset.name;
            var payload = (ParamSnapshotClip)clip.asset;
            payload.snapshot = asset;
            payload.simIndex = _simIndex;

            EditorUtility.SetDirty(timeline);
            TimelineEditor.Refresh(RefreshReason.ContentsAddedOrRemoved);
        }
    }
}

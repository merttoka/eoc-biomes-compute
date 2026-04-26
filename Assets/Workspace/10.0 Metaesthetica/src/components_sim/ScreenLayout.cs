using System;
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Metaesthetica
{
    [Serializable]
    public struct ScreenConfig
    {
        public string name;
        [Header("Output Resolution")]
        public int pixelWidth;
        public int pixelHeight;
        [Header("Physical Size (meters)")]
        public float physicalWidth;
        public float physicalHeight;
        [Header("Source Crop (pixels in composite)")]
        [Tooltip("Pixel region to sample from the composite texture (x, y = bottom-left origin)")]
        public RectInt sourcePixels;
    }

    public class ScreenLayout : MonoBehaviour
    {
        [Header("Material")]
        [Tooltip("Shared material for all screens (HDRP Unlit with _UnlitColorMap)")]
        public Material screenMaterial;

        [Header("Composite Resolution")]
        [Tooltip("Width of the composite texture in pixels (should match SimulationManager.rezX)")]
        public int compositeWidth = 8000;
        [Tooltip("Height of the composite texture in pixels (should match SimulationManager.rezY)")]
        public int compositeHeight = 2160;

        [Header("Layout")]
        public float sceneScale = 1f;
        public float wallGap = 0.5f;

        [Header("Camera")]
        public int screenLayer = 8;
        public float cameraDistance = 1f;

        [Header("Screens")]
        public List<ScreenConfig> screens = new List<ScreenConfig>();

        [Header("Render Texture Assets (for Recorder)")]
        public string rtAssetFolder = "Workspace/10.0 Metaesthetica/render_textures";
        public List<RenderTexture> rtAssets = new List<RenderTexture>();

        private List<GameObject> screenQuads = new List<GameObject>();
        private List<Camera> screenCameras = new List<Camera>();

        public IReadOnlyList<Camera> ScreenCameras => screenCameras;

        // ─── Pixel → UV conversion ───

        public Rect PixelsToUV(RectInt px)
        {
            if (compositeWidth <= 0 || compositeHeight <= 0)
                return new Rect(0, 0, 1, 1);
            return new Rect(
                (float)px.x / compositeWidth,
                (float)px.y / compositeHeight,
                (float)px.width / compositeWidth,
                (float)px.height / compositeHeight
            );
        }

        [Button("Sync Composite Size from Material")]
        public void SyncCompositeSize()
        {
            if (screenMaterial == null) return;
            var tex = screenMaterial.GetTexture("_UnlitColorMap");
            if (tex != null)
            {
                compositeWidth = tex.width;
                compositeHeight = tex.height;
                Debug.Log($"[ScreenLayout] Synced composite: {compositeWidth}x{compositeHeight}");
            }
        }

        // ─── Default screen configs ───

        [Button("1. Generate Default Layout")]
        public void GenerateDefaultScreens()
        {
            screens.Clear();
            int cw = compositeWidth, ch = compositeHeight;

            screens.Add(new ScreenConfig {
                name = "Entrance_Landscape",
                pixelWidth = 2304, pixelHeight = 1296,
                physicalWidth = 3.66f, physicalHeight = 2.05f,
                sourcePixels = new RectInt(0, ch / 4, cw / 4, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Entrance_Portrait",
                pixelWidth = 1536, pixelHeight = 2112,
                physicalWidth = 3.84f, physicalHeight = 5.28f,
                sourcePixels = new RectInt(cw / 4, 0, cw / 6, ch)
            });
            screens.Add(new ScreenConfig {
                name = "FrontWall_Portrait",
                pixelWidth = 1080, pixelHeight = 1920,
                physicalWidth = 1.71f, physicalHeight = 3.05f,
                sourcePixels = new RectInt(cw * 5 / 12, 0, cw / 10, ch)
            });
            screens.Add(new ScreenConfig {
                name = "FrontWall_Square",
                pixelWidth = 1536, pixelHeight = 1512,
                physicalWidth = 2.44f, physicalHeight = 2.40f,
                sourcePixels = new RectInt(cw / 2, ch / 8, cw / 6, ch * 3 / 4)
            });
            screens.Add(new ScreenConfig {
                name = "SideWall_Square",
                pixelWidth = 810, pixelHeight = 960,
                physicalWidth = 1.03f, physicalHeight = 1.22f,
                sourcePixels = new RectInt(cw * 2 / 3, ch / 4, cw / 10, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Round_Ceiling",
                pixelWidth = 1920, pixelHeight = 1890,
                physicalWidth = 2.4f, physicalHeight = 2.4f,
                sourcePixels = new RectInt(cw * 3 / 4, ch / 2, cw / 4, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Round_WallL",
                pixelWidth = 1920, pixelHeight = 1890,
                physicalWidth = 2.4f, physicalHeight = 2.4f,
                sourcePixels = new RectInt(cw * 3 / 4, 0, cw / 4, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Round_WallR",
                pixelWidth = 1920, pixelHeight = 1890,
                physicalWidth = 2.4f, physicalHeight = 2.4f,
                sourcePixels = new RectInt(cw * 7 / 8, 0, cw / 8, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Round_Terrace",
                pixelWidth = 1920, pixelHeight = 1620,
                physicalWidth = 3.66f, physicalHeight = 3.08f,
                sourcePixels = new RectInt(cw * 7 / 8, ch / 2, cw / 8, ch / 2)
            });
        }

        // ─── Zone presets ───

        [Button("Zone A: Entrance")]
        public void GenerateZoneA()
        {
            screens.Clear();
            int cw = compositeWidth, ch = compositeHeight;

            screens.Add(new ScreenConfig {
                name = "Entrance_Landscape",
                pixelWidth = 2304, pixelHeight = 1296,
                physicalWidth = 3.66f, physicalHeight = 2.05f,
                sourcePixels = new RectInt(0, ch / 4, cw * 3 / 5, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Entrance_Portrait",
                pixelWidth = 1536, pixelHeight = 2112,
                physicalWidth = 3.84f, physicalHeight = 5.28f,
                sourcePixels = new RectInt(cw * 3 / 5, 0, cw * 2 / 5, ch)
            });
        }

        [Button("Zone B: Front + Side")]
        public void GenerateZoneB()
        {
            screens.Clear();
            int cw = compositeWidth, ch = compositeHeight;

            screens.Add(new ScreenConfig {
                name = "FrontWall_Portrait",
                pixelWidth = 1080, pixelHeight = 1920,
                physicalWidth = 1.71f, physicalHeight = 3.05f,
                sourcePixels = new RectInt(0, 0, cw * 3 / 10, ch)
            });
            screens.Add(new ScreenConfig {
                name = "FrontWall_Square",
                pixelWidth = 1536, pixelHeight = 1512,
                physicalWidth = 2.44f, physicalHeight = 2.40f,
                sourcePixels = new RectInt(cw * 3 / 10, ch / 20, cw * 2 / 5, ch * 9 / 10)
            });
            screens.Add(new ScreenConfig {
                name = "SideWall_Square",
                pixelWidth = 810, pixelHeight = 960,
                physicalWidth = 1.03f, physicalHeight = 1.22f,
                sourcePixels = new RectInt(cw * 7 / 10, ch / 10, cw * 3 / 10, ch * 4 / 5)
            });
        }

        [Button("Zone C: Round Room")]
        public void GenerateZoneC()
        {
            screens.Clear();
            int cw = compositeWidth, ch = compositeHeight;

            screens.Add(new ScreenConfig {
                name = "Round_Ceiling",
                pixelWidth = 1920, pixelHeight = 1890,
                physicalWidth = 2.4f, physicalHeight = 2.4f,
                sourcePixels = new RectInt(0, ch / 2, cw / 2, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Round_WallL",
                pixelWidth = 1920, pixelHeight = 1890,
                physicalWidth = 2.4f, physicalHeight = 2.4f,
                sourcePixels = new RectInt(0, 0, cw / 2, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Round_WallR",
                pixelWidth = 1920, pixelHeight = 1890,
                physicalWidth = 2.4f, physicalHeight = 2.4f,
                sourcePixels = new RectInt(cw / 2, 0, cw / 2, ch / 2)
            });
            screens.Add(new ScreenConfig {
                name = "Round_Terrace",
                pixelWidth = 1920, pixelHeight = 1620,
                physicalWidth = 3.66f, physicalHeight = 3.08f,
                sourcePixels = new RectInt(cw / 2, ch / 2, cw / 2, ch / 2)
            });
        }

        // ─── RT asset creation (editor only) ───

        [Button("2. Create RT Assets")]
        public void CreateRTAssets()
        {
#if UNITY_EDITOR
            if (screens.Count == 0)
            {
                Debug.LogError("[ScreenLayout] Generate screens first.");
                return;
            }

            string fullFolder = "Assets/" + rtAssetFolder;
            if (!AssetDatabase.IsValidFolder(fullFolder))
            {
                string[] parts = fullFolder.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            rtAssets.Clear();
            for (int i = 0; i < screens.Count; i++)
            {
                var cfg = screens[i];
                string path = $"{fullFolder}/RT_{cfg.name}.renderTexture";

                var existing = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
                if (existing != null)
                {
                    existing.Release();
                    existing.width = cfg.pixelWidth;
                    existing.height = cfg.pixelHeight;
                    existing.Create();
                    rtAssets.Add(existing);
                }
                else
                {
                    var rt = new RenderTexture(cfg.pixelWidth, cfg.pixelHeight, 0, RenderTextureFormat.ARGB32);
                    rt.name = $"RT_{cfg.name}";
                    rt.filterMode = FilterMode.Bilinear;
                    AssetDatabase.CreateAsset(rt, path);
                    rtAssets.Add(rt);
                }
                Debug.Log($"[ScreenLayout] {path}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.SetDirty(this);
#endif
        }

        // ─── Build quads + cameras ───

        [Button("3. Build Screen Quads")]
        public void BuildScreenQuads()
        {
            ClearScreenQuads();

            if (screenMaterial == null)
            {
                Debug.LogError("[ScreenLayout] No screenMaterial assigned.");
                return;
            }

            if (rtAssets.Count != screens.Count)
                Debug.LogWarning("[ScreenLayout] RT assets count mismatch. Run 'Create RT Assets' for Recorder.");

            float xCursor = 0f;
            string lastWallGroup = "";

            for (int i = 0; i < screens.Count; i++)
            {
                var cfg = screens[i];
                string wallGroup = cfg.name.Split('_')[0];

                if (wallGroup != lastWallGroup && lastWallGroup != "")
                    xCursor += wallGap;
                lastWallGroup = wallGroup;

                float w = cfg.physicalWidth * sceneScale;
                float h = cfg.physicalHeight * sceneScale;

                var quad = CreateScreenQuad(cfg);
                quad.transform.SetParent(transform, false);
                quad.transform.localPosition = new Vector3(xCursor + w * 0.5f, h * 0.5f, 0f);

                RenderTexture rt = (i < rtAssets.Count && rtAssets[i] != null)
                    ? rtAssets[i]
                    : CreateRuntimeRT(cfg);

                var cam = CreateScreenCamera(cfg, quad.transform, w, h, rt);

                screenQuads.Add(quad);
                screenCameras.Add(cam);
                xCursor += w;
            }
        }

        private GameObject CreateScreenQuad(ScreenConfig cfg)
        {
            float w = cfg.physicalWidth * sceneScale;
            float h = cfg.physicalHeight * sceneScale;
            Rect uv = PixelsToUV(cfg.sourcePixels);

            var go = new GameObject(cfg.name);
            go.layer = screenLayer;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            mf.sharedMesh = BuildCroppedQuadMesh(uv, cfg.name, w, h);
            mr.sharedMaterial = screenMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            return go;
        }

        private Camera CreateScreenCamera(ScreenConfig cfg, Transform parent, float worldW, float worldH, RenderTexture rt)
        {
            var camGO = new GameObject($"Camera_{cfg.name}");
            camGO.transform.SetParent(parent, false);
            camGO.transform.localPosition = new Vector3(0f, 0f, -cameraDistance);
            camGO.transform.localRotation = Quaternion.identity;

            var cam = camGO.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = worldH * 0.5f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = cameraDistance + 1f;
            cam.cullingMask = 1 << screenLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.depth = -10;
            cam.targetTexture = rt;

            return cam;
        }

        private RenderTexture CreateRuntimeRT(ScreenConfig cfg)
        {
            var rt = new RenderTexture(cfg.pixelWidth, cfg.pixelHeight, 0, RenderTextureFormat.ARGB32);
            rt.name = $"RT_{cfg.name}";
            rt.filterMode = FilterMode.Bilinear;
            rt.Create();
            return rt;
        }

        private Mesh BuildCroppedQuadMesh(Rect uvRect, string name, float w, float h)
        {
            var mesh = new Mesh { name = "ScreenQuad_" + name };
            float hw = w * 0.5f, hh = h * 0.5f;

            mesh.vertices = new Vector3[] {
                new(-hw, -hh, 0f), new(hw, -hh, 0f),
                new(hw, hh, 0f), new(-hw, hh, 0f)
            };
            mesh.uv = new Vector2[] {
                new(uvRect.x, uvRect.y),
                new(uvRect.x + uvRect.width, uvRect.y),
                new(uvRect.x + uvRect.width, uvRect.y + uvRect.height),
                new(uvRect.x, uvRect.y + uvRect.height)
            };
            mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            mesh.normals = new Vector3[] {
                -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        // ─── Cleanup ───

        [Button("Clear Screen Quads")]
        public void ClearScreenQuads()
        {
            screenCameras.Clear();
            foreach (var go in screenQuads)
                if (go != null) DestroyImmediate(go);
            screenQuads.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--)
                DestroyImmediate(transform.GetChild(i).gameObject);
        }

        [Button("Set All Source to Full")]
        public void SetAllSourceFull()
        {
            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i];
                s.sourcePixels = new RectInt(0, 0, compositeWidth, compositeHeight);
                screens[i] = s;
            }
        }

        public List<ScreenConfig> GetScreenConfigs() => screens;
        public int ScreenCount => screens.Count;
    }
}

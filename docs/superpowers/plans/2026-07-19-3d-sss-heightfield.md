# 3D SSS Heightfield Form Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A displaced heightfield mesh with HDRP subsurface scattering driven read-only by the biome (permeability mounds + composite trails), rendered by an orbiting camera to a second external stream (`EoC/Form3D`).

**Architecture:** A compute pre-pass (`HeightBake.compute`, two kernels split by a dispatch barrier) bakes temporally-smoothed height (R16) + tangent-space normals (RGBA16) from `Biome.FieldReadArray[CH_PERMEABILITY]` and `CompositeOutputTexture` luminance. A procedural grid mesh with a stock **HDRP/Lit** material (Material Type = Subsurface Scattering, Displacement = Vertex, emissive map = the composite itself — palette-matched by construction) renders via a dedicated orbit camera into an RT pushed by `ExternalTextureSender` (new `SendSource.Form3D`). No shader graph: HDRP/Lit natively supports `_HeightMap` vertex displacement + SSS + `_EmissiveColorMap` (all property names verified against HDRP 17.3.0 in `Library/PackageCache`).

**Tech Stack:** Unity 6000.3.10f1, HDRP 17.3.0, namespace `Biomes`, OscJack 2.0.0, existing `GPUResourceManager` / clear-in-place (ADR-0008) patterns.

## Global Constraints

- Namespace `Biomes`; files under `Assets/Workspace/11.0 Biomes/src/`.
- Read-only taps on sim/biome: never modify `Biome.Step()`, sim kernels, or the composite path.
- Clear-in-place reset (ADR-0008): guarded `Allocate()` keyed on an allocation signature; RT instances stable across resets.
- Compute conventions: `[numthreads(8,8,1)]`, bounds-guard first line, texel-center UVs `(id+0.5)/rez`, `CH_PERMEABILITY = 7`.
- `SendSource.Form3D` appended LAST in the enum (serialized indices must not shift).
- No Unity test infra exists in this repo → verification = C# compile check (Task 7) + in-editor manual checklist (Task 8 doc). TDD is not applicable to GPU/editor-bound code here; each task still ends compilable and committed.
- Commit after every task; messages concise, no attribution.

## File Structure

```
Assets/Workspace/11.0 Biomes/src/
  computes/HeightBake.compute                 (new — bake kernels)
  components/render3d/HeightfieldForm.cs      (new — RTs, mesh, dispatch, material push)
  components/render3d/OrbitCamera3D.cs        (new — auto-orbit + OSC override)
  components/network/ExternalTextureSender.cs (modify — Form3D source)
  components/network/OSCMapping.cs            (modify — /form3d/* + /cam3d/*)
  Editor/CreateBioFormAssets.cs               (new — DP_BioFlesh + M_BioForm creation)
docs/ARCHITECTURE.md                          (modify — §3D form)
docs/wiring/2026-07-19-bioform-scene-wiring.md (new — manual editor checklist)
README.md                                     (modify — feature line)
```

---

### Task 1: `HeightBake.compute`

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/computes/HeightBake.compute`

**Interfaces:**
- Produces kernels `BakeHeight`, `BakeNormal`; uniforms/textures consumed by Task 2 exactly as named below.

- [ ] **Step 1: Write the compute shader**

```hlsl
// Bakes a temporally-smoothed heightfield + tangent-space normal map from the
// biome permeability channel and the composite (trail) luminance.
// Two kernels: BakeHeight writes heights, BakeNormal derives normals from the
// fully-updated heights — the dispatch boundary is the sync barrier (one kernel
// reading neighbor texels it also writes would race).

#pragma kernel BakeHeight
#pragma kernel BakeNormal

#define CH_PERMEABILITY 7

Texture2DArray<float> fieldRead;
SamplerState sampler_fieldRead;
Texture2D<float4> compositeTex;
SamplerState sampler_compositeTex;

RWTexture2D<float>  heightTex;   // R16F, persistent (temporal smoothing state)
RWTexture2D<float4> normalTex;   // RGBA16F, tangent-space, RG = n.xy*0.5+0.5

int rezX, rezY;
float permGain;      // mound height contribution
float trailGain;     // trail shimmer contribution
float smoothK;       // temporal lerp factor (0..1, per rendered frame)
float2 slopeScale;   // heightAmplitude(m) * rez / planeWorldSize(m), per axis

[numthreads(8, 8, 1)]
void BakeHeight(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= (uint)rezX || id.y >= (uint)rezY) return;

    float2 uv = float2((id.x + 0.5) / (float)rezX, (id.y + 0.5) / (float)rezY);
    float perm  = fieldRead.SampleLevel(sampler_fieldRead, float3(uv, CH_PERMEABILITY), 0);
    float3 comp = compositeTex.SampleLevel(sampler_compositeTex, uv, 0).rgb;
    float trail = dot(comp, float3(0.299, 0.587, 0.114));

    float target = saturate(permGain * perm + trailGain * trail);
    heightTex[id.xy] = lerp(heightTex[id.xy], target, smoothK);
}

[numthreads(8, 8, 1)]
void BakeNormal(uint3 id : SV_DISPATCHTHREADID) {
    if (id.x >= (uint)rezX || id.y >= (uint)rezY) return;

    uint xl = max(id.x, 1u) - 1u;          uint xr = min(id.x + 1u, (uint)(rezX - 1));
    uint yb = max(id.y, 1u) - 1u;          uint yt = min(id.y + 1u, (uint)(rezY - 1));
    float dhx = (heightTex[uint2(xr, id.y)] - heightTex[uint2(xl, id.y)]) * 0.5;
    float dhy = (heightTex[uint2(id.x, yt)] - heightTex[uint2(id.x, yb)]) * 0.5;

    // Tangent-space normal (UnpackNormalmapRGorAG reads RG, reconstructs Z).
    float3 n = normalize(float3(-dhx * slopeScale.x, -dhy * slopeScale.y, 1.0));
    normalTex[id.xy] = float4(n.xy * 0.5 + 0.5, 1.0, 1.0);
}
```

- [ ] **Step 2: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/computes/HeightBake.compute"
git commit -m "feat(bioform): height+normal bake kernels (perm mounds + composite trails, temporal smooth)"
```

---

### Task 2: `HeightfieldForm.cs`

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/render3d/HeightfieldForm.cs`

**Interfaces:**
- Consumes: `SimulationManager.biome` (`Biome.FieldReadArray`, `biomeRezX/Y`), `SimulationManager.CompositeOutputTexture`, `SimulationManager.rezX/rezY`, `GPUResourceManager`, Task 1 kernel/uniform names.
- Produces: `public RenderTexture CameraOutputTexture` (for sender), `public Camera formCamera`, public floats `permGain, trailGain, smoothK, heightScale, emissionGain` (for OSC).

- [ ] **Step 1: Write the component**

```csharp
using UnityEngine;

namespace Biomes
{
    /// <summary>Bakes a smoothed heightfield (permeability mounds + composite trail
    /// luminance) and drives a displaced HDRP/Lit SSS mesh — the first 3D form.
    /// Read-only taps on biome/composite; clear-in-place alloc per ADR-0008.
    /// Runs after SimulationManager's composite (execution order 100).</summary>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HeightfieldForm : MonoBehaviour
    {
        [Header("References")]
        public SimulationManager simManager;
        public ComputeShader heightBakeCS;
        public Material formMaterial;          // M_BioForm (HDRP/Lit, SSS + vertex displacement)
        public Camera formCamera;              // renders the form to CameraOutputTexture

        [Header("Height")]
        [Range(0f, 2f)] public float permGain = 1f;
        [Range(0f, 2f)] public float trailGain = 0.25f;
        [Range(0.01f, 1f)] public float smoothK = 0.08f;
        [Range(0f, 1f)] public float heightScale = 0.25f;   // meters at height=1

        [Header("Emission")]
        [Range(0f, 20f)] public float emissionGain = 4f;

        [Header("Mesh")]
        [Range(64, 1024)] public int gridResolution = 384;

        public RenderTexture CameraOutputTexture => form3dOutTex;

        private GPUResourceManager gpu;
        private RenderTexture heightTex, normalTex, form3dOutTex;
        private Material runtimeMat;
        private Mesh gridMesh;
        private int bakeHeightKernel = -1, bakeNormalKernel = -1;
        private int allocBiomeRezX, allocBiomeRezY, allocOutRezX, allocOutRezY, allocGridRez;

        private static readonly int s_FieldReadID    = Shader.PropertyToID("fieldRead");
        private static readonly int s_CompositeTexID = Shader.PropertyToID("compositeTex");
        private static readonly int s_HeightTexID    = Shader.PropertyToID("heightTex");
        private static readonly int s_NormalTexID    = Shader.PropertyToID("normalTex");
        private static readonly int s_RezXID         = Shader.PropertyToID("rezX");
        private static readonly int s_RezYID         = Shader.PropertyToID("rezY");
        private static readonly int s_PermGainID     = Shader.PropertyToID("permGain");
        private static readonly int s_TrailGainID    = Shader.PropertyToID("trailGain");
        private static readonly int s_SmoothKID      = Shader.PropertyToID("smoothK");
        private static readonly int s_SlopeScaleID   = Shader.PropertyToID("slopeScale");
        private static readonly int s_HeightMapID       = Shader.PropertyToID("_HeightMap");
        private static readonly int s_NormalMapID       = Shader.PropertyToID("_NormalMap");
        private static readonly int s_EmissiveMapID     = Shader.PropertyToID("_EmissiveColorMap");
        private static readonly int s_EmissiveColorID   = Shader.PropertyToID("_EmissiveColor");
        private static readonly int s_HeightAmplitudeID = Shader.PropertyToID("_HeightAmplitude");

        void Awake()
        {
            if (simManager == null || heightBakeCS == null || formMaterial == null)
            {
                Debug.LogWarning("[HeightfieldForm] Missing references — disabling.");
                enabled = false;
                return;
            }
            bakeHeightKernel = heightBakeCS.FindKernel("BakeHeight");
            bakeNormalKernel = heightBakeCS.FindKernel("BakeNormal");
        }

        void LateUpdate()
        {
            var biome = simManager.biome;
            if (biome == null || biome.FieldReadArray == null ||
                simManager.CompositeOutputTexture == null) return;   // pre-Reset frames

            AllocateIfNeeded(biome);
            Bake(biome);
            PushMaterial();
        }

        // Clear-in-place (ADR-0008): reallocate only when the signature changes so
        // CameraOutputTexture stays stable across /sim_reset (no stream teardown).
        private void AllocateIfNeeded(Biome biome)
        {
            int bx = biome.biomeRezX, by = biome.biomeRezY;
            int ox = simManager.rezX, oy = simManager.rezY;
            if (bx == allocBiomeRezX && by == allocBiomeRezY &&
                ox == allocOutRezX && oy == allocOutRezY && gridResolution == allocGridRez)
                return;

            gpu?.ReleaseAll();
            gpu = new GPUResourceManager();

            heightTex = gpu.CreateTexture2D(bx, by, FilterMode.Bilinear,
                RenderTextureFormat.RHalf, "bioform_height");
            normalTex = gpu.CreateTexture2D(bx, by, FilterMode.Bilinear,
                RenderTextureFormat.ARGBHalf, "bioform_normal");
            ClearRT(heightTex);

            form3dOutTex = new RenderTexture(ox, oy, 24, RenderTextureFormat.ARGBHalf)
                { name = "bioform_out" };
            form3dOutTex.Create();
            gpu.Track(form3dOutTex);
            if (formCamera != null) formCamera.targetTexture = form3dOutTex;

            BuildGridMesh((float)ox / oy);

            if (runtimeMat != null) Destroy(runtimeMat);
            runtimeMat = new Material(formMaterial);
            GetComponent<MeshRenderer>().sharedMaterial = runtimeMat;

            allocBiomeRezX = bx; allocBiomeRezY = by;
            allocOutRezX = ox; allocOutRezY = oy; allocGridRez = gridResolution;
        }

        private void Bake(Biome biome)
        {
            int bx = allocBiomeRezX, by = allocBiomeRezY;
            var scale = transform.lossyScale;
            float planeW = Mathf.Max(0.001f, gridMesh.bounds.size.x * scale.x);
            float planeH = Mathf.Max(0.001f, gridMesh.bounds.size.z * scale.z);
            var slope = new Vector2(heightScale * bx / planeW, heightScale * by / planeH);

            heightBakeCS.SetInt(s_RezXID, bx);
            heightBakeCS.SetInt(s_RezYID, by);
            heightBakeCS.SetFloat(s_PermGainID, permGain);
            heightBakeCS.SetFloat(s_TrailGainID, trailGain);
            heightBakeCS.SetFloat(s_SmoothKID, smoothK);
            heightBakeCS.SetVector(s_SlopeScaleID, slope);

            heightBakeCS.SetTexture(bakeHeightKernel, s_FieldReadID, biome.FieldReadArray);
            heightBakeCS.SetTexture(bakeHeightKernel, s_CompositeTexID, simManager.CompositeOutputTexture);
            heightBakeCS.SetTexture(bakeHeightKernel, s_HeightTexID, heightTex);
            heightBakeCS.Dispatch(bakeHeightKernel, (bx + 7) / 8, (by + 7) / 8, 1);

            heightBakeCS.SetTexture(bakeNormalKernel, s_HeightTexID, heightTex);
            heightBakeCS.SetTexture(bakeNormalKernel, s_NormalTexID, normalTex);
            heightBakeCS.Dispatch(bakeNormalKernel, (bx + 7) / 8, (by + 7) / 8, 1);
        }

        private void PushMaterial()
        {
            runtimeMat.SetTexture(s_HeightMapID, heightTex);
            runtimeMat.SetTexture(s_NormalMapID, normalTex);
            runtimeMat.SetTexture(s_EmissiveMapID, simManager.CompositeOutputTexture);
            runtimeMat.SetColor(s_EmissiveColorID, Color.white * emissionGain);
            runtimeMat.SetFloat(s_HeightAmplitudeID, heightScale);
        }

        // XZ grid, UV 0..1, up normals, standard tangents; 32-bit indices.
        // Bounds padded in Y so displaced verts never get frustum-culled.
        private void BuildGridMesh(float aspect)
        {
            if (gridMesh == null)
            {
                gridMesh = new Mesh { name = "bioform_grid" };
                gridMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                GetComponent<MeshFilter>().sharedMesh = gridMesh;
            }
            int n = gridResolution + 1;
            var verts = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            var normals = new Vector3[n * n];
            var tangents = new Vector4[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                float u = (float)x / gridResolution, v = (float)y / gridResolution;
                verts[i] = new Vector3((u - 0.5f) * aspect, 0f, v - 0.5f);
                uvs[i] = new Vector2(u, v);
                normals[i] = Vector3.up;
                tangents[i] = new Vector4(1f, 0f, 0f, -1f);
            }
            var tris = new int[gridResolution * gridResolution * 6];
            int t = 0;
            for (int y = 0; y < gridResolution; y++)
            for (int x = 0; x < gridResolution; x++)
            {
                int i = y * n + x;
                tris[t++] = i;     tris[t++] = i + n;     tris[t++] = i + 1;
                tris[t++] = i + 1; tris[t++] = i + n;     tris[t++] = i + n + 1;
            }
            gridMesh.Clear();
            gridMesh.vertices = verts;
            gridMesh.uv = uvs;
            gridMesh.normals = normals;
            gridMesh.tangents = tangents;
            gridMesh.triangles = tris;
            gridMesh.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(aspect, 3f, 1f));
        }

        private static void ClearRT(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        void OnDestroy()
        {
            gpu?.ReleaseAll();
            if (runtimeMat != null) Destroy(runtimeMat);
            if (gridMesh != null) Destroy(gridMesh);
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/render3d/HeightfieldForm.cs"
git commit -m "feat(bioform): heightfield form component (bake dispatch, grid mesh, HDRP Lit material push)"
```

---

### Task 3: `OrbitCamera3D.cs`

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/components/render3d/OrbitCamera3D.cs`

**Interfaces:**
- Produces: public fields `autoOrbit, azimuthDeg, elevationDeg, distance` (set by OSC in Task 5).

- [ ] **Step 1: Write the component**

```csharp
using UnityEngine;

namespace Biomes
{
    /// <summary>Slow auto-orbit around the bioform; OSC can freeze auto and drive
    /// azimuth/elevation/distance directly (/cam3d/*).</summary>
    public class OrbitCamera3D : MonoBehaviour
    {
        public Transform target;

        [Header("Auto orbit")]
        public bool autoOrbit = true;
        [Range(-30f, 30f)] public float azimuthDegPerSec = 2f;
        [Range(1f, 600f)] public float elevSinePeriod = 90f;
        [Range(0f, 30f)] public float elevSineAmp = 8f;

        [Header("Pose (OSC-writable)")]
        public float azimuthDeg = 0f;
        [Range(5f, 85f)] public float elevationDeg = 25f;
        [Range(0.2f, 20f)] public float distance = 2.2f;

        private float baseElevation;

        void Start() => baseElevation = elevationDeg;

        void LateUpdate()
        {
            if (target == null) return;
            if (autoOrbit)
            {
                azimuthDeg = Mathf.Repeat(azimuthDeg + azimuthDegPerSec * Time.deltaTime, 360f);
                elevationDeg = baseElevation
                    + elevSineAmp * Mathf.Sin(Time.time * 2f * Mathf.PI / elevSinePeriod);
            }
            float az = azimuthDeg * Mathf.Deg2Rad, el = elevationDeg * Mathf.Deg2Rad;
            var offset = new Vector3(
                Mathf.Cos(el) * Mathf.Sin(az),
                Mathf.Sin(el),
                Mathf.Cos(el) * Mathf.Cos(az)) * distance;
            transform.position = target.position + offset;
            transform.LookAt(target.position, Vector3.up);
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/render3d/OrbitCamera3D.cs"
git commit -m "feat(bioform): orbit camera (auto drift + OSC-writable pose)"
```

---

### Task 4: `ExternalTextureSender` — Form3D source

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureSender.cs`

**Interfaces:**
- Consumes: `HeightfieldForm.CameraOutputTexture` (Task 2).

- [ ] **Step 1: Append enum member** (LAST — serialized indices must not shift)

```csharp
public enum SendSource { CompositeOutput, SimOutput, BiomeLayer, Form3D }
```

- [ ] **Step 2: Add reference field** under the existing `[Header("References")]` block:

```csharp
public HeightfieldForm form3d;   // optional — only needed for Form3D streams
```

- [ ] **Step 3: Add resolve case** in the texture-resolution `switch` (alongside `SimOutput`/`BiomeLayer` cases):

```csharp
case SendSource.Form3D:
    return form3d != null ? form3d.CameraOutputTexture
        : WarnOnce(live, "Form3D stream has no HeightfieldForm assigned");
```

(`WarnOnce` returns null after logging once — same pattern the other cases use. Match its exact signature at the call site.)

- [ ] **Step 4: Add default name case** in `DefaultName`:

```csharp
SendSource.Form3D => "EoC/Form3D",
```

- [ ] **Step 5: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/ExternalTextureSender.cs"
git commit -m "feat(bioform): Form3D send source (EoC/Form3D stream)"
```

---

### Task 5: `OSCMapping` — `/form3d/*` + `/cam3d/*`

**Files:**
- Modify: `Assets/Workspace/11.0 Biomes/src/components/network/OSCMapping.cs`

**Interfaces:**
- Consumes: `HeightfieldForm` public floats (Task 2), `OrbitCamera3D` public fields (Task 3).

- [ ] **Step 1: Add serialized references** next to the existing `m_*` references:

```csharp
[SerializeField] public HeightfieldForm m_HeightfieldForm;
[SerializeField] public OrbitCamera3D m_OrbitCamera;
```

- [ ] **Step 2: Register callbacks** in the same method where `/sim_reset` callbacks are added. These only write plain C# floats/bools, so they stay inline on the OSC thread (same rule the file's header comment documents — no main-thread marshalling needed):

```csharp
// ── bioform (3D heightfield) ──
if (m_HeightfieldForm != null)
{
    m_OscServer.MessageDispatcher.AddCallback("/form3d/permGain",
        (string a, OscDataHandle d) => m_HeightfieldForm.permGain = d.GetElementAsFloat(0));
    m_OscServer.MessageDispatcher.AddCallback("/form3d/trailGain",
        (string a, OscDataHandle d) => m_HeightfieldForm.trailGain = d.GetElementAsFloat(0));
    m_OscServer.MessageDispatcher.AddCallback("/form3d/smoothK",
        (string a, OscDataHandle d) => m_HeightfieldForm.smoothK = d.GetElementAsFloat(0));
    m_OscServer.MessageDispatcher.AddCallback("/form3d/heightScale",
        (string a, OscDataHandle d) => m_HeightfieldForm.heightScale = d.GetElementAsFloat(0));
    m_OscServer.MessageDispatcher.AddCallback("/form3d/emissionGain",
        (string a, OscDataHandle d) => m_HeightfieldForm.emissionGain = d.GetElementAsFloat(0));
}
if (m_OrbitCamera != null)
{
    m_OscServer.MessageDispatcher.AddCallback("/cam3d/azimuth",
        (string a, OscDataHandle d) => m_OrbitCamera.azimuthDeg = d.GetElementAsFloat(0));
    m_OscServer.MessageDispatcher.AddCallback("/cam3d/elev",
        (string a, OscDataHandle d) => m_OrbitCamera.elevationDeg = d.GetElementAsFloat(0));
    m_OscServer.MessageDispatcher.AddCallback("/cam3d/dist",
        (string a, OscDataHandle d) => m_OrbitCamera.distance = d.GetElementAsFloat(0));
    m_OscServer.MessageDispatcher.AddCallback("/cam3d/auto",
        (string a, OscDataHandle d) => m_OrbitCamera.autoOrbit = d.GetElementAsFloat(0) > 0.5f);
}
```

- [ ] **Step 3: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/components/network/OSCMapping.cs"
git commit -m "feat(bioform): OSC /form3d/* + /cam3d/* params"
```

---

### Task 6: Editor utility — `CreateBioFormAssets.cs`

**Files:**
- Create: `Assets/Workspace/11.0 Biomes/src/Editor/CreateBioFormAssets.cs`

Creates `DP_BioFlesh.asset` (warm diffusion profile via `SerializedObject` — `DiffusionProfileSettings.profile` is internal) and `M_BioForm.mat` (HDRP/Lit with SSS + vertex displacement + emissive map keywords, diffusion profile pre-assigned by GUID+hash). All HDRP property names verified against the installed 17.3.0 package.

- [ ] **Step 1: Write the utility**

```csharp
using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.Rendering.HighDefinition;   // HDMaterial
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;   // DiffusionProfileSettings

namespace Biomes
{
    public static class CreateBioFormAssets
    {
        private const string Folder = "Assets/Workspace/11.0 Biomes/assets/BioForm";

        [MenuItem("Biomes/Create BioForm Assets")]
        public static void Create()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets/Workspace/11.0 Biomes/assets", "BioForm");

            // ── diffusion profile (warm flesh) ──
            string dpPath = $"{Folder}/DP_BioFlesh.asset";
            var dp = AssetDatabase.LoadAssetAtPath<DiffusionProfileSettings>(dpPath);
            if (dp == null)
            {
                dp = ScriptableObject.CreateInstance<DiffusionProfileSettings>();
                AssetDatabase.CreateAsset(dp, dpPath);
            }
            var so = new SerializedObject(dp);
            var scatter = so.FindProperty("profile.scatteringDistance");
            if (scatter != null) scatter.colorValue = new Color(1f, 0.35f, 0.2f);
            var tint = so.FindProperty("profile.transmissionTint");
            if (tint != null) tint.colorValue = new Color(1f, 0.45f, 0.3f);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(dp);

            // ── material (HDRP/Lit: SSS + vertex displacement + emissive map) ──
            string matPath = $"{Folder}/M_BioForm.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("HDRP/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetFloat("_MaterialID", 0);                // Subsurface Scattering
            mat.SetFloat("_DisplacementMode", 1);          // Vertex displacement
            mat.SetFloat("_HeightMapParametrization", 1);  // Amplitude mode
            mat.SetFloat("_HeightAmplitude", 0.25f);       // runtime-overwritten
            mat.SetFloat("_HeightCenter", 0f);
            mat.SetFloat("_NormalScale", 1f);
            mat.SetFloat("_Smoothness", 0.35f);
            mat.SetColor("_BaseColor", new Color(0.35f, 0.16f, 0.12f));
            mat.SetColor("_EmissiveColor", Color.white * 4f);
            mat.EnableKeyword("_HEIGHTMAP");
            mat.EnableKeyword("_VERTEX_DISPLACEMENT");
            mat.EnableKeyword("_NORMALMAP");
            mat.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
            mat.EnableKeyword("_EMISSIVE_COLOR_MAP");
            mat.EnableKeyword("_MATERIAL_FEATURE_SUBSURFACE_SCATTERING");

            // Diffusion profile reference = asset GUID as vec4 + serialized hash.
            string guid = AssetDatabase.AssetPathToGUID(dpPath);
            mat.SetVector("_DiffusionProfileAsset", GuidToVector4(guid));
            var hashProp = new SerializedObject(dp).FindProperty("profile.hash");
            if (hashProp != null)
                mat.SetFloat("_DiffusionProfileHash",
                    BitConverter.ToSingle(BitConverter.GetBytes(hashProp.uintValue), 0));
            else
                Debug.LogWarning("[BioForm] Couldn't read profile hash — assign DP_BioFlesh on M_BioForm manually.");

            HDMaterial.ValidateMaterial(mat);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            Debug.Log("[BioForm] Created/updated DP_BioFlesh + M_BioForm. Remember: register " +
                      "DP_BioFlesh in the HDRP diffusion profile list (Unity shows a Fix button " +
                      "on the material if missing).");
        }

        private static Vector4 GuidToVector4(string guid)
        {
            var v = Vector4.zero;
            for (int i = 0; i < 4; i++)
            {
                uint u = uint.Parse(guid.Substring(i * 8, 8), NumberStyles.HexNumber);
                v[i] = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
            }
            return v;
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add "Assets/Workspace/11.0 Biomes/src/Editor/CreateBioFormAssets.cs"
git commit -m "feat(bioform): editor utility creating DP_BioFlesh + M_BioForm (HDRP Lit SSS + displacement)"
```

---

### Task 7: Compile verification

No Unity test framework in this repo; the strongest headless check is compiling the C# against the project's own generated csproj (references Unity + HDRP DLLs from `Library/`).

- [ ] **Step 1: Compile**

Run from repo root (worktree note: csproj `<Compile Include>` lists are stale for *new* files — compile main assembly with new files appended, or use `dotnet build` if the csproj is SDK-resolvable):

```bash
dotnet msbuild Assembly-CSharp.csproj -t:Build -v:m -p:UnityBuild=true 2>&1 | tail -20
```

Expected: `Build succeeded` (warnings OK). If `dotnet`/`msbuild` unavailable, fall back to `csc` with references extracted from the csproj (`grep '<HintPath>' Assembly-CSharp.csproj`). New `.cs` files must be added to the compile list (Unity regenerates csproj on focus; headless, append `<Compile Include="..."/>` entries or pass all `src/**/*.cs` to csc).

- [ ] **Step 2: Fix any compile errors, re-run until clean, commit fixes**

```bash
git add -A && git commit -m "fix(bioform): compile fixes"
```

(HLSL is not covered — Unity import validates it; note any risk areas in the wiring doc.)

---

### Task 8: Docs + scene wiring checklist

**Files:**
- Create: `docs/wiring/2026-07-19-bioform-scene-wiring.md`
- Modify: `docs/ARCHITECTURE.md` (new subsection under the Unity runtime), `README.md` (feature line)

- [ ] **Step 1: Write the wiring checklist** — exact manual steps in the Unity editor (nothing here is scriptable headlessly):

```markdown
# BioForm scene wiring (TestScene) — manual editor steps

1. Run **Biomes ▸ Create BioForm Assets** (creates `assets/BioForm/DP_BioFlesh.asset`
   + `M_BioForm.mat`).
2. Register `DP_BioFlesh` in the diffusion profile list — select `M_BioForm`; if HDRP
   shows a **Fix** button next to Diffusion Profile, click it.
3. Create empty GO `BioForm3D` (position clear of the composite quad, e.g. y = -3):
   - Add `HeightfieldForm` → assign `simManager`, `HeightBake.compute`, `M_BioForm`.
   - MeshFilter/MeshRenderer auto-added; leave mesh/material slots — runtime-assigned.
4. Child GO `FormCamera`: add `Camera` (HDRP) + `OrbitCamera3D` → target = `BioForm3D`
   transform; assign the camera to `HeightfieldForm.formCamera`.
   Camera: clear flags Color (near-black), culling mask = a dedicated `BioForm` layer;
   put `BioForm3D` + lights on that layer so the 2D quads don't leak in.
5. Child GO `FormKeyLight`: HDRP spot/area behind-above the form (warm, ~4000 K,
   intensity to taste) + a very dim cool fill light. Same `BioForm` layer.
6. `ExternalTextureSender`: add stream — source `Form3D`, protocol Syphon, name blank
   (defaults to `EoC/Form3D`); assign the `form3d` reference.
7. `OSCMapping`: assign `m_HeightfieldForm` + `m_OrbitCamera`.
8. Save as prefab `assets/BioForm/BioForm3D.prefab` for promotion to show scenes.

## Validation (spec §Testing)
- [ ] Mounds rise where termites build; `/sim_resetTermites` deflates smoothly.
- [ ] Trail shimmer visible as fine relief; composite glow reads through the skin.
- [ ] Rim light bleeds through thin ridges (SSS transmission).
- [ ] `/form3d/*` + `/cam3d/*` respond; `/sim_reset` tears down neither stream.
- [ ] Existing composite stream unchanged.
- [ ] Perf: bake = two field-res dispatches — negligible vs `Biome.Step()`.
```

- [ ] **Step 2: ARCHITECTURE.md** — add a short subsection after the composite/render section: what `HeightfieldForm` reads (FieldReadArray ch7 + composite), the two-kernel bake, HDRP/Lit SSS material, orbit camera, `SendSource.Form3D`. Link the spec and this wiring doc.

- [ ] **Step 3: README.md** — one feature line under the current features summary: 3D SSS heightfield form (termite topography + trail glow, second stream `EoC/Form3D`).

- [ ] **Step 4: Commit**

```bash
git add docs/ARCHITECTURE.md README.md docs/wiring/2026-07-19-bioform-scene-wiring.md
git commit -m "docs(bioform): architecture section, README line, scene wiring checklist"
```

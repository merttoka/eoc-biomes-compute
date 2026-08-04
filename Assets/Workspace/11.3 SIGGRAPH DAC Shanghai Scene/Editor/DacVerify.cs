using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Biomes.EditorTools
{
    /// <summary>
    /// Measures the Scene_DAC success criteria instead of eyeballing them.
    ///
    /// <para><b>Why this exists.</b> Most of the show's criteria sound subjective — "the hue
    /// spread closes", "the loop is seamless", "the centre is deader than the edges" — and were
    /// originally going to be judged by eye. Judged by eye, three of them were called wrong
    /// during development: the centre/edge ratio was blamed on termite composite weight (it was
    /// the mound overlay masking the agent layer), `spawnScale` was declared innocent (it was
    /// the actual cause), and the cutout was flagged as a risk on a luminance ratio that does
    /// not answer the question the criterion asks. Each was settled by measuring.</para>
    ///
    /// <para>Runs from the menu, or headless:
    /// <c>Unity -batchmode -executeMethod Biomes.EditorTools.DacVerify.Run</c>. It drives the
    /// sim from EDIT mode — FixedUpdate does not fire there, so ShowArc and ShanghaiTransect are
    /// ticked explicitly, which makes the step count exact and the run reproducible.</para>
    ///
    /// <para><b>Read the numbers, not just the verdicts.</b> Mean luminance cannot see motion,
    /// and "visibly deader" is about motion. The agent-layer figures (overlay off) are the ones
    /// that speak to that; the composite figures include the city.</para>
    /// </summary>
    public static class DacVerify
    {
        const string ScenePath = "Assets/Workspace/11.3 SIGGRAPH DAC Shanghai Scene/Scene_DAC.unity";
        static readonly string OutDir =
            Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? ".", "Recordings/dac_verify");

        static readonly StringBuilder Log = new();
        static int _errors, _exceptions, _editModeDestroy;

        // SimulationManager.Render() is private and only called from LateUpdate, which does not
        // run in edit mode. Reached by reflection rather than widening the production API.
        static readonly System.Reflection.MethodInfo s_Render =
            typeof(SimulationManager).GetMethod("Render",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);

        static void Say(string s) { Log.AppendLine(s); Debug.Log("[DacVerify] " + s); }

        static void Hook(string msg, string stack, LogType t)
        {
            // Pre-existing and edit-mode-only: Biome.CreateDebugGrid uses Destroy(), correct at
            // runtime and illegal here. Counted apart so it cannot mask a real error.
            if (msg.StartsWith("Destroy may not be called from edit mode")) { _editModeDestroy++; return; }
            if (t == LogType.Error) _errors++;
            else if (t == LogType.Exception) _exceptions++;
        }

        [MenuItem("Biomes/Verify Scene_DAC criteria")]
        public static void Run()
        {
            Directory.CreateDirectory(OutDir);
            Log.Clear(); _errors = _exceptions = _editModeDestroy = 0;
            Application.logMessageReceived += Hook;

            try { Body(); }
            catch (Exception e) { _exceptions++; Say("FATAL: " + e); }
            finally { Application.logMessageReceived -= Hook; }

            Say("");
            Say($"errors={_errors} exceptions={_exceptions} ({_editModeDestroy} edit-mode Destroy ignored)");
            File.WriteAllText(Path.Combine(OutDir, "dac_verify.log"), Log.ToString());
            Say($"written to {OutDir}");
            if (Application.isBatchMode) EditorApplication.Exit(_errors + _exceptions > 0 ? 1 : 0);
        }

        static void Body()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sm = UnityEngine.Object.FindFirstObjectByType<SimulationManager>();
            var arc = UnityEngine.Object.FindFirstObjectByType<ShowArc>();
            var tr = UnityEngine.Object.FindFirstObjectByType<ShanghaiTransect>();
            var firing = UnityEngine.Object.FindFirstObjectByType<NeuronFiringSource>();
            var phys = sm.simulations.Find(s => s is PhysarumSim);

            sm.Reset();
            if (tr != null && !tr.IsLoaded) tr.Load();
            arc?.Restart();

            int total = Mathf.RoundToInt(arc.loopSeconds * sm.simRate);
            Say($"=== Scene_DAC {sm.rezX}x{sm.rezY}, {sm.simulations.Count} sims, {total} frames ===");
            Say($"    spawnScale={firing?.spawnScale}  moundOverlay={sm.moundOverlayStrength}");
            Say("");

            // ── 1 · both screens must be pure crops of this master ────────────────────
            bool c26 = sm.rezX >= 9472 && sm.rezY >= 850;      // 9472x800 at y+50
            bool c12 = sm.rezX >= 9236 && sm.rezY >= 900;      // 9000x900 at x+236
            Say($"1 · crops fit master: 2.6 {(c26 ? "PASS" : "FAIL")}, 1.2 {(c12 ? "PASS" : "FAIL")}");

            // ── run the real loop, capturing what the later criteria need ─────────────
            Texture2D fSeamPrev = null, fSeam = null, fWrap = null;
            var hue = new Dictionary<int, (float var, float mean)>();
            float peakFiring = 0f;

            for (int lap = 0; lap < 2; lap++)
                for (int f = 0; f < total; f++)
                {
                    arc?.Tick(); tr?.SeedNow(); sm.Step();
                    var sv = firing?.ScaledValues;
                    if (sv != null) foreach (var v in sv) if (v > peakFiring) peakFiring = v;

                    if (lap == 0 && (f == total - 60 || f == total - 1))
                    {
                        s_Render.Invoke(sm, null);
                        if (f == total - 60) fSeamPrev = Grab(sm.CompositeOutputTexture);
                        else fSeam = Grab(sm.CompositeOutputTexture);
                    }
                    if (lap == 0 && (f == 1200 || f == 4500))
                    {
                        s_Render.Invoke(sm, null);
                        var pt = Grab(phys.GetOutputTexture());
                        hue[f] = HueStats(pt);
                        UnityEngine.Object.DestroyImmediate(pt);
                    }
                    if (lap == 1 && f == 0) { s_Render.Invoke(sm, null); fWrap = Grab(sm.CompositeOutputTexture); }
                }

            // ── 2 · loop seam ─────────────────────────────────────────────────────────
            if (fSeamPrev && fSeam && fWrap)
            {
                float normal = MeanAbsDiff(fSeamPrev, fSeam);   // 59 ordinary frames
                float seam = MeanAbsDiff(fSeam, fWrap);         // 1 frame, across the wrap
                Say($"2 · seam {(seam <= normal ? "PASS" : "FAIL")}: wrap delta {seam:0.00000} vs " +
                    $"{normal:0.00000} over 59 frames (ratio {(normal > 0 ? seam / normal : 0):0.000})");
            }

            // ── 3 · hue spread closes (physarum layer; the composite is mostly overlay) ─
            if (hue.ContainsKey(1200) && hue.ContainsKey(4500))
            {
                var a = hue[1200]; var b = hue[4500];
                Say($"3 · hue {(b.var < a.var ? "PASS" : "FAIL")}: circular variance {a.var:0.0000} -> {b.var:0.0000}, " +
                    $"mean hue {b.mean:0.000} (0.0 = red)");
            }

            // ── 4 + 8 · centre vs edges, on the AGENT layer with the overlay off ───────
            float saveMound = sm.moundOverlayStrength;
            sm.Reset(); sm.moundOverlayStrength = 0f;
            for (int i = 0; i < 800; i++) { arc?.ScrubTo(55f); tr?.SeedNow(); sm.Step(); }
            s_Render.Invoke(sm, null);
            var bare = Grab(sm.CompositeOutputTexture);
            float L = Band(bare, 0f, .2f), C = Band(bare, .4f, .6f), R = Band(bare, .8f, 1f);
            float edge = (L + R) * 0.5f;
            Say($"4 · centre deader {(edge > 0 && C < edge ? "PASS" : "FAIL")}: agent layer C={C:0.0000} " +
                $"edges={edge:0.0000} (C/edge {(edge > 0 ? C / edge : 999):0.00}; <1 wanted)");
            Say($"8 · cutout: the centre band the 1.2 cutout removes carries {(edge > 0 ? C / edge : 999):0.00}x " +
                "the edge activity on the agent layer — judge completeness on the masked crop, not this number");
            File.WriteAllBytes(Path.Combine(OutDir, "agent_layer_t55.png"), Down(bare, 1400).EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(bare);
            sm.moundOverlayStrength = saveMound;

            // ── 9 · rings/stamps land on agents, at the EDGES especially ──────────────
            // The old three-copies-of-spawnScale desync was zero-error at centre and worst at
            // the edges, so only the edge figure discriminates.
            sm.Reset(); sm.moundOverlayStrength = 0f;
            for (int i = 0; i < 700; i++) { arc?.Tick(); tr?.SeedNow(); sm.Step(); }
            s_Render.Invoke(sm, null);
            var ag = Grab(phys.GetOutputTexture());
            var scale = firing != null ? firing.SpawnScale : NeuronLayout.DefaultScale;
            var edgeUv = new List<Vector2>(); var rndUv = new List<Vector2>();
            if (firing?.PositionsCPU != null)
                foreach (var np in firing.PositionsCPU)
                    if (Mathf.Abs(np.x - 0.5f) > 0.35f) edgeUv.Add(NeuronLayout.ToFieldUV(np, scale));
            UnityEngine.Random.InitState(12345);
            for (int i = 0; i < 128; i++) rndUv.Add(new Vector2(UnityEngine.Random.value, UnityEngine.Random.value));
            float mE = DiscMean(ag, edgeUv, 28), mR = DiscMean(ag, rndUv, 28);
            Say($"9 · rings on agents {(mR > 0 && mE / mR > 2f ? "PASS" : "FAIL")}: edge-neuron density " +
                $"{(mR > 0 ? mE / mR : 0):0.00}x random ({edgeUv.Count} edge neurons)");
            UnityEngine.Object.DestroyImmediate(ag);
            sm.moundOverlayStrength = saveMound;

            Say($"    peak firing over 2 laps: {peakFiring:0.000} " +
                $"{(peakFiring <= 0f ? "<- NO FIRING: is ShowArc.driveFiringPlayhead on?" : "")}");

            foreach (var t in new[] { fSeamPrev, fSeam, fWrap })
                if (t) UnityEngine.Object.DestroyImmediate(t);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────
        static Texture2D Grab(RenderTexture rt)
        {
            if (rt == null) return null;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); t.Apply();
            RenderTexture.active = prev; return t;
        }

        static Texture2D Down(Texture2D tex, int pw)
        {
            int ph = Mathf.Max(1, tex.height * pw / tex.width);
            var rt = RenderTexture.GetTemporary(pw, ph, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var s = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
            s.ReadPixels(new Rect(0, 0, pw, ph), 0, 0); s.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); return s;
        }

        static float Band(Texture2D tex, float x0, float x1)
        {
            int a = Mathf.Clamp(Mathf.RoundToInt(x0 * tex.width), 0, tex.width - 1);
            int b = Mathf.Clamp(Mathf.RoundToInt(x1 * tex.width), a + 1, tex.width);
            var px = tex.GetPixels(a, 0, b - a, tex.height);
            double s = 0; foreach (var p in px) s += p.r * 0.299 + p.g * 0.587 + p.b * 0.114;
            return (float)(s / px.Length);
        }

        static float DiscMean(Texture2D tex, List<Vector2> uvs, int r)
        {
            if (tex == null || uvs.Count == 0) return 0f;
            int W = tex.width, H = tex.height;
            var px = tex.GetPixels();
            double total = 0;
            foreach (var uv in uvs)
            {
                int cx = Mathf.RoundToInt(uv.x * W), cy = Mathf.RoundToInt(uv.y * H);
                double s = 0; int n = 0;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx * dx + dy * dy > r * r) continue;
                        var p = px[Mathf.Clamp(cy + dy, 0, H - 1) * W + Mathf.Clamp(cx + dx, 0, W - 1)];
                        s += p.r * 0.299 + p.g * 0.587 + p.b * 0.114; n++;
                    }
                total += s / Mathf.Max(1, n);
            }
            return (float)(total / uvs.Count);
        }

        static float MeanAbsDiff(Texture2D a, Texture2D b)
        {
            var pa = a.GetPixels32(); var pb = b.GetPixels32();
            int n = Mathf.Min(pa.Length, pb.Length);
            double s = 0;
            for (int i = 0; i < n; i++)
                s += (Mathf.Abs(pa[i].r - pb[i].r) + Mathf.Abs(pa[i].g - pb[i].g) + Mathf.Abs(pa[i].b - pb[i].b)) / (3.0 * 255.0);
            return (float)(s / n);
        }

        /// <summary>Circular variance of hue, luminance-weighted. Hue is an angle, so a plain
        /// standard deviation is meaningless across the 1.0/0.0 wrap — the resultant-vector
        /// length is the correct measure. 0 = one hue (ONE BODY), toward 1 = spread (MANY).</summary>
        static (float var, float mean) HueStats(Texture2D tex)
        {
            if (tex == null) return (0, 0);
            var px = tex.GetPixels();
            double sx = 0, sy = 0, w = 0;
            foreach (var p in px)
            {
                float lum = p.r * 0.299f + p.g * 0.587f + p.b * 0.114f;
                if (lum < 0.02f) continue;                       // background
                Color.RGBToHSV(p, out float h, out float s, out _);
                if (s < 0.05f) continue;                          // hue is noise when desaturated
                double a = h * 2.0 * Math.PI;
                sx += Math.Cos(a) * lum; sy += Math.Sin(a) * lum; w += lum;
            }
            if (w <= 0) return (0, 0);
            double Rl = Math.Sqrt(sx * sx + sy * sy) / w;
            double mean = Math.Atan2(sy, sx) / (2.0 * Math.PI); if (mean < 0) mean += 1.0;
            return ((float)(1.0 - Rl), (float)mean);
        }
    }
}

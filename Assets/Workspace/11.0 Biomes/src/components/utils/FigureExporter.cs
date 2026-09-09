using System;
using System.Collections.Generic;
using UnityEngine;
using EasyButtons;

namespace Biomes
{
    /// <summary>
    /// One-click export of every sim output + the composite under a SINGLE timestamp,
    /// for paper figures. The per-component export buttons (SimulationBase.ExportPNG,
    /// SimulationManager.ExportPNG, Biome.ExportPNGs) each stamp their own time, so a
    /// figure assembled from separate clicks mixes moments; this writes the same rendered
    /// instant into one timestamped folder. Reads the sims and composite through the
    /// SimulationManager — one reference instead of one per texture.
    ///
    /// <para>Also records ffmpeg-ready frame sequences (Start/Stop Frame Export): numbered
    /// consecutively per source so <c>-i %06d.png</c> works directly. LateUpdate must see
    /// the frame the manager just composited, hence the execution-order offset below
    /// (SimulationManager renders in its own LateUpdate).</para>
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class FigureExporter : MonoBehaviour
    {
        [Tooltip("Manager whose sims + composite are exported.")]
        public SimulationManager simManager;

        [Tooltip("Folder relative to the project root (parent of Assets/). Every export " +
                 "click creates one timestamped subfolder holding all PNGs of that click.")]
        public string exportFolder = "Exports/Figures";

        [Tooltip("Also run the Biome's per-channel PNG export into the same timestamped " +
                 "folder (under a <biome name> subfolder), so the whole figure set lands " +
                 "in one place. Uses the Biome's own exportNormalized setting.")]
        public bool includeBiomeChannels = false;

        [Tooltip("Encode PNGs as sRGB so files look like the screen. The sim/composite " +
                 "textures hold LINEAR values (the project renders in linear color space); " +
                 "the display applies the sRGB transfer when showing them, but a raw dump " +
                 "of linear values reads as crushed-dark in every image viewer. Off = raw " +
                 "linear values (only for numeric/data pipelines).")]
        public bool encodeSRGB = true;

        [Button("Export All (one timestamp)")]
        public void ExportAll()
        {
            if (simManager == null)
            {
                Debug.LogWarning("[FigureExporter] No SimulationManager assigned.");
                return;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
            string dir = System.IO.Path.Combine(projectRoot, exportFolder, stamp);
            System.IO.Directory.CreateDirectory(dir);

            int exported = 0;

            // Per-sim outputs, index-prefixed to match the composite's simInput0..7 order.
            for (int i = 0; i < simManager.simulations.Count; i++)
            {
                var sim = simManager.simulations[i];
                if (sim == null) continue;
                var tex = sim.GetOutputTexture();
                if (tex == null || !tex.IsCreated())
                {
                    Debug.Log($"[FigureExporter] {sim.SimName}: no live output texture " +
                              "(sim not started?) — skipped.");
                    continue;
                }
                SavePNG(tex, System.IO.Path.Combine(dir, $"{i:D2}_{sim.SimName}.png"));
                exported++;
            }

            var composite = simManager.CompositeOutputTexture;
            if (composite != null && composite.IsCreated())
            {
                SavePNG(composite, System.IO.Path.Combine(dir, "Composite.png"));
                exported++;
            }
            else
            {
                Debug.LogWarning("[FigureExporter] Composite texture missing (manager not reset?).");
            }

            // Biome channels ride along by pointing the Biome's own exporter at this run's
            // folder for the duration of the call — no change to its API, and its files
            // (channel-named, per-biome subfolder) slot in beside the sim PNGs.
            if (includeBiomeChannels && simManager.biome != null)
            {
                var biome = simManager.biome;
                string prevFolder = biome.exportFolder;
                biome.exportFolder = System.IO.Path.Combine(exportFolder, stamp);
                try { biome.ExportPNGs(); }
                finally { biome.exportFolder = prevFolder; }
            }

            Debug.Log($"[FigureExporter] Exported {exported} PNGs → {dir}");
        }

        // ── Frame sequence (ffmpeg) ──────────────────────────────────────────────

        [Header("Frame sequence (ffmpeg)")]
        [Tooltip("Drive Time.captureDeltaTime while recording: every rendered frame equals " +
                 "exactly 1/this seconds of game time, so every sim step gets rendered and " +
                 "captured no matter how slow the wall clock gets — a 5-sim-minute timeline " +
                 "yields exactly 5min × this many frames. Without it the fixed-timestep sim " +
                 "runs several catch-up steps per rendered frame under encode load (capped " +
                 "by maxAllowedTimestep) and the capture skips steps unevenly. 0 = off " +
                 "(free-run; only correct when something else, e.g. Unity Recorder in " +
                 "constant-FPS mode, drives the capture clock — never run both at once).")]
        [Range(0, 120)] public int captureFramerate = 30;

        [Tooltip("Capture every Nth rendered frame while recording (1 = all). With " +
                 "captureFramerate set, effective video fps = captureFramerate / this. " +
                 "PNG encode is synchronous on the main thread, so capture costs real " +
                 "fps — sim/recorded time stays correct, only wall-clock slows.")]
        [Range(1, 10)] public int frameEvery = 1;
        [Tooltip("Capture the composite each frame (Composite/%06d.png). Untick when Unity Recorder " +
                 "already records the composite (via SimulationManager.recorderTarget) and you only " +
                 "want the sim / biome-channel sequences from here.")]
        public bool framesIncludeComposite = true;
        [Tooltip("Capture each sim's own output, one subfolder per sim. Multiplies the per-frame encode cost.")]
        public bool framesIncludeSims = false;

        [Tooltip("Biome channels captured each frame, one subfolder per channel — pick the " +
                 "few the video needs rather than all 15; every entry adds a readback + " +
                 "PNG encode per frame (cheap at biome rez, but it adds up). Rendered RAW " +
                 "(no per-frame min/max stretch — that would flicker across a sequence), so " +
                 "faint channels export dark; brighten in post/ffmpeg.")]
        [BiomeChannelField]
        public List<int> frameBiomeChannels = new();

        private string _framesDir;        // null = not recording
        private int _frameCounter;
        private int _framesWritten;
        // Per-sim counters so every subfolder numbers gaplessly from 1 even when a sim
        // starts or stops mid-recording — ffmpeg's %06d input tolerates a late start
        // (-start_number) but not holes.
        private readonly int[] _simFramesWritten = new int[8];
        private readonly int[] _biomeFramesWritten = new int[BiomeChannel.Count];

        // Reusable UAV target for Biome.RenderChannelTo (same shape Biome.ExportPNGs
        // allocates per call — cached here because frame capture runs every frame).
        // Rebuilt if the biome resolution changes; released on disable.
        private RenderTexture _biomeScratch;

        private RenderTexture BiomeScratch(Biome biome)
        {
            if (_biomeScratch == null
                || _biomeScratch.width != biome.RezX || _biomeScratch.height != biome.RezY)
            {
                ReleaseBiomeScratch();
                _biomeScratch = new RenderTexture(biome.RezX, biome.RezY, 0,
                    RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true,
                    dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
                };
                _biomeScratch.Create();
            }
            return _biomeScratch;
        }

        private void ReleaseBiomeScratch()
        {
            if (_biomeScratch == null) return;
            _biomeScratch.Release();
            if (Application.isPlaying) Destroy(_biomeScratch); else DestroyImmediate(_biomeScratch);
            _biomeScratch = null;
        }

        public bool IsRecordingFrames => _framesDir != null;

        [Button("Start Frame Export")]
        public void StartFrameExport()
        {
            if (simManager == null)
            {
                Debug.LogWarning("[FigureExporter] No SimulationManager assigned.");
                return;
            }
            if (_framesDir != null)
            {
                Debug.LogWarning("[FigureExporter] Already recording frames.");
                return;
            }
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
            _framesDir = System.IO.Path.Combine(projectRoot, exportFolder, stamp + "_frames");
            System.IO.Directory.CreateDirectory(framesIncludeComposite
                ? System.IO.Path.Combine(_framesDir, "Composite") : _framesDir);
            if (!framesIncludeComposite && !framesIncludeSims && frameBiomeChannels.Count == 0)
                Debug.LogWarning("[FigureExporter] Nothing selected to export (composite, sims, biome channels all off) — only the capture clock will run.");
            _frameCounter = 0;
            _framesWritten = 0;
            Array.Clear(_simFramesWritten, 0, _simFramesWritten.Length);
            Array.Clear(_biomeFramesWritten, 0, _biomeFramesWritten.Length);
            if (captureFramerate > 0)
                Time.captureDeltaTime = 1f / captureFramerate;
            Debug.Log($"[FigureExporter] Frame export started → {_framesDir}" +
                      (captureFramerate > 0 ? $" (capture clock {captureFramerate} fps)" : " (free-run clock)"));
        }

        [Button("Stop Frame Export")]
        public void StopFrameExport()
        {
            if (_framesDir == null) return;
            if (captureFramerate > 0)
                Time.captureDeltaTime = 0f;   // release the capture clock → realtime again
            float videoFps = (captureFramerate > 0 ? captureFramerate : 60f) / Mathf.Max(1, frameEvery);
            Debug.Log($"[FigureExporter] Frame export stopped: {_framesWritten} frames → {_framesDir}\n" +
                      $"ffmpeg -framerate {videoFps:F0} -i \"{_framesDir}/<subfolder>/%06d.png\" " +
                      "-c:v libx264 -pix_fmt yuv420p -vf \"crop=trunc(iw/2)*2:trunc(ih/2)*2\" out.mp4");
            _framesDir = null;
        }

        // Runs after SimulationManager.LateUpdate (execution-order offset on the class),
        // so the composite holds THIS frame's render when we read it back.
        void LateUpdate()
        {
            if (_framesDir == null || simManager == null) return;
            _frameCounter++;
            if (_frameCounter % Mathf.Max(1, frameEvery) != 0) return;

            var composite = simManager.CompositeOutputTexture;
            if (composite == null || !composite.IsCreated()) return;   // manager not reset yet

            _framesWritten++;
            if (framesIncludeComposite)
                SavePNG(composite, System.IO.Path.Combine(_framesDir, "Composite",
                    _framesWritten.ToString("D6") + ".png"));

            if (framesIncludeSims)
            {
                int n = Mathf.Min(simManager.simulations.Count, _simFramesWritten.Length);
                for (int i = 0; i < n; i++)
                {
                    var sim = simManager.simulations[i];
                    if (sim == null) continue;
                    var tex = sim.GetOutputTexture();
                    if (tex == null || !tex.IsCreated()) continue;
                    string sub = System.IO.Path.Combine(_framesDir, $"{i:D2}_{sim.SimName}");
                    System.IO.Directory.CreateDirectory(sub);
                    SavePNG(tex, System.IO.Path.Combine(sub,
                        (++_simFramesWritten[i]).ToString("D6") + ".png"));
                }
            }

            // Picked biome channels, rendered raw through the same debug kernel the grid
            // uses (RenderChannelTo no-ops until the biome has been reset).
            if (frameBiomeChannels.Count > 0 && simManager.biome != null
                && simManager.biome.FieldReadArray != null)
            {
                var biome = simManager.biome;
                var scratch = BiomeScratch(biome);
                foreach (int ch in frameBiomeChannels)
                {
                    if (ch < 0 || ch >= BiomeChannel.Count) continue;
                    biome.RenderChannelTo(ch, scratch);
                    string sub = System.IO.Path.Combine(_framesDir,
                        $"biome_{BiomeChannel.Names[ch]}");
                    System.IO.Directory.CreateDirectory(sub);
                    SavePNG(scratch, System.IO.Path.Combine(sub,
                        (++_biomeFramesWritten[ch]).ToString("D6") + ".png"));
                }
            }
        }

        void OnDisable()
        {
            StopFrameExport();
            ReleaseBiomeScratch();
        }

        private void SavePNG(RenderTexture rt, string path) =>
            PngExport.Save(rt, path, encodeSRGB);
    }

    /// <summary>
    /// Shared PNG write-out for the export buttons (FigureExporter, SimulationManager,
    /// SimulationBase). The render targets hold LINEAR values (the project renders in
    /// linear color space); with encodeSRGB on, a blit through a pooled sRGB target
    /// applies the hardware linear→sRGB encode first so the file matches the on-screen
    /// look — ReadPixels alone copies the linear values verbatim, which exports
    /// crushed-dark in every image viewer.
    /// </summary>
    public static class PngExport
    {
        /// <summary>Resolve (and create) a folder under the project root — the parent of
        /// Assets/ — e.g. "Exports/Figures".</summary>
        public static string Dir(string sub)
        {
            string dir = System.IO.Path.Combine(
                System.IO.Directory.GetParent(Application.dataPath).FullName, sub);
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Save(RenderTexture rt, string path, bool encodeSRGB = true)
        {
            RenderTexture src = rt, tmp = null;
            if (encodeSRGB)
            {
                tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(rt, tmp);
                src = tmp;
            }
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = src;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
            else UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}

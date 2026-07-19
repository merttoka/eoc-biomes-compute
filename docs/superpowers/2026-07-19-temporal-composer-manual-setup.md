# Temporal Composer — Manual Setup + Verification Checklist

**Date:** 2026-07-19
**Target:** `11.2 SIGGRAPH Scene` / `Scene_SIGGRAPH.unity`
**Status:** consolidated from Task 3–9 per-task manual checklists (code side of all tasks is done, compiled, tested; everything below requires the Unity Editor GUI and was not executable headlessly)

Run in order — later steps assume earlier ones are wired. Each step names the task it came from. Do this on mac dev first; re-run the perf gates (§3) on the show machine (Windows, RTX 5080) before the show.

## 1. Scene wiring order

1. **Compile check.** Open the project in Unity `6000.3.10f1`; let it finish compiling — 0 errors in Console. *(Task 3)*
2. **`CompositeSequencer`.** Open `Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity`. Create empty GameObject `TemporalComposer`; add `CompositeSequencer`. Assign: `simManager` = the scene's main `SimulationManager`, `sequencerCS` = `SequencerComposite.compute`, `composerOutMat` = `Assets/Workspace/11.2 SIGGRAPH Scene/materials/m_composite.mat`. *(Task 3)*
3. **`CellRig_A`.** Duplicate the existing `SimulationManager` GameObject hierarchy (manager + `Biome` + one or two sims — keep it light, e.g. just Physarum). Rename root `CellRig_A`.
   - On its `SimulationManager`: `rezX = rezY = 1024`, `ownsGlobalTiming = OFF`, `stepsPerTick = 0`, `limitFPS = OFF`; assign a **different** preset asset than the main scene (e.g. `assets/Snapshots/Physarum_20260711_201424`); clear `compositeOutMat` / `compositeOutputQuad` / `recordingCamera` (the rig renders only to its own composite texture, never to screen).
   - Add `BiomeCellRig`; assign `manager`.
   - Save scene, enter Play mode, tick `Running` on the rig. Expected: no console errors; the rig's `CompositeOutputTexture` is non-null and animating (preview via material or frame debugger); main output unchanged. Confirm `stepsPerTick = 0` in the Inspector (else double-stepping from `FixedUpdate`).
   - Drag `CellRig_A` into `Assets/Workspace/11.2 SIGGRAPH Scene/assets/` → save as `BiomeCellRig.prefab`.
   - **Rule:** the Timeline mixer must toggle `Running` only — never `SetActive(false)` on the rig (its `OnDisable` → `Release()` tears down the RT the composer holds). *(Task 4)*
4. **`CellRig_B`.** Duplicate `CellRig_A` → `CellRig_B`; give it a different snapshot preset (e.g. a Termite or Boid variant). Save as its own prefab. Spec caps at 4 cells — C/D optional, same pattern. *(Task 10)*
5. **`PlayableDirector` + `ShowSequence`.** On `TemporalComposer`: Add Component → `PlayableDirector`. In the Project window, `Assets/Workspace/11.2 SIGGRAPH Scene/assets/` → right-click → Create → Timeline → name `ShowSequence`; assign to the `PlayableDirector`. *(Task 5)*
6. **`BiomeCellTrack`.** With `TemporalComposer` selected, Window → Sequencing → Timeline. Add track → `Biomes` → `Biome Cell Track`; bind to `CompositeSequencer`. Add two `BiomeCellClip`s — A: `source = Rig`, `rig = CellRig_A`, `dstRect` = left band, `mode = Overlay`; B: `rig = CellRig_B`, `dstRect` = right band, `mode = Overlay`. ~10s each with 2–3s ease-in/out (drag clip edges). *(Task 5, sized per Task 10)*
7. **Reset signals.** Create `Assets/Workspace/11.2 SIGGRAPH Scene/assets/signals/`. Create → Signal ×4: `Sig_ResetSims`, `Sig_ResetPhysarum`, `Sig_ResetBoids`, `Sig_ResetTermites`.
   - On the main `SimulationManager`: Add Component → `SignalReceiver`. Add 4 reactions: `Sig_ResetSims`→`ResetSimsOnly`, `Sig_ResetPhysarum`→`ResetPhysarum`, `Sig_ResetBoids`→`ResetBoids`, `Sig_ResetTermites`→`ResetTermites`.
   - Add a Signal Track bound to the main `SimulationManager` GameObject. *(Task 5)*
8. **`PatchScatterTrack`.** Add track → `Patch Scatter Track` (orange), bind to `CompositeSequencer`. Add a clip: `sourceA = MainComposite`, `sourceB = DiffusionReturn`, `crossfadeCenter = 0.5` (defaults otherwise). *(Task 6, sourced per Task 10)*
9. **`ParamSnapshotTrack`.** Add track → `Param Snapshot Track`, bind to the **main** `SimulationManager`. Add a clip: `snapshot = assets/Snapshots/Physarum_20260711_195316`, `simIndex` = index of `PhysarumSim` in the manager's `simulations` list (check inspector), 10s ease-in. *(Task 7, timing per Task 10)*
10. **`RoutingTrack`.** Add track → `Routing Track`, bind to `CompositeSequencer`. Add a clip: `influenceSource = DiffusionReturn`. *(Task 8)*
11. **Second receiver (`TD_Diffusion`).** On the `NetworkIO`/`TextureIO` GameObject: add a second `ExternalTextureReceiver` — `enableReceive = ON`, `protocol = Syphon` (mac dev) / `Spout` (show machine), `streamName = "TD_Diffusion"`, `selfDrive = ON`. Assign it to `CompositeSequencer.diffusionReturn`. Assign the existing first receiver to `CompositeSequencer.inputReceiver`. *(Task 8)*
12. **Sender (`EoC/Composer`).** On `ExternalTextureSender`: add a stream with `source = ComposerOutput`, protocol Syphon (mac) / Spout (show), name left blank (defaults `EoC/Composer`); assign the `sequencer` field to the scene's `CompositeSequencer`. *(Task 8)*
13. **`ScreenLayout` re-point.** Confirm `ScreenLayout.screenMaterial` (and any other display/recorder path) is the same `m_composite.mat` that `CompositeSequencer.composerOutMat` now writes into every `LateUpdate` — i.e. displays and the outbound sender read `composerOutTex`, not the raw sim `compositeOutTex`, going forward. If the main `SimulationManager` still has its own `compositeOutMat` pointed at `m_composite.mat`, clear it — only `CompositeSequencer` should write that material now (two writers racing on the same material per frame is a bug). *(Task 3 design intent — spec: "Displays: ScreenLayout + senders sample composerOutTex, not the sim composite")*
14. **Full-frame replace clip.** Add a third `BiomeCellClip` on `BiomeCellTrack`: `rig = CellRig_A`, `mode = Replace`, `duckBase = ON`, `dstRect = (0, 0, 1, 1)` — full-frame takeover, fades back out per the 90s pass below. *(Task 10)*

## 2. Author the 90s demonstration pass

On `ShowSequence`, lay out clips per this timing (adjust exact clip edges in the Timeline window to match):

| Time | What |
|---|---|
| 0–20s | Main composite only; `ParamSnapshotClip` morphs physarum → `Physarum_20260711_195316` (10s ease) |
| 15–45s | `BiomeCellClip` A (Overlay, left band) and B (Overlay, right band) fade in 3s, hold, fade out |
| 40–70s | `PatchScatterClip` (`sourceA = MainComposite`, `sourceB = DiffusionReturn`, `crossfadeCenter = 0.5`) — patches dissolve raw sim → diffusion over the clip |
| 55–65s | `RoutingClip` (`DiffusionReturn` feeds sims) + `Sig_ResetTermites` emitter at 60s |
| 70–90s | `BiomeCellClip` A, `mode = Replace`, `duckBase = ON`, full-frame `dstRect (0,0,1,1)` — cell takes over the output, then fades back |

Play the full pass in the editor. Expected: every transition lands; Console clean. *(Task 10, Step 1)*

## 3. Verification steps

Run these after the wiring above, roughly in dependency order:

1. **Base pass 1:1.** Enter Play mode with only `CompositeSequencer` wired (no clips authored yet, or before any clip plays): the composite quad shows exactly what it showed pre-sequencer; Console has no errors; frame rate unchanged (±1–2 fps). *(Task 3)*
2. **`debugOutlines`.** Toggle `debugOutlines` on `CompositeSequencer`:
   - With nothing active: no change, no errors. *(Task 3)*
   - During an active `BiomeCellClip`: green outline around the cell's `dstRect`. *(Task 5)*
   - During active patches: orange outline per active patch. *(Task 6)*
3. **Cell rig runtime.** In Play mode with a cell clip active: `CompositeOutputTexture` on the rig's manager is non-null and animating; main output unchanged while the rig runs. *(Task 4)*
4. **Rig stop-on-Stop.** Press Stop on the `PlayableDirector` mid-clip (a `BiomeCellClip` actively weighted). Expected: the rig's `Running` flips to `false` (verify in Inspector) — it must not keep simulating after the graph tears down. *(Task 5, fix-pass note)*
5. **Reset signal fires.** During playback, at the `Sig_ResetTermites` emitter (60s in the full pass, or a test emitter at ~5s for `Sig_ResetPhysarum` while authoring): the corresponding sim visibly respawns; Console clean, no exceptions. *(Task 5)*
6. **Patch determinism.** With a `PatchScatterClip` active, scrub the director back and forth in the Timeline window. Expected: patch layout at a given time is byte-identical every pass; no console errors; Profiler → Memory → GC Alloc ≈ 0 B/frame in steady state after the clip's first frame. Stop, change `seed` → different scatter on next play; revert `seed` → same scatter as before. *(Task 6)*
7. **Patch diffusion crossfade.** With `sourceB = DiffusionReturn` wired to a live stream: patches dissolve per-patch between the two sources per the sigmoid/`crossfadeRoll` curve. With no live diffusion stream: the layer degrades cleanly to `sourceA` alone (never black). *(Task 6)*
8. **Param morph.** Play across a `ParamSnapshotClip`: the sim's look morphs into the snapshot over the ease duration and holds. Scrub to before the clip: params only snap back once the clip re-enters (recaptures "from") — expected, sims are forward-only, no revert-on-stop safety net. Point `snapshot` at a non-`IParamSet` asset (e.g. a Material): single console warning, no errors. *(Task 7)*
9. **Routing override — no TD.** Play with the routing clip active and the diffusion receiver having no live stream: `influenceOverride` resolves to null → sims run normally; patches using `DiffusionReturn` degrade to `sourceA`. No errors. *(Task 8)*
10. **Routing override — with TD (mac, optional).** Publish a Syphon stream named `TD_Diffusion` (TD or any test sender): cell/patch clips using `DiffusionReturn` show it; during the routing clip the sims visibly react to it. Confirm `EoC/Composer` appears as a Syphon source in TD. *(Task 8)*
11. **Thumbnail capture color check.** `Biomes → Biome Palette` — grid lists the 11.1/11.2 preset/snapshot assets (dark placeholder tiles where no thumb exists yet). In Play mode with the composer running, select a tile → `Capture` → a `<name>_thumb.png` appears next to the asset; the tile now shows it. **Check the thumbnail's color actually matches the live composite at capture time** (not black, not a wrong-channel/washed-out readback — `SnapshotThumbnailCache.Capture` blits `ARGBHalf` → `ARGB32` `Texture2D` via `ReadPixels`, worth an eyeball check the first time). With the `ShowSequence` Timeline open: `Insert` on a tile drops a 10s `ParamSnapshotClip` at the playhead with the asset assigned; play across it → params morph. Drag a tile onto the Project window → carries the asset (standard object drag). *(Task 9)*
12. **Full 90s pass.** Play `ShowSequence` start to finish per §2. Expected: every transition lands; Console clean throughout. *(Task 10, Step 1.3)*

## 4. Performance gates (mac dev numbers; re-check on show machine)

1. Window → Analysis → Profiler. Play the 15–45s section of the pass (2 live cells + main sim running concurrently).
2. Record: GPU ms/frame (or CPU `Gfx.WaitForPresent` as proxy), `CompositeSequencer.LateUpdate` CPU ms, GC Alloc/frame.
3. **Pass criteria:**
   - GC Alloc ≈ 0 B/frame in steady state (first frame of each clip may allocate — events build lazily; not a fail).
   - `CompositeSequencer.LateUpdate` < 0.5 ms CPU.
   - Overall fps ≥ `targetFPS` × 0.9 with 2 cells running.
4. **If cells are too heavy:** drop rig `rezX`/`rezY` to 768 or `cellRate` to 12 — note whichever values you land on directly in the scene (rig Inspector), so the show config is self-documenting.
5. Re-run this section on the actual show machine (Windows, RTX 5080) before relying on the mac numbers — the plan's perf budget (`docs/superpowers/specs/2026-07-19-temporal-composer-design.md`, "Performance budget") assumes StreamDiffusion is also running there and dominates GPU time; Unity's own `targetFrameRate`/sim-Hz caps exist specifically to leave it headroom.

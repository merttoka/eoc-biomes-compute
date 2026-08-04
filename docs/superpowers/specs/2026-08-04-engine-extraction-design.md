---
date: 2026-08-04
status: approved
supersedes: none
tags: [git, refactor, branch-hygiene, ca-sims]
---

# Engine extraction: archive the Shanghai delivery, rebuild main from the engine work

## Problem

Local `main` sits 32 commits ahead of `origin/main` (`969ad48`, Jul 19, MFT brightness
ramp) with a dirty working tree on top: 12 modified files, 16 untracked. Roughly a third
of that pile is a reusable simulation engine and two thirds is delivery machinery for a
SIGGRAPH DAC Shanghai submission that no longer needs the data work it was built around.

Nothing has been pushed since Jul 19. `main` is meant to be production-ready and is
instead the working surface of an abandoned show branch.

## Goals

- `origin/main` becomes reachable ground truth again.
- The engine work — cellular automata sims, the neuron-layout single-owner fix — lands on
  a reviewable branch with clean, small commits.
- Every byte of the Shanghai work stays recoverable, on one branch, buildable.
- Nothing Shanghai-specific reaches `main` or `ca-dev`, including the 7.5 MB of
  verification screenshots.
- A Shanghai-free scene exists on `ca-dev` that actually exercises the CA sims, so the
  engine is not shipped scene-less and untested.

## Non-goals

- Rewriting or squashing history on the archive branch. It is a record, not a deliverable.
- Generalizing `ShanghaiTransect` into a reusable raster seeder. The mechanism is
  reusable, but the decision is deferred until the engine settles.
- Bringing show machinery (`ShowArc`, `CueExporter`, `ShanghaiTransect`) forward at all.
- Merging `ca-dev` into `main`. This spec ends with `ca-dev` and the archive pushed as
  WIP branches; the merge is a later decision.
- Recovering the archived Scene_DAC. C5 builds a fresh scene from 11.2 instead.

## Key finding: `show/` is a leaf

`Assets/Workspace/11.0 Biomes/src/components/show/` imports six core types
(`SimulationManager` ×5, `NeuronFiringSource`, `BiomeInjector`, `FieldSimulationBase`,
`Biome`, `BiomeChannel`). Core imports zero show types. The only mentions of `ShowArc`
inside core are two Inspector tooltip *strings*:

- `src/components/core/FieldSimulationBase.cs:90`
- `src/components/network/BiomeInjector.cs:143`

Both read *"Driven by ShowArc.centreKeepOut."* Dropping `show/` therefore breaks no
compilation — it only leaves two tooltips referencing a class that no longer exists.

## Branch topology

```
origin/main (969ad48)
│
├── main                      reset --hard origin/main. Untouched thereafter.
│
├── ca-dev                    branched from main. Receives the engine work. Pushed as WIP.
│   ├── C1  fix(neuron): spawnScale gets a single owner   [cherry-pick 8ec8768]
│   ├── C2  feat(sim): field-native CA sims                [snapshot]
│   ├── C3  fix(scenes): 11.1 + 11.2 config and debugLog   [snapshot]
│   ├── C4  docs: ADR-0011, CA spec, session logs          [snapshot]
│   └── C5  feat(scene): 11.3 CA sandbox, duplicated from 11.2   [in-Editor]
│
└── archive/dac-shanghai      current main HEAD (3e54469) + one WIP commit
                              carrying the uncommitted and untracked tree.
```

## Approach: hybrid replay + snapshot

Two mechanisms, chosen per commit by whether replay is cheaper than restatement.

**Replay** (`git cherry-pick`) for `8ec8768`. Everything between `origin/main` and it is
docs and specs only, so it applies against a near-pristine tree. It keeps its real message
and arrives as one coherent unit: `NeuronLayout.cs` + `Biomes.Core` asmdef +
`neuron_layout.hlsl` + 143 lines of EditMode tests + the four compute shaders that consume
the shared include. Expect one trivial conflict on `docs/INDEX.md` (a one-line append that
`8f36364` also touched).

**Snapshot** (`git checkout archive/dac-shanghai -- <paths>`) for everything else.
`893460e` is a single 30-file commit mixing engine and show, and three later commits plus
the working tree revise the same files. Replaying it means resolving the same conflicts
repeatedly to reach a state already on disk. Snapshotting takes the final content of each
engine file in one step — which also folds in the `23ec319` `cellular_common.hlsl` fix and
the uncommitted quality pass without separate commits for each.

### What the snapshot inherits from the working tree

The uncommitted changes are a quality pass, not unfinished work, and are kept:

- `SimulationManager.cs` — extracts `ConfigureAndReset(SimulationBase)`, one funnel every
  reset path goes through, so manager-owned settings cannot desync. Motivated by the real
  `neuronSpawnScale` drift across 11.2 and 11.3.
- `FieldSimulationBase.cs` — drops the `BindNeuronPositionsForIgnition` wrapper that only
  forwarded to `BuildNeuronPositions`.
- `LookupCA.compute`, `CyclicCAParams.cs`, `LookupCAParams.cs` — comment and dead-field
  cleanup.

The equivalent cleanups in `CueExporter.cs`, `ShowArc.cs`, `ShanghaiTransect.cs` and
`DacVerify.cs` are archive-only, since those files do not travel.

## File manifest

### C1 — cherry-pick `8ec8768`

Carried by the cherry-pick, listed for verification:

```
Assets/Tests/EditMode/Biomes.Sequencer.Tests.asmdef
Assets/Tests/EditMode/NeuronLayoutTests.cs (+.meta)
Assets/Workspace/11.0 Biomes/src/core_math/            (Biomes.Core.asmdef, NeuronLayout.cs, +metas)
Assets/Workspace/11.0 Biomes/src/computes/includes/neuron_layout.hlsl (+.meta)
Assets/Workspace/11.0 Biomes/src/computes/{BoidSim,PhysarumSim,TermiteSim,SimulationManager}.compute
Assets/Workspace/11.0 Biomes/src/components/core/{SimulationBase,SimulationManager}.cs
Assets/Workspace/11.0 Biomes/src/components/network/{BiomeInjector,NeuronFiringSource}.cs
Assets/Workspace/11.1 CURRENTS Scene/Scene_CURRENTS.unity
Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity
docs/INDEX.md
docs/sessions/2026-08-02-composer-bioform-validation.md
```

### C2 — snapshot: CA engine

New:

```
Assets/Workspace/11.0 Biomes/src/components/Sim/CyclicCASim.cs (+.meta)
Assets/Workspace/11.0 Biomes/src/components/Sim/LookupCASim.cs (+.meta)
Assets/Workspace/11.0 Biomes/src/components/core/FieldSimulationBase.cs (+.meta)
Assets/Workspace/11.0 Biomes/src/computes/CyclicCA.compute (+.meta)
Assets/Workspace/11.0 Biomes/src/computes/LookupCA.compute (+.meta)
Assets/Workspace/11.0 Biomes/src/computes/includes/cellular_common.hlsl (+.meta)
Assets/Workspace/11.0 Biomes/src/params/CyclicCAParams.cs (+.meta)
Assets/Workspace/11.0 Biomes/src/params/LookupCAParams.cs (+.meta)
```

Modified — final content, so the C2 diff is the CA delta on top of C1:

```
Assets/Workspace/11.0 Biomes/src/components/core/Biome.cs
Assets/Workspace/11.0 Biomes/src/components/core/BiomeFieldConfig.cs
Assets/Workspace/11.0 Biomes/src/components/core/SimulationBase.cs
Assets/Workspace/11.0 Biomes/src/components/core/SimulationManager.cs
Assets/Workspace/11.0 Biomes/src/components/network/BiomeInjector.cs
Assets/Workspace/11.0 Biomes/src/computes/Biome.compute
```

Plus the two tooltip rewordings named above, applied by hand in this commit.

### C3 — snapshot: 11.1 and 11.2 scene state

```
Assets/Workspace/11.1 CURRENTS Scene/assets/BiomeFieldConfig_Homeostatic.asset
Assets/Workspace/11.2 SIGGRAPH Scene/assets/BiomeFieldConfig_Homeostatic.asset
Assets/Workspace/11.1 CURRENTS Scene/Scene_CURRENTS.unity
Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity
```

The `.asset` changes are the `23ec319` field addition. The two `.unity` files also appear
in the C1 manifest: the cherry-pick brings `8ec8768`'s one-line edit, and C3 snapshots
them again to pick up the remaining `ef10172` `debugLog` disable. Snapshotting a file C1
already touched is expected — C3's diff is only the delta.

**Gated on the scene-reference check below** — if either scene references a show
component, snapshot only the two `.asset` files and disable `debugLog` by hand in the
Editor.

### C4 — snapshot: docs

```
docs/adr/0011-field-native-sims-derive-simulationbase.md
docs/superpowers/specs/2026-07-23-cellular-automata-sims-design.md
docs/superpowers/specs/2026-08-04-engine-extraction-design.md   (this file)
docs/sessions/2026-08-02-cellular-automata-dac-machinery.md
docs/INDEX.md
.claude/skills/eoc-docs/SKILL.md
```

`README.md` and `docs/ARCHITECTURE.md` were edited only by `4455c6e`, a ShowArc commit.
Review the diffs and port by hand whatever describes the engine; discard whatever
describes the show. Do not snapshot them wholesale.

The MFT extraction specs (`4f11dd8`, `e1f69b1`) are unrelated to Shanghai and come along.
The Scene_DAC spec (`2ebb759`) stays on the archive.

### C5 — new `11.3 SIGGRAPH DAC Scene`, duplicated from 11.2

A fresh CA sandbox. Not recovered from the archive — the archived
`11.3 SIGGRAPH DAC Shanghai Scene` is Shanghai-wired and stays where it is. The two folder
names differ, so they never collide across branches.

Source: `Assets/Workspace/11.2 SIGGRAPH Scene/` as committed on `ca-dev` after C3.
Excluded: `Scene_SIGGRAPH_test.unity`, `ShowSequence.playable`, `signals/`,
`m_temporal_out.mat` — all untracked and archive-only, so they are absent from `ca-dev`
already and require no action.

Target: `Assets/Workspace/11.3 SIGGRAPH DAC Scene/` containing `Scene_DAC.unity` (renamed
from the duplicated `Scene_SIGGRAPH.unity`), plus the duplicated `assets/` and
`materials/` folders.

**This step must happen inside the Unity Editor, not the shell.** Every Unity asset is
identified by the GUID in its `.meta`. A `cp -r` produces two assets claiming one GUID;
Unity resolves the collision on import by reassigning one of them, which silently breaks
whichever references pointed at the reassigned copy. Duplicating via the Project window
(select the folder, ⌘D) makes Unity mint fresh GUIDs *and* rewrite intra-selection
references so the duplicated scene points at the duplicated materials rather than 11.2's.

Procedure inside the Editor:

1. Project window → select `11.2 SIGGRAPH Scene` → ⌘D.
2. Rename the copy to `11.3 SIGGRAPH DAC Scene`.
3. Rename `Scene_SIGGRAPH.unity` inside it to `Scene_DAC.unity`.
4. Open `Scene_DAC.unity`, confirm materials resolve to the 11.3 copies (Inspector should
   show no `(Missing)` and no path pointing back into 11.2).
5. Add `CyclicCASim` and `LookupCASim` components under the SimulationManager, assign
   `CyclicCA.compute` / `LookupCA.compute` and the two params assets.
6. Save, exit Play mode, commit.

Wiring the CA sims into the scene is exploratory work, not mechanical transfer. Commit the
duplicated folder first, then wire, so a broken wiring attempt is one `git checkout` away
from a clean slate.

## Archive-only (never reaches `ca-dev`)

```
Assets/Workspace/11.0 Biomes/src/components/show/          ShowArc, CueExporter, ShanghaiTransect
Assets/Workspace/11.0 Biomes/src/computes/ShanghaiTransect.compute
Assets/Workspace/11.0 Biomes/src/Editor/ShanghaiTransectBaker.cs
Assets/Workspace/11.3 SIGGRAPH DAC Shanghai Scene/         entire folder, incl. DacVerify.cs
Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH_test.unity
Assets/Workspace/11.2 SIGGRAPH Scene/assets/ShowSequence.playable
Assets/Workspace/11.2 SIGGRAPH Scene/assets/signals/
Assets/Workspace/11.2 SIGGRAPH Scene/materials/m_temporal_out.mat
Assets/StreamingAssets/biomes11/shanghai_transect.bytes
tools/encode_dac.sh
docs/DELIVERY_DAC_SHANGHAI.md
docs/verification/2026-08-02-dac-headless/              7.5 MB of PNGs
docs/superpowers/specs/*scene-dac*
```

`docs/verification/` is 7.5 MB against a 13.7 MB pack. Keeping it off `ca-dev` is the
single largest reason not to simply fast-forward main.

## Procedure

Ordered. Each step is verifiable before the next.

**0. Close Unity.** Branch switching with the Editor open makes it rewrite `.meta` files
and reimport against a tree that changed underneath it. Non-negotiable for every step
below.

**1. Archive.**
```
git switch -c archive/dac-shanghai          # dirty tree comes along
git add -A
git commit -m "wip: working tree at engine-extraction handoff"
```
This is also how *this spec* survives the reset. It is currently untracked, so `git add -A`
sweeps it into the WIP commit; step 2 then deletes it from the working tree, and C4
restores it onto `ca-dev` from the archive. Do not move it aside by hand.

Verify: `git status` clean; `git log --oneline | wc -l` shows 33 commits ahead of
`origin/main`.

**1b. Push the archive immediately.**
```
git push -u origin archive/dac-shanghai
```
Before anything destructive runs, not after. This is the step that makes step 2 safe: once
the archive is on the remote, a mistaken `reset --hard` costs a re-clone rather than the
work. Do not proceed to step 2 until this push succeeds.

**2. Reset main.**
```
git switch main
git reset --hard origin/main
```
Verify: `git rev-parse main` equals `git rev-parse origin/main`. `git status` clean.

**3. Branch.**
```
git switch -c ca-dev
```

**4. C1.**
```
git cherry-pick 8ec8768
```
Resolve the `docs/INDEX.md` conflict if it appears. Verify the file list matches the C1
manifest.

**5. Scene-reference check** (gates C3). Unity scenes name no types — a `MonoBehaviour`
appears only as `m_Script: {fileID: 11500000, guid: <32 hex>, type: 3}`. Grepping for
`ShowArc` finds nothing whether or not the scene uses it, so resolve the GUIDs first:

```
for f in ShowArc CueExporter ShanghaiTransect; do
  guid=$(git show "archive/dac-shanghai:Assets/Workspace/11.0 Biomes/src/components/show/$f.cs.meta" \
         | awk '/^guid:/{print $2}')
  echo "== $f $guid"
  grep -l "$guid" \
    "Assets/Workspace/11.1 CURRENTS Scene/Scene_CURRENTS.unity" \
    "Assets/Workspace/11.2 SIGGRAPH Scene/Scene_SIGGRAPH.unity" 2>/dev/null
done
```

Each `==` line with no path under it is a clean component. Any path printed means that
scene embeds a show component: drop the two `.unity` files from C3, snapshot only the
`.asset` files, and disable `debugLog` by hand in step 8.

**6. C2, C3, C4** — snapshot per manifest, one commit each:
```
git checkout archive/dac-shanghai -- <paths>
```
Apply the two tooltip rewordings by hand inside C2 before committing it.

**7. Verification** — see below. Run it before Unity reopens, while the tree is still
purely the product of git operations.

**8. Reopen Unity on `ca-dev`.** Let it reimport. Check the Console for missing-script
warnings and for compile errors in `Biomes.Core` and the CA sims. Run the EditMode suite.

**9. C5** — duplicate 11.2 into 11.3 in the Project window, per the C5 procedure. Commit
the duplicated folder, then wire the CA sims and commit again.

**10. Push `ca-dev`.**
```
git push -u origin ca-dev
```
The archive went up at step 1b. `main` is never pushed — it already matches `origin/main`
exactly, and pushing it would be a no-op.

Steps 0–7 are mechanical and scriptable end to end, and end at a verified `ca-dev` with
four commits. Steps 8–10 need the Editor open and a human reading the Console. The handoff
between them is the natural stopping point for an agent executing this spec.

## Verification

The manifest above is a plan, not the source of truth. These two commands are:

**No Shanghai reached `ca-dev`:**
```
git diff --name-only main..ca-dev \
  | grep -vF '11.3 SIGGRAPH DAC Scene/' \
  | grep -iE 'shanghai|dac|11\.3|verification|encode_dac'
```
Must return nothing. The `grep -vF` exists because C5's new folder legitimately contains
both `DAC` and `11.3` in its name; without the exclusion every file in the CA sandbox is a
false positive. Run this after step 7 (where the folder does not yet exist) and again
after step 9 (where it does).

**No engine work was left behind:**
```
git diff --stat ca-dev archive/dac-shanghai -- \
  'Assets/Workspace/11.0 Biomes/src/components/core' \
  'Assets/Workspace/11.0 Biomes/src/components/Sim' \
  'Assets/Workspace/11.0 Biomes/src/components/network' \
  'Assets/Workspace/11.0 Biomes/src/params' \
  'Assets/Workspace/11.0 Biomes/src/core_math' \
  'Assets/Workspace/11.0 Biomes/src/computes'
```
Must return only `ShanghaiTransect.compute` and its `.meta`. Anything else is a file the
snapshot missed.

**Tests pass:** run the EditMode suite; `NeuronLayoutTests` (143 lines) must be green.

**Repo weight:** `git count-objects -vH` on a fresh clone of `ca-dev` — the pack should be
near the pre-Shanghai size, not 13.7 MB.

**Compilation** is the Unity Console check in step 8. There is no headless compile
available, so this gate is manual.

## Risks

| Risk | Mitigation |
|---|---|
| `reset --hard` on main feels destructive | All 32 commits are on `archive/dac-shanghai` before the reset runs. Step 1 verifies this before step 2 touches anything. |
| Snapshot misses a file, `ca-dev` will not compile | The second verification command derives the miss list rather than trusting the manifest. |
| `Scene_CURRENTS`/`Scene_SIGGRAPH` reference a show component by GUID | Step 5 gates C3. Fall back to `.asset`-only and a hand edit. |
| Untracked `.DS_Store` files get committed into the archive by `git add -A` | Cosmetic, on an archive branch. Ignore, or add to `.gitignore` first. |
| The CA engine was only ever exercised inside Scene_DAC | C5 rebuilds a sandbox from 11.2. Until it is wired, nothing on `ca-dev` instantiates a CA sim. |
| `cp -r` of the 11.2 folder produces duplicate GUIDs | C5 is an in-Editor ⌘D, never a shell copy. Unity mints fresh GUIDs and rewires intra-selection references. |
| C5's folder name trips the Shanghai-detection grep | Verification command excludes `11.3 SIGGRAPH DAC Scene/` by fixed string. |

## Resolved decisions

1. **CA sandbox scene: yes, as C5.** `Assets/Workspace/11.3 SIGGRAPH DAC Scene/`,
   duplicated in-Editor from 11.2, with `Scene_SIGGRAPH.unity` renamed `Scene_DAC.unity`.
   The CA sims get wired there. Distinct from the archived
   `11.3 SIGGRAPH DAC Shanghai Scene`, so the names never collide.
2. **`ShanghaiTransect` stays archived.** Revisitable as a generic `EpochRasterSeeder`
   later; not part of this work.
3. **`ca-dev` and `archive/dac-shanghai` both get pushed.** `main` is not pushed — it
   already equals `origin/main`.

Assumption worth a glance: the new folder drops "Shanghai" from the name but keeps
"SIGGRAPH DAC". Say so if you would rather it were named for what it is — a CA sandbox.

# ECHO — Agent Models Track (CLAUDE.md)

Working log for bringing the 4 agent models (Listener, Tracker, Mimic, Ambusher) into the game,
from raw `.fbx` to a fully working AI agent. Specs live in:
- `ECHO_Complete_Project_Plan.md` — the *what* (§18 agent/visual separation, §22 character pipeline)
- `ECHO_AI_and_Sound_Implementation_Guide.md` — the *how* (§2.2 folders, §4 sound, §7 nav, §8 framework, §9–12 agents)

## Rules to keep (from the docs)
- Agents are **blind**: AI code never looks up the player (no `FindWithTag("Player")`, no `FirstPersonController`).
  Only exceptions: `ContactSense` (touch, `CompareTag`) and `AgentBody` claw hit (physics overlap → `PlayerDeath.Kill`).
- Brain → `AgentMotor` → NavMeshAgent → `AgentAnimatorBridge` → Animator. **The brain never touches the Animator directly.**
- Animator: **Root Motion OFF**. Parameters: `Speed` (float), `Listening` (bool), `Alert`/`Attack`/`Hit` (triggers), `Dead` (bool).
- Speed blend thresholds: 0 idle, 1.2 sneak, 2.2 walk, 5 run.
- Work in a sandbox scene, never edit `SampleScene.unity` while someone else is.
- Commits: `feat:` / `fix:` / `chore:` / `test:` prefixes, small commits.

## Gotchas learned (read before touching agents)
- **Never use `??` / `?.` on Unity objects** (components, GameObjects) — Unity's fake null breaks them. Use `TryGetComponent` / `if (x == null)`.
- **LayerMask saved in prefabs keeps only *named* layers.** Floors + player in SampleScene sit on **unnamed layer 8**, so a mask like
  `~Agents` silently drops the player. Agent claw mask = Everything (`~0`); the body skips its own colliders.
- **Prefab overrides stick**: if a scene instance was saved with an old value, rebuilding the prefab won't fix it — rebuild the scene
  (or Revert in the Inspector).
- Layers added: **9 = Environment** (walls/floors that block sound), **10 = Agents**. Slot 8 untouched on purpose.
- No `Echo.Runtime.asmdef` yet: all scripts are in Assembly-CSharp (teammates' code is too). Adding an asmdef later needs refs to
  StarterAssets, InputSystem, AI.Navigation, and an Editor asmdef for `Scripts/Editor`.
- Teammates' `InteractionNoise` binds to `Echo.Sound.Noise.Make` by reflection — keep that signature frozen
  `(SoundType, Vector3, float, SoundSource, GameObject, bool)`. Door.cs calls `NavGraph.OnDoorChanged` by reflection too.
- The Unity Editor only advances Play-mode time while it has focus (runInBackground off).

## Folder layout for models
```
Assets/Art/Characters/<AgentName>/
    Model/       <model>.fbx
    Materials/   (extract here if you need to edit materials)
    Textures/
    Animations/  <AgentName>.controller   (generated)
Assets/Prefabs/Agents/<AgentName>.prefab  (generated)
Assets/Scenes/Sandbox/Sandbox_<AgentName>.unity (generated)
```

## Agent model status

| Agent | FBX | Import + clips | Animator | Prefab | Sandbox | Moves (A* + NavMesh) | AI brain | Tested in play |
|---|---|---|---|---|---|---|---|---|
| **Listener** (`ECHO_Creature.fbx`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ (scripted) — needs hands-on play by you |
| **Tracker** (`Demon_animated.fbx`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ teammate's core + my adapter | ✅ (scripted ghost trail) — needs hands-on play |
| Mimic | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| **Ambusher** (Zombie Monster, Models/2) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ RouteModel + AmbusherBrain (mine, guide §12) | ✅ (scripted ghost laps) — needs hands-on play |

## Shared framework status (written 2026-10-14, guide §4/§7/§8)
- Sound (`Scripts/Sound`): SoundType, SoundEvent/PerceivedSound, SoundBus, Acoustics, Hearing, Noise, SoundLibrary, AudioService, SoundDebug ✅
- Navigation (`Scripts/Navigation`): NavNode, IGraph/Edge, NavGraph (Floyd–Warshall sound distances), ZoneVolume, AStar ✅; `Core/MinHeap` ✅
- AI common (`Scripts/AI/Common`): AgentRegistry (in Core), HabituationMemory, AgentMotor, AgentBrain, AgentBody, ContactSense,
  AgentAnimatorBridge, SearchPattern, AgentDebugOverlay, AgentFootsteps ✅
- Editor: `NavGraphTools` (ECHO ▸ Nav Graph ▸ Auto-Link Nodes), `AgentAssetBuilder`, `ListenerSandboxBuilder` ✅
- Audio ✅ (2026-10-14): `Assets/Audio/{SFX,Agents,Ambience,Music}` (83 clips), `EchoSoundLibrary.asset` (14/15 types — **Jump missing**),
  `AudioOcclusion`, `TensionMusic` (calm/hunt crossfade by AlertLevel, no mixer), `EchoAudioImporter` (auto import settings),
  ECHO ▸ Audio ▸ Build Sound Library. Cutting script: `Tools/Audio/import_sounds.py` (needs `pip install soundfile numpy scipy`).
- Not yet: `EchoMixer.mixer` + snapshots (must be made by hand in the Audio Mixer window; then assign groups in the library),
  reverb zones, NavMesh/NavGraph/Zones/Systems in **SampleScene**, EditMode unit tests (need an asmdef first),
  `CREDITS.md` sources/licences (TODO for the owner).

## Step plan per agent
1. Folder + move FBX into `Assets/Art/Characters/<Name>/Model/`
2. Add an `AgentSpec` in `AgentAssetBuilder.cs` (clip names, sensitivity, brain type) + a menu item
3. Run **ECHO ▸ Agents ▸ <Name> ▸ 1. Build Model, Animator and Prefab**
4. Sandbox scene (copy `ListenerSandboxBuilder` or drop the prefab into `Sandbox_Listener`)
5. Play-test: sounds → reaction → search → return; touch/close sound → attack; shooting it → hurt/death
6. Brain per guide §10–12; tune with F1 overlay
7. Integrate into SampleScene (needs NavMesh, NavGraph nodes, Zones, Environment layer on walls there)

---

## Log
### 2026-10-14 — Session 1: inspection
- Read both design docs. Created this CLAUDE.md.
- `ECHO_Creature.fbx` was in two places (Scenes/ and Starter Assets/…/FirstPersonController/). Rig `ECHO_Rig`, 56 bones,
  12 skinned meshes, ≈ 1.19 × 2.18 × 0.77 m, materials Skin/Keratin/Eye/MouthInterior,
  clips Idle 3.2s, Walk 1.07s, Run 0.67s, Crawl 1.2s, Attack 1.47s, HitReact 0.87s, Death 2.13s, Scream 2.4s, Twitch 1.6s.

### 2026-10-14 — Session 1: Listener, end to end
- **Step 1 (user):** created `Assets/Art/Characters/Listener/{Model,Materials,Textures,Animations}` and put the FBX in `Model/`.
  Cleaned up: deleted the leftover `Assets/Scenes/ECHO_Creature.fbx`, the stray `ECHO_Torso.controller`, an orphan `Charaters.meta`;
  the Starter Assets copy had been deleted by the user.
- Checked the model: faces +Z, feet at y = 0, clips are **in place** (no root motion) → perfect for NavMeshAgent driving.
- Checked team code first: no sound core / framework existed anywhere (Tracker branch keeps its own types in `Echo.AI.Tracker`, no clashes).
- **Wrote the shared framework** (see status above) from guide §4, §7, §8, with fixes:
  - `NavGraph` builds in `Start` (NavMesh loaded by then); edge length never below straight line (A* stays admissible).
  - `AgentMotor`: works without a NavGraph (plain NavMesh fallback), `Warp()` for resets, `LookAt()` turns every frame
    (guide's FaceTowards from the 5 Hz brain only turned ~4°/tick).
  - `AgentBody`: hits the player through `PlayerDeath.Kill` (team uses one-hit death, not HP); `IsAttacking`; windup 0.5 s.
  - `AgentAnimatorBridge`: skips missing parameters, adds `Alert` trigger, forces root motion off.
  - `AgentFootsteps`: stride-based agent footsteps (AgentBody source → player hears, agents ignore).
- **Wrote the Listener** (`Scripts/AI/Listener`): `ListenerPriority` (guide §9.2 + Jump/Reload weights, `ShouldSwitch`) and
  `ListenerBrain` (guide §9.3 FSM) + extra **Alert** state: loud sound while calm → stop, face it, Scream anim + voice, then Run.
  Fixed: stale motor Arrived/Failed carried into a new state (cleared in `Enter`); no re-targeting mid-swing.
- **Generated assets** with ECHO ▸ Agents ▸ Listener ▸ 1:
  - FBX clips renamed (Idle, Walk, Run, Crawl, Attack, HitReact, Death, Scream, Twitch), Idle/Walk/Run/Crawl/Twitch looped.
  - `Listener.controller`: Locomotion blend (Idle 0, **Crawl 1.2 = sneak**, Walk 2.2, Run 5), Listen = Twitch, Alert = Scream,
    Attack, Hit = HitReact, Death; any-state triggers blocked while Dead.
  - `Listener.prefab`: NavMeshAgent (r 0.4, h 2, avoidance 40), kinematic RB, capsule, Hearing 1.15, motor, body, brain,
    footsteps, bridge; child `Model` (layer Agents); child `ContactSense` (r 1.3, Ignore Raycast).
- **Sandbox** with ECHO ▸ Agents ▸ Listener ▸ 2 → `Sandbox_Listener.unity`: two 15×20 m rooms, divider wall with one doorway,
  4 crates, all on Environment; NavMesh baked (`Sandbox_Listener_NavMesh.asset`); 20 nodes auto-linked (all connected);
  zones WestRoom/EastRoom; Systems (AudioService, SoundDebug, AgentDebugOverlay); player copied from Sandbox_Interaction.
- **Play tests (scripted, all passed):**
  1. Gunshot in the other room → heard *through the doorway* (p 0.22) → Investigate (walk 2.2 m/s, Speed param 2.2) via the door.
  2. Gunshot in its own room → Alert (scream) → Run → Search 4 points with Listen pose → after 20 s Return → Idle at home, facing home.
  3. Gunshot at the player → walked over, estimate was ~4 m off → searched → **touched the player → Attack → player killed**, scene reloaded.
- Bugs hit & fixed: `??` on a Unity component (builder crash); claw LayerMask dropped unnamed layer 8 (player unhittable);
  stale prefab override in the scene (fixed by rebuilding the scene).
- User played the sandbox by hand: "It's fine".

### 2026-10-14 — Session 1: game audio
- Source: `D:\Game Sounds and Models` — 30 hand-picked clips (wav/mp3/flac; a Sonniss GDC 2026 part-5 folder was there
  briefly but is not used). Unity can't import FLAC and several clips were "raw" takes with many hits.
- `Tools/Audio/import_sounds.py` (run in a private venv with soundfile/numpy/scipy): onset detection → single hits, trim,
  fades, peak-normalise, 96 kHz → 48 kHz, mono for 3D, crossfaded loops. Output 83 WAVs, ~list in `Assets/Audio/CREDITS.md`.
  Notable: "jump land concrete raw" is really running steps → Run; "heavy breathing monster" growls → AgentVoice;
  metal-grate steps → Listener's own heavy feet.
- Unity: `EchoAudioImporter` (SFX/Agents mono + Decompress On Load, loops/ambience/music Streaming, Vorbis 70 %);
  `SoundLibraryBuilder` fills `Assets/Audio/EchoSoundLibrary.asset` from folder names (folder = SoundType); volumes/distances in code.
- Listener prefab now has `Voice` (breathing loop, 12 m, linear rolloff) and `Feet` (own step clips, pitch 0.8–0.95) children,
  both with `AudioOcclusion` (muffled behind Environment walls). `AgentFootsteps` plays own clips and still emits the AI event.
- Sandbox: `AudioService.library` assigned, `TensionMusic` (DarkDrone calm / HorrorDrone hunt), 5 ambience loops (audio only,
  never SoundEvents).
- Verified in play: gunshot/door/glass/footstep/voice play through the pool; ambience + music running; Listener breathing
  drops 0.7 → 0.42 when the wall is between it and the player (occlusion works).
- Gotcha: when Unity is not the focused window, Play mode advances only one frame per MCP call. For scripted tests turn on
  ECHO ▸ Dev ▸ Run In Background (Play Mode), or test things that happen within one frame.
- **Next:** fill CREDITS.md sources; find a Jump clip; then the next agent model.

### 2026-10-14 — Session 1: Tracker
- User put `Demon_animated.fbx` in `Assets/Art/Characters/Tracker/Model/`.
- Model facts: Generic rig (48 deform bones incl. IK helpers — deform bones ARE baked, so it animates in Unity),
  exported **5.95 m tall** (→ `ModelScale 0.4` = 2.38 m), faces +Z, in-place clips, 25 fps, most clips padded to 4.84 s.
  Clips: Idle1-3, Walk1-2, Run1, Punch1-3, Get-damage, Death, Telepathic(+loop), Spasm, Throw(+loop/catch), Jump-long/short,
  Come-out1/2, Turn-left/right, Shoot1/2, `@Pose_A` (empty — harmless import warning). Extra `Ball-mesh` (throw prop, own bone) → hidden.
- **No textures** in either FBX (Listener too): materials import as flat light grey. → art TODO (export textures from Blender).
- Brain: the Tracker core already existed on teammate **Gayuth-W's** branch `feat/tracker-agent` (`Echo.AI.Tracker`: TrackerDecider,
  memory + route-based credibility, weighted-LS velocity, InterceptScorer, ConfidenceModel, ColdTrailSearch). Copied those files
  **unchanged** into the working tree (`git checkout origin/feat/tracker-agent -- …`), incl. their `Assets/Scripts/AI.meta`
  (replaced mine so the folder GUIDs match → no merge conflict later). Their `ECHO > Tracker > Run Core Checks`: **all passed** here.
- My glue (separate folder `Assets/Scripts/AI/TrackerAgent/`, easy to hand over):
  `NavGraphTrackerWorld` (ITrackerWorld over NavGraph; door states; `NavGraph.Version` added for route-cache invalidation),
  `TrackerBrain : AgentBrain` (PerceivedSound → TrackerPercept, touch + being shot → percepts, executes MoveTo/Stop/Attack,
  Listening pose in Analyse/Hold, "Telepathic" Alert when it locks on from calm, LookAt predicted position, gizmos).
  `AgentClicks` (signature clicks, faster with AlertLevel). Tracker clips: Idle1 / Walk2 (sneak) / Walk1 / Run1,
  Listen = Telepathic-loop, Alert = Telepathic (1.2 s), Attack = Punch1 (1.4 s), Hit = Get-damage (0.8 s), Death.
- `AgentAssetBuilder` now has per-spec `ModelScale`, `HiddenMeshes`, one-shot durations (state speed = clip length / seconds),
  click clips; `ListenerSandboxBuilder.Build(spec, scenePath)` builds the same two-room sandbox for any agent →
  `Sandbox_Tracker.unity`. Menus: ECHO ▸ Agents ▸ Tracker ▸ 1 / 2.
- New dev tool `DevTools/SoundTrailProbe` — invisible ghost that walks a route and emits real player footsteps.
- **Play test (ghost runs S wall → W wall → N wall at 3 m/s):** 17 steps accepted, 0 suspect/filtered, v ≈ 2.6 m/s,
  confidence 0.58 (Run band) → Intercept → **waited at the NW corner before the ghost arrived** = prediction works.
  No strike on the ghost: it has no body (no touch) and close steps are localised ±1.9 m > `strikeMaxError` 1.5 — fine for a
  real player (touch), but a tuning knob to discuss with the Tracker owner.
- ⚠ Coordination: the Tracker logic belongs to Gayuth-W (M2). Tell them about `AI/TrackerAgent/` before they write their own body.
- **Next:** user plays `Sandbox_Tracker` by hand; textures for both models; then Mimic / Ambusher model.

### 2026-10-14 — Session 1: textures + look ("agents have no skin colour, graphics not good")
- Diagnosis: Listener skin was a **procedural** Blender material (noise + vertex-colour mask `ECHO_Mask` + bump) and the
  low-poly meshes had **no UVs** → FBX can't carry it. Tracker's .blend pointed at missing `tex
etop0_*.png` files.
- **Listener source:** user says `ECHO_stage7_backup.blend` is the latest look, but it has **no actions**; `Untitled.blend`
  (15:05, newest) = stage7 mesh/material + all 9 actions → used a copy of it. Originals untouched.
- **Listener bake** (Blender 5.2 background, `blender.exe -b`, script: `Tools/Blender/bake_listener.py`, stages prep/normal/color/rough/export):
  `ECHO_HighPoly` collection is *excluded* from the view layer in the file (script re-includes it); rest pose; multi-object
  Smart UV Project + pack → one 2048 atlas for the 8 skin pieces; HP→LP selected-to-active bakes (cage extrusion 0.02,
  max ray 0.08, margin 6): NORMAL (OpenGL), base colour + roughness via an Emission swap of the BSDF inputs.
  Outputs: `Models/4/ECHO_stage8_textured.blend` (new), `Models/4/Exports/ECHO_Creature/ECHO_Creature_Textured.fbx` + `Textures/`.
  Gotchas: enabling every Cycles device (incl. AMD iGPU) crashed → one NVIDIA (OptiX, RTX 2050) only; **PC has 7.2 GB RAM** —
  EMIT bakes ran out of memory until only the current LP/HP pair is renderable per bake. Each ~15 s.
  Known small artifact: rainbow speckles on tiny islands (claw tips) in the normal map.
- Unity: textured FBX copied over `Listener/Model/ECHO_Creature.fbx` (same GUID); `Listener_MetallicSmoothness.png` packed from
  roughness (metal 0, smoothness = 1 − roughness).
- **Tracker textures:** user added `Models/1/Demon_tex` (+ `demonblind.unitypackage` = Asset Store "Demon Horror Creature with
  Weapon"; NOT imported, only its textures used). Resized 4096 → 2048 (repo size), packed MetallicSmoothness, AO.
  Default set → `Tracker_Skin`, damaged set → `Tracker_Skin_Damaged`.
- New `AgentMaterialBuilder` (URP Lit from `<Agent>/Textures/<Prefix>_BaseColor/_Normal/_MetallicSmoothness`, + AO, tint,
  emission) + FBX material **remaps** (`ModelImporter.AddRemap`) — all driven from `AgentSpec.Materials` in `AgentAssetBuilder`.
  Listener: Listener_Skin (normal ×1.6), Listener_Teeth (ivory), Listener_Eye (black, faint red glow).
  New `AgentDamageLook`: swaps to the damaged material at ≤ 50 % health (Tracker).
- **Sandbox look** now matches SampleScene: Horror materials (tiled copies in `Assets/Art/Materials/Sandbox/`), exp² fog
  0.028, dark flat ambient, NightLabSky, Global Volume with the team's `EchoHorrorProfile`, cold/green ceiling lamps + red
  emergency lamp at the door, faint directional, player camera post-processing on + the same Flashlight as SampleScene.
- Screenshots: positioned (temporary camera) captures come out **black** here; use Scene-view captures. A **cyan** model in the
  editor = shader variant still compiling (wait a few seconds), not a broken material.

### 2026-10-14 — Session 1: Ambusher (user = Member 4, owns this logic)
- Model: `Models/2/Zombie+Monster+package` (Unity version inside; its `Library/` makes the folder ~16k files — never `find` it whole).
  Copied base mesh → `Ambusher/Model/Ambusher_Zombie.fbx`, 14 clip FBXs → `Ambusher/Animations/Ambusher@<clip>.fbx`.
  Generic rig (Maya HumanIK `Character1_*`), file scale 0.01, 3.0 m tall → `ModelScale 0.8`, faces +Z, all clips in place.
  14 SkinnedMeshRenderers, 6 materials: Mat_Body, **Mat_Body1 = legs**, Mat_Armor, Mat_Mask, Mat_Belt_Bolts, Mat_Weapon (spiked club).
  Textures 4096 TGA → PNG 2048 (Belt/Mask/Weapon 1024); Metallic TGA already has gloss in alpha (= URP MetallicSmoothness);
  emission maps are all black → skipped. 67 MB.
  Clips: Idle idol_breathing, Sneak/Walk walking, Run running, Listen **searching**, Alert scream (when shot), Attack attack (1.45 s),
  Hit hit, Death deth. Also: idol, idol2, muscules, attack1, deth2, taking_damage.
- `AgentAssetBuilder`: `AnimationFolder/AnimationPrefix` (one clip per "Model@clip.fbx"), `StepPitch`, `BreathingOnlyWhileWaiting`.
  Ambusher: hearing 1.4, damage 50, avoidance 55, steps = Listener metal steps pitched 0.55–0.68, breathing loop 4 m only in Wait
  (`AgentWaitAudio`, uses mute so `AudioOcclusion` keeps owning volume).
- Logic (`Scripts/AI/Ambusher/`): `RouteModel` (guide §12.2 + `Evidence(zone)`, `Decay(t)` public, `TopTransitions`),
  `AmbusherBrain` (guide §12.5) with fixes: lazy init when NavGraph is built; LookAt every frame while waiting; clean lunge-then-swing
  attack; reflex strike ≤ 2.2 m in any state; roars + backs off when shot; **`minEvidence 1.8`** (guide's 0.35 confidence alone is
  passed by the uniform prior when a zone has 2–3 neighbours → it committed after 1 sound); **decision log** (`logDecisions`) with
  reasons. Mimic API kept: `HasAmbushPoint`, `IsWaiting`, `AmbushPoint`. `AcousticSensor` (guide §12.6) ready for SampleScene.
- `ECHO > Ambusher > Run Core Checks`: 11 RouteModel checks (prior, adjacency, learning, normalisation, prediction, threshold,
  e^-1 forgetting, habit change, clear) — **all pass**.
- Sandbox builder: **9 zones** (West/East × SW/SE/NW/NE + Doorway, non-overlapping), corner nodes = AmbushSpot, room centres =
  Junction+Hub, doorway = Doorway. **Bug fixed:** AutoLink ran before zones existed → every node was "Unzoned" (affected all sandboxes).
  `Sandbox_Ambusher.unity` built.
- **Play test** (ghost runs laps of the west room, 3.5 m/s): learns in ~3 laps, then
  `t=33.1 Observe→MoveToAmbush: predicted route from West_SW, conf 0.77 → Node_08 (West_NE)` → `Wait` → ghost 3.4 m away → `Attack`
  → `Observe` → next lap ambushes `Node_06 (West_SE)` (novelty: a different corner).
- **Next:** user plays `Sandbox_Ambusher` (walk the same loop 3×, then change route → it should relocate); credits for the Zombie
  Monster package; Mimic model; SampleScene integration.

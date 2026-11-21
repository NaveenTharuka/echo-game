# ECHO

*The enemy cannot see you. It can hear you.*

A first-person horror shooter set in an abandoned underground research facility. Four blind creatures hunt
the player by **sound only**. Every step, door, gunshot, and thrown bottle is a sound event that they hear,
reason about, and path-find towards.

SLIIT Year 3 group project: **SE3032 Graphics & Visualization** + **SE3062 Intelligent Systems**.

## Run it

1. Unity **6000.3.8f1** (URP 17.3, Input System, AI Navigation 2.0).
2. Open `Assets/Scenes/SampleScene.unity` and press Play.

### Goal
Get the **medicine** from the locked Lab and get back out alive. The objective panel (top right) shows the
current task with an arrow, the distance, and whether it is up or down a floor:

1. Find the Lab (the sealed bulkhead, Containment wing).
2. Restore power to the Lab door (fusebox, warehouse west wall). The generator is loud.
3. Find the key card (Key room, below the Lab stairs).
4. Open the Lab door and take the medicine. That trips an alarm every creature hears.
5. Get out the way you came in.

### Controls
| Key | Action |
|---|---|
| WASD / mouse | Move / look |
| Shift | Sprint (stamina, loud) |
| C (toggle) / Left Ctrl (hold) | Crouch (slow, quiet) |
| Space | Jump |
| E | Interact / pick up |
| Left mouse | Shoot |
| Right mouse | Throw the held object (a decoy noise) |
| L | Flashlight |

Debug: **F1** agent states (F2/F3/F4 time scale), **F5** each agent's hearing area on the floor,
**F6** Tracker reasoning panel, **F9** god mode (editor only).

## The four agents (Intelligent Systems)

All agents are blind. AI code never reads the player's position. It only gets noisy `PerceivedSound`s from
its `Hearing` component (position error grows as the sound gets fainter), plus touch (`ContactSense`).

| Agent | Idea | Core technique | Code |
|---|---|---|---|
| **Listener** | Investigates sounds, follows footstep trails, searches where the prey would flee | Priority-scored FSM, hysteresis, escape-aware search over doorway nodes | `Assets/Scripts/AI/Listener` |
| **Tracker** | Remembers the sound trail and intercepts where the player is going | Weighted least-squares velocity, route-based credibility, intercept scoring over A* ETAs | `Assets/Scripts/AI/Tracker` |
| **Mimic** | Fakes sounds to lure the other agents onto the player | Forward model of what each hunter would hear and do (utility over fake sound type and place) | `Assets/Scripts/AI/Mimic` |
| **Ambusher** | Learns the player's routes and waits on the predicted one | Zone-transition Markov model with exponential forgetting, ambush-spot selection | `Assets/Scripts/AI/Ambusher` |

Shared framework:
- `Scripts/Sound`: SoundBus, Hearing, Acoustics. Sound travels around walls along the nav graph, using Floyd–Warshall distances.
- `Scripts/Navigation`: NavGraph, A*, zones, doors.
- `Scripts/AI/Common`: AgentBrain, AgentMotor, AgentBody, patrols, debug overlays.

Each brain drives the body like this: brain → `AgentMotor` → NavMeshAgent → `AgentAnimatorBridge` → Animator.
The brain never touches the Animator, and root motion is off.

## Graphics & Visualization

- **Level** (`SampleScene`): four floors (−30 m to 0 m), baked NavMesh, horror lighting and post-processing (fog, `EchoHorrorProfile` volume).
- **Agent models**: textures baked in Blender from procedural materials, plus custom procedural animation clips.
  - Scripts: `Tools/Blender`.
  - Build pipeline: **ECHO ▸ Agents ▸ ... ▸ Build Model, Animator and Prefab** (`Scripts/Editor/AgentAssetBuilder.cs`).
- **Agent movement**: speed-matched locomotion blend trees (no foot sliding), slowing down while turning, chase lunges.
- **Player interaction physics**: doors, barricades, carry and throw (`Scripts/Interaction`).
- **Audio**: wall/doorway occlusion, distance filtering, room reverb from zone size.
  - Clip processing scripts: `Tools/Audio`.

## Project layout

```
Assets/Scripts/      AI, Sound, Navigation, Player, Interaction, Gameplay, UI, Editor, DevTools
Assets/Art/          Characters/<Agent>/{Model,Textures,Animations}, materials
Assets/Prefabs/      Agents/<Agent>.prefab
Assets/Scenes/       SampleScene (the game) + Sandbox/ test scenes, one per agent
Assets/Audio/        SFX, Agents, Ambience, Music (+ CREDITS.md)
Docs/Map/            Floor plans (ECHO_floor_plan_v5.png = objectives + agent territories)
Tools/               Blender, audio and map scripts
```

## Tests

- Play-mode self-tests (start them in Play mode from a script or the console):
  - `DevTools/Phase1SelfTest`: body, chase, crouch, stairs.
  - `DevTools/Phase2SelfTest`: patrols, full objective chain.
- Core checks: **ECHO ▸ Tracker ▸ Run Core Checks**, **ECHO ▸ Ambusher ▸ Run Core Checks**.

## Team

| Member | Graphics role | Agent |
|---|---|---|
| Maheesh09 | Agent Controller | Ambusher |
| Gayuth-W | | Tracker |
| NaveenTharuka | | |
| SihanUdayaratna03 | | |

Design docs: `ECHO_Complete_Project_Plan.md`, `ECHO_AI_and_Sound_Implementation_Guide.md`.

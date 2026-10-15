# ECHO — AI & Sound Implementation Guide

> **Companion to:** `ECHO_Complete_Project_Plan.md` (the *what*). This document is the *how*.
> **Covers:** the sound system (what agents perceive **and** what the player hears), navigation and A*, the shared agent framework, and the full intelligent programming of **Listener, Tracker, Mimic and Ambusher**.
> **Project facts this guide is written against:** Unity 6000.3, URP, Starter Assets First Person Controller (`StarterAssets` namespace, own `Unity.StarterAssets` assembly), AI Navigation package 2.0.10 and Test Framework already installed, level = `SampleScene` (see `Docs/Map/ECHO_floor_plan_v2.png`).
> **About the code:** every snippet is a complete, reviewed starting point written for this project's APIs, but it has **not yet been compiled or play-tested in the project**. Expect small fixes when you paste it in, and tune every number in §19 by playing.

---

## Table of contents

0. [Read this first — the 12 decisions that shape everything](#0-read-this-first--the-12-decisions-that-shape-everything)
1. [The golden rule: the agents are blind](#1-the-golden-rule-the-agents-are-blind)
2. [Architecture overview](#2-architecture-overview)
3. [Unity setup, step by step](#3-unity-setup-step-by-step)
4. [Sound system A — sound as information (what agents perceive)](#4-sound-system-a--sound-as-information-what-agents-perceive)
5. [Sound system B — sound as audio (what the player hears)](#5-sound-system-b--sound-as-audio-what-the-player-hears)
6. [Connecting the player and the world to the sound system](#6-connecting-the-player-and-the-world-to-the-sound-system)
7. [Navigation: NavMesh + waypoint graph + your own A*](#7-navigation-navmesh--waypoint-graph--your-own-a)
8. [The shared agent framework](#8-the-shared-agent-framework)
9. [Agent 1 — LISTENER](#9-agent-1--listener)
10. [Agent 2 — TRACKER](#10-agent-2--tracker)
11. [Agent 3 — MIMIC](#11-agent-3--mimic)
12. [Agent 4 — AMBUSHER](#12-agent-4--ambusher)
13. [How the four agents interact](#13-how-the-four-agents-interact)
14. [Combat, health, death and reset](#14-combat-health-death-and-reset)
15. [Debugging and visualisation tools](#15-debugging-and-visualisation-tools)
16. [Testing](#16-testing)
17. [Performance budget](#17-performance-budget)
18. [Team workflow: who builds what, in which order](#18-team-workflow-who-builds-what-in-which-order)
19. [Master tuning table](#19-master-tuning-table)
20. [Edge-case checklist](#20-edge-case-checklist)
21. [Viva cheat-sheet](#21-viva-cheat-sheet)

---

# 0. Read this first — the 12 decisions that shape everything

| # | Decision | Why |
|---|---|---|
| 1 | **Agents never read the player's Transform for decisions.** Their only knowledge of the player comes from **sounds** and **touch** (a 1.3 m contact sensor). | It is the game's premise ("it cannot see you") and it is what makes the AI interesting. Enforced by architecture, not by good intentions (§1). |
| 2 | **One call makes a noise:** `Noise.Make(...)`. It plays the audio clip **and** publishes a `SoundEvent`. | The player and the AI always experience the *same* sound. A fake sound from the Mimic fools both. |
| 3 | **Sound travels through corridors, not through walls.** Effective distance = straight line if unobstructed, otherwise the shortest route along the waypoint graph. | Cheap, believable, and explainable. No acoustic simulation. |
| 4 | **Agents hear an approximate position**, not the exact one. Error grows as the sound gets fainter. | Blind hunters must *search*. This is what makes Search behaviour, prediction and deception meaningful. |
| 5 | **Two-layer navigation:** Unity NavMesh for walking (local steering, collision), **your own A\*** over a hand-placed waypoint graph for planning. | The IS module wants A* you can explain, tune and justify. A custom graph also gives you *zones* (rooms) to reason about. |
| 6 | **Every agent = Perception → Memory → Decision → Action**, running on a **think tick** (5 Hz) while movement runs every frame. | Matches the plan's architecture; keeps CPU cost low; easy to explain. |
| 7 | **FSM for behaviour states + utility scoring for choices** inside states. | FSMs are readable and debuggable; scoring is where each agent's "intelligence" lives. |
| 8 | **Each agent has a different decision core:** Listener = priority + hysteresis; Tracker = weighted regression + candidate scoring + credibility; Mimic = utility AI over deception actions with a model of other agents; Ambusher = Markov route model + interception feasibility. | Four genuinely different algorithms — not four skins on one chase script. |
| 9 | **The brain never touches the Animator.** Brain → Motor (movement) → AnimatorBridge reads velocity. | Clean separation required by the plan (§18 there). |
| 10 | **Algorithms live in plain C# classes** (no MonoBehaviour) so they can be unit-tested. | Testable, defensible in the viva, and reusable. |
| 11 | **Everything is visualised** (sound rings, paths, predictions, heatmaps, state labels). | You will not be able to tune or demo blind AI without seeing what it believes. |
| 12 | **Shared code is frozen early (Day 4).** After that, each member works in their own folder and their own sandbox scene. | Prevents merge conflicts and lets four people work in parallel. |

---

# 1. The golden rule: the agents are blind

## 1.1 What an agent is allowed to know

| Information | Allowed? | How it gets it |
|---|---|---|
| The facility layout (where floors, doors and rooms are) | ✅ | NavMesh + waypoint graph. Justification: the creatures *live* here; this is memory of the building, not vision. |
| Sounds it can hear (type, approximate position, loudness, time) | ✅ | `Hearing` component → `PerceivedSound` |
| That something is touching it / brushing past it | ✅ | `ContactSense` trigger (radius ≈ 1.3 m) |
| That it has been hit (and roughly from where) | ✅ | `TakeDamage(amount, fromPosition)` — the gunshot is also a sound anyway |
| Where other agents are and what they are doing | ⚠️ Only the Mimic, via `AgentRegistry` | Justification: the Mimic is the "orchestrator" of the hive. Everyone else ignores the registry. |
| The player's exact position | ❌ **Never** for decisions | — |
| Whether a sound was real or fake | ❌ | `SoundEvent.Source` exists for debugging/audio only; agents receive `PerceivedSound`, which has no source field |

## 1.2 How to enforce it (do this, it will come up in the viva)

1. Agent code receives `PerceivedSound`, a struct **without** the emitter or source. The richer `SoundEvent` stays inside the sound system.
2. Nothing under `Scripts/AI/` may contain `GameObject.FindWithTag("Player")`, `FindFirstObjectByType<FirstPersonController>()`, or a serialized reference to the player. Add this to your code-review checklist.
3. The **only** place AI code touches the player object is hit resolution in `AgentBody.AttackRoutine` — a physics overlap at the claws *after* the brain has already decided to attack based on sound/touch. That is physics, not perception.
4. Optional automated guard (Edit Mode test): scan `Assets/Scripts/AI/**/*.cs` and fail if it finds `"Player"` tag lookups.

```csharp
// Assets/Tests/EditMode/BlindnessGuardTests.cs
using System.IO;
using NUnit.Framework;

public class BlindnessGuardTests
{
    [Test]
    public void AiCodeNeverLooksUpThePlayer()
    {
        foreach (var file in Directory.GetFiles("Assets/Scripts/AI", "*.cs", SearchOption.AllDirectories))
        {
            string code = File.ReadAllText(file);
            Assert.IsFalse(code.Contains("FindWithTag(\"Player\")"), file);
            Assert.IsFalse(code.Contains("FirstPersonController"), file);
        }
    }
}
```

## 1.3 Consequences you must design around

- **Silence is safety.** A player who stands still emits nothing, so agents must *search* and *predict*. This is intended.
- **Attacks are triggered by sound or touch.** An agent strikes when it hears a sound very close (≤ ~2 m, fresh, precise) or when the contact sensor fires.
- **Agents can be wrong.** They go to *estimated* positions, they get fooled by the Mimic, and the Tracker can mispredict. Being wrong in a believable way is the goal, not a bug.

---

# 2. Architecture overview

## 2.1 Runtime data flow

```text
  PLAYER / WORLD                      SOUND SYSTEM                         AGENTS
 ───────────────                ───────────────────────            ─────────────────────────
 PlayerNoise (footsteps,         Noise.Make(type,pos,i,src)
   landing)        ─────────►        │
 Weapon (gunshot, impact)            ├──► AudioService.Play()  ───►  (player hears clip)
 Door, NoisyPhysicsObject,           │
 Generator, Switch                   └──► SoundBus.Emit(SoundEvent)
 Mimic (fake sounds)                          │
                                              ├──► Emitted event ──► SoundDebug, NoiseMeter HUD
                                              │
                                              └──► every IHearer.Hear(e)
                                                        │
                                          Hearing: effective distance (Acoustics)
                                                   loudness, threshold
                                                   position error  ──►  inbox (PerceivedSound)
                                                                              │ (drained on think tick)
                                                                              ▼
                                             AgentBrain.Think()  ── Listener / Tracker / Mimic / Ambusher
                                                   │ memory, scoring, FSM
                                                   ▼
                                             AgentMotor.MoveTo(goal, gait)
                                                   │ A* over NavGraph → node path
                                                   ▼
                                             NavMeshAgent (local steering)  ──►  AgentAnimatorBridge ──► Animator
```

## 2.2 Folder layout

```text
Assets/
├── Audio/
│   ├── SFX/            (footsteps, gun, doors, impacts, glass, machinery)
│   ├── Agents/         (breathing, clicks, growls, signature loops)
│   ├── Ambience/       (hum, drips, vents, distant metal)
│   ├── Music/          (tension layers)
│   └── EchoMixer.mixer
├── Prefabs/
│   ├── Agents/         (Listener, Tracker, Mimic, Ambusher)
│   ├── Interactables/  (Door, ThrowableCan, Bottle, Generator, Switch)
│   └── Systems/        (Systems.prefab = SoundDebug + AudioService + NavGraph root)
├── Scenes/
│   ├── SampleScene.unity          (the real level — integration only)
│   └── Sandbox/                   (one per member: Sandbox_Listener.unity, …)
├── Scripts/
│   ├── Echo.Runtime.asmdef
│   ├── Core/           (AgentRegistry, IDamageable, GameManager, MinHeap)
│   ├── Sound/          (SoundType, SoundEvent, SoundBus, Acoustics, Hearing, Noise,
│   │                    AudioService, SoundLibrary, AudioOcclusion, TensionDirector)
│   ├── Navigation/     (NavNode, NavGraph, IGraph, AStar, ZoneVolume, Door)
│   ├── Player/         (PlayerNoise, PlayerHealth, Weapon, NoiseMeterUI)
│   ├── Interaction/    (NoisyPhysicsObject, Generator, Switch)
│   ├── AI/
│   │   ├── Common/     (AgentBrain, AgentMotor, AgentBody, ContactSense,
│   │   │                AgentAnimatorBridge, SearchPattern, AgentDebugOverlay)
│   │   ├── Listener/   (ListenerBrain, ListenerPriority)
│   │   ├── Tracker/    (TrackerBrain, TrackerPredictor)
│   │   ├── Mimic/      (MimicBrain, DeceptionPlanner)
│   │   └── Ambusher/   (AmbusherBrain, RouteModel, InterceptPlanner)
│   └── Editor/
│       ├── Echo.Editor.asmdef      (Editor platform only)
│       └── NavGraphTools.cs
└── Tests/
    └── EditMode/       (Echo.Tests.EditMode.asmdef, AStarTests, RouteModelTests, …)
```

## 2.3 Assemblies

- `Assets/Scripts/Echo.Runtime.asmdef` — references **Unity.StarterAssets**, **Unity.AI.Navigation**, **Unity.InputSystem**.
- `Assets/Scripts/Editor/Echo.Editor.asmdef` — Editor-only, references `Echo.Runtime`.
- `Assets/Tests/EditMode/` — create via *Assets ▸ Create ▸ Testing ▸ Tests Assembly Folder*, then add `Echo.Runtime` to its references.

> Assemblies make compile times fast and let EditMode tests reference your code. If an asmdef ever causes "type not found" errors, check its *Assembly Definition References* first.

## 2.4 Scene objects (one of each)

| Object | Components | Owner |
|---|---|---|
| `Systems` | `AudioService`, `SoundDebug`, `AgentDebugOverlay`, `TensionDirector`, `GameManager` | shared |
| `NavGraph` | `NavGraph` + child `NavNode`s | Member 4 (+1) |
| `Zones` | child `ZoneVolume`s (Box triggers) | Member 1 |
| `NavMeshSurface` | `NavMeshSurface` | Member 1 |
| Agents | prefab instances | each owner |

---

# 3. Unity setup, step by step

## 3.1 Layers and tags

*Project Settings ▸ Tags and Layers*

| Layer | Used for |
|---|---|
| `Environment` | walls, floors, static geometry — **sound occluders** |
| `Props` | static props (crates, beds) — not occluders |
| `Interactable` | doors, throwables, switches |
| `Agents` | agent bodies |
| `Player` | the player (the floor colliders currently share unnamed layer 8 with the player — **move floors/walls to `Environment`** before building the acoustics) |
| `Zones` | zone trigger volumes; exclude from all raycasts |

Tags: `Player` (already on `PlayerCapsule`).

## 3.2 Bake the NavMesh

1. Create an empty `NavMeshSurface` object → *Add Component ▸ NavMesh Surface*.
2. *Collect Objects:* **All Game Objects**. *Use Geometry:* **Physics Colliders** (render meshes of alpha walls and decals will otherwise confuse the bake).
3. *Include Layers:* `Environment`, `Props`, `Interactable`. Exclude `Player`, `Agents`, `Zones`.
4. Agent type (Navigation window ▸ Agents): radius **0.4**, height **2.0**, step height **0.45** (stairs!), max slope **40°**.
5. Press **Bake**. Check: stairwell, ramps, catwalk stairs and all doorways are blue. Fix gaps by adjusting step height or collider shapes.
6. **Doors do not carve** the NavMesh. A closed door is handled by the graph (§7.6) so agents can path *through* a closed door and open it.

## 3.3 Zones

Zones are named rooms the AI can reason about ("the player is in the Warehouse").

1. Create `Zones` (empty). For each area add a child with a **Box Collider (Is Trigger)** + `ZoneVolume` (§7.3), layer `Zones`.
2. Suggested zones for the current level (see the floor plan):

| Zone id | Area | Plan equivalent |
|---|---|---|
| `SpawnStairwell` | stairwell, 0 → −6 m | Entry |
| `EntranceCorridors` | two L-corridors at −6 m | Maintenance corridor |
| `EntranceHall` | Room_1 | Main hall |
| `RampWest` | ramp −6 → −12 m | Transit |
| `CorridorTee` | Tee junction −12 m | Main corridor |
| `RampsNorth` | two ramps −12 → −24 m | Transit |
| `Warehouse` | Room_3 floor | Storage room |
| `WarehouseCatwalks` | Room_3 upper level | Storage (upper) |
| `CorridorCorner` | corner −12 m | Main corridor |
| `Passthrough` | small passthrough | Airlock |
| `Containment` | Room_2 | Research core / lab |

Rename to match your final story names; the code only needs unique strings.

## 3.4 Waypoint nodes

1. Create `NavGraph` (empty) + `NavGraph` component.
2. Add child empties with `NavNode` (§7.2). Placement rules:
   - one node at every **doorway** (tag `Doorway`), every **junction** (`Junction`), every **corner** of a corridor, the **top and bottom of every ramp/stair**;
   - 2–6 nodes inside each room on a loose grid (≈ 5–6 m apart), tag `Room`;
   - mark 1–3 nodes per zone as `AmbushSpot` (beside door frames, behind shelves, dark corners next to the route);
   - mark the end of dead-end corridors `DeadEnd`; mark 1–2 central nodes `Hub`.
   - Expect **60–90 nodes** for this level.
3. Run *ECHO ▸ Nav Graph ▸ Auto-Link Nodes* (editor tool §7.4). It links nodes with a direct NavMesh route and assigns zones.
4. Inspect gizmos: every node must have ≥ 1 link; the graph must be **connected** (the tool reports islands).

---

# 4. Sound system A — sound as information (what agents perceive)

## 4.1 Sound types and sources

```csharp
// Assets/Scripts/Sound/SoundType.cs
namespace Echo.Sound
{
    public enum SoundType
    {
        Footstep, Run, Jump, Land, Gunshot, Reload, BulletImpact,
        Door, MetalImpact, GlassBreak, ObjectDrop, Machinery, Generator, Switch,
        AgentVoice
    }

    /// Ground truth about who made a sound. Used by audio, debugging and filtering
    /// of agents' own body noise. NEVER exposed to agent decision code.
    public enum SoundSource { Player, Environment, AgentBody, Mimic }
}
```

## 4.2 The event (ground truth) and the percept (what an agent gets)

```csharp
// Assets/Scripts/Sound/SoundEvent.cs
using UnityEngine;

namespace Echo.Sound
{
    public readonly struct SoundEvent
    {
        public readonly int Id;
        public readonly SoundType Type;
        public readonly Vector3 Position;
        public readonly float Intensity;      // 0..1  (1 = gunshot)
        public readonly float Time;
        public readonly SoundSource Source;   // ground truth – not for agents
        public readonly GameObject Emitter;

        public SoundEvent(int id, SoundType type, Vector3 position, float intensity,
                          float time, SoundSource source, GameObject emitter)
        {
            Id = id; Type = type; Position = position; Intensity = intensity;
            Time = time; Source = source; Emitter = emitter;
        }
    }

    /// What an agent actually perceives: an *estimate*.
    public struct PerceivedSound
    {
        public int EventId;
        public SoundType Type;
        public Vector3 EstimatedPosition;
        public float PositionError;   // metres (radius of uncertainty)
        public float Loudness;        // 0..1 at the agent's ear
        public float TimeHeard;
    }
}
```

## 4.3 The bus

```csharp
// Assets/Scripts/Sound/SoundBus.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Sound
{
    public interface IHearer { void Hear(in SoundEvent e); }

    public static class SoundBus
    {
        static readonly List<IHearer> hearers = new();
        static int nextId;

        /// Anyone may observe ground truth: audio, debug drawing, HUD noise meter.
        public static event Action<SoundEvent> Emitted;

        // Required if "Enter Play Mode Options" disables domain reload.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { hearers.Clear(); nextId = 0; Emitted = null; }

        public static void Register(IHearer h) { if (!hearers.Contains(h)) hearers.Add(h); }
        public static void Unregister(IHearer h) => hearers.Remove(h);

        public static SoundEvent Emit(SoundType type, Vector3 position, float intensity,
                                      SoundSource source, GameObject emitter = null)
        {
            var e = new SoundEvent(++nextId, type, position, Mathf.Clamp01(intensity),
                                   Time.time, source, emitter);
            Emitted?.Invoke(e);
            for (int i = hearers.Count - 1; i >= 0; i--) hearers[i].Hear(e);
            return e;
        }
    }
}
```

**Why event-driven?** With ≤ 8 hearers, a sound costs a few raycasts. Polling "what sounds exist?" every frame would be wasteful and miss short sounds.

## 4.4 Propagation model (Acoustics)

**The model in one sentence:** *a sound of intensity `i` can be heard up to `R = i × 45 m × sensitivity` of effective distance, and loudness falls off quadratically.*

```text
effective distance d:
    if the straight line source→ear is clear of Environment colliders:  d = straight-line distance
    else:                                   d = max( route along waypoint graph , 1.15 × straight line )

R        = intensity × 45 m × hearingSensitivity
loudness = (1 − d/R)²     if d < R,   else 0
heard    = loudness ≥ threshold (≈ 0.03)
```

| Sound | Intensity | Heard up to (sensitivity 1.0) |
|---|---:|---:|
| Footstep (walk) | 0.15 | 6.75 m |
| Run step | 0.30 | 13.5 m |
| Landing (scaled by fall speed) | 0.10–0.60 | 4.5–27 m |
| Door | 0.50 | 22.5 m |
| Metal impact (thrown can) | 0.35–0.70 | 16–31 m |
| Glass break | 0.80 | 36 m |
| Gunshot | 1.00 | 45 m |

Why "route along the graph"? Sound in a facility leaks through doorways and down corridors. A gunshot on the other side of a 30 cm wall is heard as if it came round through the door — which is also *where the agent will walk to*. The graph distances are precomputed (all-pairs, §7.5), so this is an O(1) lookup.

```csharp
// Assets/Scripts/Sound/Acoustics.cs
using Echo.Navigation;
using UnityEngine;

namespace Echo.Sound
{
    public static class Acoustics
    {
        public const float MetresPerIntensity = 45f;
        static int occluderMask = -1;
        static int OccluderMask => occluderMask >= 0 ? occluderMask
                                 : (occluderMask = LayerMask.GetMask("Environment"));

        public static float EffectiveDistance(Vector3 source, Vector3 ear)
        {
            float straight = Vector3.Distance(source, ear);
            if (!Physics.Linecast(source + Vector3.up * 0.3f, ear, OccluderMask,
                                  QueryTriggerInteraction.Ignore))
                return straight;

            var g = NavGraph.Instance;
            if (g == null) return straight * 2f;
            int a = g.Nearest(source), b = g.Nearest(ear);
            float route = Vector3.Distance(source, g.Position(a))
                        + g.SoundDistance(a, b)
                        + Vector3.Distance(g.Position(b), ear);
            return Mathf.Max(route, straight * 1.15f);
        }

        public static float Loudness(float intensity, float sensitivity, float effectiveDistance)
        {
            float radius = intensity * MetresPerIntensity * sensitivity;
            if (radius <= 0f || effectiveDistance >= radius) return 0f;
            float x = 1f - effectiveDistance / radius;
            return x * x;
        }
    }
}
```

> Closed doors: `NavGraph.SoundDistance` already includes a **+12 m** penalty for every closed door on the route (§7.5), so a closed door muffles sound. Opening/closing a door triggers a recompute.

## 4.5 Hearing (the agent's ear)

```csharp
// Assets/Scripts/Sound/Hearing.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.Sound
{
    [DisallowMultipleComponent]
    public class Hearing : MonoBehaviour, IHearer
    {
        [Tooltip("1 = normal. Ambusher 1.4, Listener 1.15, Tracker 1.0, Mimic 0.9")]
        public float sensitivity = 1f;
        [Tooltip("Sounds quieter than this at the ear are ignored")]
        public float threshold = 0.03f;
        public float earHeight = 1.6f;
        [Tooltip("Position error (m) for the faintest / loudest audible sound")]
        public float maxPositionError = 6f, minPositionError = 0.5f;
        [Tooltip("Loudness that wakes the brain immediately instead of waiting for its tick")]
        public float urgentLoudness = 0.45f;

        readonly List<PerceivedSound> inbox = new();
        public bool HasUrgent { get; private set; }

        void OnEnable() => SoundBus.Register(this);
        void OnDisable() => SoundBus.Unregister(this);

        public void Hear(in SoundEvent e)
        {
            if (e.Source == SoundSource.AgentBody) return;   // agents recognise their own kind's movement
            if (e.Emitter == gameObject) return;             // the Mimic ignores its own fakes

            Vector3 ear = transform.position + Vector3.up * earHeight;
            float d = Acoustics.EffectiveDistance(e.Position, ear);
            float loud = Acoustics.Loudness(e.Intensity, sensitivity, d);
            if (loud < threshold) return;

            // Fainter sound => worse localisation. Blind hunters must search.
            float err = Mathf.Lerp(maxPositionError, minPositionError, loud);
            Vector2 r = Random.insideUnitCircle * err;
            Vector3 guess = e.Position + new Vector3(r.x, 0f, r.y);
            if (NavMesh.SamplePosition(guess, out var hit, 3f, NavMesh.AllAreas)) guess = hit.position;

            inbox.Add(new PerceivedSound
            {
                EventId = e.Id, Type = e.Type, EstimatedPosition = guess,
                PositionError = err, Loudness = loud, TimeHeard = Time.time
            });
            if (loud >= urgentLoudness) HasUrgent = true;
        }

        public void Drain(List<PerceivedSound> into)
        {
            into.AddRange(inbox);
            inbox.Clear();
            HasUrgent = false;
        }
    }
}
```

## 4.6 One call to make a noise

```csharp
// Assets/Scripts/Sound/Noise.cs
using UnityEngine;

namespace Echo.Sound
{
    public static class Noise
    {
        /// Plays the audio AND informs the AI. Use this everywhere.
        public static void Make(SoundType type, Vector3 position, float intensity,
                                SoundSource source, GameObject emitter = null, bool playAudio = true)
        {
            if (playAudio) AudioService.Play(type, position, intensity);
            SoundBus.Emit(type, position, intensity, source, emitter);
        }
    }
}
```

Use `playAudio: false` only for sounds whose audio is already looping elsewhere (e.g. a generator's hum AudioSource that also pulses an AI event every few seconds).

## 4.7 Habituation (so agents don't investigate the generator forever)

A running generator or a dripping pipe emits periodic low-intensity events. Each brain keeps a tiny memory of *(type, position)* of recent sounds that led nowhere. If the same type is heard within 3 m of a habituated spot, multiply its priority by 0.2. Implemented once in `HabituationMemory` (below); every brain owns one (`AgentBrain.Habituation`).

```csharp
// Assets/Scripts/AI/Common/HabituationMemory.cs
using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    public class HabituationMemory
    {
        struct Entry { public SoundType Type; public Vector3 Pos; public float Until; }
        readonly List<Entry> entries = new();

        public void MarkBoring(SoundType type, Vector3 pos, float forSeconds = 60f)
            => entries.Add(new Entry { Type = type, Pos = pos, Until = Time.time + forSeconds });

        public float Factor(in PerceivedSound s)
        {
            entries.RemoveAll(e => e.Until < Time.time);
            foreach (var e in entries)
                if (e.Type == s.Type && (e.Pos - s.EstimatedPosition).sqrMagnitude < 9f) return 0.2f;
            return 1f;
        }
    }
}
```

Call `MarkBoring` when an investigation of a sound ends without contact **and** the sound type is `Machinery`, `Generator`, `ObjectDrop` or `Switch`.

---

# 5. Sound system B — sound as audio (what the player hears)

The AI side decides *who reacts*. The audio side decides *how scary it feels*. Same `Noise.Make` call drives both.

## 5.1 Sound library (data, not code)

```csharp
// Assets/Scripts/Sound/SoundLibrary.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Echo.Sound
{
    [CreateAssetMenu(menuName = "ECHO/Sound Library")]
    public class SoundLibrary : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public SoundType type;
            public AudioClip[] clips;
            [Range(0f, 1f)] public float volume = 1f;
            public Vector2 pitch = new(0.94f, 1.06f);
            public float minDistance = 1f, maxDistance = 30f;
            public AudioMixerGroup group;
        }

        public Entry[] entries;
        Dictionary<SoundType, Entry> map;

        public Entry Get(SoundType t)
        {
            if (map == null) { map = new(); foreach (var e in entries) map[e.type] = e; }
            return map.TryGetValue(t, out var entry) ? entry : null;
        }
    }
}
```

Create one asset (*Create ▸ ECHO ▸ Sound Library*) and fill 3–6 clip variations per type. Variation + random pitch is what stops footsteps sounding robotic.

## 5.2 Audio service (pooled one-shots)

```csharp
// Assets/Scripts/Sound/AudioService.cs
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Sound
{
    public class AudioService : MonoBehaviour
    {
        public static AudioService Instance { get; private set; }
        public SoundLibrary library;
        public int poolSize = 24;

        readonly List<AudioSource> pool = new();
        int next;

        void Awake()
        {
            Instance = this;
            for (int i = 0; i < poolSize; i++)
            {
                var go = new GameObject("SFX " + i);
                go.transform.SetParent(transform);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 1f;                         // fully 3D
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.dopplerLevel = 0f;
                pool.Add(s);
            }
        }

        public static void Play(SoundType type, Vector3 pos, float intensity)
        {
            if (Instance != null) Instance.PlayAt(type, pos, intensity);
        }

        void PlayAt(SoundType type, Vector3 pos, float intensity)
        {
            var e = library ? library.Get(type) : null;
            if (e == null || e.clips == null || e.clips.Length == 0) return;
            var s = pool[next]; next = (next + 1) % pool.Count;
            s.transform.position = pos;
            s.clip = e.clips[Random.Range(0, e.clips.Length)];
            s.pitch = Random.Range(e.pitch.x, e.pitch.y);
            s.volume = e.volume * Mathf.Lerp(0.6f, 1f, intensity);
            s.minDistance = e.minDistance;
            s.maxDistance = e.maxDistance;
            s.outputAudioMixerGroup = e.group;
            s.Play();
        }
    }
}
```

## 5.3 Mixer

Create `Assets/Audio/EchoMixer.mixer` with groups:

```text
Master
├── SFX        (player actions, impacts)
├── Enemies    (agent voices, agent footsteps, Mimic fakes)
├── Ambience   (hum, drips, vents)
├── Music      (tension layers)
└── UI
```

- Add a gentle **Compressor** on Master and **Duck Volume** on Ambience driven by SFX (gunshots briefly push the ambience down — sounds big without being loud).
- Create three **snapshots**: `Calm`, `Alert`, `Hunt` (music and ambience levels, a low-pass on Ambience in `Hunt`).

## 5.4 Rooms sound like rooms

- Add an **Audio Reverb Zone** per zone: `Warehouse` → *Hangar*, `Containment`/`EntranceHall` → *Room*/*Auditorium*, corridors → *StoneCorridor*, stairwell → *Hallway*.
- Ambient loops: one 3D `AudioSource` per room (hum, vent, drip), `minDistance` 3, `maxDistance` 20, plus random one-shots (distant metal creak every 20–60 s) from a small `AmbientScatter` script. These are **audio only** — do **not** emit `SoundEvent`s for decorative ambience, or agents will investigate them.

## 5.5 Occlusion for the player's ears

Agents' sounds behind a wall should sound muffled. Put this on agent voice/footstep sources:

```csharp
// Assets/Scripts/Sound/AudioOcclusion.cs
using UnityEngine;

namespace Echo.Sound
{
    [RequireComponent(typeof(AudioSource), typeof(AudioLowPassFilter))]
    public class AudioOcclusion : MonoBehaviour
    {
        public float openCutoff = 22000f, blockedCutoff = 1200f, blockedVolume = 0.6f;
        AudioLowPassFilter lowPass; AudioSource source; float baseVolume, nextCheck; bool blocked;
        static Transform ears;

        void Awake()
        {
            lowPass = GetComponent<AudioLowPassFilter>(); source = GetComponent<AudioSource>();
            baseVolume = source.volume;
        }

        void Update()
        {
            if (ears == null && Camera.main) ears = Camera.main.transform;
            if (ears == null) return;
            if (Time.time >= nextCheck)
            {
                nextCheck = Time.time + 0.1f;   // 10 Hz is plenty
                blocked = Physics.Linecast(transform.position, ears.position,
                                           LayerMask.GetMask("Environment"), QueryTriggerInteraction.Ignore);
            }
            float k = Time.deltaTime * 6f;
            lowPass.cutoffFrequency = Mathf.Lerp(lowPass.cutoffFrequency, blocked ? blockedCutoff : openCutoff, k);
            source.volume = Mathf.Lerp(source.volume, blocked ? baseVolume * blockedVolume : baseVolume, k);
        }
    }
}
```

## 5.6 Agent identity sounds (presentation)

| Agent | Signature | Implementation |
|---|---|---|
| Listener | slow wet breathing; heavy footsteps | looping `AudioSource` (max distance 12 m) + footsteps via `Noise.Make(..., SoundSource.AgentBody)` |
| Tracker | soft mechanical clicks every 3–5 s, faster while intercepting | coroutine one-shots |
| Mimic | its body is silent; its *fakes* are the signature. Occasionally a distorted copy of the player's last sound | fakes via `Noise.Make(..., SoundSource.Mimic, gameObject)` |
| Ambusher | almost silent; a low rumble/heartbeat only within 4 m while waiting (fair warning) | short-range loop, enabled in `Wait` state |

Agent footsteps use `SoundSource.AgentBody`, so the player hears them but other agents ignore them (§4.5).

## 5.7 Tension music (reads AI state — allowed, it is presentation)

```csharp
// Assets/Scripts/Sound/TensionDirector.cs
using Echo.AI;
using UnityEngine;
using UnityEngine.Audio;

namespace Echo.Sound
{
    public class TensionDirector : MonoBehaviour
    {
        public AudioMixerSnapshot calm, alert, hunt;
        public float transition = 2f;
        int current = -1;

        void Update()
        {
            float level = 0f;
            foreach (var a in AgentRegistry.All) level = Mathf.Max(level, a.AlertLevel);
            int target = level > 0.7f ? 2 : level > 0.3f ? 1 : 0;
            if (target == current) return;
            current = target;
            (target == 2 ? hunt : target == 1 ? alert : calm).TransitionTo(transition);
        }
    }
}
```

## 5.8 Noise meter HUD (player readability)

The player must learn "running is loud". A small bar near the crosshair that spikes with the intensity of the player's own sounds and decays over 1 s:

```csharp
// Assets/Scripts/Player/NoiseMeterUI.cs
using Echo.Sound;
using UnityEngine;
using UnityEngine.UI;

public class NoiseMeterUI : MonoBehaviour
{
    public Image fill;          // Image Type = Filled
    float level;

    void OnEnable() => SoundBus.Emitted += OnSound;
    void OnDisable() => SoundBus.Emitted -= OnSound;

    void OnSound(SoundEvent e) { if (e.Source == SoundSource.Player) level = Mathf.Max(level, e.Intensity); }

    void Update()
    {
        level = Mathf.MoveTowards(level, 0f, Time.deltaTime);
        fill.fillAmount = level;
    }
}
```

## 5.9 Where to get sounds

- **Freesound.org** (filter by CC0), **Sonniss GDC Game Audio Bundles** (free, royalty-free), **Kenney** audio packs, Unity Asset Store free SFX packs.
- The SciFi Warehouse Kit already has `background_rumble.wav` and `Fan_St.wav` — good ambience starters.
- Keep a `Assets/Audio/CREDITS.md` with source + licence for every clip (assessors like it).
- Import settings: one-shots → *Decompress On Load*, Vorbis 70 %; loops/music → *Streaming*. Force **Mono** for 3D sounds.

---

# 6. Connecting the player and the world to the sound system

## 6.1 Footsteps, runs and landings (no changes to Starter Assets code)

```csharp
// Assets/Scripts/Player/PlayerNoise.cs
using Echo.Sound;
using StarterAssets;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerNoise : MonoBehaviour
{
    public float walkStride = 1.6f, runStride = 2.2f;
    public float walkIntensity = 0.15f, runIntensity = 0.30f;

    CharacterController cc; FirstPersonController fpc; StarterAssetsInputs input;
    float travelled, airborneMinVy; bool wasGrounded = true;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        fpc = GetComponent<FirstPersonController>();
        input = GetComponent<StarterAssetsInputs>();
    }

    void Update()
    {
        Vector3 v = cc.velocity;
        Vector3 feet = transform.position;

        if (fpc.Grounded)
        {
            if (!wasGrounded)   // just landed: louder the harder we hit
            {
                float impact = -airborneMinVy;                    // m/s
                float i = Mathf.Clamp(0.08f + impact * 0.05f, 0.1f, 0.6f);
                Noise.Make(SoundType.Land, feet, i, SoundSource.Player, gameObject);
                travelled = 0f;
            }

            float speed = new Vector2(v.x, v.z).magnitude;
            if (speed > 0.3f)
            {
                travelled += speed * Time.deltaTime;
                bool running = input.sprint && speed > fpc.MoveSpeed + 0.3f;
                if (travelled >= (running ? runStride : walkStride))
                {
                    travelled = 0f;
                    Noise.Make(running ? SoundType.Run : SoundType.Footstep, feet,
                               running ? runIntensity : walkIntensity, SoundSource.Player, gameObject);
                }
            }
            airborneMinVy = 0f;
        }
        else
        {
            airborneMinVy = Mathf.Min(airborneMinVy, v.y);
        }
        wasGrounded = fpc.Grounded;
    }
}
```

Add it to `PlayerCapsule`. **Crouch** (optional, plan §54 says it may be cut): add a crouch action in the Input Actions asset, shrink `CharacterController.height`, use `MoveSpeed × 0.5` and footstep intensity **0.05**.

## 6.2 Weapon

```csharp
// inside your Weapon.Fire()
Noise.Make(SoundType.Gunshot, muzzle.position, 1f, SoundSource.Player, gameObject);
if (Physics.Raycast(cam.position, cam.forward, out var hit, 100f, shootMask, QueryTriggerInteraction.Ignore))
{
    if (hit.collider.TryGetComponent<IDamageable>(out var d)) d.TakeDamage(damage, cam.position);
    Noise.Make(SoundType.BulletImpact, hit.point, 0.3f, SoundSource.Environment);
}
```

Design note: the impact creates a *second*, quieter sound far away. Agents near the impact but out of range of the gunshot will investigate the *impact* — a natural decoy tactic for clever players.

## 6.3 Physics objects (throwables, knocked props)

```csharp
// Assets/Scripts/Interaction/NoisyPhysicsObject.cs
using Echo.Sound;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class NoisyPhysicsObject : MonoBehaviour
{
    public SoundType type = SoundType.MetalImpact;
    public float minImpulse = 1.5f, maxImpulse = 12f;
    public float minIntensity = 0.15f, maxIntensity = 0.7f;
    public bool breakOnHardImpact;            // glass bottles
    public GameObject brokenPrefab;
    float lastTime;

    void OnCollisionEnter(Collision c)
    {
        float impulse = c.impulse.magnitude;
        if (impulse < minImpulse || Time.time - lastTime < 0.15f) return;
        lastTime = Time.time;
        float t = Mathf.InverseLerp(minImpulse, maxImpulse, impulse);
        Vector3 p = c.GetContact(0).point;

        if (breakOnHardImpact && t > 0.5f)
        {
            Noise.Make(SoundType.GlassBreak, p, 0.8f, SoundSource.Environment);
            if (brokenPrefab) Instantiate(brokenPrefab, transform.position, transform.rotation);
            Destroy(gameObject);
            return;
        }
        Noise.Make(type, p, Mathf.Lerp(minIntensity, maxIntensity, t), SoundSource.Environment);
    }
}
```

Throwing: on input, spawn/hold the object, then `rb.AddForce(cam.forward * 9f + Vector3.up * 2f, ForceMode.VelocityChange)`. Use **Continuous Dynamic** collision detection on throwables. Pick-ups = `Interactable` layer + a raycast from the camera.

## 6.4 Doors

```csharp
// Assets/Scripts/Navigation/Door.cs
using Echo.Sound;
using UnityEngine;

namespace Echo.Navigation
{
    public class Door : MonoBehaviour
    {
        public NavNode sideA, sideB;            // the two graph nodes this door connects
        public bool isOpen, isLocked;
        public Transform leaf; public Vector3 openEuler = new(0, 100, 0);
        public float swingSpeed = 4f;
        Quaternion closedRot, openRot;

        public float OpenDelay => 0.6f;         // time an agent spends opening it

        void Awake() { closedRot = leaf.localRotation; openRot = closedRot * Quaternion.Euler(openEuler); }

        void Update()
        {
            leaf.localRotation = Quaternion.Slerp(leaf.localRotation, isOpen ? openRot : closedRot,
                                                  Time.deltaTime * swingSpeed);
        }

        public void SetOpen(bool open, GameObject who, bool byAgent)
        {
            if (isLocked || isOpen == open) return;
            isOpen = open;
            Noise.Make(SoundType.Door, transform.position, open ? 0.5f : 0.6f,
                       byAgent ? SoundSource.AgentBody : SoundSource.Player, who);
            NavGraph.Instance?.OnDoorChanged();
        }
    }
}
```

Two important gameplay consequences:
1. An **agent opening a door makes a door sound the player hears** (`AgentBody` → other agents ignore it). Great tension cue.
2. **Closed doors muffle sound** (+12 m in the sound-distance matrix).

## 6.5 Machinery, generator, switches

- Switch: `Noise.Make(SoundType.Switch, pos, 0.25f, SoundSource.Player, gameObject)`.
- Generator start-up: one loud `Generator` event (0.8) → looping hum audio + a quiet `Machinery` AI pulse (0.2, `playAudio:false`) every 6 s. Habituation (§4.7) stops agents obsessing over it after the first investigation. Objective design: restoring power is *loud on purpose* — the player must plan for the agents it summons.

---

# 7. Navigation: NavMesh + waypoint graph + your own A*

## 7.1 Why two layers

| Layer | Does | Why |
|---|---|---|
| **Waypoint graph + A\*** (yours) | *Where to go and by which route* | Visible, explainable, tunable per agent (custom costs), gives zones and node tags for reasoning, cheap (≈ 80 nodes). |
| **NavMesh + NavMeshAgent** (Unity) | *How to physically walk between two nodes* | Robust collision-free movement, local avoidance between agents, stairs/ramps. |

> Viva answer to "isn't NavMesh already A*?": yes, Unity's `NavMesh.CalculatePath` uses A* over polygons internally, but it is a black box with fixed costs. We implemented A* over a semantic graph so each agent can use **its own cost function** (Ambusher avoids the player's route, Mimic avoids exposure, closed doors cost time), and so we can expose and test the algorithm.

## 7.2 Node and graph data

```csharp
// Assets/Scripts/Navigation/NavNode.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Navigation
{
    [Flags]
    public enum NodeTag { None = 0, Corridor = 1, Room = 2, Doorway = 4, Junction = 8,
                          AmbushSpot = 16, DeadEnd = 32, Hub = 64 }

    public class NavNode : MonoBehaviour
    {
        public string zone;                         // filled by the auto-linker from ZoneVolume
        public NodeTag tags;
        public List<NavNode> links = new();         // filled by the auto-linker

        void OnDrawGizmos()
        {
            Gizmos.color = (tags & NodeTag.AmbushSpot) != 0 ? Color.magenta
                         : (tags & NodeTag.Doorway) != 0 ? Color.yellow : Color.cyan;
            Gizmos.DrawSphere(transform.position + Vector3.up * 0.2f, 0.25f);
            Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
            foreach (var l in links) if (l) Gizmos.DrawLine(transform.position + Vector3.up * 0.2f,
                                                              l.transform.position + Vector3.up * 0.2f);
        }
    }
}
```

```csharp
// Assets/Scripts/Navigation/IGraph.cs
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Navigation
{
    public readonly struct Edge
    {
        public readonly int To; public readonly float Length; public readonly Door Door;
        public Edge(int to, float length, Door door) { To = to; Length = length; Door = door; }

        /// Path cost for planning: length, + time to open a closed door, ∞ if locked.
        public float Cost => Door == null ? Length
                           : Door.isLocked ? float.PositiveInfinity
                           : Door.isOpen ? Length : Length + 3f;
    }

    /// Kept abstract so A* can be unit-tested on a tiny fake graph.
    public interface IGraph
    {
        int Count { get; }
        Vector3 Position(int node);
        IReadOnlyList<Edge> Neighbours(int node);
    }
}
```

```csharp
// Assets/Scripts/Navigation/NavGraph.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.Navigation
{
    public class NavGraph : MonoBehaviour, IGraph
    {
        public static NavGraph Instance { get; private set; }
        public const float ClosedDoorSoundPenalty = 12f;

        public NavNode[] Nodes { get; private set; }
        List<Edge>[] adj;
        float[,] soundDist;
        readonly Dictionary<NavNode, int> index = new();

        public int Count => Nodes.Length;
        public Vector3 Position(int i) => Nodes[i].transform.position;
        public IReadOnlyList<Edge> Neighbours(int i) => adj[i];
        public float SoundDistance(int a, int b) => soundDist[a, b];
        public NavNode Node(int i) => Nodes[i];
        public int IndexOf(NavNode n) => index.TryGetValue(n, out var i) ? i : -1;

        void Awake() { Instance = this; Build(); }

        public void Build()
        {
            Nodes = GetComponentsInChildren<NavNode>();
            index.Clear();
            for (int i = 0; i < Nodes.Length; i++) index[Nodes[i]] = i;

            var doors = FindObjectsByType<Door>(FindObjectsSortMode.None);
            adj = new List<Edge>[Nodes.Length];
            for (int i = 0; i < Nodes.Length; i++)
            {
                adj[i] = new List<Edge>();
                foreach (var other in Nodes[i].links)
                {
                    if (other == null || !index.TryGetValue(other, out int j)) continue;
                    Door door = null;
                    foreach (var d in doors)
                        if ((d.sideA == Nodes[i] && d.sideB == other) || (d.sideB == Nodes[i] && d.sideA == other))
                            door = d;
                    adj[i].Add(new Edge(j, PathLength(Position(i), Position(j)), door));
                }
            }
            RebuildSoundDistances();
        }

        public void OnDoorChanged() => RebuildSoundDistances();

        /// Floyd–Warshall, O(V³): ~0.5 M operations for 80 nodes, run at load and on door changes.
        void RebuildSoundDistances()
        {
            int n = Nodes.Length;
            soundDist = new float[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) soundDist[i, j] = i == j ? 0f : float.PositiveInfinity;
            for (int i = 0; i < n; i++)
                foreach (var e in adj[i])
                {
                    float w = e.Length + (e.Door != null && !e.Door.isOpen ? ClosedDoorSoundPenalty : 0f);
                    if (w < soundDist[i, e.To]) soundDist[i, e.To] = w;
                }
            for (int k = 0; k < n; k++)
                for (int i = 0; i < n; i++)
                {
                    float ik = soundDist[i, k];
                    if (float.IsPositiveInfinity(ik)) continue;
                    for (int j = 0; j < n; j++)
                    {
                        float via = ik + soundDist[k, j];
                        if (via < soundDist[i, j]) soundDist[i, j] = via;
                    }
                }
        }

        /// Nearest node, preferring one with a clear line (so we don't pick a node behind a wall).
        public int Nearest(Vector3 p)
        {
            int best = -1, bestBlocked = -1; float bd = float.MaxValue, bbd = float.MaxValue;
            int mask = LayerMask.GetMask("Environment");
            for (int i = 0; i < Nodes.Length; i++)
            {
                float d = (Position(i) - p).sqrMagnitude;
                if (d >= bd && d >= bbd) continue;
                bool clear = !Physics.Linecast(p + Vector3.up * 0.5f, Position(i) + Vector3.up * 0.5f,
                                               mask, QueryTriggerInteraction.Ignore);
                if (clear && d < bd) { bd = d; best = i; }
                else if (!clear && d < bbd) { bbd = d; bestBlocked = i; }
            }
            return best >= 0 ? best : bestBlocked;
        }

        static float PathLength(Vector3 a, Vector3 b)
        {
            var path = new NavMeshPath();
            if (NavMesh.SamplePosition(a, out var ha, 1.5f, NavMesh.AllAreas) &&
                NavMesh.SamplePosition(b, out var hb, 1.5f, NavMesh.AllAreas) &&
                NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path) &&
                path.status == NavMeshPathStatus.PathComplete)
            {
                float len = 0f;
                for (int i = 1; i < path.corners.Length; i++) len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                return len;
            }
            return Vector3.Distance(a, b);
        }
    }
}
```

> Performance note: `Nearest` does up to 80 linecasts. It is called a few times per second in total, which is fine. If profiling shows otherwise, cache the result per agent and refresh only when the agent moves > 2 m.

## 7.3 Zones

```csharp
// Assets/Scripts/Navigation/ZoneVolume.cs
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Navigation
{
    [RequireComponent(typeof(BoxCollider))]
    public class ZoneVolume : MonoBehaviour
    {
        public string zoneId;
        static readonly List<ZoneVolume> all = new();
        BoxCollider box;

        void OnEnable() { box = GetComponent<BoxCollider>(); box.isTrigger = true; all.Add(this); }
        void OnDisable() => all.Remove(this);

        public static string ZoneAt(Vector3 p)
        {
            foreach (var z in all)
                if (z.box.bounds.Contains(p)) return z.zoneId;
            return null;
        }

        public static IEnumerable<ZoneVolume> All => all;
    }
}
```

## 7.4 Editor tool: auto-link nodes and assign zones

```csharp
// Assets/Scripts/Editor/NavGraphTools.cs
using System.Linq;
using Echo.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

public static class NavGraphTools
{
    const float MaxLinkDistance = 14f;
    const float MaxDetour = 1.25f;   // link only if the walk is nearly straight

    [MenuItem("ECHO/Nav Graph/Auto-Link Nodes")]
    static void AutoLink()
    {
        var graph = Object.FindFirstObjectByType<NavGraph>();
        if (graph == null) { Debug.LogError("No NavGraph in scene"); return; }
        var nodes = graph.GetComponentsInChildren<NavNode>();
        var zones = Object.FindObjectsByType<ZoneVolume>(FindObjectsSortMode.None);

        foreach (var n in nodes) { Undo.RecordObject(n, "Auto-link"); n.links.Clear(); }

        for (int i = 0; i < nodes.Length; i++)
        for (int j = i + 1; j < nodes.Length; j++)
        {
            Vector3 a = nodes[i].transform.position, b = nodes[j].transform.position;
            float straight = Vector3.Distance(a, b);
            if (straight > MaxLinkDistance) continue;
            if (!NavMesh.SamplePosition(a, out var ha, 1.5f, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(b, out var hb, 1.5f, NavMesh.AllAreas)) continue;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path) ||
                path.status != NavMeshPathStatus.PathComplete) continue;
            float len = 0f;
            for (int k = 1; k < path.corners.Length; k++) len += Vector3.Distance(path.corners[k - 1], path.corners[k]);
            if (len > straight * MaxDetour) continue;   // goes round a wall: let intermediate nodes handle it
            nodes[i].links.Add(nodes[j]); nodes[j].links.Add(nodes[i]);
        }

        foreach (var n in nodes)
        {
            var z = zones.FirstOrDefault(v => v.GetComponent<BoxCollider>().bounds.Contains(n.transform.position));
            n.zone = z ? z.zoneId : "Unzoned";
            EditorUtility.SetDirty(n);
        }

        // Connectivity report (BFS from node 0)
        var seen = new System.Collections.Generic.HashSet<NavNode> { nodes[0] };
        var queue = new System.Collections.Generic.Queue<NavNode>(); queue.Enqueue(nodes[0]);
        while (queue.Count > 0) foreach (var l in queue.Dequeue().links) if (seen.Add(l)) queue.Enqueue(l);
        int isolated = nodes.Count(n => n.links.Count == 0);
        Debug.Log($"Auto-link: {nodes.Length} nodes, {nodes.Sum(n => n.links.Count) / 2} edges, " +
                  $"reachable from first node: {seen.Count}/{nodes.Length}, isolated: {isolated}");
    }
}
```

## 7.5 A* (the algorithm you will explain in the viva)

```csharp
// Assets/Scripts/Core/MinHeap.cs  — binary heap of (node, priority) with lazy deletion
using System.Collections.Generic;

namespace Echo
{
    public class MinHeap
    {
        readonly List<(int node, float pri)> h = new();
        public int Count => h.Count;
        public void Clear() => h.Clear();

        public void Push(int node, float pri)
        {
            h.Add((node, pri));
            int i = h.Count - 1;
            while (i > 0) { int p = (i - 1) / 2; if (h[p].pri <= h[i].pri) break; (h[p], h[i]) = (h[i], h[p]); i = p; }
        }

        public int Pop()
        {
            int top = h[0].node; int last = h.Count - 1;
            h[0] = h[last]; h.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < h.Count && h[l].pri < h[m].pri) m = l;
                if (r < h.Count && h[r].pri < h[m].pri) m = r;
                if (m == i) break;
                (h[m], h[i]) = (h[i], h[m]); i = m;
            }
            return top;
        }
    }
}
```

```csharp
// Assets/Scripts/Navigation/AStar.cs
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Navigation
{
    public static class AStar
    {
        /// Per-agent cost shaping. MUST return >= baseCost (multipliers >= 1, additions >= 0)
        /// so the Euclidean heuristic stays admissible. Return +∞ to forbid an edge.
        public delegate float CostModifier(int from, int to, float baseCost);

        public static bool FindPath(IGraph g, int start, int goal, List<int> result,
                                    CostModifier modify = null)
        {
            result.Clear();
            if (start < 0 || goal < 0) return false;
            int n = g.Count;
            var gScore = new float[n];
            var parent = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { gScore[i] = float.PositiveInfinity; parent[i] = -1; }

            Vector3 goalPos = g.Position(goal);
            var open = new Echo.MinHeap();
            gScore[start] = 0f;
            open.Push(start, Vector3.Distance(g.Position(start), goalPos));

            while (open.Count > 0)
            {
                int cur = open.Pop();
                if (closed[cur]) continue;                       // stale entry (lazy deletion)
                if (cur == goal)
                {
                    for (int c = goal; c != -1; c = parent[c]) result.Add(c);
                    result.Reverse();
                    return true;
                }
                closed[cur] = true;

                var edges = g.Neighbours(cur);
                for (int k = 0; k < edges.Count; k++)
                {
                    var e = edges[k];
                    if (closed[e.To]) continue;
                    float cost = e.Cost;
                    if (modify != null) cost = modify(cur, e.To, cost);
                    if (float.IsPositiveInfinity(cost)) continue;

                    float tentative = gScore[cur] + cost;
                    if (tentative < gScore[e.To])
                    {
                        gScore[e.To] = tentative;
                        parent[e.To] = cur;
                        open.Push(e.To, tentative + Vector3.Distance(g.Position(e.To), goalPos));
                    }
                }
            }
            return false;   // goal unreachable (e.g. locked doors)
        }
    }
}
```

**Facts to know for the viva:**
- `f(n) = g(n) + h(n)`; `g` = cost so far, `h` = straight-line (Euclidean, 3D) distance to the goal.
- `h` is **admissible** (never overestimates) because every edge cost is ≥ its NavMesh walking length ≥ the straight-line distance; door penalties and agent modifiers only *add*. It is also **consistent**, so A* never needs to reopen closed nodes → optimal paths.
- Complexity: O(E log V) with a binary heap. With V ≈ 80, E ≈ 250 a search is a few microseconds.
- Lazy deletion: instead of "decrease-key", push duplicates and skip nodes already closed.

## 7.6 Re-planning rules (do not re-plan every frame)

Re-plan when (checked on the think tick):
1. The brain picks a **new goal** that is > 3 m from the current goal.
2. The motor reports **Failed** (no path, or no progress for 2.5 s — stuck).
3. A **door** on the remaining path becomes **locked**.
4. The agent's cost function changed meaningfully (Ambusher: player estimate moved to a different zone).

---

# 8. The shared agent framework

## 8.1 Registry

```csharp
// Assets/Scripts/Core/AgentRegistry.cs
using System.Collections.Generic;
using UnityEngine;

namespace Echo.AI
{
    public static class AgentRegistry
    {
        static readonly List<AgentBrain> agents = new();
        public static IReadOnlyList<AgentBrain> All => agents;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => agents.Clear();

        public static void Add(AgentBrain a) { if (!agents.Contains(a)) agents.Add(a); }
        public static void Remove(AgentBrain a) => agents.Remove(a);
        public static void ResetAll() { foreach (var a in agents) a.ResetAgent(); }
    }
}
```

## 8.2 Motor (A* route + NavMesh walking + doors + stuck detection)

```csharp
// Assets/Scripts/AI/Common/AgentMotor.cs
using System;
using System.Collections.Generic;
using Echo.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    [RequireComponent(typeof(NavMeshAgent))]
    public class AgentMotor : MonoBehaviour
    {
        public enum Gait { Sneak, Walk, Run }
        public float sneakSpeed = 1.2f, walkSpeed = 2.2f, runSpeed = 5f;
        public float waypointRadius = 0.8f;
        public float stuckTimeout = 2.5f;

        /// Set by the brain to express this agent's preferences (see each agent).
        public AStar.CostModifier CostModifier;

        public bool IsMoving { get; private set; }
        public Vector3 Goal { get; private set; }
        public Gait CurrentGait { get; private set; }
        public IReadOnlyList<int> NodePath => nodePath;
        public bool ArrivedFlag { get; private set; }      // consumed by brains on their tick
        public bool FailedFlag { get; private set; }

        NavMeshAgent nav;
        readonly List<int> nodePath = new();
        int idx; float lastProgressTime, bestRemaining, doorWaitUntil;

        void Awake() { nav = GetComponent<NavMeshAgent>(); }

        public float SpeedFor(Gait g) => g == Gait.Run ? runSpeed : g == Gait.Walk ? walkSpeed : sneakSpeed;

        public bool MoveTo(Vector3 destination, Gait gait)
        {
            ArrivedFlag = FailedFlag = false;
            if (!NavMesh.SamplePosition(destination, out var hit, 3f, NavMesh.AllAreas)) { FailedFlag = true; return false; }
            Goal = hit.position; CurrentGait = gait; nav.speed = SpeedFor(gait);

            var g = NavGraph.Instance;
            int s = g.Nearest(transform.position), t = g.Nearest(Goal);
            nodePath.Clear();
            if (s != t && !AStar.FindPath(g, s, t, nodePath, CostModifier)) { FailedFlag = true; return false; }

            // Skip the first node if it lies behind us.
            if (nodePath.Count > 1 &&
                Vector3.Distance(transform.position, g.Position(nodePath[1])) <
                Vector3.Distance(g.Position(nodePath[0]), g.Position(nodePath[1])))
                nodePath.RemoveAt(0);

            idx = 0; IsMoving = true; ResetProgress();
            nav.isStopped = false;
            nav.SetDestination(CurrentTarget());
            return true;
        }

        public void SetGait(Gait gait) { CurrentGait = gait; nav.speed = SpeedFor(gait); }

        public void Stop()
        {
            IsMoving = false; nodePath.Clear();
            if (nav.isOnNavMesh) { nav.isStopped = true; nav.ResetPath(); }
        }

        public void FaceTowards(Vector3 p, float degPerSec = 240f)
        {
            Vector3 d = p - transform.position; d.y = 0f;
            if (d.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                                    Quaternion.LookRotation(d), degPerSec * Time.deltaTime);
        }

        Vector3 CurrentTarget() => idx < nodePath.Count ? NavGraph.Instance.Position(nodePath[idx]) : Goal;
        void ResetProgress() { lastProgressTime = Time.time; bestRemaining = float.MaxValue; }

        void Update()
        {
            if (!IsMoving || nav.pathPending) return;
            if (Time.time < doorWaitUntil) return;

            bool last = idx >= nodePath.Count;
            if (nav.remainingDistance <= (last ? nav.stoppingDistance + 0.15f : waypointRadius))
            {
                if (last) { IsMoving = false; ArrivedFlag = true; return; }

                // About to traverse edge nodePath[idx] -> nodePath[idx+1]: open a closed door first.
                if (idx + 1 < nodePath.Count && TryGetDoor(nodePath[idx], nodePath[idx + 1], out var door) && !door.isOpen)
                {
                    if (door.isLocked) { IsMoving = false; FailedFlag = true; return; }
                    door.SetOpen(true, gameObject, byAgent: true);   // makes a door sound the player hears
                    doorWaitUntil = Time.time + door.OpenDelay;
                }
                idx++; ResetProgress();
                nav.SetDestination(CurrentTarget());
                return;
            }

            if (nav.remainingDistance < bestRemaining - 0.2f) { bestRemaining = nav.remainingDistance; lastProgressTime = Time.time; }
            else if (Time.time - lastProgressTime > stuckTimeout) { Stop(); FailedFlag = true; }
        }

        static bool TryGetDoor(int a, int b, out Door door)
        {
            foreach (var e in NavGraph.Instance.Neighbours(a))
                if (e.To == b) { door = e.Door; return door != null; }
            door = null; return false;
        }

        public void ConsumeFlags() { ArrivedFlag = FailedFlag = false; }

        void OnDrawGizmosSelected()
        {
            if (NavGraph.Instance == null || nodePath.Count == 0) return;
            Gizmos.color = Color.green;
            Vector3 prev = transform.position;
            for (int i = idx; i < nodePath.Count; i++) { var p = NavGraph.Instance.Position(nodePath[i]); Gizmos.DrawLine(prev, p); prev = p; }
            Gizmos.DrawLine(prev, Goal); Gizmos.DrawWireSphere(Goal, 0.4f);
        }
    }
}
```

NavMeshAgent settings: `Angular Speed` 360, `Acceleration` 12, `Stopping Distance` 0.5, `Auto Braking` on, `Obstacle Avoidance` Medium, give each agent a different `Avoidance Priority` (40/45/50/55).

## 8.3 Brain base class (tick, perception drain, hooks)

```csharp
// Assets/Scripts/AI/Common/AgentBrain.cs
using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    [RequireComponent(typeof(Hearing), typeof(AgentMotor), typeof(AgentBody))]
    public abstract class AgentBrain : MonoBehaviour
    {
        [SerializeField] protected float thinkInterval = 0.2f;   // 5 Hz decisions

        protected Hearing Hearing; protected AgentMotor Motor; protected AgentBody Body;
        protected AgentAnimatorBridge Anim;                       // may be null in greybox prototypes
        protected readonly List<PerceivedSound> NewSounds = new();
        protected readonly HabituationMemory Habituation = new();
        protected Vector3 Home;
        /// Snapshot of the motor's one-shot events since the previous tick (read these, not the motor flags).
        protected bool Arrived, Failed;
        float nextThink;

        public bool IsAlive => !Body.IsDead;

        public abstract string DisplayName { get; }
        public abstract string StateName { get; }
        public virtual float AlertLevel => 0f;                // 0 calm … 1 hunting (music, Mimic)
        public virtual float CurrentInterest => 0f;           // priority of what it is pursuing (Mimic's model)
        public float HearingSensitivity => Hearing.sensitivity;
        public virtual string DebugText => "";

        protected virtual void Awake()
        {
            Hearing = GetComponent<Hearing>(); Motor = GetComponent<AgentMotor>(); Body = GetComponent<AgentBody>();
            TryGetComponent(out Anim);
            Home = transform.position;
        }
        protected virtual void OnEnable() => AgentRegistry.Add(this);
        protected virtual void OnDisable() => AgentRegistry.Remove(this);

        void Update()
        {
            if (Body.IsDead) return;
            if (Time.time < nextThink && !Hearing.HasUrgent) return;
            nextThink = Time.time + thinkInterval * Random.Range(0.85f, 1.15f); // de-synchronise agents
            NewSounds.Clear();
            Hearing.Drain(NewSounds);
            // Take the motor's events *before* thinking: a MoveTo() that fails inside Think()
            // sets FailedFlag again, and the brain sees it on its next tick instead of losing it.
            Arrived = Motor.ArrivedFlag; Failed = Motor.FailedFlag;
            Motor.ConsumeFlags();
            Think();
        }

        /// Perception -> memory -> decision -> action. Called at ~5 Hz or immediately on loud sounds.
        protected abstract void Think();

        /// Touch sense: something (the player) brushed against the agent.
        public virtual void OnContact(Vector3 where) => Body.TryAttack(where);

        /// Got hurt: by default treat it like a very loud, precise sound at `from`.
        public virtual void OnHurt(Vector3 from) { }

        public virtual void ResetAgent() { Motor.Stop(); }
    }
}
```

## 8.4 Body: health, touch, attack

```csharp
// Assets/Scripts/Core/IDamageable.cs
public interface IDamageable { void TakeDamage(float amount, UnityEngine.Vector3 from); }
```

```csharp
// Assets/Scripts/AI/Common/AgentBody.cs
using System.Collections;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    public class AgentBody : MonoBehaviour, IDamageable
    {
        public float maxHealth = 100f;
        public float attackDamage = 35f, attackCooldown = 1.6f, windup = 0.35f, reach = 1.1f, hitRadius = 0.9f;
        public LayerMask playerMask;
        public float Health { get; private set; }
        public bool IsDead => Health <= 0f;

        AgentBrain brain; AgentMotor motor; AgentAnimatorBridge anim; float nextAttack;

        void Awake()
        {
            Health = maxHealth;
            brain = GetComponent<AgentBrain>(); motor = GetComponent<AgentMotor>(); TryGetComponent(out anim);
        }

        public void TryAttack(Vector3 towards)
        {
            if (IsDead || Time.time < nextAttack) return;
            nextAttack = Time.time + attackCooldown;
            StartCoroutine(Attack(towards));
        }

        IEnumerator Attack(Vector3 towards)
        {
            motor.Stop();
            float t = 0f;
            while (t < windup) { motor.FaceTowards(towards, 720f); t += Time.deltaTime; yield return null; }
            anim?.Attack();
            Noise.Make(SoundType.AgentVoice, transform.position, 0.4f, SoundSource.AgentBody, gameObject);
            // Hit resolution is physics, not perception: whatever is in front of the claws gets hit.
            var centre = transform.position + Vector3.up * 1.1f + transform.forward * reach;
            foreach (var c in Physics.OverlapSphere(centre, hitRadius, playerMask, QueryTriggerInteraction.Ignore))
                if (c.TryGetComponent<IDamageable>(out var d)) d.TakeDamage(attackDamage, transform.position);
        }

        public void TakeDamage(float amount, Vector3 from)
        {
            if (IsDead) return;
            Health -= amount;
            anim?.Hit();
            if (IsDead) { motor.Stop(); anim?.Die(); GetComponent<Collider>().enabled = false; return; }
            brain.OnHurt(from);
        }

        public void Revive() { Health = maxHealth; GetComponent<Collider>().enabled = true; anim?.Revive(); }
    }
}
```

```csharp
// Assets/Scripts/AI/Common/ContactSense.cs  (child object of the agent)
using UnityEngine;

namespace Echo.AI
{
    [RequireComponent(typeof(SphereCollider))]
    public class ContactSense : MonoBehaviour
    {
        public float radius = 1.3f, repeat = 0.5f;
        AgentBrain brain; float next;

        void Awake()
        {
            var c = GetComponent<SphereCollider>(); c.isTrigger = true; c.radius = radius;
            brain = GetComponentInParent<AgentBrain>();
        }

        void OnTriggerStay(Collider other)
        {
            if (Time.time < next || !other.CompareTag("Player")) return;
            next = Time.time + repeat;
            brain.OnContact(other.transform.position);   // touch is a legitimate sense
        }
    }
}
```

> Trigger events need a Rigidbody on one side: add a **kinematic Rigidbody** to each agent root (`isKinematic = true`, no gravity).

## 8.5 Animator bridge (the only script that talks to the Animator)

```csharp
// Assets/Scripts/AI/Common/AgentAnimatorBridge.cs
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    public class AgentAnimatorBridge : MonoBehaviour
    {
        public Animator animator;
        static readonly int Speed = Animator.StringToHash("Speed");
        static readonly int Listening = Animator.StringToHash("Listening");
        static readonly int AttackT = Animator.StringToHash("Attack");
        static readonly int HitT = Animator.StringToHash("Hit");
        static readonly int DieB = Animator.StringToHash("Dead");
        NavMeshAgent nav;

        void Awake() { nav = GetComponent<NavMeshAgent>(); if (!animator) animator = GetComponentInChildren<Animator>(); }

        void Update()
        {
            if (!animator) return;
            animator.SetFloat(Speed, nav.velocity.magnitude, 0.1f, Time.deltaTime);   // blend tree: idle/sneak/walk/run
        }

        public void SetListening(bool on) { if (animator) animator.SetBool(Listening, on); }
        public void Attack() { if (animator) animator.SetTrigger(AttackT); }
        public void Hit() { if (animator) animator.SetTrigger(HitT); }
        public void Die() { if (animator) animator.SetBool(DieB, true); }
        public void Revive() { if (animator) animator.SetBool(DieB, false); }
    }
}
```

Animator setup (Mixamo clips): a 1D blend tree on `Speed` (0 idle, 1.2 sneak/creep, 2.2 walk, 5 run), an additive/override "Listening" pose (head tilt), and triggers for Attack/Hit, a bool for Dead. Turn **Root Motion off** (NavMeshAgent drives position). Agent footsteps: Animation Events on foot contacts calling `Noise.Make(SoundType.Footstep, pos, 0.15f, SoundSource.AgentBody, gameObject)`.

## 8.6 Search pattern (shared helper)

```csharp
// Assets/Scripts/AI/Common/SearchPattern.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    public static class SearchPattern
    {
        /// Reachable points on a ring around `centre`, ordered so the agent sweeps around it.
        public static List<Vector3> Ring(Vector3 centre, float radius, int count)
        {
            var pts = new List<Vector3>();
            float start = Random.value * 360f;
            for (int i = 0; i < count; i++)
            {
                float a = (start + i * 360f / count) * Mathf.Deg2Rad;
                Vector3 p = centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius * Random.Range(0.6f, 1f);
                if (NavMesh.SamplePosition(p, out var hit, 2f, NavMesh.AllAreas) &&
                    !NavMesh.Raycast(centre, hit.position, out _, NavMesh.AllAreas))   // same room: no wall between
                    pts.Add(hit.position);
            }
            if (pts.Count == 0) pts.Add(centre);
            return pts;
        }
    }
}
```

## 8.7 Brain template (copy this for each agent)

Every agent's `Think()` follows the same shape. Only the contents differ.

```csharp
protected override void Think()
{
    // 1. PERCEPTION  – NewSounds already drained from Hearing.
    // 2. MEMORY      – update this agent's own memory structures from NewSounds.
    // 3. REFLEXES    – very close + fresh sound => attack (same for all agents, different thresholds).
    // 4. DECISION    – scoring / prediction / planning → maybe change state & goal.
    // 5. STATE TICK  – run the current state's logic (timers, arrival, failure, re-plan).
}
```

---

# 9. Agent 1 — LISTENER

**One-line identity:** *reacts to the most important thing it heard recently, investigates it, searches the area, and goes back.*

## 9.1 Perception & memory

- Hearing sensitivity **1.15** (good ears).
- Memory: only the **current target sound** + habituation list. (Deliberately short memory — that is the contrast with the Tracker.)

## 9.2 Priority function (the Listener's core)

```text
priority(s) = loudness(s) × typeWeight(s.type) × recency(s) × habituation(s)

recency(s)  = exp( −(now − s.timeHeard) / 8 s )
typeWeight  = Gunshot 1.0 · GlassBreak 0.9 · MetalImpact 0.75 · Land 0.7 · Door 0.6 · Run 0.6
              BulletImpact 0.55 · ObjectDrop 0.5 · Footstep 0.45 · Switch 0.4 · Generator 0.35
              Machinery 0.15 · AgentVoice 0.0
```

**Switching rule (hysteresis):** adopt a new sound only if `priority(new) > 1.25 × priority(current)` *or* the current target is older than 10 s. Without hysteresis the agent zig-zags between two sounds — a classic bug.

```csharp
// Assets/Scripts/AI/Listener/ListenerPriority.cs  (pure logic, unit-testable; the Mimic uses it too)
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    public static class ListenerPriority
    {
        public const float SwitchFactor = 1.25f;

        public static float TypeWeight(SoundType t) => t switch
        {
            SoundType.Gunshot => 1f, SoundType.GlassBreak => 0.9f, SoundType.MetalImpact => 0.75f,
            SoundType.Land => 0.7f, SoundType.Door => 0.6f, SoundType.Run => 0.6f,
            SoundType.BulletImpact => 0.55f, SoundType.ObjectDrop => 0.5f, SoundType.Footstep => 0.45f,
            SoundType.Switch => 0.4f, SoundType.Generator => 0.35f, SoundType.Machinery => 0.15f,
            _ => 0f
        };

        public static float Score(float loudness, SoundType type, float age, float habituation = 1f)
            => loudness * TypeWeight(type) * Mathf.Exp(-age / 8f) * habituation;
    }
}
```

## 9.3 State machine

```text
             loud/important sound                  arrived
   IDLE ──────────────────────────► INVESTIGATE ─────────────► SEARCH ──(points done / 20 s)──► RETURN ──► IDLE
     │  faint sound                     ▲   │ better sound (hysteresis)                 │
     ▼                                  │   └──────────────► INVESTIGATE (re-plan)       │ new sound
 SUSPICIOUS ──(2nd sound / 2.5 s)───────┘                                               ▼
     │ nothing                                                                   INVESTIGATE
     └──────────► IDLE
   any state:  sound ≤ 2.2 m, fresh, precise  ─or─  touch  ──►  ATTACK ──► SEARCH (tight radius)
```

| State | Entry | Each tick | Exit when |
|---|---|---|---|
| Idle | stop or slow patrol between 2–3 home nodes, `SetListening(true)` at each | consider sounds | any sound with priority ≥ 0.05 |
| Suspicious | face the sound, stand still (listening pose) | wait up to 2.5 s for a confirming sound | confirmation → Investigate (Walk); timeout → Investigate at Sneak if priority ≥ 0.1 else Idle |
| Investigate | `MoveTo(estimate, priority ≥ 0.5 ? Run : Walk)` | switch target with hysteresis; on arrive → Search; on fail → re-plan to a point 2 m closer, then Search | arrived / failed twice |
| Search | build `SearchPattern.Ring(centre, positionError + 2, 4)` | visit each point, pause 1.5 s listening at each | all points visited or 20 s → Return; new sound → Investigate |
| Return | `MoveTo(Home, Walk)` | — | arrived → Idle |
| Attack | `Body.TryAttack(pos)` | after cooldown → Search with radius 3 | — |

## 9.4 Implementation

```csharp
// Assets/Scripts/AI/Listener/ListenerBrain.cs
using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    public class ListenerBrain : AgentBrain
    {
        enum State { Idle, Suspicious, Investigate, Search, Return, Attack }

        [Header("Listener")]
        public float strikeDistance = 2.2f;
        public float suspiciousCutoff = 0.15f, runCutoff = 0.5f;
        public float searchDuration = 20f, searchPause = 1.5f;

        State state = State.Idle;
        PerceivedSound target; float targetPriority; bool hasTarget;
        float stateTime, pauseUntil;
        List<Vector3> searchPoints = new(); int searchIndex;
        int investigateFailures;

        public override string DisplayName => "LISTENER";
        public override string StateName => state.ToString();
        public override float AlertLevel => state switch { State.Investigate => 0.7f, State.Search => 0.5f,
                                                           State.Attack => 1f, State.Suspicious => 0.3f, _ => 0f };
        public override float CurrentInterest => hasTarget ? CurrentPriority() : 0f;
        public override string DebugText => hasTarget
            ? $"target {target.Type} p={CurrentPriority():0.00} err={target.PositionError:0.0}m" : "no target";

        float CurrentPriority() => ListenerPriority.Score(target.Loudness, target.Type,
                                        Time.time - target.TimeHeard, Habituation.Factor(target));

        protected override void Think()
        {
            // --- 3. reflex: very close, precise, fresh sound => strike
            foreach (var s in NewSounds)
                if (s.PositionError < 1.5f &&
                    Vector3.Distance(s.EstimatedPosition, transform.position) <= strikeDistance)
                { Strike(s.EstimatedPosition); return; }

            // --- 4. decision: pick the best new sound, apply hysteresis
            PerceivedSound best = default; float bestP = 0f;
            foreach (var s in NewSounds)
            {
                float p = ListenerPriority.Score(s.Loudness, s.Type, 0f, Habituation.Factor(s));
                if (p > bestP) { bestP = p; best = s; }
            }
            if (bestP > 0.05f)
            {
                bool better = !hasTarget || bestP > ListenerPriority.SwitchFactor * CurrentPriority()
                              || Time.time - target.TimeHeard > 10f;
                if (better) Adopt(best, bestP);
            }

            // --- 5. state tick
            stateTime += thinkInterval;
            switch (state)
            {
                case State.Suspicious:
                    Motor.FaceTowards(target.EstimatedPosition);
                    if (stateTime > 2.5f)
                    {
                        if (targetPriority >= 0.1f) BeginInvestigate(AgentMotor.Gait.Sneak);
                        else Enter(State.Idle);
                    }
                    break;

                case State.Investigate:
                    if (Arrived) BeginSearch(target.EstimatedPosition, target.PositionError + 2f);
                    else if (Failed)
                    {
                        if (++investigateFailures >= 2) BeginSearch(transform.position, 4f);
                        else Motor.MoveTo(Vector3.MoveTowards(target.EstimatedPosition, transform.position, 2f), Motor.CurrentGait);
                    }
                    break;

                case State.Search:
                    if (stateTime > searchDuration || searchIndex >= searchPoints.Count) { EndInvestigation(); break; }
                    if (Time.time < pauseUntil) break;                       // listening pause
                    if (!Motor.IsMoving)
                    {
                        if (Arrived || Failed)
                        {
                            searchIndex++; pauseUntil = Time.time + searchPause;
                            if (Anim) Anim.SetListening(true);
                        }
                        else if (searchIndex < searchPoints.Count)
                        {
                            if (Anim) Anim.SetListening(false);
                            Motor.MoveTo(searchPoints[searchIndex], AgentMotor.Gait.Walk);
                        }
                    }
                    break;

                case State.Return:
                    if (Arrived || Failed) Enter(State.Idle);
                    break;

                case State.Attack:
                    if (stateTime > 1.8f) BeginSearch(transform.position, 3f);
                    break;
            }
        }

        void Adopt(PerceivedSound s, float p)
        {
            target = s; targetPriority = p; hasTarget = true; investigateFailures = 0;
            if (p < suspiciousCutoff && state is State.Idle or State.Return) { Enter(State.Suspicious); Motor.Stop(); }
            else BeginInvestigate(p >= runCutoff ? AgentMotor.Gait.Run : AgentMotor.Gait.Walk);
        }

        void BeginInvestigate(AgentMotor.Gait gait)
        {
            Enter(State.Investigate);
            Motor.MoveTo(target.EstimatedPosition, gait);
        }

        void BeginSearch(Vector3 centre, float radius)
        {
            Enter(State.Search);
            searchPoints = SearchPattern.Ring(centre, Mathf.Clamp(radius, 2f, 8f), 4);
            searchIndex = 0; pauseUntil = Time.time + searchPause;
        }

        void EndInvestigation()
        {
            if (hasTarget && target.Type is SoundType.Machinery or SoundType.Generator
                                         or SoundType.ObjectDrop or SoundType.Switch)
                Habituation.MarkBoring(target.Type, target.EstimatedPosition);
            hasTarget = false;
            Enter(State.Return);
            Motor.MoveTo(Home, AgentMotor.Gait.Walk);
        }

        void Strike(Vector3 where) { Enter(State.Attack); Body.TryAttack(where); }

        public override void OnContact(Vector3 where) => Strike(where);

        public override void OnHurt(Vector3 from)
        {
            Adopt(new PerceivedSound { Type = SoundType.Gunshot, EstimatedPosition = from,
                                       PositionError = 2f, Loudness = 1f, TimeHeard = Time.time }, 1f);
        }

        public override void ResetAgent()
        {
            base.ResetAgent(); hasTarget = false; Enter(State.Idle);
            transform.position = Home;
        }

        void Enter(State s) { state = s; stateTime = 0f; }
    }
}
```

## 9.5 What to demonstrate

1. Shoot → Listener runs to the area → searches 4 points with listening pauses → returns.
2. Throw a can far away while it is investigating → it switches (hysteresis visible in the debug overlay).
3. Turn the generator on twice → second time it ignores it (habituation).

---

# 10. Agent 2 — TRACKER

**One-line identity:** *builds a short history of where sounds came from, estimates the player's velocity, rejects implausible sounds, predicts where the player is going and cuts them off.*

## 10.1 Memory

```text
history: ring buffer of up to 10 observations within the last 25 s
  Observation { position, time, positionError, credibility (0..1), type }
```

## 10.2 Credibility (makes the Tracker resistant to the Mimic)

A real player cannot teleport. For a new observation `o` and the previous credible one `p`:

```text
allowed = maxPlayerSpeed × (o.t − p.t) + o.err + p.err + 2 m       (maxPlayerSpeed ≈ 6.5 m/s)
d       = graph route distance between the two estimates (NavGraph.SoundDistance between nearest nodes)
credibility = 1                        if d ≤ allowed
            = exp( −(d − allowed) / 4 ) otherwise
```

Observations with credibility < 0.3 go to a *suspect* list. If two suspects agree with each other (consistent with walking between them), the Tracker concludes the player really moved (or its history was stale) and **resets** its history to the suspects. This is a simple form of **outlier rejection / data association** — worth naming in the viva.

## 10.3 Velocity estimation (weighted least squares)

Fit position as a linear function of time over the credible observations, weighting recent ones more:

```text
w_i  = credibility_i × exp( −(now − t_i) / τ )      τ = 8 s
t̄   = Σ w_i t_i / Σ w_i         p̄ = Σ w_i p_i / Σ w_i
v    = Σ w_i (t_i − t̄)(p_i − p̄) / Σ w_i (t_i − t̄)²       (vector, flattened to XZ)
|v| is clamped to maxPlayerSpeed
```

Needs ≥ 2 credible observations spanning ≥ 1 s; otherwise the Tracker behaves like a careful Listener (investigate latest).

## 10.4 Prediction → candidate scoring

```text
lead      = (now − t_last) + ETA_tracker          (iterate twice: guess ETA from straight-line distance / runSpeed)
p_pred    = p_last + v × lead                      (clamped to reachable distance)
reach     = maxPlayerSpeed × lead + 5 m

candidates = graph nodes whose route distance from node(p_last) ≤ reach

score(n) = 1.0 × max(0, cos∠(v, n − p_last))            direction agreement
         + 1.2 × exp( −|n − p_pred|² / (2 · 6²) )          close to the extrapolated point
         + 0.4 × heat(n)                                   where sounds have been heard before (0..1)
         + 0.3 × [n is Junction or Doorway]                choke points are good intercepts
         − 0.6 × [n is DeadEnd]
         − 0.5 × [Tracker would have to pass p_last to get there]   (don't run through the player's position)

prediction confidence = (best − second) / best         (0 = ambiguous, 1 = obvious)
```

`heat(n)` = normalised count of all heard sounds whose nearest node was `n` (the Tracker's long-term memory, decays ×0.995 per tick).

## 10.5 States

```text
IDLE (listen at hub) ──sound──► ANALYZE ──(<2 credible obs)──► INVESTIGATE_LATEST ─► SEARCH
                                  │
                                  └──(≥2)──► PREDICT ──► INTERCEPT (run to predicted node)
                                                  ▲            │ arrived
                       new credible obs moved     │            ▼
                       prediction > 6 m  ─────────┘        HOLD (4 s, listening) ──nothing──► SEARCH ──► IDLE
any state: close/precise sound or touch ──► ATTACK
```

## 10.6 Implementation (pure predictor + brain)

```csharp
// Assets/Scripts/AI/Tracker/TrackerPredictor.cs   (plain C#, unit-testable)
using System.Collections.Generic;
using UnityEngine;

namespace Echo.AI
{
    public class TrackerPredictor
    {
        public struct Obs { public Vector3 P; public float T, Err, Cred; }

        public float MaxPlayerSpeed = 6.5f, Window = 25f, Tau = 8f;
        public int Capacity = 10;
        public readonly List<Obs> History = new();
        readonly List<Obs> suspects = new();

        public System.Func<Vector3, Vector3, float> RouteDistance = Vector3.Distance; // injected: graph distance

        public float Add(Vector3 p, float t, float err)
        {
            History.RemoveAll(o => t - o.T > Window);
            float cred = 1f;
            if (History.Count > 0)
            {
                var last = History[^1];
                float allowed = MaxPlayerSpeed * (t - last.T) + err + last.Err + 2f;
                float d = RouteDistance(last.P, p);
                if (d > allowed) cred = Mathf.Exp(-(d - allowed) / 4f);
            }

            var o = new Obs { P = p, T = t, Err = err, Cred = cred };
            if (cred < 0.3f)
            {
                suspects.Add(o);
                if (suspects.Count >= 2 && Consistent(suspects[^2], suspects[^1]))
                { History.Clear(); History.AddRange(suspects); suspects.Clear(); }   // player really moved
                return cred;
            }
            History.Add(o);
            if (History.Count > Capacity) History.RemoveAt(0);
            return cred;
        }

        bool Consistent(Obs a, Obs b) =>
            RouteDistance(a.P, b.P) <= MaxPlayerSpeed * Mathf.Abs(b.T - a.T) + a.Err + b.Err + 2f;

        /// Weighted least-squares velocity on the XZ plane. Returns false if not enough evidence.
        public bool TryVelocity(float now, out Vector3 v)
        {
            v = Vector3.zero;
            if (History.Count < 2 || History[^1].T - History[0].T < 1f) return false;
            float sw = 0, st = 0; Vector3 sp = Vector3.zero;
            foreach (var o in History) { float w = o.Cred * Mathf.Exp(-(now - o.T) / Tau); sw += w; st += w * o.T; sp += w * o.P; }
            if (sw <= 1e-4f) return false;
            float tBar = st / sw; Vector3 pBar = sp / sw;
            float den = 0; Vector3 num = Vector3.zero;
            foreach (var o in History)
            {
                float w = o.Cred * Mathf.Exp(-(now - o.T) / Tau);
                float dt = o.T - tBar; den += w * dt * dt; num += w * dt * (o.P - pBar);
            }
            if (den <= 1e-4f) return false;
            v = num / den; v.y = 0f;
            v = Vector3.ClampMagnitude(v, MaxPlayerSpeed);
            return true;
        }

        public Obs Last => History[^1];
        public void Clear() { History.Clear(); suspects.Clear(); }
    }
}
```

```csharp
// Assets/Scripts/AI/Tracker/TrackerBrain.cs
using Echo.Navigation;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    public class TrackerBrain : AgentBrain
    {
        enum State { Idle, InvestigateLatest, Intercept, Hold, Search, Attack }

        [Header("Tracker")]
        public float strikeDistance = 2.2f, holdTime = 4f, replanThreshold = 6f;

        readonly TrackerPredictor predictor = new();
        float[] heat;
        State state = State.Idle;
        float stateTime, confidence;
        Vector3 predicted, velocity;

        public override string DisplayName => "TRACKER";
        public override string StateName => state.ToString();
        public override float AlertLevel => state is State.Intercept or State.Attack ? 0.9f : state == State.Idle ? 0f : 0.5f;
        public override float CurrentInterest => predictor.History.Count > 0 ? 0.6f : 0f;
        public override string DebugText =>
            $"obs {predictor.History.Count}  v={velocity.magnitude:0.0}m/s  conf={confidence:0.00}";

        protected override void Awake()
        {
            base.Awake();
            predictor.RouteDistance = (a, b) =>
            {
                var g = NavGraph.Instance; int na = g.Nearest(a), nb = g.Nearest(b);
                return g.SoundDistance(na, nb) + Vector3.Distance(a, g.Position(na)) + Vector3.Distance(b, g.Position(nb));
            };
        }

        void Start() { heat = new float[NavGraph.Instance.Count]; }

        protected override void Think()
        {
            var g = NavGraph.Instance;
            for (int i = 0; i < heat.Length; i++) heat[i] *= 0.995f;

            // PERCEPTION -> MEMORY
            bool gotCredible = false;
            foreach (var s in NewSounds)
            {
                if (s.PositionError < 1.5f && Vector3.Distance(s.EstimatedPosition, transform.position) <= strikeDistance)
                { Enter(State.Attack); Body.TryAttack(s.EstimatedPosition); return; }
                if (ListenerPriority.TypeWeight(s.Type) < 0.3f) continue;          // ignore machinery etc.
                float cred = predictor.Add(s.EstimatedPosition, s.TimeHeard, s.PositionError);
                heat[g.Nearest(s.EstimatedPosition)] += 1f;
                gotCredible |= cred >= 0.3f;
            }

            // DECISION
            if (gotCredible)
            {
                if (predictor.TryVelocity(Time.time, out velocity))
                {
                    Vector3 newPrediction = Predict(out confidence);
                    if (state != State.Intercept || Vector3.Distance(newPrediction, predicted) > replanThreshold)
                    {
                        predicted = newPrediction;
                        Enter(State.Intercept);
                        Motor.MoveTo(predicted, AgentMotor.Gait.Run);
                    }
                }
                else
                {
                    Enter(State.InvestigateLatest);
                    Motor.MoveTo(predictor.Last.P, AgentMotor.Gait.Walk);
                }
            }

            // STATE TICK
            stateTime += thinkInterval;
            switch (state)
            {
                case State.InvestigateLatest:
                case State.Intercept:
                    if (Arrived) Enter(State.Hold);
                    else if (Failed) Enter(State.Search);
                    break;
                case State.Hold:
                    Motor.Stop();
                    if (stateTime > holdTime) { Enter(State.Search); Motor.MoveTo(predictor.History.Count > 0 ? predictor.Last.P : Home, AgentMotor.Gait.Walk); }
                    break;
                case State.Search:
                    if (Arrived || Failed || stateTime > 15f)
                    { predictor.Clear(); Enter(State.Idle); Motor.MoveTo(Home, AgentMotor.Gait.Walk); }
                    break;
                case State.Attack:
                    if (stateTime > 1.8f) Enter(State.Search);
                    break;
            }
        }

        Vector3 Predict(out float conf)
        {
            var g = NavGraph.Instance;
            var last = predictor.Last;
            float eta = Vector3.Distance(transform.position, last.P) / Motor.runSpeed;
            float lead = (Time.time - last.T) + eta;
            Vector3 pPred = last.P + velocity * lead;
            float reach = predictor.MaxPlayerSpeed * lead + 5f;

            int from = g.Nearest(last.P), me = g.Nearest(transform.position);
            float maxHeat = 1e-3f; foreach (var h in heat) maxHeat = Mathf.Max(maxHeat, h);
            float best = float.MinValue, second = float.MinValue; int bestNode = from;
            Vector3 dir = velocity.sqrMagnitude > 0.01f ? velocity.normalized : Vector3.zero;

            for (int n = 0; n < g.Count; n++)
            {
                if (g.SoundDistance(from, n) > reach) continue;
                Vector3 np = g.Position(n);
                Vector3 off = np - last.P; off.y = 0f;
                float score = 1.0f * Mathf.Max(0f, off.sqrMagnitude > 0.01f ? Vector3.Dot(dir, off.normalized) : 0f)
                            + 1.2f * Mathf.Exp(-(np - pPred).sqrMagnitude / (2f * 36f))
                            + 0.4f * heat[n] / maxHeat;
                var tags = g.Node(n).tags;
                if ((tags & (NodeTag.Junction | NodeTag.Doorway)) != 0) score += 0.3f;
                if ((tags & NodeTag.DeadEnd) != 0) score -= 0.6f;
                if (g.SoundDistance(me, n) > g.SoundDistance(me, from) + g.SoundDistance(from, n) - 1f) score -= 0.5f; // must pass the player
                if (score > best) { second = best; best = score; bestNode = n; }
                else if (score > second) second = score;
            }
            conf = best > 0f && second > float.MinValue ? Mathf.Clamp01((best - second) / best) : 0f;
            return g.Position(bestNode);
        }

        public override void OnContact(Vector3 where) { Enter(State.Attack); Body.TryAttack(where); }
        public override void OnHurt(Vector3 from) { predictor.Add(from, Time.time, 2f); }
        public override void ResetAgent() { base.ResetAgent(); predictor.Clear(); Enter(State.Idle); transform.position = Home; }

        void Enter(State s) { state = s; stateTime = 0f; }

        void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;
            Gizmos.color = new Color(1f, 0.6f, 0f);
            for (int i = 1; i < predictor.History.Count; i++)
                Gizmos.DrawLine(predictor.History[i - 1].P + Vector3.up, predictor.History[i].P + Vector3.up);
            if (predictor.History.Count > 0)
            {
                Gizmos.DrawRay(predictor.Last.P + Vector3.up, velocity);
                Gizmos.color = Color.red; Gizmos.DrawWireSphere(predicted, 0.8f);
            }
        }
    }
}
```

## 10.7 What to demonstrate

1. Walk/run a straight route making noise → orange breadcrumb line + velocity arrow + red predicted point **ahead** of you → Tracker arrives there before you.
2. Mimic fakes a sound far away → Tracker's debug shows `cred 0.05 (suspect)` and it **does not** take the bait, while the Listener does.
3. Explain: *Listener uses one sound; Tracker uses a sequence.*

---

# 11. Agent 3 — MIMIC

**One-line identity:** *a manipulator. It barely hunts; it decides where and which fake sound to make so that the **other agents** and **the player** end up where it wants them.*

## 11.1 What makes it intelligent

The Mimic uses a **model of the other agents' decision functions** (it knows the Listener's priority formula and hysteresis rule because they are public static code) to **simulate** "if I make sound X at place P, who will react, and will that help?". This is utility-based decision making with a forward model — a strong IS talking point.

## 11.2 Knowledge

- Its own `Hearing` (sensitivity 0.9) → **player estimate** = last heard sound position, time, error; plus the **type of the player's most recent sound** (used for mimicry).
- `AgentRegistry` → for every other agent: position, `HearingSensitivity`, `CurrentInterest`, `StateName`, and (for the Ambusher) its current ambush point.

## 11.3 Deception actions

| Action | When | Where to place the fake | Sound type | Intended effect |
|---|---|---|---|---|
| **Summon** | player estimate is fresh (< 15 s) and some hunter is idle/far | near the player estimate (0–3 m offset) | high-weight type (MetalImpact/GlassBreak), or the player's own last type | pull hunters onto the player |
| **Flush** | player silent for > 12 s near a known spot (hiding) | node at the border of the player's zone **opposite** to the Ambusher's ambush point | Door slam / AgentVoice growl | scare the player into moving (moving = noise) toward the ambush |
| **Herd** | Ambusher is waiting and confident | a node on the *alternative* route the player could take | AgentVoice / heavy footsteps | make the alternative route sound occupied |
| **Cover** | Mimic itself was hurt or a gunshot was loud near it | 10–15 m away, away from the Mimic | a *moving* sequence of Run footsteps (4 steps over 2 s) | escape while everyone chases the trail |

## 11.4 Utility function

For each action `a`, candidate node `n` (within **voice range 18 m** of the Mimic — it projects its voice; farther nodes require relocating first), and sound type `k`:

```text
U(a, n, k) = Gain(a, n, k) − Risk(n) − Repetition(n, k) − TravelCost(n)

ReactsTo(h, n, k) = σ( 8 × ( Loudness(intensity_k, h.sensitivity, EffDist(n, h)) × typeWeight(k)
                             − 1.25 × h.CurrentInterest ) )            ← simulated Listener rule (σ = logistic)

Summon gain = Σ_hunters ReactsTo(h,n,k) × max(0, dist(h, playerEst) − dist(n, playerEst)) / 20
Flush  gain = alignment( playerEst → away from n , playerEst → ambushPoint ) × (ambusher waiting ? 1 : 0.4)
Herd   gain = [n on the alternative route] × ambusher confidence
Cover  gain = 1 if threatened else 0

Risk       = 0.6 if dist(Mimic, playerEst) < 8 m        (don't reveal itself)
Repetition = 0.2 if k == lastFakeType  +  0.3 if n used in the last 30 s
TravelCost = 0.02 × metres the Mimic must walk to get within voice range
Act only if U > 0.15. Cooldown 10–15 s after each fake (random), never more than one fake in flight.
```

**Mimicry flavour:** when the action allows several types, prefer the **player's own most recent sound type** — the player hears their own door creak somewhere else. Creepy, cheap, and on-concept.

## 11.5 States

```text
OBSERVE (hide at a relocation node, listen) ──decision tick (1 s)──► SELECT_DECEPTION
      ▲                                                                    │ best U > 0.15
      │                                                                    ▼
   COOLDOWN ◄── GENERATE_SOUND ◄── RELOCATE (move within voice range, Sneak)
      │  (10–15 s)
      └── if threatened ──► COVER (emit moving trail) + RELOCATE away (Run)
```

## 11.6 Implementation

```csharp
// Assets/Scripts/AI/Mimic/MimicBrain.cs
using System.Collections;
using Echo.Navigation;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    public class MimicBrain : AgentBrain
    {
        enum State { Observe, Relocate, Generate, Cooldown, Flee }
        enum Action { None, Summon, Flush, Herd, Cover }

        [Header("Mimic")]
        public float voiceRange = 18f, decisionInterval = 1f;
        public Vector2 cooldownRange = new(10f, 15f);

        State state = State.Observe;
        float stateTime, nextDecision, cooldownUntil;
        Vector3 playerEst; float playerEstTime = -999f, playerEstErr = 99f; SoundType playerLastType = SoundType.Footstep;
        Action plannedAction; int plannedNode = -1; SoundType plannedType; float plannedUtility;
        SoundType lastFakeType; int lastFakeNode = -1; float lastFakeTime = -999f;

        public override string DisplayName => "MIMIC";
        public override string StateName => state.ToString();
        public override float AlertLevel => state == State.Generate ? 0.4f : 0.1f;
        public override string DebugText => $"plan {plannedAction} node {plannedNode} U={plannedUtility:0.00}  player est age {Time.time - playerEstTime:0}s";

        protected override void Think()
        {
            // PERCEPTION -> MEMORY (the Mimic keeps a single "best guess" of the player)
            foreach (var s in NewSounds)
            {
                if (ListenerPriority.TypeWeight(s.Type) < 0.3f) continue;
                playerEst = s.EstimatedPosition; playerEstTime = s.TimeHeard; playerEstErr = s.PositionError;
                playerLastType = s.Type;
                if (s.Type == SoundType.Gunshot && Vector3.Distance(s.EstimatedPosition, transform.position) < 12f)
                    StartCover();
            }

            stateTime += thinkInterval;
            switch (state)
            {
                case State.Observe:
                    if (Time.time >= nextDecision && Time.time >= cooldownUntil)
                    {
                        nextDecision = Time.time + decisionInterval;
                        if (SelectDeception()) BeginRelocateOrGenerate();
                    }
                    break;
                case State.Relocate:
                    if (Arrived || InVoiceRange(plannedNode)) { Motor.Stop(); Enter(State.Generate); }
                    else if (Failed || stateTime > 12f) Enter(State.Observe);
                    break;
                case State.Generate:
                    Emit(plannedAction, plannedNode, plannedType);
                    cooldownUntil = Time.time + Random.Range(cooldownRange.x, cooldownRange.y);
                    Enter(State.Cooldown);
                    Motor.MoveTo(HidingSpotAwayFrom(playerEst), AgentMotor.Gait.Sneak);
                    break;
                case State.Cooldown:
                    if (Time.time >= cooldownUntil) Enter(State.Observe);
                    break;
                case State.Flee:
                    if (Arrived || Failed || stateTime > 8f) { Enter(State.Cooldown); }
                    break;
            }
        }

        // ---------- decision ----------
        bool SelectDeception()
        {
            var g = NavGraph.Instance;
            float playerAge = Time.time - playerEstTime;
            if (playerAge > 40f) return false;                                     // no idea where the player is

            AmbusherBrain ambusher = null;
            foreach (var a in AgentRegistry.All) if (a is AmbusherBrain ab) ambusher = ab;

            plannedUtility = 0.15f; plannedAction = Action.None;
            for (int n = 0; n < g.Count; n++)
            {
                Vector3 np = g.Position(n);
                float travel = Mathf.Max(0f, Vector3.Distance(transform.position, np) - voiceRange);
                float risk = Vector3.Distance(transform.position, playerEst) < 8f ? 0.6f : 0f;

                // SUMMON: fake near the player's estimate to pull hunters in
                if (playerAge < 15f && Vector3.Distance(np, playerEst) < 3f + playerEstErr)
                    Consider(Action.Summon, n, PreferMimicry(SoundType.MetalImpact), SummonGain(np, SoundType.MetalImpact), risk, travel);

                // FLUSH: player has been silent (hiding) -> scare them toward the ambush
                if (playerAge > 12f && ambusher != null && ambusher.HasAmbushPoint)
                {
                    Vector3 away = (playerEst - np); away.y = 0f;
                    Vector3 toAmbush = ambusher.AmbushPoint - playerEst; toAmbush.y = 0f;
                    float dist = Vector3.Distance(np, playerEst);
                    if (dist > 4f && dist < 12f && away.sqrMagnitude > 0.01f && toAmbush.sqrMagnitude > 0.01f)
                    {
                        float align = Vector3.Dot(away.normalized, toAmbush.normalized);
                        Consider(Action.Flush, n, SoundType.Door, Mathf.Max(0f, align) * (ambusher.IsWaiting ? 1f : 0.4f), risk, travel);
                    }
                }
            }
            return plannedAction != Action.None;
        }

        void Consider(Action a, int node, SoundType type, float gain, float risk, float travel)
        {
            float rep = (type == lastFakeType ? 0.2f : 0f) + (node == lastFakeNode && Time.time - lastFakeTime < 30f ? 0.3f : 0f);
            float u = gain - risk - rep - 0.02f * travel;
            if (u > plannedUtility) { plannedUtility = u; plannedAction = a; plannedNode = node; plannedType = type; }
        }

        /// Forward model: simulate each hunter's own reaction rule.
        float SummonGain(Vector3 at, SoundType type)
        {
            float intensity = IntensityOf(type), gain = 0f;
            foreach (var h in AgentRegistry.All)
            {
                if (h == this || h is MimicBrain || !h.IsAlive) continue;
                float loud = Acoustics.Loudness(intensity, h.HearingSensitivity,
                                                Acoustics.EffectiveDistance(at, h.transform.position + Vector3.up * 1.6f));
                float drive = loud * ListenerPriority.TypeWeight(type) - ListenerPriority.SwitchFactor * h.CurrentInterest;
                float reacts = 1f / (1f + Mathf.Exp(-8f * drive));
                float improvement = Vector3.Distance(h.transform.position, playerEst) - Vector3.Distance(at, playerEst);
                gain += reacts * Mathf.Max(0f, improvement) / 20f;
            }
            return gain;
        }

        SoundType PreferMimicry(SoundType fallback) =>
            playerLastType is SoundType.Door or SoundType.MetalImpact or SoundType.ObjectDrop or SoundType.GlassBreak
                ? playerLastType : fallback;

        static float IntensityOf(SoundType t) => t switch
        {
            SoundType.GlassBreak => 0.8f, SoundType.MetalImpact => 0.6f, SoundType.Door => 0.55f,
            SoundType.Run => 0.3f, SoundType.Footstep => 0.15f, SoundType.AgentVoice => 0.5f, _ => 0.4f
        };

        // ---------- action ----------
        void BeginRelocateOrGenerate()
        {
            if (InVoiceRange(plannedNode)) { Enter(State.Generate); return; }
            Enter(State.Relocate);
            Vector3 target = NavGraph.Instance.Position(plannedNode);
            Vector3 standOff = target + (transform.position - target).normalized * (voiceRange * 0.8f);
            Motor.MoveTo(standOff, AgentMotor.Gait.Sneak);
        }

        bool InVoiceRange(int node) => node >= 0 && Vector3.Distance(transform.position, NavGraph.Instance.Position(node)) <= voiceRange;

        void Emit(Action a, int node, SoundType type)
        {
            Vector3 p = NavGraph.Instance.Position(node);
            // The fake is REAL audio: the player hears it too.
            Noise.Make(type, p, IntensityOf(type), SoundSource.Mimic, gameObject);
            lastFakeType = type; lastFakeNode = node; lastFakeTime = Time.time;
        }

        void StartCover()
        {
            if (state == State.Flee) return;
            Vector3 hide = HidingSpotAwayFrom(playerEst);
            Enter(State.Flee);
            Motor.MoveTo(hide, AgentMotor.Gait.Run);
            StartCoroutine(FakeTrail(transform.position, transform.position + (transform.position - hide).normalized * 12f));
        }

        IEnumerator FakeTrail(Vector3 from, Vector3 to)
        {
            for (int i = 1; i <= 4; i++)
            {
                Noise.Make(SoundType.Run, Vector3.Lerp(from, to, i / 4f), 0.3f, SoundSource.Mimic, gameObject);
                yield return new WaitForSeconds(0.5f);
            }
        }

        Vector3 HidingSpotAwayFrom(Vector3 threat)
        {
            var g = NavGraph.Instance; int best = 0; float bestScore = float.MinValue;
            for (int n = 0; n < g.Count; n++)
            {
                float d = Vector3.Distance(g.Position(n), threat);
                float near = Vector3.Distance(g.Position(n), transform.position);
                float s = Mathf.Min(d, 30f) - 0.3f * near + ((g.Node(n).tags & NodeTag.Room) != 0 ? 3f : 0f);
                if (s > bestScore) { bestScore = s; best = n; }
            }
            return g.Position(best);
        }

        public override void OnHurt(Vector3 from) { playerEst = from; playerEstTime = Time.time; StartCover(); }
        public override void OnContact(Vector3 where) { Body.TryAttack(where); StartCover(); }
        public override void ResetAgent() { base.ResetAgent(); playerEstTime = -999f; Enter(State.Observe); transform.position = Home; }

        void Enter(State s) { state = s; stateTime = 0f; }
    }
}
```

## 11.7 Design notes

- **Fakes can backfire** — a Summon placed on a *wrong* player estimate drags the Listener away from the real player. That is the plan's "Example 3" (player escapes thanks to a fake) emerging naturally, not scripted.
- The **Tracker** rejects many fakes (credibility), the **Listener** does not. Point this out in the demo.
- Keep fakes **rare** (10–15 s cooldown). If players hear fakes every few seconds, sound stops meaning anything.

## 11.8 What to demonstrate

1. Hide silently → after ~12 s a door slams in the next room (Flush) → you move → the Ambusher is waiting.
2. Make a noise → Mimic places a metal clang near you → the Listener comes running (Summon) — debug overlay shows the Mimic's chosen action and utility.
3. Shoot near the Mimic → it flees while a trail of footsteps runs the other way (Cover).

---

# 12. Agent 4 — AMBUSHER

**One-line identity:** *learns which way the player tends to move between rooms, predicts the route, gets there first, and waits in silence.*

## 12.1 How a blind agent "observes player movement"

It cannot see the player, so **every heard sound is mapped to a zone** (`ZoneVolume.ZoneAt(estimate)`). A change of zone between consecutive credible sounds is a **zone transition** `A → B`. That is its training data.

To give it enough data, the Ambusher has the best ears (sensitivity **1.4**) and — optionally — taps the facility's **acoustic sensors** (see §12.6): microphones in a few corridors (the Security Cam props are perfect for this) that report only *which zone* a sound happened in.

## 12.2 Route model (Markov chain with forgetting)

```text
count[A][B]  += 1 on each observed transition A → B          (only between adjacent zones)
every tick:  count[A][B] ×= exp(−Δt / 120 s)                  (forget old habits)

P(B | A) = (count[A][B] + α) / (Σ_C count[A][C] + α · deg(A))   α = 0.5  (Laplace smoothing)
```

**Upgrade (optional, better for corridors):** second-order chain `P(C | A, B)` — the next zone depends on where the player came from. Use it when it has ≥ 3 observations, otherwise fall back to first order.

**Predicting where the player will pass** (forward propagation over a horizon H = 3 transitions):

```text
π₀ = one-hot(current player zone)
for k = 1..H:   π_k = π_{k−1} · P          (distribution over zones after k moves)
pass(Z) = 1 − Π_k (1 − π_k(Z))             (probability of passing through zone Z at least once)
route confidence = max_Z pass(Z) over zones ≠ current
```

## 12.3 Choosing the interception point

```text
candidates: nodes tagged AmbushSpot | Doorway | Junction in zones with pass(Z) ≥ 0.25

playerETA(n)   = graphDistance(playerLastNode, n) / 2.5 m/s  − (now − lastHeard)
ambusherETA(n) = graphDistance(myNode, n) / runSpeed  + 2 s (settle in)
feasible(n)    = σ( (playerETA − ambusherETA) / 2 s )        (soft: 1 = surely first, 0 = surely late)

quality(n)     = 1.0 AmbushSpot · 0.7 Doorway · 0.5 Junction
novelty(n)     = 0.6 if used in the last 60 s else 1           (don't camp the same corner)

score(n) = pass(zone(n)) × quality(n) × feasible(n) × novelty(n)
commit if score ≥ 0.2 and route confidence ≥ 0.35; else stay in OBSERVE at a hub
```

Cost modifier while moving to the ambush (so it does not bump into the player):

```csharp
Motor.CostModifier = (from, to, c) =>
    Vector3.Distance(NavGraph.Instance.Position(to), playerEstimate) < 10f ? c * 3f : c;   // ≥ c → A* stays admissible
```

## 12.4 States

```text
OBSERVE (hub, listening, learning) ──confident prediction──► SELECT_AMBUSH_POINT ──► MOVE_TO_AMBUSH (Run far, Sneak last 8 m)
     ▲                                                                                     │ arrived
     │                                                                                     ▼
     └──── RELOCATE ◄── timeout 25 s / player heard on a different route ─────────────  WAIT (silent, listening)
                                                                                           │ sound ≤ 5 m, fresh
                                                                                           ▼
                                                                                         ATTACK (lunge) ──► OBSERVE
```

## 12.5 Implementation

```csharp
// Assets/Scripts/AI/Ambusher/RouteModel.cs   (plain C#, unit-testable)
using System.Collections.Generic;
using UnityEngine;

namespace Echo.AI
{
    public class RouteModel
    {
        public float Alpha = 0.5f, ForgetTau = 120f;
        readonly Dictionary<string, Dictionary<string, float>> counts = new();
        readonly Dictionary<string, HashSet<string>> adjacency;   // zone graph
        string lastZone; float lastTime;
        public string CurrentZone => lastZone;

        public RouteModel(Dictionary<string, HashSet<string>> zoneAdjacency) { adjacency = zoneAdjacency; }

        public void Observe(string zone, float time)
        {
            if (string.IsNullOrEmpty(zone)) return;
            Decay(time);
            if (lastZone != null && zone != lastZone && adjacency.TryGetValue(lastZone, out var nbrs) && nbrs.Contains(zone))
            {
                if (!counts.TryGetValue(lastZone, out var row)) counts[lastZone] = row = new Dictionary<string, float>();
                row[zone] = (row.TryGetValue(zone, out var c) ? c : 0f) + 1f;
            }
            lastZone = zone; lastTime = time;
        }

        void Decay(float time)
        {
            float k = Mathf.Exp(-(time - lastTime) / ForgetTau);
            if (k >= 0.999f) return;
            foreach (var row in counts.Values)
                foreach (var key in new List<string>(row.Keys)) row[key] *= k;
        }

        public float P(string from, string to)
        {
            if (!adjacency.TryGetValue(from, out var nbrs) || !nbrs.Contains(to)) return 0f;
            counts.TryGetValue(from, out var row);
            float total = 0f; if (row != null) foreach (var v in row.Values) total += v;
            float c = row != null && row.TryGetValue(to, out var x) ? x : 0f;
            return (c + Alpha) / (total + Alpha * nbrs.Count);
        }

        /// Probability the player passes through each zone within `horizon` transitions.
        public Dictionary<string, float> PassProbability(string from, int horizon = 3)
        {
            var pass = new Dictionary<string, float>();
            var pi = new Dictionary<string, float> { [from] = 1f };
            var notYet = new Dictionary<string, float>();
            for (int k = 0; k < horizon; k++)
            {
                var next = new Dictionary<string, float>();
                foreach (var (z, pz) in pi)
                    if (adjacency.TryGetValue(z, out var nbrs))
                        foreach (var n in nbrs)
                            next[n] = (next.TryGetValue(n, out var v) ? v : 0f) + pz * P(z, n);
                foreach (var (z, pz) in next)
                    notYet[z] = (notYet.TryGetValue(z, out var q) ? q : 1f) * (1f - pz);
                pi = next;
            }
            foreach (var (z, q) in notYet) if (z != from) pass[z] = 1f - q;
            return pass;
        }

        public float TotalObservations()
        {
            float t = 0f; foreach (var row in counts.Values) foreach (var v in row.Values) t += v; return t;
        }

        public void Clear() { counts.Clear(); lastZone = null; }
    }
}
```

```csharp
// Assets/Scripts/AI/Ambusher/AmbusherBrain.cs
using System.Collections.Generic;
using Echo.Navigation;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    public class AmbusherBrain : AgentBrain
    {
        enum State { Observe, MoveToAmbush, Wait, Attack, Relocate }

        [Header("Ambusher")]
        public float strikeDistance = 5f, waitTimeout = 25f, minConfidence = 0.35f, playerSpeedGuess = 2.5f;
        public bool keepMemoryAcrossDeaths = true;     // "it remembers you"

        RouteModel model;
        State state = State.Observe;
        float stateTime, confidence;
        Vector3 playerEst; float playerEstTime = -999f; int ambushNode = -1;
        readonly Dictionary<int, float> lastUsed = new();
        Dictionary<string, float> lastPass = new();

        public bool HasAmbushPoint => ambushNode >= 0 && state is State.MoveToAmbush or State.Wait;
        public bool IsWaiting => state == State.Wait;
        public Vector3 AmbushPoint => ambushNode >= 0 ? NavGraph.Instance.Position(ambushNode) : transform.position;

        public override string DisplayName => "AMBUSHER";
        public override string StateName => state.ToString();
        public override float AlertLevel => state == State.Attack ? 1f : state == State.Wait ? 0.5f : 0.1f;
        public override string DebugText =>
            $"zone {model?.CurrentZone ?? "-"}  obs {model?.TotalObservations():0.0}  conf {confidence:0.00}  node {ambushNode}";

        void Start()
        {
            model = new RouteModel(BuildZoneAdjacency());
            Motor.CostModifier = (from, to, c) =>
                Time.time - playerEstTime < 20f &&
                Vector3.Distance(NavGraph.Instance.Position(to), playerEst) < 10f ? c * 3f : c;
        }

        /// Called by AcousticSensor (optional) – zone-level reports from facility microphones.
        public void SensorReport(string zone, Vector3 approx, float time)
        {
            model.Observe(zone, time); playerEst = approx; playerEstTime = time;
        }

        protected override void Think()
        {
            // PERCEPTION -> MEMORY (learning happens continuously, whatever the state)
            foreach (var s in NewSounds)
            {
                if (ListenerPriority.TypeWeight(s.Type) < 0.3f) continue;
                model.Observe(ZoneVolume.ZoneAt(s.EstimatedPosition), s.TimeHeard);
                playerEst = s.EstimatedPosition; playerEstTime = s.TimeHeard;

                if (state == State.Wait && Vector3.Distance(s.EstimatedPosition, transform.position) <= strikeDistance)
                { Enter(State.Attack); Motor.MoveTo(s.EstimatedPosition, AgentMotor.Gait.Run); }
            }

            stateTime += thinkInterval;
            switch (state)
            {
                case State.Observe:
                case State.Relocate:
                    if (stateTime > 1f && TrySelectAmbush()) BeginMove();
                    else if (state == State.Relocate && (Arrived || Failed)) Enter(State.Observe);
                    break;

                case State.MoveToAmbush:
                    if (Vector3.Distance(transform.position, AmbushPoint) < 8f) Motor.SetGait(AgentMotor.Gait.Sneak);
                    if (Arrived) { Enter(State.Wait); Motor.Stop(); }
                    else if (Failed) Enter(State.Relocate);
                    break;

                case State.Wait:
                    // Face the most likely approach direction (the neighbour node with the highest pass probability)
                    Motor.FaceTowards(LikelyApproach());
                    bool predictionBroken = model.CurrentZone != null && lastPass.Count > 0 &&
                                            !lastPass.ContainsKey(model.CurrentZone) &&
                                            model.CurrentZone != NavGraph.Instance.Node(ambushNode).zone;
                    if (stateTime > waitTimeout || predictionBroken) { Enter(State.Relocate); Motor.MoveTo(Home, AgentMotor.Gait.Sneak); }
                    break;

                case State.Attack:
                    if (Arrived || Failed || stateTime > 3f)
                    {
                        Body.TryAttack(transform.position + transform.forward);
                        if (stateTime > 1.5f) Enter(State.Observe);
                    }
                    break;
            }
        }

        bool TrySelectAmbush()
        {
            var g = NavGraph.Instance;
            if (model.CurrentZone == null || Time.time - playerEstTime > 30f) return false;
            lastPass = model.PassProbability(model.CurrentZone, 3);
            confidence = 0f; foreach (var p in lastPass.Values) confidence = Mathf.Max(confidence, p);
            if (confidence < minConfidence) return false;

            int me = g.Nearest(transform.position), player = g.Nearest(playerEst);
            float age = Time.time - playerEstTime, bestScore = 0.2f; int best = -1;
            for (int n = 0; n < g.Count; n++)
            {
                var node = g.Node(n);
                if (!lastPass.TryGetValue(node.zone ?? "", out float pass) || pass < 0.25f) continue;
                float quality = (node.tags & NodeTag.AmbushSpot) != 0 ? 1f
                              : (node.tags & NodeTag.Doorway) != 0 ? 0.7f
                              : (node.tags & NodeTag.Junction) != 0 ? 0.5f : 0f;
                if (quality == 0f) continue;
                float playerEta = g.SoundDistance(player, n) / playerSpeedGuess - age;
                float myEta = g.SoundDistance(me, n) / Motor.runSpeed + 2f;
                float feasible = 1f / (1f + Mathf.Exp(-(playerEta - myEta) / 2f));
                float novelty = lastUsed.TryGetValue(n, out var t) && Time.time - t < 60f ? 0.6f : 1f;
                float score = pass * quality * feasible * novelty;
                if (score > bestScore) { bestScore = score; best = n; }
            }
            if (best < 0) return false;
            ambushNode = best; lastUsed[best] = Time.time;
            return true;
        }

        void BeginMove() { Enter(State.MoveToAmbush); Motor.MoveTo(AmbushPoint, AgentMotor.Gait.Run); }

        Vector3 LikelyApproach()
        {
            var g = NavGraph.Instance; Vector3 best = transform.position + transform.forward; float bp = -1f;
            foreach (var e in g.Neighbours(ambushNode))
            {
                string z = g.Node(e.To).zone ?? "";
                float p = z == model.CurrentZone ? 1f : lastPass.TryGetValue(z, out var v) ? v : 0f;
                if (p > bp) { bp = p; best = g.Position(e.To); }
            }
            return best;
        }

        static Dictionary<string, HashSet<string>> BuildZoneAdjacency()
        {
            var g = NavGraph.Instance; var adj = new Dictionary<string, HashSet<string>>();
            for (int i = 0; i < g.Count; i++)
            {
                string a = g.Node(i).zone; if (string.IsNullOrEmpty(a)) continue;
                if (!adj.ContainsKey(a)) adj[a] = new HashSet<string>();
                foreach (var e in g.Neighbours(i))
                {
                    string b = g.Node(e.To).zone;
                    if (!string.IsNullOrEmpty(b) && b != a) adj[a].Add(b);
                }
            }
            return adj;
        }

        public override void OnContact(Vector3 where) { Enter(State.Attack); Body.TryAttack(where); }
        public override void OnHurt(Vector3 from) { playerEst = from; playerEstTime = Time.time; Enter(State.Relocate); Motor.MoveTo(Home, AgentMotor.Gait.Run); }

        public override void ResetAgent()
        {
            base.ResetAgent();
            if (!keepMemoryAcrossDeaths) model.Clear();
            ambushNode = -1; Enter(State.Observe); transform.position = Home;
        }

        void Enter(State s) { state = s; stateTime = 0f; }
    }
}
```

## 12.6 Optional: acoustic sensors (more data for the Ambusher)

```csharp
// Assets/Scripts/AI/Ambusher/AcousticSensor.cs  — put on 3–5 Security Cam props in corridors
using Echo.Navigation;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    public class AcousticSensor : MonoBehaviour, IHearer
    {
        public float sensitivity = 0.8f, threshold = 0.05f;
        AmbusherBrain ambusher;

        void OnEnable() { SoundBus.Register(this); ambusher = FindFirstObjectByType<AmbusherBrain>(); }
        void OnDisable() => SoundBus.Unregister(this);

        public void Hear(in SoundEvent e)
        {
            if (ambusher == null || e.Source == SoundSource.AgentBody) return;
            float loud = Acoustics.Loudness(e.Intensity, sensitivity, Acoustics.EffectiveDistance(e.Position, transform.position));
            if (loud >= threshold)   // reports only the zone + a coarse position
                ambusher.SensorReport(ZoneVolume.ZoneAt(transform.position), transform.position, Time.time);
        }
    }
}
```

Story justification: *"The Ambusher is wired into the facility's intercom microphones."* Gameplay counter: shooting a sensor disables it (add `IDamageable`).

## 12.7 What to demonstrate

1. Loop Hall → Ramp → Tee → Ramps → Warehouse twice, making normal footstep noise.
2. Debug overlay: transition counts rise, `conf` passes 0.35, ambush node appears (magenta sphere) at a junction on that route.
3. Third loop: the Ambusher is waiting at the Tee doorway. If you take a different route, it times out and relocates (forgetting curve visible).

---

# 13. How the four agents interact

```text
            ┌──────────── real sounds (player, environment) ────────────┐
            ▼                         ▼                         ▼        ▼
        LISTENER                  TRACKER                   AMBUSHER   MIMIC
   (latest, priority)     (sequence, credibility)      (zone transitions)  (player estimate)
            ▲                         ▲ rejects implausible        ▲             │
            │ fooled                  │ fakes                      │             │ reads AgentRegistry:
            └────────── fake sounds ──┴────────────────────────────┘◄────────────┘ positions, interest,
                                                                                    ambush point
```

| Pairing | Interaction |
|---|---|
| Mimic → Listener | Summon pulls the Listener onto the player — or away from them if the estimate was wrong (player exploit) |
| Mimic → Tracker | Most fakes are rejected by credibility — shows a different model of the world |
| Mimic → Ambusher | Flush pushes the player toward the Ambusher's waiting point (only cooperation in the system) |
| Listener ↔ Tracker | No direct link; both converge on loud events, the Tracker usually *ahead* of the Listener |
| Everyone | Agents' body sounds (`AgentBody`) are ignored by other agents so they don't hunt each other |

Rules of thumb:
- Only the Mimic reads `AgentRegistry` for decisions (plus the presentation-only `TensionDirector`).
- No agent sends commands to another. Influence happens **only through sound** — that is the ecosystem.

---

# 14. Combat, health, death and reset

- **PlayerHealth** implements `IDamageable` (100 HP; Listener/Tracker hit 35, Ambusher 50, Mimic 15). Low-health heartbeat audio and a red vignette on the URP Volume.
- **Agents' health:** 100 HP; rifle 34 per shot → 3 shots. Being shot calls `OnHurt(from)`. Shooting is loud (intensity 1.0) — killing one agent summons the others.
- **Agent death:** `AgentBody` disables the collider and stops the motor; the brain stops thinking (`Body.IsDead`). The other agents and the registry keep working (edge case in the plan). Optional: respawn after 60 s at Home out of the player's view.
- **Player death → restart:** `GameManager` fades to black, moves the player to the spawn point, calls `AgentRegistry.ResetAll()`, revives agents. The **Ambusher keeps its route model** (`keepMemoryAcrossDeaths`), which makes the second attempt feel like the facility learned — good demo moment.
- **Victory/extraction:** trigger at the extraction zone; freeze agents (`enabled = false` on brains).

---

# 15. Debugging and visualisation tools

You need these from Day 4. Build them before the agents.

## 15.1 Sound rings

```csharp
// Assets/Scripts/Sound/SoundDebug.cs
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Sound
{
    public class SoundDebug : MonoBehaviour
    {
        public bool show = true; public float lifetime = 1.5f;
        readonly List<(SoundEvent e, float until)> recent = new();

        void OnEnable() => SoundBus.Emitted += Add;
        void OnDisable() => SoundBus.Emitted -= Add;
        void Add(SoundEvent e) => recent.Add((e, Time.time + lifetime));

        void OnDrawGizmos()
        {
            if (!show || !Application.isPlaying) return;
            recent.RemoveAll(r => r.until < Time.time);
            foreach (var (e, until) in recent)
            {
                float k = (until - Time.time) / lifetime;
                Gizmos.color = e.Source switch
                {
                    SoundSource.Player => new Color(0.2f, 0.8f, 1f, k),
                    SoundSource.Mimic => new Color(1f, 0.2f, 1f, k),
                    SoundSource.AgentBody => new Color(0.5f, 0.5f, 0.5f, k * 0.5f),
                    _ => new Color(1f, 0.85f, 0.2f, k)
                };
                Gizmos.DrawWireSphere(e.Position, e.Intensity * Acoustics.MetresPerIntensity * (1f - k * 0.3f));
            }
        }
    }
}
```

## 15.2 Agent overlay (F1)

```csharp
// Assets/Scripts/AI/Common/AgentDebugOverlay.cs
using UnityEngine;
using UnityEngine.InputSystem;

namespace Echo.AI
{
    public class AgentDebugOverlay : MonoBehaviour
    {
        public bool visible = true;

        void Update() { if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame) visible = !visible; }

        void OnGUI()
        {
            if (!visible) return;
            GUILayout.BeginArea(new Rect(10, 10, 460, 400), GUI.skin.box);
            foreach (var a in AgentRegistry.All)
                GUILayout.Label($"<b>{a.DisplayName}</b>  [{a.StateName}]  alert {a.AlertLevel:0.0}\n{a.DebugText}",
                                new GUIStyle(GUI.skin.label) { richText = true });
            GUILayout.EndArea();
        }
    }
}
```

## 15.3 Other tools worth 30 minutes each

- **Path gizmos** — `AgentMotor.OnDrawGizmosSelected` (already included).
- **Heatmaps** — Tracker `heat[]` and Ambusher transition counts drawn as coloured spheres on nodes.
- **Time scale keys** — F2 = 0.25×, F3 = 1×, F4 = 3× (`Time.timeScale`) for watching decisions.
- **Decision log (CSV)** — every state change: `time, agent, from, to, reason, score`. Gives you *evidence* tables and graphs for the report and viva.
- **Debug draw in Game view** — enable Gizmos in the Game view toolbar during demos of the AI section.
- Disable overlays in the final build via a `#if DEVELOPMENT_BUILD || UNITY_EDITOR` guard.

---

# 16. Testing

## 16.1 Edit Mode unit tests (pure logic — fast, run on every commit)

```csharp
// Assets/Tests/EditMode/AStarTests.cs
using System.Collections.Generic;
using Echo.Navigation;
using NUnit.Framework;
using UnityEngine;

public class AStarTests
{
    class FakeGraph : IGraph
    {
        public readonly List<Vector3> pos = new(); public readonly List<List<Edge>> adj = new();
        public int Count => pos.Count;
        public Vector3 Position(int i) => pos[i];
        public IReadOnlyList<Edge> Neighbours(int i) => adj[i];
        public int Add(Vector3 p) { pos.Add(p); adj.Add(new List<Edge>()); return pos.Count - 1; }
        public void Link(int a, int b) { float d = Vector3.Distance(pos[a], pos[b]); adj[a].Add(new Edge(b, d, null)); adj[b].Add(new Edge(a, d, null)); }
    }

    [Test]
    public void FindsShortestRoute()
    {
        var g = new FakeGraph();
        int a = g.Add(new Vector3(0, 0, 0)), b = g.Add(new Vector3(10, 0, 0)),
            c = g.Add(new Vector3(5, 0, 1)), d = g.Add(new Vector3(5, 0, 20));
        g.Link(a, c); g.Link(c, b); g.Link(a, d); g.Link(d, b);
        var path = new List<int>();
        Assert.IsTrue(AStar.FindPath(g, a, b, path));
        CollectionAssert.AreEqual(new[] { a, c, b }, path);
    }

    [Test]
    public void ForbiddenEdgeForcesDetourOrFails()
    {
        var g = new FakeGraph();
        int a = g.Add(Vector3.zero), b = g.Add(new Vector3(5, 0, 0));
        g.Link(a, b);
        var path = new List<int>();
        Assert.IsFalse(AStar.FindPath(g, a, b, path, (f, t, c) => float.PositiveInfinity));
    }
}
```

```csharp
// Assets/Tests/EditMode/RouteModelTests.cs
using System.Collections.Generic;
using Echo.AI;
using NUnit.Framework;

public class RouteModelTests
{
    [Test]
    public void LearnsRepeatedRoute()
    {
        var adj = new Dictionary<string, HashSet<string>>
        {
            ["Hall"] = new() { "Tee", "Stairs" }, ["Tee"] = new() { "Hall", "Warehouse", "Containment" },
            ["Warehouse"] = new() { "Tee" }, ["Containment"] = new() { "Tee" }, ["Stairs"] = new() { "Hall" }
        };
        var m = new RouteModel(adj);
        float t = 0f;
        for (int lap = 0; lap < 3; lap++)
            foreach (var z in new[] { "Hall", "Tee", "Warehouse", "Tee", "Hall" }) m.Observe(z, t += 5f);

        Assert.Greater(m.P("Tee", "Warehouse"), m.P("Tee", "Containment"));
        Assert.Greater(m.PassProbability("Hall", 2)["Warehouse"], 0.4f);
    }
}
```

Also test: `ListenerPriority.Score` ordering (gunshot > footstep at equal loudness), `Acoustics.Loudness` (0 beyond radius, 1 at distance 0), `TrackerPredictor.TryVelocity` (straight-line synthetic observations → correct velocity), credibility rejects a teleport.

## 16.2 Sandbox scenes and scripted scenarios (Play Mode)

Each owner keeps `Scenes/Sandbox/Sandbox_<Agent>.unity`: a copy of the level (or a small greybox) with only their agent. Add a `ScenarioRunner` that, on key press, emits scripted sounds — so you can test without playing:

```csharp
// Example: press 1 = gunshot at A, 2 = footstep trail A→B over 6 s, 3 = Mimic-style teleporting sound
Noise.Make(SoundType.Gunshot, pointA.position, 1f, SoundSource.Player);
```

## 16.3 Integration checklist (Day 18)

- All four agents active for 10 minutes: no exceptions, no agent stuck > 5 s, no state flip-flopping more than 3× in 2 s.
- Player death + restart 3× in a row: agents reset correctly, Ambusher keeps its memory.
- Lock a door at runtime: agents re-plan.
- Kill one agent: others continue.

---

# 17. Performance budget

| Item | Frequency | Cost | Notes |
|---|---|---|---|
| Movement (NavMeshAgent) | every frame | built-in | 4 agents: negligible |
| Brain `Think()` | 5 Hz (+ urgent wake-ups) | < 0.1 ms each | jittered so agents don't align |
| Mimic deception planning | 1 Hz | O(V × agents) ≈ 80 × 3 loudness evals | each eval ≤ 1 linecast |
| A* | only on goal change / failure | µs | ~80 nodes |
| Floyd–Warshall | load + each door change | ~0.5–2 ms | V³ with V ≈ 80 |
| Sound emit | per sound | ≤ 1 linecast + matrix lookup per hearer | footsteps ≈ 2 / s |
| Audio occlusion | 10 Hz per occluded source | 1 linecast | only on agent sources |

Rules: no `Find*` in `Update`; no LINQ in per-frame code; cache `LayerMask.GetMask`; reuse lists. Profile with the Unity Profiler (CPU, *Deep Profile* off) and record a screenshot for the optimisation section of the report.

---

# 18. Team workflow: who builds what, in which order

## 18.1 Ownership

| Member | Graphics role | Agent | Also owns (shared systems) |
|---|---|---|---|
| 1 | World Builder | **Listener** | Sound core (`SoundBus`, `Hearing`, `Acoustics`, `SoundDebug`), zones, NavMesh bake, node placement |
| 2 | Systems Engineer | **Tracker** | `PlayerNoise`, `Weapon`, `Door`, `NoisyPhysicsObject`, generator/switches, `PlayerHealth` |
| 3 | Core Developer | **Mimic** | `AudioService`, `SoundLibrary`, mixer, ambience, `TensionDirector`, `AudioOcclusion`, sourcing clips |
| 4 | Agent Controller | **Ambusher** | `NavGraph`, `AStar`, `AgentMotor`, `AgentBrain`, `AgentBody`, `AgentAnimatorBridge`, `ContactSense`, agent prefabs + Animator, `AgentDebugOverlay` |

## 18.2 Dependency order (what blocks what)

```text
Day 3   Layers/tags, asmdefs, folders, NavMesh bake, Zones                  (M1)
Day 3   SoundType/SoundEvent/SoundBus/Noise + SoundDebug                   (M1)   ◄── everyone needs this
Day 3   PlayerNoise + Weapon emitting events                               (M2)
Day 3-4 NavNode/NavGraph/AStar + auto-link tool + AgentMotor               (M4)   ◄── every agent needs this
Day 4   AgentBrain/AgentBody/AnimatorBridge/Registry + Overlay             (M4)
Day 4   Acoustics + Hearing                                                 (M1)
Day 4   AudioService + SoundLibrary + placeholder clips                    (M3)
──────── FREEZE shared contracts end of Day 4 ────────
Day 5   Listener                                                           (M1)
Day 6-7 Tracker                                                            (M2)
Day 8-9 Mimic (needs Listener's ListenerPriority + Ambusher API stubs)     (M3)
Day 10-11 Ambusher                                                          (M4)
```

The Mimic depends on `AmbusherBrain.HasAmbushPoint / IsWaiting / AmbushPoint` — M4 should commit those three properties as **stubs on Day 4** so M3 is not blocked.

## 18.3 Frozen contracts (change only by team agreement)

`SoundType`, `SoundSource`, `SoundEvent`, `PerceivedSound`, `IHearer`, `Noise.Make`, `Acoustics.Loudness/EffectiveDistance`, `IGraph`, `Edge`, `NavGraph` public API, `AStar.FindPath`, `AgentMotor.MoveTo/Stop/SetGait/FaceTowards/flags`, `AgentBrain` virtuals, `AgentRegistry`, `IDamageable`, `ListenerPriority`.

## 18.4 Git and scene hygiene

- Branches: `feature/sound-core`, `feature/nav-graph`, `feature/listener`, `feature/tracker`, `feature/mimic`, `feature/ambusher`, merged into `develop` (plan §37).
- **Never two people editing `SampleScene.unity` at the same time.** Work in sandbox scenes; put agents in **prefabs**; Member 1 integrates into the main scene.
- Configure **UnityYAMLMerge** (Smart Merge) as the git merge tool for `.unity`/`.prefab`.
- Small commits with the plan's prefixes (`feat:`, `fix:`, `perf:`, `test:`). Each member's AI folder is theirs — that also makes individual Git history obvious.

---

# 19. Master tuning table

| Parameter | Start value | Where |
|---|---|---|
| Metres per intensity | 45 m | `Acoustics.MetresPerIntensity` |
| Hearing threshold | 0.03 | `Hearing.threshold` |
| Sensitivity L / T / M / A | 1.15 / 1.0 / 0.9 / 1.4 | `Hearing.sensitivity` |
| Position error (faint → loud) | 6 m → 0.5 m | `Hearing` |
| Closed-door sound penalty | +12 m | `NavGraph.ClosedDoorSoundPenalty` |
| Walk / run step intensity | 0.15 / 0.30 | `PlayerNoise` |
| Walk / run stride | 1.6 / 2.2 m | `PlayerNoise` |
| Gunshot / impact | 1.0 / 0.3 | `Weapon` |
| Door open / close | 0.5 / 0.6 | `Door` |
| Agent speeds (sneak/walk/run) | 1.2 / 2.2 / 5.0 m/s | `AgentMotor` |
| Think interval | 0.2 s | `AgentBrain` |
| Listener switch factor | 1.25 | `ListenerPriority` |
| Listener recency τ | 8 s | `ListenerPriority` |
| Listener search | 4 points, 1.5 s pauses, 20 s max | `ListenerBrain` |
| Tracker window / capacity | 25 s / 10 obs | `TrackerPredictor` |
| Tracker max player speed | 6.5 m/s | `TrackerPredictor` |
| Tracker re-plan threshold | 6 m | `TrackerBrain` |
| Mimic voice range / cooldown | 18 m / 10–15 s | `MimicBrain` |
| Mimic utility threshold | 0.15 | `MimicBrain` |
| Ambusher forget τ / α | 120 s / 0.5 | `RouteModel` |
| Ambusher min confidence / wait | 0.35 / 25 s | `AmbusherBrain` |
| Strike distance L / T / A | 2.2 / 2.2 / 5 m | brains |
| Contact radius | 1.3 m | `ContactSense` |

Tune with the overlay on and time-scale keys. Change **one** number at a time and write down why.

---

# 20. Edge-case checklist

| Case | Handling |
|---|---|
| Sound estimate lands off the NavMesh | `Hearing` snaps it with `NavMesh.SamplePosition`; if that fails the motor reports `Failed` → agent searches where it is |
| Goal unreachable (locked door) | A* returns false → brain picks a fallback (search locally / relocate) |
| Agent stuck (no progress 2.5 s) | motor `Failed` → re-plan once, then change state |
| Two sounds at once | priority + hysteresis (Listener), credibility (Tracker), zone model (Ambusher) |
| Sound source destroyed (thrown bottle broke) | irrelevant: agents only stored a position estimate |
| Player silent for a long time | Listener returns home; Tracker clears history after search; Mimic may Flush; Ambusher waits then relocates |
| Player dies | `AgentRegistry.ResetAll()`; Ambusher keeps memory |
| Agent dies | brain stops; others unaffected; Mimic skips dead agents (check `Body.IsDead` in its loops) |
| Mimic fake expires | agents' recency decay and search timeouts end the investigation naturally |
| Player stops repeating a route | Ambusher's exponential forgetting lowers confidence → it returns to Observe |
| Door changes state | `NavGraph.OnDoorChanged()` recomputes sound distances; motor handles closed doors on its path |
| Agents block each other in a corridor | NavMeshAgent avoidance priorities differ; stuck detection re-plans |
| Domain reload disabled | `SoundBus`/`AgentRegistry` reset via `RuntimeInitializeOnLoadMethod` |

---

# 21. Viva cheat-sheet

## Common questions — short answers

- **How can a blind agent find the player?** It hears sounds through a propagation model (route distance, quadratic fall-off, threshold) and gets an uncertain position estimate; it must search/predict. Touch is the only other sense.
- **Why A\*?** Optimal and efficient goal-directed search; with an admissible, consistent heuristic it expands far fewer nodes than Dijkstra and never re-opens nodes.
- **Heuristic?** Euclidean 3D distance. Admissible because every edge cost ≥ walking length ≥ straight-line distance, and all modifiers only increase costs.
- **When do you re-plan?** New goal (> 3 m change), motor failure/stuck, door locked, cost function changed. Never per frame.
- **How is AI separated from visuals?** Brain → Motor → NavMeshAgent; AnimatorBridge reads velocity. Brains never reference the Animator.
- **Complexity?** A*: O(E log V). Floyd–Warshall: O(V³) at load/door change. Listener tick: O(new sounds). Tracker: O(history + V). Mimic: O(V × agents). Ambusher: O(Z² × H) for prediction + O(V) for candidates.

## Per agent

| Agent | Algorithmic core | Alternatives you rejected (and why) |
|---|---|---|
| Listener | Priority function (loudness × type × recency × habituation) with **hysteresis**; FSM with search pattern | "Go to latest sound" (zig-zags, exploitable); utility over all memory (that is the Tracker's job) |
| Tracker | **Weighted least-squares** velocity; **credibility gating** (outlier rejection); **candidate scoring** on graph nodes | Kalman filter (more maths, similar result at this scale — mention as future work); straight-line extrapolation only (walks into walls) |
| Mimic | **Utility AI** over deception actions with a **forward model** of other agents' reaction rules | Random fakes (no intelligence, ruins the sound language); scripted fakes (not autonomous) |
| Ambusher | **Markov chain** of zone transitions with **Laplace smoothing** and **exponential forgetting**; interception by **ETA feasibility** | Storing full paths (sparse data, overfits); always ambushing the last seen zone (not predictive) |

## Demo order that tells the story (matches plan §36)

1. Footsteps vs running vs gunshot — sound rings in Game view.
2. Listener: shoot → investigate → search → return; habituation on the generator.
3. Tracker: noisy run along a corridor → prediction ahead → intercept; ignores a Mimic fake.
4. Mimic: hide → Flush → you move → Ambusher.
5. Ambusher: route learned over two loops → waiting at the junction on the third.
6. Restart after death: the Ambusher still remembers.

---

*Build the ugly version first: by the end of Day 5 a grey capsule with `ListenerBrain` should run to your gunshot along a green A* line, search four points and walk home. Everything in this document builds on that loop.*

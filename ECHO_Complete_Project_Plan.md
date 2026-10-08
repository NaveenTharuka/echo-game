# ECHO — Complete 3D Autonomous-Agent Shooting Game Project Plan

> **Project:** ECHO  
> **Tagline:** *The enemy cannot see you. It can hear you.*  
> **Modules:** SE3032 Graphics & Visualization + SE3062 Intelligent Systems  
> **Team:** 4 students  
> **Engine:** Unity  
> **Primary language:** C#  
> **Development window:** 22 days  
> **Target:** A polished, playable vertical slice rather than a large commercial-scale game

---

# 1. Executive Summary

**ECHO** is a stealth/horror/tactical 3D shooting game set inside an abandoned underground research facility after a catastrophic power failure.

The central gameplay mechanic is **sound-based enemy perception**.

The player is not simply fighting enemies that continuously see and chase them. Actions such as shooting, running, jumping, opening doors, throwing objects, breaking glass, activating machinery, and dropping objects generate sound events. Autonomous agents perceive those events, reason about them, select goals, calculate paths, and react.

The game is intentionally designed as a **small but highly polished vertical slice**. The team should build one strong facility level with a limited number of rooms and a small set of highly interactive systems.

The four team members will each own a distinct autonomous agent:

1. **Listener** — investigates sound events.
2. **Tracker** — remembers sound history and predicts player movement.
3. **Mimic** — generates fake sound events to manipulate the player and other agents.
4. **Ambusher** — learns frequently used player routes and attempts interception.

The project combines:

- 3D environment design
- lighting and materials
- custom Blender modeling
- AI-generated assets
- Unity gameplay programming
- sound-event perception
- A* pathfinding
- autonomous decision-making
- physics interactions
- character animation
- optimization
- Git-based collaborative development

---

# 2. Assignment Alignment

## 2.1 Graphics & Visualization

The Graphics assignment requires a playable interactive 3D/VR environment and specifically defines the theme as an **interactive 3D shooting game**.

The assignment evaluates visual fidelity, 3D modeling, environment design, physical interaction, stability, workflow, optimization, and individual contribution.

The four Graphics responsibilities are:

| Member | Graphics Responsibility |
|---|---|
| Student 1 | World Builder — level design, lighting, texturing, initial NavMesh |
| Student 2 | Systems Engineer — player physics and environmental interactions |
| Student 3 | Core Developer — custom 3D modeling, topology and UV mapping |
| Student 4 | Agent Controller — agent movement, rotation and animation |

The assignment requires Student 3 to design, model, and import at least two custom 3D models from scratch using Blender or Maya.

## 2.2 Intelligent Systems

The Intelligent Systems assignment focuses on:

- autonomous agents
- AI decision-making
- pathfinding
- dynamic search
- agent-specific implementation
- technical justification

Each of the four members must own one distinct autonomous agent.

The agents must go beyond basic patrol-and-shoot behavior or simple uninformed search.

## 2.3 Key Assessment Implications

The project must therefore demonstrate:

- four genuinely different autonomous agents
- meaningful pathfinding
- dynamic decision-making
- stable simultaneous operation
- clean separation between AI and visual/gameplay systems
- continuous Git history
- clear individual ownership
- technically defensible algorithms
- visual cohesion
- custom modeling
- physics/environmental interaction
- optimization

---

# 3. Project Vision

## 3.1 Player Fantasy

The player should feel:

> "I am inside a facility where making noise can get me killed."

The player must constantly make decisions such as:

- Should I shoot this enemy?
- Should I run or walk?
- Should I open this door?
- Can I distract the enemy?
- Where will the enemy investigate?
- Is the enemy actually following me?
- Has an agent learned my route?
- Can I manipulate one agent to affect another?

## 3.2 Core Gameplay Question

Every major gameplay system should reinforce:

> **"What can the enemy hear, and what will it do with that information?"**

---

# 4. Game Setting

## 4.1 Location

An abandoned underground research facility.

The facility contains:

- research laboratories
- maintenance corridors
- storage rooms
- generator rooms
- control rooms
- machinery
- underground transit/maintenance areas
- emergency systems

The facility suffered a major power failure.

Most of the facility is dark.

Emergency systems remain partially active.

## 4.2 Visual Direction

The target art direction is:

- dark
- industrial
- scientific
- abandoned
- wet
- atmospheric
- cinematic
- horror-oriented

### Visual language

```text
Dark concrete
Dirty metal
Wet floors
Rust
Pipes
Steam
Emergency red lights
Cold fluorescent lights
Strong shadows
Subtle fog
Limited visibility
```

## 4.3 Lighting

Use a controlled lighting hierarchy:

### Primary lighting

- baked/static environment lighting where appropriate
- limited real-time lights
- emergency lights
- fluorescent lights

### Secondary lighting

- red warning lights
- generator glow
- equipment screens
- small point lights

### Atmospheric effects

- fog
- steam
- dust
- subtle volumetric effects

Do not place expensive real-time lights everywhere.

---

# 5. Scope Philosophy

The most important rule of the project is:

> **Build one polished level instead of several unfinished levels.**

## 5.1 Do NOT build

- open-world gameplay
- multiplayer
- procedural worlds
- large inventory systems
- crafting
- dozens of weapons
- ten enemy classes
- massive maps
- full physical simulation of every object
- complicated progression systems
- multiple large levels

## 5.2 DO build

- one strong environment
- one central sound mechanic
- four strong autonomous agents
- one primary weapon
- several interactive objects
- doors
- throwable objects
- environmental switches
- generators
- strong lighting
- good animations
- polished audio
- stable gameplay

---

# 6. Target Gameplay Loop

```text
Explore facility
      ↓
Find objective
      ↓
Move carefully
      ↓
Generate / hear sound
      ↓
AI perceives sound
      ↓
AI selects response
      ↓
AI calculates path
      ↓
AI investigates / predicts / distracts / intercepts
      ↓
Player reacts
      ↓
Complete objective
      ↓
Reach extraction
```

---

# 7. Proposed Level Structure

The entire game should be one connected facility.

A practical structure:

```text
                         ┌─────────────────┐
                         │   LABORATORY A  │
                         └────────┬────────┘
                                  │
                         ┌────────▼────────┐
                         │ MAIN CORRIDOR   │
                         └───┬──────────┬──┘
                             │          │
                    ┌────────▼───┐  ┌──▼──────────┐
                    │  STORAGE   │  │ GENERATOR   │
                    │    ROOM    │  │    ROOM     │
                    └──────┬─────┘  └────┬────────┘
                           │              │
                           └──────┬───────┘
                                  │
                         ┌────────▼────────┐
                         │  RESEARCH CORE  │
                         └────────┬────────┘
                                  │
                         ┌────────▼────────┐
                         │  EXTRACTION     │
                         └─────────────────┘
```

## 7.1 Important Level Design Requirements

The map should contain:

- long corridors
- intersections
- rooms
- alternative routes
- hiding areas
- doors
- sound-producing objects
- environmental interaction points
- strategic ambush locations

The level must be large enough for pathfinding to matter but small enough to finish and optimize.

---

# 8. Core Sound System

The sound system is the foundation of ECHO.

Do **not** attempt physically accurate acoustic simulation.

Instead, implement a gameplay-level **Sound Event System**.

## 8.1 Sound Event

Every important sound becomes a structured event.

Example:

```csharp
public class SoundEvent
{
    public Vector3 position;
    public float intensity;
    public SoundType type;
    public float timestamp;
}
```

Possible sound types:

```text
Gunshot
Footstep
Running
Jump
Door
MetalImpact
GlassBreak
Machinery
ObjectDrop
Generator
```

## 8.2 Example Sound Intensities

| Sound | Example Intensity |
|---|---:|
| Quiet walking | 0.10 |
| Footstep | 0.15 |
| Running | 0.30 |
| Door | 0.50 |
| Metal object | 0.70 |
| Glass break | 0.80 |
| Gunshot | 1.00 |

These are starting values only and should be tuned through gameplay testing.

## 8.3 Sound Detection

An agent can calculate whether it can perceive a sound using:

```text
distance
×
sound intensity
×
agent hearing sensitivity
×
environment modifiers
```

Conceptually:

```text
PerceivedStrength =
    intensity
    × hearingSensitivity
    × distanceFactor
```

The project does not need a physically accurate acoustic model.

---

# 9. Sound Manager Architecture

```text
PLAYER
  │
  ├── Shoot
  ├── Run
  ├── Jump
  ├── Open Door
  └── Throw Object
          │
          ▼
    Sound Manager
          │
          ▼
     Sound Event
          │
          ├─────────────┐
          │             │
          ▼             ▼
     Listener        Tracker
          │             │
          └──────┬──────┘
                 │
                 ▼
              AI World
```

The Mimic can also generate Sound Events:

```text
Mimic
  ↓
Fake Sound Event
  ↓
Sound Manager
  ↓
Other Agents
```

This creates a unified perception architecture.

---

# 10. Autonomous Agent Architecture

All four agents should follow a common high-level architecture:

```text
Perception
    ↓
World State
    ↓
Memory
    ↓
Decision System
    ↓
Goal Selection
    ↓
Pathfinding
    ↓
Movement
    ↓
Animation
    ↓
World Update
    ↓
Re-plan
```

The four agents should share infrastructure but implement different decision logic.

---

# 11. Agent 1 — LISTENER

## 11.1 Purpose

The Listener is the basic sound-investigation enemy.

It detects sound events and determines whether they are worth investigating.

## 11.2 Behavior

```text
IDLE
 ↓
HEAR SOUND
 ↓
EVALUATE SOUND
 ↓
SELECT SOUND
 ↓
CALCULATE PATH
 ↓
INVESTIGATE
 ↓
SEARCH
 ↓
RETURN
```

## 11.3 Decision Factors

The Listener can consider:

- sound intensity
- distance
- recency
- sound type
- current state
- current target
- threat level

## 11.4 Example

Player shoots.

```text
Gunshot
   ↓
Sound Event
   ↓
Listener detects it
   ↓
Distance calculation
   ↓
Priority calculation
   ↓
A* pathfinding
   ↓
Move to sound source
   ↓
Search area
```

## 11.5 Technical Ownership

The Listener owner should implement:

- sound perception
- sound priority
- investigation state machine
- path request
- investigation timeout
- search behavior

---

# 12. Agent 2 — TRACKER

## 12.1 Purpose

The Tracker remembers previous sound events and attempts to predict where the player went.

Unlike the Listener, it does not blindly investigate only the latest sound.

## 12.2 Memory

Example:

```text
10:01 — Gunshot — Position A
10:04 — Door — Position B
10:06 — Footsteps — Position C
```

The Tracker can estimate:

```text
A → B → C
```

and predict:

```text
Likely next location = D
```

## 12.3 Behavior

```text
Collect sound events
       ↓
Store recent history
       ↓
Estimate movement direction
       ↓
Generate candidate locations
       ↓
Score candidates
       ↓
Select predicted location
       ↓
A*
       ↓
Intercept
```

## 12.4 Candidate Scoring

Possible factors:

```text
Recent sound proximity
Movement direction
Time between events
Known player routes
Distance to candidate
Available paths
```

## 12.5 Technical Ownership

The Tracker owner should implement:

- sound history
- memory representation
- movement estimation
- prediction
- candidate scoring
- A* integration
- interception behavior

---

# 13. Agent 3 — MIMIC

## 13.1 Purpose

The Mimic creates fake sounds.

This agent does not simply chase the player.

It manipulates the sound-based ecosystem.

## 13.2 Example

```text
Player hides in Room A

Mimic creates:

Fake footsteps → Corridor B

Listener hears them

Listener moves to Corridor B

Player escapes through Corridor C
```

## 13.3 Possible Fake Sounds

- footsteps
- door opening
- metal impact
- object drop
- machinery activation

## 13.4 Decision Logic

The Mimic can select fake sounds based on:

- player last known position
- current enemy locations
- player hiding location
- escape route
- recent sound history

## 13.5 Technical Ownership

The Mimic owner should implement:

- fake sound generation
- sound type selection
- target position selection
- timing
- cooldowns
- strategic sound placement

---

# 14. Agent 4 — AMBUSHER

## 14.1 Purpose

The Ambusher studies repeated player movement.

It attempts to intercept rather than directly chase.

## 14.2 Example

Player repeatedly uses:

```text
Storage
   ↓
Main Corridor
   ↓
Research Lab
```

The Ambusher detects repeated route usage.

It can select:

```text
Main Corridor intersection
```

as an interception point.

## 14.3 Behavior

```text
Observe player movement
       ↓
Store route history
       ↓
Identify repeated paths
       ↓
Calculate likely future route
       ↓
Select interception point
       ↓
A*
       ↓
Move to interception point
       ↓
Wait
       ↓
Detect player
       ↓
Attack
```

## 14.4 Technical Ownership

The Ambusher owner should implement:

- route history
- route frequency
- interception point selection
- prediction
- waiting behavior
- re-planning

---

# 15. Why the Four Agents Are Different

| Agent | Main Input | Memory | Main Goal |
|---|---|---|---|
| Listener | Current sound | Limited | Investigate |
| Tracker | Sound sequence | Strong | Predict/intercept |
| Mimic | World state | Strategic | Create deception |
| Ambusher | Player routes | Strong | Position for interception |

The agents should not be four skins over the same chase algorithm.

---

# 16. Decision Architecture

A useful common state model:

```text
IDLE
  ↓
SUSPICIOUS
  ↓
INVESTIGATING
  ↓
TARGET FOUND
  ↓
PURSUING
  ↓
SEARCHING
  ↓
RETURNING
```

However, each agent should add its own states.

## Listener

```text
Idle
InvestigateSound
SearchArea
Return
```

## Tracker

```text
Idle
AnalyzeHistory
Predict
Intercept
Search
```

## Mimic

```text
Observe
SelectDeception
GenerateSound
Relocate
Cooldown
```

## Ambusher

```text
Observe
AnalyzeRoutes
SelectAmbushPoint
MoveToAmbush
Wait
Attack
Relocate
```

---

# 17. Pathfinding

A* should be the primary pathfinding approach for the agents.

Conceptually:

```text
Agent
  ↓
Goal
  ↓
A* Search
  ↓
Path
  ↓
Waypoint Array
  ↓
Agent Controller
  ↓
Movement
```

The pathfinding layer should be independent of animation.

## 17.1 Dynamic Replanning

Agents should recalculate paths when:

- target changes
- route becomes unavailable
- target moves significantly
- environment changes
- current path becomes invalid

Do not recalculate expensive paths every frame.

---

# 18. Agent/Visual Separation

AI should not directly manipulate animation states everywhere.

Use:

```text
AI
 ↓
Agent Goal
 ↓
Path
 ↓
Agent Controller
 ↓
Animator
```

Example:

```text
AI says:

"Investigate position X"

Agent Controller:

Move along path

Animator:

Walk
```

When the AI changes to attack:

```text
AI:

"Attack target"

Agent Controller:

Stop / rotate

Animator:

Attack
```

This improves maintainability and helps demonstrate clean architecture during the viva.

---

# 19. Unity Architecture

Recommended project architecture:

```text
Assets/
│
├── Art/
│   ├── Characters/
│   ├── Environment/
│   ├── Props/
│   ├── Materials/
│   └── Textures/
│
├── Animations/
│
├── Audio/
│   ├── SFX/
│   ├── Ambience/
│   └── Music/
│
├── Prefabs/
│
├── Scenes/
│   ├── MainMenu
│   └── EchoFacility
│
├── Scripts/
│   ├── Player/
│   ├── AI/
│   │   ├── Common/
│   │   ├── Listener/
│   │   ├── Tracker/
│   │   ├── Mimic/
│   │   └── Ambusher/
│   ├── Sound/
│   ├── Interaction/
│   ├── Environment/
│   └── UI/
│
└── UI/
```

---

# 20. Common AI Interfaces

A useful architecture is to define shared contracts.

Example:

```csharp
public interface IAgentDecision
{
    void EvaluateWorld();
    void SelectGoal();
}

public interface IPathRequester
{
    void RequestPath(Vector3 destination);
}

public interface ISoundListener
{
    void OnSoundDetected(SoundEvent soundEvent);
}
```

The exact implementation can differ between agents.

The purpose is to keep shared infrastructure reusable.

---

# 21. Asset Production Strategy

Do not create every asset from scratch.

Use three categories.

## Category A — Required Custom Assets

These should be made by the team:

- ECHO creature
- generator
- special research machine

At least two custom 3D models should be completed from scratch for the Core Developer responsibility.

## Category B — AI-Generated Assets

Use tools such as:

- Meshy
- Tripo
- other suitable AI 3D-generation platforms

Potential uses:

- laboratory equipment
- special props
- machinery
- decorative objects
- secondary creatures
- hero props

## Category C — Modular/Existing Assets

Use existing assets for:

- walls
- floors
- doors
- pipes
- generic furniture
- cables
- generic machinery
- repeated environmental pieces

The objective is visual cohesion, not modeling every object manually.

---

# 22. Recommended Character Pipeline

```text
Concept
  ↓
AI image generation
  ↓
Meshy / Tripo
  ↓
Raw 3D character
  ↓
Blender
  ↓
Cleanup
  ↓
Scale
  ↓
Topology optimization
  ↓
UV/material cleanup
  ↓
FBX
  ↓
Mixamo
  ↓
Rig + animations
  ↓
Unity
  ↓
Prefab
  ↓
AI Agent Controller
```

## 22.1 Character Generation Guidelines

Generate characters in:

- neutral pose
- preferably T-pose for humanoid rigging
- symmetrical proportions
- game-oriented proportions
- moderate polygon density

Avoid extremely complex creatures if rapid rigging is required.

---

# 23. Blender Responsibilities

Blender should be used for:

- custom modeling
- mesh cleanup
- topology
- UV mapping
- material cleanup
- scale correction
- polygon reduction
- export preparation

Do not spend excessive time making every asset perfect.

The two required custom models should receive the most modeling attention.

---

# 24. Environment Production

Use modular pieces.

Example:

```text
Wall_Straight
Wall_Corner
Floor
Ceiling
Door
DoorFrame
Pipe_Straight
Pipe_Corner
Vent
Light
Cable
Generator
Desk
Computer
Crate
Machine
```

These pieces can be reused to create multiple rooms.

This improves:

- speed
- consistency
- optimization
- visual cohesion

---

# 25. Player Mechanics

The minimum player system should contain:

## Movement

- walk
- run
- jump
- crouch if feasible

## Weapon

Prefer one primary weapon.

Required functionality:

- aim
- shoot
- reload if needed
- muzzle flash
- impact effect
- sound

## Flashlight

The flashlight should contribute to atmosphere.

Optional future interaction:

- certain enemies may react to light

Only implement this if the core sound system is already complete.

---

# 26. Environmental Interactions

The environment should contain:

- doors
- movable boxes
- throwable objects
- metal objects
- glass objects
- electrical switches
- generator
- machinery
- breakable lights

These interactions should generate sound events.

Example:

```text
Open Door
   ↓
Sound Event
   ↓
Listener detects

Throw Metal Object
   ↓
Sound Event
   ↓
Listener detects

Shoot
   ↓
Large Sound Event
   ↓
Multiple agents react
```

---

# 27. Physics

Use physics selectively.

Objects that require physics:

- throwable objects
- movable boxes
- selected props

Do not give every object expensive Rigidbody simulation.

For static environment objects:

- use static geometry
- use appropriate colliders
- avoid unnecessary physics components

---

# 28. Audio Design

Audio is extremely important for ECHO.

## Ambient audio

- facility hum
- distant machinery
- electrical buzzing
- ventilation
- water dripping
- distant metallic noises

## Gameplay audio

- footsteps
- gunshots
- doors
- glass
- metal impacts
- generator
- switches
- enemy sounds

## AI audio

Each agent can have subtle identity cues.

Example:

```text
Listener → heavy breathing / footsteps
Tracker → subtle mechanical sound
Mimic → unusual sound cues
Ambusher → almost silent
```

Do not make the AI audio so obvious that the player always knows what is happening.

---

# 29. Visual Effects

Use effects sparingly:

- muzzle flash
- sparks
- steam
- dust
- smoke
- light flicker
- electrical sparks
- impact particles
- subtle fog

The goal is atmosphere rather than visual overload.

---

# 30. UI

Keep UI minimal.

Possible UI:

```text
Health
Ammo
Objective
Interaction prompt
Optional sound indicator
```

Avoid large HUD elements that destroy the horror atmosphere.

---

# 31. Objective Design

A simple objective structure is enough.

Example:

```text
OBJECTIVE 1
Find the generator room.

        ↓

OBJECTIVE 2
Restore emergency power.

        ↓

OBJECTIVE 3
Reach the research core.

        ↓

OBJECTIVE 4
Download the facility data.

        ↓

OBJECTIVE 5
Reach extraction.
```

The objectives should naturally move the player through the map.

---

# 32. Story

Keep the story minimal.

Possible premise:

The facility was conducting research on an autonomous organism capable of detecting and responding to environmental signals.

During a power failure, containment systems failed.

The player enters the facility to recover critical research data.

The facility is now occupied by autonomous entities that rely heavily on sound.

The player must retrieve the data and escape.

Do not spend development time on extensive dialogue or cinematic cutscenes.

---

# 33. AI + Gameplay Integration

The best demo moments should combine multiple systems.

## Example 1

```text
Player shoots
      ↓
Gunshot Sound Event
      ↓
Listener detects
      ↓
A* path
      ↓
Listener investigates
      ↓
Player hides
```

## Example 2

```text
Player throws metal object
      ↓
Listener changes direction
      ↓
Tracker analyzes history
      ↓
Tracker predicts player route
      ↓
Tracker intercepts
```

## Example 3

```text
Player hides
      ↓
Mimic creates fake footsteps
      ↓
Listener investigates fake sound
      ↓
Player escapes
```

## Example 4

```text
Player repeatedly uses corridor
      ↓
Ambusher stores route history
      ↓
Ambusher predicts route
      ↓
Ambusher waits at interception point
      ↓
Player encounters ambush
```

---

# 34. Team Responsibilities

## Member 1 — World Builder + Listener

### Graphics

- level design
- environment layout
- lighting
- materials
- texturing
- NavMesh

### AI

- Listener
- sound perception
- investigation
- search behavior

### Git responsibility

Maintain meaningful commits throughout development.

---

## Member 2 — Systems Engineer + Tracker

### Graphics

- player movement
- shooting
- physics
- doors
- throwable objects
- environmental interaction

### AI

- Tracker
- sound memory
- prediction
- interception

---

## Member 3 — Core Developer + Mimic

### Graphics

- ECHO creature
- generator
- custom models
- Blender
- topology
- UV mapping
- asset import

### AI

- Mimic
- fake sound generation
- deception logic

---

## Member 4 — Agent Controller + Ambusher

### Graphics

- agent movement
- rotation
- animation
- Animator integration
- smooth path following

### AI

- Ambusher
- route history
- interception logic

---

# 35. 22-Day Development Plan

## Days 1–2 — Pre-production

### Deliverables

- final concept
- technical architecture
- asset list
- map layout
- team roles
- Git repository
- Unity project
- basic documentation

### Do NOT

- polish models
- build huge environments
- spend hours on visual effects

---

# Day 3 — Unity Foundation

Implement:

- player controller
- camera
- basic shooting
- basic scene
- basic enemy prefab
- NavMesh foundation

Goal:

> Player can walk and shoot inside a greybox environment.

---

# Day 4 — Sound System

Implement:

- SoundEvent
- SoundManager
- sound registration
- sound types
- sound intensity
- basic hearing range

Goal:

> Player actions generate sound events.

---

# Day 5 — Listener

Implement:

- sound detection
- priority
- investigation
- A* pathfinding
- search behavior

Goal:

> Player shoots → Listener hears → Listener investigates.

This is the first major milestone.

---

# Days 6–7 — Tracker

Implement:

- sound history
- timestamps
- previous positions
- movement estimation
- candidate prediction
- interception

Goal:

> Tracker does something different from Listener.

---

# Days 8–9 — Mimic

Implement:

- fake sound generation
- sound selection
- fake location selection
- cooldown
- strategic deception

Goal:

> Mimic can manipulate another AI.

---

# Days 10–11 — Ambusher

Implement:

- route history
- route frequency
- interception point
- ambush state
- waiting behavior

Goal:

> Ambusher can anticipate player movement.

---

# Days 12–14 — Asset Integration

Complete:

- ECHO model
- generator
- custom machine
- environment assets
- animations
- materials
- prefabs

Import everything into Unity.

---

# Days 15–16 — Environment

Build the final level.

Add:

- walls
- corridors
- rooms
- doors
- pipes
- machinery
- generator
- props
- lighting
- fog
- atmosphere

---

# Day 17 — Gameplay Integration

Add:

- objectives
- extraction
- enemy attacks
- health
- death
- restart
- victory state

---

# Day 18 — Full AI Integration

Run:

```text
Listener
Tracker
Mimic
Ambusher
```

simultaneously.

Fix:

- null references
- pathfinding bugs
- state loops
- animation problems
- performance problems

---

# Day 19 — Audio + VFX

Add:

- ambience
- footsteps
- weapon audio
- door audio
- impact audio
- steam
- sparks
- muzzle flash
- lighting effects

---

# Day 20 — Optimization

Check:

- polygon count
- texture sizes
- draw calls
- unnecessary materials
- physics objects
- AI update frequency
- path recalculation frequency
- lighting
- memory usage

Do not run expensive AI decision logic every frame.

Example:

```text
Movement update → frequent

Decision update → lower frequency

Path recalculation → only when required
```

---

# Day 21 — Polish + Testing

Test:

- new game
- complete level
- restart
- all four agents
- objective progression
- physics
- doors
- sounds
- animations
- final extraction

Record bugs.

Fix only high-impact bugs.

---

# Day 22 — Demo + Viva Preparation

Freeze features.

Prepare:

- final build
- 3-minute demo
- screenshots
- architecture diagram
- AI diagrams
- Git history
- individual explanations
- optimization evidence

---

# 36. Three-Minute Demo Plan

## 0:00–0:20 — Environment

Show:

- facility
- lighting
- custom models
- atmosphere
- ECHO creature

## 0:20–0:50 — Core Mechanic

```text
Player shoots

↓
Sound event

↓
Listener hears

↓
Listener investigates
```

## 0:50–1:20 — Tracker

```text
Player moves through several locations

↓
Multiple sound events

↓
Tracker analyzes history

↓
Predicts movement

↓
Intercepts
```

## 1:20–1:50 — Mimic

```text
Player hides

↓
Mimic creates fake footsteps

↓
Listener reacts

↓
Player escapes
```

## 1:50–2:20 — Ambusher

```text
Player repeatedly uses a route

↓
Ambusher learns route

↓
Selects interception point

↓
Ambush
```

## 2:20–2:45 — Physics

Show:

- door
- throwable object
- generator
- movable object
- environmental interaction

## 2:45–3:00 — Final Shot

Show:

- player
- facility
- ECHO
- lighting
- atmosphere

End with the ECHO tagline:

> **The enemy cannot see you. It can hear you.**

---

# 37. Git Strategy

Because individual Git history matters, commits must be continuous.

Recommended structure:

```text
main
│
└── develop
    │
    ├── feature/world-builder
    ├── feature/systems-engineer
    ├── feature/core-models
    ├── feature/agent-controller
    │
    ├── feature/listener
    ├── feature/tracker
    ├── feature/mimic
    └── feature/ambusher
```

The exact branch strategy can be simplified if the team prefers fewer branches, but every member must have meaningful individual history.

## Good commit messages

```text
feat: add sound event data model
feat: implement listener sound detection
feat: add tracker sound history
feat: implement player prediction
feat: add fake sound generation
feat: add ambush route analysis
feat: import ECHO creature
feat: add generator interaction
fix: resolve listener path reset bug
perf: reduce AI decision update frequency
art: improve facility emergency lighting
```

## Bad commit messages

```text
update
final
stuff
changes
new
fix
done
```

---

# 38. Branching Rules

Recommended:

```text
main
  ↓
Stable submission build

develop
  ↓
Integration branch

feature/*
  ↓
Individual work
```

Avoid directly committing unfinished experimental code into `main`.

---

# 39. Performance Strategy

## AI

Do not perform expensive reasoning every frame.

Example:

```text
Movement:
10–60 updates/sec depending on implementation

Decision:
5–10 times/sec

Pathfinding:
Only when target/goal changes
```

These values are starting points, not strict requirements.

## Physics

Only physics-relevant objects should use Rigidbody.

## Textures

Avoid unnecessarily huge textures.

Use:

- 1K textures for ordinary props where possible
- higher resolution only for hero assets

## Models

Use lower polygon versions for repeated objects.

## Lighting

Avoid dozens of expensive real-time lights.

## Reuse

Reuse modular assets.

---

# 40. Asset Optimization Pipeline

```text
Generated Asset
      ↓
Blender
      ↓
Remove unnecessary geometry
      ↓
Check topology
      ↓
UV
      ↓
Texture optimization
      ↓
FBX
      ↓
Unity
      ↓
Prefab
      ↓
LOD if necessary
```

---

# 41. AI Optimization

Avoid:

```csharp
void Update()
{
    CalculateComplexDecision();
    RunAStar();
}
```

every frame.

Prefer:

```text
Update movement frequently

Evaluate decisions periodically

Run pathfinding only when necessary

React immediately to important events
```

Event-driven sound detection is especially useful.

---

# 42. Testing Strategy

## Unit-Level Testing

Test:

- sound intensity calculation
- distance calculation
- sound priority
- route frequency
- prediction
- ambush point selection

## Gameplay Testing

Test:

- player can complete objective
- doors work
- objects produce sounds
- enemies respond
- extraction works

## AI Testing

Test each agent separately.

### Listener

```text
Sound → Investigate
```

### Tracker

```text
Multiple sounds → Prediction
```

### Mimic

```text
Fake sound → Other AI reacts
```

### Ambusher

```text
Repeated route → Interception
```

## Integration Testing

All four agents must operate together.

---

# 43. Edge Cases

Important edge cases include:

### Sound source disappears

Agent should not freeze.

### Target position becomes unreachable

Agent should recalculate.

### Multiple sounds occur simultaneously

Agent should prioritize.

### Player dies

Agents should reset appropriately.

### Agent dies

System should continue functioning.

### Path becomes invalid

Agent should request a new path.

### Mimic sound expires

Other agents should eventually stop investigating.

### Player does not repeat a route

Ambusher should abandon outdated predictions.

---

# 44. AI State Debugging

During development, show debug information.

For example:

```text
LISTENER

State: INVESTIGATING
Target: Sound #182
Position: 12.4, 0, 18.2
Priority: 0.73
Path Nodes: 14
```

Tracker:

```text
TRACKER

Recent Sounds: 5
Estimated Direction: NE
Prediction Confidence: 0.71
Target: Corridor C
```

Ambusher:

```text
AMBUSHER

Frequent Route: Storage → Corridor
Frequency: 4
Interception Point: Node 17
Confidence: 0.82
```

Disable debug UI for the final build.

---

# 45. Technical Documentation

Each agent owner should document:

1. Problem definition
2. Perception
3. World state
4. Data structures
5. Decision architecture
6. Goal selection
7. Pathfinding
8. Movement
9. Edge cases
10. Complexity
11. Design alternatives
12. Why the chosen approach was selected

---

# 46. Viva Preparation

Every member should be able to explain:

```text
What does my agent do?
Why does it exist?
What information does it perceive?
What information does it remember?
How does it choose a goal?
How does it select a path?
Why A*?
What heuristic is used?
When does it recalculate?
What happens if the path is invalid?
How is the AI separated from visuals?
What happens in edge cases?
What is the computational cost?
```

Do not memorize only the code.

Understand the architecture.

---

# 47. Questions the Viva May Ask

## General

### Why did you choose ECHO?

Because the sound-based mechanic creates a clear connection between player interaction, autonomous perception, pathfinding, and decision-making.

### Why not make multiple levels?

Because the project prioritizes a polished playable prototype and the development window is limited.

### Why Unity?

Because it provides integrated support for 3D rendering, physics, animation, audio, navigation, and rapid game development.

### Why Blender?

Because the Graphics assignment explicitly requires custom 3D modeling work.

---

# 48. AI Viva Questions

## Why A*?

A* provides efficient goal-directed pathfinding using path cost and a heuristic.

## Why not simply move toward the player?

Because that would not demonstrate meaningful autonomous decision-making.

## Why does the Tracker need memory?

Because its defining behavior depends on analyzing multiple previous observations rather than only the latest sound.

## Why does the Mimic generate sounds?

Because the game's central information channel is sound. The Mimic turns that channel into a strategic deception mechanism.

## Why does the Ambusher need route history?

Because its goal is to predict and intercept rather than directly chase.

---

# 49. Graphics Viva Questions

## Why modular environment pieces?

They improve:

- development speed
- consistency
- reuse
- optimization

## Why not use maximum polygon models?

Because unnecessary geometry increases rendering cost without necessarily improving visible quality.

## Why use texture compression?

To reduce memory usage and improve runtime performance.

## Why limit real-time lights?

Because real-time lighting can increase rendering cost.

## Why separate static and dynamic objects?

Because they have different rendering and physics requirements.

---

# 50. Recommended Technology Stack

| Area | Technology |
|---|---|
| Game Engine | Unity 6 LTS |
| Language | C# |
| 3D Modeling | Blender |
| AI 3D Generation | Meshy / Tripo |
| Character Rigging | Mixamo |
| Pathfinding | A* |
| Navigation | Unity NavMesh / project pathfinding |
| Decision Making | FSM + Utility/Scoring logic |
| Audio | Unity Audio |
| Version Control | Git + GitHub |
| Rendering | URP |
| Physics | Unity Physics |

---

# 51. Asset Tool Strategy

## Meshy

Use for:

- character generation
- props
- machines
- image-to-3D
- texture workflows
- rapid iteration

## Tripo

Use as an alternative generator for:

- characters
- props
- environment objects

Compare generated assets rather than committing to one platform for everything.

## Blender

Use for:

- cleanup
- topology
- UV
- scale
- optimization
- required custom models

## Mixamo

Use for:

- humanoid rigging
- walk
- run
- idle
- attack
- hit
- death
- other required animations

---

# 52. Asset Generation Prompt Template

For ECHO characters:

```text
A dark biomechanical humanoid creature designed for a
third-person/first-person horror game, abandoned underground
research facility aesthetic, industrial science-fiction horror,
asymmetrical mechanical-organic details, dark worn material,
subtle metallic surfaces, intimidating silhouette, game-ready
character, neutral T-pose, symmetrical humanoid proportions,
clean readable anatomy, no environment, no weapons, isolated
character, realistic PBR appearance.
```

For props:

```text
A modular abandoned underground research facility generator,
industrial science-fiction laboratory equipment, worn metal,
dark gray materials, exposed cables, mechanical panels,
emergency red indicators, realistic PBR game asset, isolated
object, no background, clean geometry.
```

Always inspect and clean generated models before Unity import.

---

# 53. Definition of Done

The project is considered complete when:

## Gameplay

- [ ] Player can move
- [ ] Player can shoot
- [ ] Player can interact
- [ ] Player can complete objective
- [ ] Player can reach extraction
- [ ] Player can die/restart

## Sound

- [ ] Shooting creates sound
- [ ] Running creates sound
- [ ] Doors create sound
- [ ] Throwables create sound
- [ ] Environmental objects create sound
- [ ] Sound events have intensity and position

## AI

- [ ] Listener works
- [ ] Tracker works
- [ ] Mimic works
- [ ] Ambusher works
- [ ] All four work simultaneously
- [ ] Agents use meaningful pathfinding
- [ ] Agents have different decision systems
- [ ] Agents recover from invalid paths

## Graphics

- [ ] Environment is cohesive
- [ ] Lighting is polished
- [ ] Materials are consistent
- [ ] Custom model #1 complete
- [ ] Custom model #2 complete
- [ ] Animations work
- [ ] Physics works

## Optimization

- [ ] Unnecessary high-poly assets removed
- [ ] Textures optimized
- [ ] Physics optimized
- [ ] AI decision frequency controlled
- [ ] Pathfinding not running unnecessarily
- [ ] No major frame-rate problems

## Submission

- [ ] Git history is clean
- [ ] Individual commits exist
- [ ] Documentation complete
- [ ] Demo recorded
- [ ] Final build tested
- [ ] Viva preparation complete

---

# 54. Emergency Scope Reduction Plan

If the team falls behind, remove features in this order.

## Remove first

1. Crouching
2. Jumping
3. Multiple weapons
4. Breakable glass
5. Complex objective system
6. Extra environmental puzzles
7. Advanced VFX

## Keep at all costs

1. Sound system
2. Listener
3. Tracker
4. Mimic
5. Ambusher
6. A*
7. Main environment
8. Shooting
9. Custom models
10. Physics interactions
11. Lighting
12. Stable final build

The **sound + four-agent system** is the identity of ECHO.

---

# 55. If the Team Gets Ahead

Only after the MVP is stable, consider:

- flashlight-sensitive behavior
- more advanced sound propagation
- destructible lights
- additional sound types
- richer search patterns
- better animations
- dynamic environmental events
- more sophisticated Utility AI
- better prediction
- additional visual effects

Do not add these before the core system is reliable.

---

# 56. Final Architecture

```text
                         ECHO GAME
                             │
          ┌──────────────────┼──────────────────┐
          │                  │                  │
        PLAYER            WORLD             AUDIO
          │                  │                  │
          │            Environment             │
          │            Interactions             │
          │                  │                  │
          └──────────────────┼──────────────────┘
                             │
                             ▼
                       SOUND EVENTS
                             │
                             ▼
                      SOUND MANAGER
                             │
             ┌───────────────┼───────────────┐
             │               │               │
             ▼               ▼               ▼
          LISTENER         TRACKER         MIMIC
             │               │               │
             │               │        Fake Sound Events
             │               │               │
             └───────────────┼───────────────┘
                             │
                             ▼
                         AI WORLD
                             │
                             ▼
                         AMBUSHER
                             │
                             ▼
                      DECISION SYSTEM
                             │
                             ▼
                           A*
                             │
                             ▼
                       PATH / GOAL
                             │
                             ▼
                     AGENT CONTROLLER
                             │
                  ┌──────────┴──────────┐
                  │                     │
               Movement              Rotation
                  │                     │
                  └──────────┬──────────┘
                             ▼
                          Animator
                             │
                             ▼
                           AGENT
```

---

# 57. The Core Technical Idea

The strongest way to explain ECHO is:

> **ECHO is not primarily a game about shooting enemies. It is a game about information.**

The player generates information through sound.

The AI receives information.

Different agents interpret that information differently.

```text
Same Sound
    │
    ├── Listener
    │      → Investigate
    │
    ├── Tracker
    │      → Predict
    │
    ├── Mimic
    │      → Manipulate
    │
    └── Ambusher
           → Intercept
```

That is what makes the four agents meaningfully different.

---

# 58. Final Development Principle

For the entire 22-day project, use this rule:

> **Function first → integration second → visual polish third.**

The recommended progression is:

```text
DAY 1–5
Technical foundation

       ↓

DAY 6–11
Four autonomous agents

       ↓

DAY 12–14
Assets

       ↓

DAY 15–17
Environment + gameplay

       ↓

DAY 18
Integration

       ↓

DAY 19–20
Audio + optimization

       ↓

DAY 21
Polish + testing

       ↓

DAY 22
Demo + viva
```

Do not spend the first week making beautiful models while the AI does not work.

By the end of the first week, the ugly prototype should already demonstrate:

```text
Player shoots
      ↓
Sound Event
      ↓
AI hears
      ↓
A*
      ↓
AI investigates
```

Everything after that is about making this core experience deeper, more intelligent, more atmospheric, and more polished.

---

# 59. Project Success Criteria

ECHO succeeds if a viewer can understand the concept within 30 seconds of seeing the demo.

The viewer should see:

```text
Player makes noise
       ↓
Enemy reacts
       ↓
Player manipulates sound
       ↓
Enemy behavior changes
       ↓
Another agent behaves differently
       ↓
Player is forced to adapt
```

The technical evaluator should see:

```text
Perception
    ↓
Memory
    ↓
Decision-making
    ↓
Pathfinding
    ↓
Movement
    ↓
Animation
```

The Graphics evaluator should see:

```text
Cohesive environment
+
Custom models
+
Lighting
+
Materials
+
Physics
+
Animation
+
Optimization
```

And each team member should be able to clearly demonstrate:

```text
"My role"
+
"My agent"
+
"My implementation"
+
"My Git history"
+
"My technical reasoning"
```

---

# 60. Final One-Sentence Description

> **ECHO is a dark 3D tactical horror shooter where every sound becomes information, and four autonomous enemies interpret that information in fundamentally different ways to investigate, predict, deceive, and ambush the player.**

---

## Source Basis

This project plan is based primarily on the provided SLIIT assignment specifications and the team's ECHO concept document.

### Graphics & Visualization assignment

- SE3032 — Graphics and Visualization, Year 3 Semester 1, 2026
- Interactive 3D/VR Environment assignment
- Group size: 4
- Interactive 3D shooting game requirement
- Four Graphics responsibilities
- Custom modeling requirement
- Project milestones
- Graphics marking rubric

### Intelligent Systems assignment

- SE3062 — Intelligent Systems
- Autonomous Agents in 3D Environments
- Four distinct autonomous agents
- Pathfinding and decision-making requirements
- Individual agent ownership
- AI implementation and viva requirements

### ECHO concept

- Sound-based enemy perception
- Listener
- Tracker
- Mimic
- Ambusher
- Industrial research facility
- Sound-event architecture
- A* pathfinding
- Environmental interactions
- Modular asset strategy
- Three-minute demonstration structure


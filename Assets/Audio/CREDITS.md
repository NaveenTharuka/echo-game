# ECHO — Audio credits

Every clip in `Assets/Audio` was cut from the source recordings below (2026-10-14) by a script:
trimmed, sliced into single hits, 2 ms fade-in / short fade-out, peak-normalised, 16-bit, ≤ 48 kHz,
loops crossfaded to be seamless. The originals are kept outside the project in `D:\Game Sounds and Models`.

> **TODO (owner):** fill in where each source file was downloaded from and its licence
> (e.g. Freesound CC0 / Pixabay / Sonniss GDC bundle — royalty-free, no attribution required).

| Game folder | Source file | Source / licence |
|---|---|---|
| SFX/Footstep | footstep concrete raw.mp3 | TODO |
| SFX/Run | jump land concrete raw.wav | TODO |
| SFX/Land | land thud raw.mp3, jump land concrete raw.wav | TODO |
| SFX/Gunshot | rifile shot indoor.mp3 | TODO |
| SFX/Reload | magazine insert.wav, rifle reload raw.wav | TODO |
| SFX/BulletImpact | bullet impact metal raw.wav, ricochet raw.mp3 | TODO |
| SFX/Door | metal door open raw.wav, heavy door slam.wav | TODO |
| SFX/MetalImpact | pipe hit raw.wav, metal can drop.wav | TODO |
| SFX/GlassBreak | glass break.mp3, bottle smash raw.wav | TODO |
| SFX/ObjectDrop | metal can drop.wav, pipe hit raw.wav | TODO |
| SFX/Machinery | mechanical clicking.wav | TODO |
| SFX/Generator | generator start.wav | TODO |
| SFX/Switch | switch click.mp3, lever pull.flac | TODO |
| Agents/Common (AgentVoice) | heavy breathing monster.wav | TODO |
| Agents/Listener | creature breathing slow.wav, footstep metal grate raw.flac | TODO |
| Agents/Tracker | insect clicks.wav, mechanical clicking.wav | TODO |
| Agents/Ambusher | heavy breathing monster.wav | TODO |
| Ambience | air vent loop.wav, industrial hum loop.wav, room tone industrial.flac, water drip reverb.wav, motor loop.wav | TODO |
| Music | dark ambient drone.wav, horror drone.wav | TODO |

## Second batch (2026-10-21)

Cut by `Tools/Audio/import_new_sounds.py` from `D:\Game Sounds and Models\New Sounds` (list of outputs in
`Tools/Audio/new_sounds_report.json`): breaths split into single inhales/exhales and de-noised, heartbeat loops
rebuilt beat by beat at 66 / 100 / 142 bpm, siren cut to whole cycles, screams sliced into hurt / attack / death,
boomy steps given harmonics so small speakers play them. `*_Far.wav` files (new and first batch) are baked distant
versions (low-passed, softened attack, synthetic concrete-facility reverb) that the game crossfades in with distance.
The first batch's rifle shot and magazine reloads moved to `Unused/` (the gun is now the shotgun).

| Game folder | Source file | Source / licence |
|---|---|---|
| Player/Breath/Calm | Normal_breathing.mp3 | TODO |
| Player/Breath/Heavy | breathing_after_scare.mp3 | TODO |
| Player/Breath/Exhausted | fast_breathing.mp3 | TODO |
| Player/Heartbeat | normal_heartbeat.mp3 | TODO |
| Player/DryFire | dry_fire_click.mp3 | TODO |
| SFX/Jump | player_jump.mp3 | TODO |
| SFX/Gunshot, SFX/Reload (ShotgunPump) | shotgun_fire_with_reload.mp3 | TODO |
| Objectives (Alarm_Siren) | alarm_siren.mp3 | TODO |
| Agents/Listener (Scream/Hurt/Attack/Death) | listener_scream.mp3 | TODO |
| Agents/Ambusher (Scream/Hurt/Attack/Death, Step) | ambusher_scream.mp3, ambusher_footsteps.mp3 | TODO |
| Agents/Tracker (Scream/Hurt/Attack/Death, Step) | tracker_scream.mp3, other_agents_footsteps.mp3 | TODO |
| Agents/Mimic (Step) | other_agents_footsteps.mp3 | TODO |

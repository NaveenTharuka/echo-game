"""Cuts the raw clips in D:/Game Sounds and Models into game-ready WAVs under Assets/Audio.
usage: python import_sounds.py <source dir> <project Assets/Audio dir>
Every output: 16-bit PCM, 48 kHz max, 2 ms fade-in, short fade-out, peak normalised.
SFX + agent sounds are mono (3D). Loops get a 1.5 s crossfade so they loop seamlessly.
"""
import os, sys, json
import numpy as np
import soundfile as sf
from scipy.signal import resample_poly

SRC, DST = sys.argv[1], sys.argv[2]
report = []


def load(name, mono=True):
    x, sr = sf.read(os.path.join(SRC, name), always_2d=True)
    if sr > 48000:
        x = resample_poly(x, 48000, sr, axis=0); sr = 48000
    if mono:
        x = x.mean(1, keepdims=True)
    return x, sr


def env_db(m, sr, hop_s=0.005):
    hop = int(sr * hop_s); n = len(m) // hop
    return 20 * np.log10(np.sqrt((m[:n * hop].reshape(n, hop) ** 2).mean(1)) + 1e-9), hop


def onsets(x, sr, gap, rel):
    db, hop = env_db(x.mean(1), sr)
    thr = np.percentile(db, 99) - rel
    on = db > thr; g = max(1, int(gap / 0.005)); ev = []; i = 0; n = len(db)
    while i < n:
        if on[i]:
            j = i
            while j < n and on[j:j + g].any(): j += 1
            ev.append((i * hop, j * hop, db[i:j].max())); i = j
        else:
            i += 1
    return ev


def finish(x, sr, peak_db=-1.0, fade_out=0.03):
    x = x.copy()
    fi = min(len(x), int(sr * 0.002)); fo = min(len(x), int(sr * fade_out))
    x[:fi] *= np.linspace(0, 1, fi)[:, None]
    x[len(x) - fo:] *= np.linspace(1, 0, fo)[:, None]
    p = np.abs(x).max()
    if p > 0: x *= (10 ** (peak_db / 20)) / p
    return x


def trim(x, sr, rel=50, tail=0.15):
    db, hop = env_db(x.mean(1), sr)
    on = np.where(db > db.max() - rel)[0]
    if len(on) == 0: return x
    a = max(0, on[0] * hop - int(sr * 0.005)); b = min(len(x), (on[-1] + 1) * hop + int(sr * tail))
    return x[a:b]


def write(rel, x, sr, src, note=""):
    path = os.path.join(DST, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    sf.write(path, x, sr, subtype="PCM_16")
    report.append({"file": rel, "source": src, "seconds": round(len(x) / sr, 2), "note": note})


def whole(name, rel, max_len=None, peak=-1.0, fade=0.05):
    x, sr = load(name)
    x = trim(x, sr)
    if max_len: x = x[:int(sr * max_len)]
    write(rel, finish(x, sr, peak, fade), sr, name)


def slices(name, folder, prefix, n, gap, rel, min_len, max_len, peak=-1.0, start=0, skip_loudest=0):
    x, sr = load(name)
    ev = [e for e in onsets(x, sr, gap, rel) if (e[1] - e[0]) / sr >= min_len]
    ev.sort(key=lambda e: -e[2]); ev = ev[skip_loudest:skip_loudest + n]; ev.sort(key=lambda e: e[0])
    starts = [e[0] for e in onsets(x, sr, gap, rel)]
    for k, (a, b, _) in enumerate(ev):
        nxt = min([s for s in starts if s > a] + [len(x)])
        a0 = max(0, a - int(sr * 0.008))
        b0 = min(nxt - int(sr * 0.01), a + int(sr * max_len), len(x))
        seg = x[a0:max(b0, a0 + int(sr * min_len))]
        write(f"{folder}/{prefix}_{start + k + 1:02d}.wav", finish(seg, sr, peak, min(0.06, len(seg) / sr * 0.3)), sr, name)
    return len(ev)


def loop(name, rel, mono, xfade=1.5, max_len=None, peak=-3.0):
    x, sr = load(name, mono=mono)
    if max_len: x = x[:int(sr * max_len)]
    L = int(sr * xfade); N = len(x)
    w = np.linspace(0, 1, L)[:, None]
    seam = x[N - L:] * (1 - w) + x[:L] * w            # tail fading into the head
    y = np.concatenate([x[L:N - L], seam])            # ends where it starts: x[L-1] -> x[L]
    p = np.abs(y).max()
    if p > 0: y *= (10 ** (peak / 20)) / p
    write(rel, y, sr, name, "seamless loop (crossfaded)")


# ---------------- player
slices("footstep concrete raw.mp3", "SFX/Footstep", "Footstep", 8, 0.06, 24, 0.08, 0.40)
slices("jump land concrete raw.wav", "SFX/Run", "Run", 8, 0.05, 24, 0.08, 0.32)
whole("land thud raw.mp3", "SFX/Land/Land_01.wav")
slices("jump land concrete raw.wav", "SFX/Land", "Land", 1, 0.2, 20, 0.3, 0.8, start=1)   # the long 0.48 s hit
whole("rifile shot indoor.mp3", "SFX/Gunshot/Gunshot_01.wav", max_len=2.0, fade=0.3)
whole("magazine insert.wav", "SFX/Reload/Reload_01.wav")
slices("rifle reload raw.wav", "SFX/Reload", "Reload", 4, 0.1, 22, 0.05, 0.6, start=1)

# ---------------- world
slices("bullet impact metal raw.wav", "SFX/BulletImpact", "BulletImpact", 6, 0.15, 24, 0.15, 1.2)
slices("ricochet raw.mp3", "SFX/BulletImpact", "Ricochet", 3, 0.08, 20, 0.15, 0.5)
slices("metal door open raw.wav", "SFX/Door", "Door", 3, 0.3, 26, 1.0, 3.0, fade_out=None) if False else \
    slices("metal door open raw.wav", "SFX/Door", "Door", 3, 0.3, 26, 1.0, 3.0)
whole("heavy door slam.wav", "SFX/Door/DoorSlam_01.wav", fade=0.2)
slices("pipe hit raw.wav", "SFX/MetalImpact", "MetalImpact", 6, 0.15, 24, 0.2, 1.5)
whole("metal can drop.wav", "SFX/MetalImpact/CanDrop_01.wav")
whole("glass break.mp3", "SFX/GlassBreak/GlassBreak_01.wav")
whole("bottle smash raw.wav", "SFX/GlassBreak/BottleSmash_01.wav")
whole("metal can drop.wav", "SFX/ObjectDrop/ObjectDrop_01.wav", peak=-4)
slices("pipe hit raw.wav", "SFX/ObjectDrop", "ObjectDrop", 2, 0.15, 24, 0.2, 1.0, peak=-4, start=1, skip_loudest=6)
slices("mechanical clicking.wav", "SFX/Machinery", "Machinery", 4, 0.03, 22, 0.03, 0.4)
whole("generator start.wav", "SFX/Generator/Generator_01.wav", fade=0.2)
whole("switch click.mp3", "SFX/Switch/Switch_01.wav")
whole("lever pull.flac", "SFX/Switch/Lever_01.wav")

# ---------------- agents
slices("heavy breathing monster.wav", "Agents/Common", "AgentVoice", 5, 0.1, 18, 0.4, 1.1)
loop("creature breathing slow.wav", "Agents/Listener/Listener_Breathing_Loop.wav", mono=True)
slices("footstep metal grate raw.flac", "Agents/Listener", "Listener_Step", 8, 0.04, 22, 0.08, 0.35)
whole("insect clicks.wav", "Agents/Tracker/Tracker_InsectClicks.wav")
slices("mechanical clicking.wav", "Agents/Tracker", "Tracker_Click", 4, 0.03, 22, 0.03, 0.4, skip_loudest=4)
loop("heavy breathing monster.wav", "Agents/Ambusher/Ambusher_Breathing_Loop.wav", mono=True)

# ---------------- ambience + music
loop("air vent loop.wav", "Ambience/AirVent_Loop.wav", mono=True)
loop("industrial hum loop.wav", "Ambience/IndustrialHum_Loop.wav", mono=True)
loop("room tone industrial.flac", "Ambience/RoomTone_Loop.wav", mono=False)
loop("water drip reverb.wav", "Ambience/WaterDrip_Loop.wav", mono=True)
loop("motor loop.wav", "Ambience/Motor_Loop.wav", mono=True, xfade=0.5)
loop("dark ambient drone.wav", "Music/DarkDrone_Loop.wav", mono=False, xfade=3.0)
loop("horror drone.wav", "Music/HorrorDrone_Loop.wav", mono=False, xfade=3.0)

with open(sys.argv[3], "w") as f:
    json.dump(report, f, indent=1)
print(len(report), "files")
for r in report: print(f"{r['seconds']:6.2f}s  {r['file']}")

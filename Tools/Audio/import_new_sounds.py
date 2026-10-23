"""Second sound batch (2026-10-21): cuts, cleans and shapes D:/Game Sounds and Models/New Sounds into Assets/Audio,
and bakes "far" (distant) versions of every loud sound, old and new.

usage: python -I import_new_sounds.py <new sounds dir> <project Assets/Audio dir> <report.json>

Outputs are 16-bit, 48 kHz, mono (the game places them in 3D or plays them in the head).
  * breaths   - single inhales / exhales in 3 tiers (Calm, Heavy, Exhausted), de-noised
  * heartbeat - seamless loops at 3 speeds, rebuilt beat by beat (not sped-up audio)
  * siren     - seamless loop of whole rise/fall cycles
  * screams   - full scream, short scream, hurt / attack barks, death; per agent
  * steps     - single steps per agent (Ambusher, Tracker, Mimic), given some upper body so they read on small speakers
  * gun       - shotgun shot (+ pump inside its tail), pump on its own, dry-fire clicks; player jump
  * *_Far.wav - the same sound heard from far away in a concrete facility: highs gone, transient softened, long tail.
                AudioService crossfades near -> far with distance.
"""
import os, sys, json, glob
import numpy as np
import soundfile as sf
from scipy.signal import resample_poly, butter, sosfilt, sosfiltfilt, stft, istft, fftconvolve

SRC, DST, REPORT = sys.argv[1], sys.argv[2], sys.argv[3]
SR = 48000
report = []
rng = np.random.default_rng(7)


# ------------------------------------------------------------------ basics

def load(name, channel=None):
    """Mono float at 48 kHz. channel=None mixes L+R; an int picks one (for out-of-phase stereo files)."""
    x, sr = sf.read(os.path.join(SRC, name), always_2d=True)
    m = x.mean(1) if channel is None else x[:, min(channel, x.shape[1] - 1)]
    if sr != SR:
        g = np.gcd(SR, sr)
        m = resample_poly(m, SR // g, sr // g)
    return m.astype(np.float64)


def hp(x, f, order=2): return sosfiltfilt(butter(order, f, 'hp', fs=SR, output='sos'), x)
def lp(x, f, order=2): return sosfiltfilt(butter(order, f, 'lp', fs=SR, output='sos'), x)


def fades(x, fin=0.002, fout=0.03):
    x = x.copy(); a = min(len(x), int(SR * fin)); b = min(len(x), int(SR * fout))
    if a: x[:a] *= np.linspace(0, 1, a)
    if b: x[len(x) - b:] *= np.linspace(1, 0, b) ** 1.5
    return x


def norm(x, peak_db=-1.0):
    p = np.abs(x).max()
    return x * (10 ** (peak_db / 20) / p) if p > 0 else x


def pitch(x, ratio):
    """Resample: ratio > 1 = higher and shorter (like a smaller body)."""
    if abs(ratio - 1) < 1e-3: return x
    up, down = 1000, int(round(1000 * ratio))
    g = np.gcd(up, down)
    return resample_poly(x, up // g, down // g)


def saturate(x, drive=2.5, mix=0.35):
    """Soft clipping adds harmonics: a 60 Hz thud gets 120/180 Hz partials a laptop speaker can play."""
    y = np.tanh(drive * x / (np.abs(x).max() + 1e-9)) / np.tanh(drive) * np.abs(x).max()
    return (1 - mix) * x + mix * y


def denoise(x, strength=1.6, floor=0.12):
    """Spectral gate: noise profile = quiet frames per bin; smooth gain so it doesn't 'warble'."""
    f, t, Z = stft(x, SR, nperseg=1024, noverlap=768)
    mag = np.abs(Z)
    noise = np.percentile(mag, 15, axis=1, keepdims=True)
    g = np.clip(1 - strength * noise / (mag + 1e-12), floor, 1)
    k = np.ones((3, 5)) / 15                               # smooth over 3 bins x 5 frames
    from scipy.signal import convolve2d
    g = convolve2d(g, k, mode='same', boundary='symm')
    _, y = istft(Z * g, SR, nperseg=1024, noverlap=768)
    return y[:len(x)]


def env_db(x, hop_s=0.01, smooth=1):
    hop = int(SR * hop_s); n = len(x) // hop
    e = 20 * np.log10(np.sqrt((x[:n * hop].reshape(n, hop) ** 2).mean(1)) + 1e-9)
    return np.convolve(e, np.ones(smooth) / smooth, 'same') if smooth > 1 else e


def segments(x, rel=0.35, min_s=0.12, smooth=15, hp_f=300):
    """Regions above floor + rel*(top-floor) of a smoothed, high-passed envelope -> [(a, b)] in samples."""
    e = env_db(hp(x, hp_f, 4), smooth=smooth)
    lo, hi = np.percentile(e, 8), np.percentile(e, 97)
    on = e > lo + rel * (hi - lo)
    out, i, n, hop = [], 0, len(e), int(SR * 0.01)
    while i < n:
        if on[i]:
            j = i
            while j < n and on[j]: j += 1
            if (j - i) * 0.01 >= min_s: out.append((i * hop, j * hop))
            i = j
        else:
            i += 1
    return out


def write(rel, x, src, note=""):
    path = os.path.join(DST, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    sf.write(path, np.clip(x, -1, 1).astype(np.float32), SR, subtype="PCM_16")
    report.append({"file": rel.replace("\\", "/"), "source": src, "seconds": round(len(x) / SR, 2), "note": note})


def cut(x, a_s, b_s, fin=0.004, fout=0.08):
    return fades(x[int(a_s * SR):int(b_s * SR)], fin, fout)


# ------------------------------------------------------------------ "far" version

def facility_ir(rt60=1.9, length=2.6):
    """Concrete corridors: a few strong early reflections, then a dense tail that loses its highs as it decays."""
    n = int(SR * length); t = np.arange(n) / SR
    tail = rng.standard_normal(n) * np.exp(-6.91 * t / rt60)
    # high frequencies die faster: blend a bright early tail into a dark late tail
    bright, dark = lp(tail, 6000), lp(tail, 1400)
    w = np.clip(t / 0.6, 0, 1)
    tail = bright * (1 - w) + dark * w
    tail[:int(SR * 0.012)] = 0                                    # gap before the diffuse tail
    for d, g in ((0.011, 0.7), (0.019, 0.5), (0.027, -0.45), (0.041, 0.35), (0.058, -0.3), (0.077, 0.22)):
        tail[int(d * SR)] += g * 6                                # early reflections (walls, floor, ceiling)
    return tail / np.sqrt((tail ** 2).sum())


IR = None


def far(x, cutoff=1500, wet=0.7, rt60=1.9):
    """Distant version: highs and the sharp attack go first, the room takes over (dry ~ -10 dB under the tail)."""
    global IR
    if IR is None or abs(len(IR) / SR - rt60 * 1.37) > 0.01: IR = facility_ir(rt60, rt60 * 1.37)
    d = lp(lp(x, cutoff, 2), cutoff * 1.4, 2)                     # air absorption, ~4th order
    d = hp(d, 45)
    # soften the attack: 15 ms rise on the envelope
    att = int(SR * 0.015)
    d[:att] *= np.linspace(0.25, 1, att)
    rev = fftconvolve(d, IR)[:len(d) + len(IR)]
    rev = lp(rev, cutoff * 0.9)
    pre = int(SR * 0.025)                                          # pre-delay: the room answers a bit later
    rev = np.concatenate([np.zeros(pre), rev])
    dry = np.concatenate([d, np.zeros(len(rev) - len(d))])
    y = (1 - wet) * dry / (np.abs(dry).max() + 1e-9) + wet * rev / (np.abs(rev).max() + 1e-9)
    # trim the tail where it falls 60 dB below the peak
    e = env_db(y)
    keep = np.where(e > e.max() - 60)[0]
    end = min(len(y), (keep[-1] + 1) * int(SR * 0.01) + int(SR * 0.05)) if len(keep) else len(y)
    return fades(y[:end], 0.003, 0.25)


def write_far(rel_near, x, src, peak_db=-2.0, **kw):
    base, ext = os.path.splitext(rel_near)
    write(base + "_Far" + ext, norm(far(x, **kw), peak_db), src, "distant version (baked): air absorption + facility tail")


def far_from_existing(folder_glob, **kw):
    """Far versions for clips already in the project (the first batch)."""
    for p in sorted(glob.glob(os.path.join(DST, folder_glob))):
        if p.endswith("_Far.wav") or p.endswith(".meta"): continue
        x, sr = sf.read(p, always_2d=True); x = x.mean(1)
        if sr != SR: x = resample_poly(x, SR, sr)
        rel = os.path.relpath(p, DST)
        write_far(rel, x, os.path.basename(p), **kw)


# ------------------------------------------------------------------ player: breathing

def breaths(name, tier, channel, gain_db, quiet_is_inhale=None, max_each=6):
    """Single breaths. Inhale/exhale: by loudness when they alternate loud/quiet (panting), else by brightness."""
    x = load(name, channel)
    x = hp(x, 90, 4)
    x = denoise(x)
    segs = segments(x)
    info = []
    for a, b in segs:
        s = x[a:b]
        sp = np.abs(np.fft.rfft(s * np.hanning(len(s)))); f = np.fft.rfftfreq(len(s), 1 / SR)
        info.append((a, b, 20 * np.log10(np.sqrt((s ** 2).mean()) + 1e-9), (sp * f).sum() / sp.sum()))
    lv = np.array([i[2] for i in info])
    keep = lv > np.median(lv) - 10                         # drop half-breaths and fragments
    info = [i for i, k in zip(info, keep) if k]
    lv = np.array([i[2] for i in info]); ce = np.array([i[3] for i in info])
    if quiet_is_inhale:
        inh = lv < np.median(lv)
    else:
        inh = ce > np.median(ce)
    # one gain for the whole file keeps the natural in/out balance
    pad_a, pad_b = int(SR * 0.06), int(SR * 0.12)
    clips = []
    for (a, b, _, _), isin in zip(info, inh):
        s = x[max(0, a - pad_a):min(len(x), b + pad_b)]
        clips.append((isin, fades(s, 0.03, 0.12)))
    peak = max(np.abs(c).max() for _, c in clips)
    g = 10 ** (gain_db / 20) / peak
    n_in = n_out = 0
    for isin, c in clips:
        if isin and n_in < max_each:
            n_in += 1; write(f"Player/Breath/{tier}/Breath_{tier}_In_{n_in:02d}.wav", c * g, name, "inhale")
        elif not isin and n_out < max_each:
            n_out += 1; write(f"Player/Breath/{tier}/Breath_{tier}_Out_{n_out:02d}.wav", c * g, name, "exhale")
    return n_in, n_out


# ------------------------------------------------------------------ player: heartbeat

def heartbeat_loops():
    name = "normal_heartbeat.mp3"
    x = load(name)
    x = hp(x, 25, 2)
    e = env_db(x, smooth=3)
    thr = np.percentile(e, 97) - 14
    hop = int(SR * 0.01)
    starts, i = [], 0
    while i < len(e):
        if e[i] > thr:
            starts.append(max(0, i * hop - int(SR * 0.03))); i += 30          # a lub-dub lasts ~0.5 s
        else:
            i += 1
    beats = [x[s:s + int(SR * 0.62)] for s in starts[1:-1]]                   # skip possibly clipped ends
    beats = [saturate(fades(b, 0.003, 0.12), 2.2, 0.3) for b in beats]
    pick = [beats[k] for k in np.linspace(0, len(beats) - 1, 8).astype(int)]
    for label, bpm, beat_len, accent in (("Calm", 66, 0.62, 1.0), ("Fast", 100, 0.52, 1.15), ("Pound", 142, 0.38, 1.35)):
        period = 60.0 / bpm
        n = int(round(8 * period * SR))
        y = np.zeros(n)
        for k, b in enumerate(pick):
            b = fades(b[:int(SR * beat_len)], 0.003, 0.08) * (accent if k % 2 == 0 else accent * 0.92)
            if label == "Pound": b = saturate(b, 3.0, 0.35)
            s = int(round(k * period * SR + rng.normal(0, 0.006) * SR)) % n     # tiny human jitter
            idx = (s + np.arange(len(b))) % n                                    # wrap: the loop has no seam
            np.add.at(y, idx, b)
        write(f"Player/Heartbeat/Heartbeat_{label}_Loop.wav", norm(y, -2), name, f"{bpm} bpm, 8 beats, seamless")


# ------------------------------------------------------------------ siren

def siren_loop():
    name = "alarm_siren.mp3"
    x = hp(load(name), 60)
    e = env_db(x, hop_s=0.01, smooth=25)
    body = e[300:3700] - e[300:3700].mean()                                    # 3 .. 37 s, steady part
    ac = np.correlate(body, body, 'full')[len(body) - 1:]
    lag = 500 + np.argmax(ac[500:1500])                                        # cycle between 5 and 15 s
    P = lag * int(SR * 0.01)
    # start of the loop: a minimum of the envelope (between two wails) after 3 s
    a = (300 + np.argmin(e[300:300 + lag])) * int(SR * 0.01)
    cycles = max(1, int((len(x) - a - int(SR * 3)) // P))
    cycles = min(cycles, 3)
    L = cycles * P; cf = int(SR * 0.4)
    seg = x[a:a + L + cf].copy()
    w = np.linspace(0, 1, cf)
    seg[:cf] = seg[:cf] * w + seg[L:L + cf] * (1 - w)                          # end flows into the start
    y = seg[:L]
    write("Objectives/Alarm_Siren_Loop.wav", norm(y, -2), name, f"{cycles} cycles of {P / SR:.2f} s, seamless")
    write("Objectives/Alarm_Siren_Start.wav", norm(fades(x[:a + int(SR * 0.4)], 0.01, 0.4), -2), name,
          "the siren winding up (plays once before the loop)")
    return P / SR


# ------------------------------------------------------------------ agents

def scream_set(name, agent, channel=None, voice_pitch=1.0, full_end=None, short_end=1.9, bark_offsets=(0.0, 0.35, 0.8),
               body_hp=70):
    x = hp(load(name, channel), body_hp, 2)
    x = pitch(x, voice_pitch)
    segs = segments(x, rel=0.25, min_s=0.3, hp_f=150)
    a = max(0, segs[0][0] - int(SR * 0.02)) if segs else 0
    end = int(full_end * SR) if full_end else len(x)
    full = fades(x[a:end], 0.004, 0.6)
    full = norm(full, -1)
    write(f"Agents/{agent}/{agent}_Scream_01.wav", full, name, "full scream (alert / pack call)")
    write_far(f"Agents/{agent}/{agent}_Scream_01.wav", full, name, cutoff=1300)
    short = norm(fades(x[a:a + int(SR * short_end)], 0.004, 0.45), -1)
    write(f"Agents/{agent}/{agent}_Scream_02.wav", short, name, "short scream")
    write_far(f"Agents/{agent}/{agent}_Scream_02.wav", short, name, cutoff=1300)
    # hurt barks: short slices of the scream's body, each pitched a little differently
    for k, (off, pr) in enumerate(zip(bark_offsets, (1.08, 0.97, 1.15))):
        s = x[a + int(off * SR):a + int((off + 0.7) * SR)]
        write(f"Agents/{agent}/{agent}_Hurt_{k + 1:02d}.wav", norm(fades(pitch(s, pr), 0.004, 0.28), -2), name,
              "hurt bark (slice of the scream)")
    # attack snarls: the scream's onset, faster and higher, cut short
    for k, pr in enumerate((1.12, 1.22)):
        s = x[a:a + int(0.85 * SR)]
        write(f"Agents/{agent}/{agent}_Attack_{k + 1:02d}.wav", norm(fades(pitch(s, pr), 0.003, 0.3), -1), name,
              "attack snarl")
    # death: lower, longer fall-off
    d = pitch(x[a:a + int(min(len(x) - a, 3.2 * SR))], 0.82)
    d = d * np.linspace(1, 0.2, len(d)) ** 1.2
    write(f"Agents/{agent}/{agent}_Death_01.wav", norm(fades(d, 0.004, 0.8), -2), name, "death (scream, lower)")


def steps(name, agent, prefix, which, ratio=1.0, hp_f=40, drive=0.0, gain_db=-1.0, max_len=0.6, min_gap=0.4):
    x = hp(load(name), hp_f, 2)
    e = env_db(x, smooth=2)
    thr = np.percentile(e, 95) - 9
    hop = int(SR * 0.01)
    onsets, i = [], 0
    while i < len(e):
        if e[i] > thr and (i == 0 or e[i - 1] <= thr):
            onsets.append(max(0, i * hop - int(SR * 0.015))); i += int(min_gap * 100)   # a heavy step re-crosses on its decay
        else:
            i += 1
    out = []
    for k, s in enumerate(onsets):
        nxt = onsets[k + 1] if k + 1 < len(onsets) else len(x)
        seg = x[s:min(nxt - int(SR * 0.01), s + int(SR * max_len))]
        out.append(seg)
    chosen = [out[i] for i in which if i < len(out)]
    for k, seg in enumerate(chosen):
        seg = pitch(seg, ratio)
        if drive: seg = saturate(seg, drive, 0.4)
        write(f"Agents/{agent}/{prefix}_{k + 1:02d}.wav", norm(fades(seg, 0.002, 0.08), gain_db), name, "single step")
    return len(out)


# ------------------------------------------------------------------ run

# Player breathing. Normal_breathing's L/R are partly out of phase (corr -0.33): use the left channel only.
print("breath calm", breaths("Normal_breathing.mp3", "Calm", 0, -6))
print("breath heavy", breaths("breathing_after_scare.mp3", "Heavy", 0, -3, quiet_is_inhale=True))
print("breath exhausted", breaths("fast_breathing.mp3", "Exhausted", 0, -2, quiet_is_inhale=True))
heartbeat_loops()
print("siren cycle", siren_loop())

# Jump: shoe scuff + effort. Three takes from one recording (pitch / length).
j = hp(load("player_jump.mp3"), 70)
j = j[np.argmax(np.abs(j) > 0.02 * np.abs(j).max()) - int(SR * 0.005):]
for k, (pr, ln) in enumerate(((1.0, 0.75), (0.95, 0.65), (1.06, 0.7))):
    write(f"SFX/Jump/Jump_{k + 1:02d}.wav", norm(fades(pitch(j[:int(SR * ln)], pr), 0.002, 0.2), -2),
          "player_jump.mp3", "jump take-off")

# Dry fire: trigger + hammer, trigger only, and a duller one.
d = hp(load("dry_fire_click.mp3"), 120)
write("Player/DryFire/DryFire_01.wav", norm(cut(d, 0.08, 0.57, 0.001, 0.05), -2), "dry_fire_click.mp3", "trigger + hammer")
write("Player/DryFire/DryFire_02.wav", norm(cut(d, 0.08, 0.32, 0.001, 0.05), -2), "dry_fire_click.mp3", "trigger only")
write("Player/DryFire/DryFire_03.wav", norm(lp(pitch(cut(d, 0.08, 0.57, 0.001, 0.05), 0.9), 5000), -3),
      "dry_fire_click.mp3", "duller")

# Shotgun: pump (0.0-0.45 s) then the shot at 0.62 s, whose tail has the next pump in it.
g = hp(load("shotgun_fire_with_reload.mp3"), 35)
shot = norm(cut(g, 0.60, 3.0, 0.0005, 0.6), -0.5)
write("SFX/Gunshot/Gunshot_Shotgun_01.wav", shot, "shotgun_fire_with_reload.mp3", "shotgun blast + pump in the tail")
write_far("SFX/Gunshot/Gunshot_Shotgun_01.wav", shot, "shotgun_fire_with_reload.mp3", cutoff=1100, wet=0.75, rt60=2.4)
shot2 = norm(lp(pitch(cut(g, 0.60, 2.4, 0.0005, 0.5), 0.96), 9000), -0.5)
write("SFX/Gunshot/Gunshot_Shotgun_02.wav", shot2, "shotgun_fire_with_reload.mp3", "blast, slightly darker/lower")
write_far("SFX/Gunshot/Gunshot_Shotgun_02.wav", shot2, "shotgun_fire_with_reload.mp3", cutoff=1100, wet=0.75, rt60=2.4)
write("SFX/Reload/ShotgunPump_01.wav", norm(cut(g, 0.0, 0.45, 0.001, 0.06), -2), "shotgun_fire_with_reload.mp3", "pump")

# Agent voices.
scream_set("listener_scream.mp3", "Listener", short_end=1.8, bark_offsets=(0.05, 0.4, 0.9))
scream_set("ambusher_scream.mp3", "Ambusher", full_end=5.3, short_end=2.0)
scream_set("tracker_scream.mp3", "Tracker", channel=0, full_end=7.0, short_end=2.0)   # L/R phasey -> left only

# Agent steps. ambusher_footsteps: 5 boomy steps (centroid 84 Hz) -> harmonics so they're heard on any speaker.
print("ambusher steps", steps("ambusher_footsteps.mp3", "Ambusher", "Ambusher_Step", range(5), hp_f=30, drive=2.5, max_len=0.75, min_gap=0.8))
# other_agents_footsteps: ~10 steps -> Tracker gets the heavier half as-is, Mimic the rest, lighter and higher.
n = steps("other_agents_footsteps.mp3", "Tracker", "Tracker_Step", range(0, 10, 2), hp_f=45, drive=1.5)
steps("other_agents_footsteps.mp3", "Mimic", "Mimic_Step", range(1, 10, 2), ratio=1.18, hp_f=110, gain_db=-3)
print("other steps found", n)

# Far versions for the first batch's loud sounds (gunshot/reload moved to Unused are skipped).
far_from_existing("SFX/GlassBreak/*.wav", cutoff=1600)
far_from_existing("SFX/MetalImpact/*.wav", cutoff=1500)
far_from_existing("SFX/Door/*.wav", cutoff=1300)
far_from_existing("SFX/Generator/*.wav", cutoff=1200, rt60=2.4)
far_from_existing("SFX/Land/*.wav", cutoff=1100)
far_from_existing("SFX/ObjectDrop/*.wav", cutoff=1400)
far_from_existing("SFX/BulletImpact/*.wav", cutoff=1600)
far_from_existing("Agents/Common/AgentVoice_*.wav", cutoff=1300)

with open(REPORT, "w") as f:
    json.dump(report, f, indent=1)
print(len(report), "files")
for r in report: print(f"{r['seconds']:6.2f}s  {r['file']}  ({r['note']})")

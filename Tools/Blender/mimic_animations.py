"""Mimic (old ECHO_Creature body, Models/4): a new, procedural, foot-locked animation set.

Why procedural: the creature's old clips were blocked out by hand (feet slid, loops popped, no overlap). Every clip here is
computed from a small set of pose parameters per frame:
  * legs use analytic two-bone IK; feet roll heel -> flat -> ball around fixed ground pivots, so a planted foot moves
    backwards exactly as fast as the NavMeshAgent moves forwards (stance travel = speed x period x duty);
  * arms, hands and fingers use chained spring lag (each segment's absolute angle trails the one above), the head is
    stabilised against the trunk's sway, loops are exact periods (last key == first key).

Rig: ECHO_Rig (56 bones, metres, front = -Y, Z up, 2.17 m). Bone rotations are written as "world deltas" (rotation in
armature space away from the rest pose) and converted to local quaternions, so nothing depends on bone rolls.

Run on a copy of ECHO_stage8_textured.blend (never on the source):
  blender.exe -b <work>.blend --python mimic_animations.py -- <out_dir> anim          builds actions, saves Mimic_anim.blend
  blender.exe -b <out_dir>/Mimic_anim.blend --python mimic_animations.py -- <out_dir> render [Clip ...]   contact sheets
  blender.exe -b <out_dir>/Mimic_anim.blend --python mimic_animations.py -- <out_dir> export              Mimic.fbx
"""
import bpy, math, os, sys, time
from mathutils import Matrix, Vector, Quaternion

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0]; STAGE = argv[1]; EXTRA = argv[2:]
os.makedirs(OUT, exist_ok=True)
t0 = time.time()
def log(*a): print(f"[mimic {time.time()-t0:6.1f}s]", *a, flush=True)

FPS = 30
TAU = 2 * math.pi
rig = bpy.data.objects.get("ECHO_Rig") or bpy.data.objects.get("Mimic_Rig")
rig.data.pose_position = 'POSE'          # stage8 was saved in Rest Position
BONES = rig.data.bones
REST = {b.name: b.matrix_local.copy() for b in BONES}
ORDER = sorted((b.name for b in BONES), key=lambda n: len(BONES[n].parent_recursive))
PARENT = {b.name: (b.parent.name if b.parent else None) for b in BONES}
SIDES = (("L", 1.0), ("R", -1.0))
FINGERS = ("index", "middle", "ring", "pinky")

def bdir(n):
    b = BONES[n]; return (b.tail_local - b.head_local).normalized()

# Foot geometry (measured from the leg mesh): sole at z = 0, heel pivot behind the ankle, ball pivot under the toe joint.
HEEL_Y, BALL_Y = 0.12, -0.08
HEAD_LIFT = -12.0     # the hunch adds up to ~40 deg forward: lift the face so it looks ahead, not at the floor
L_THIGH = (BONES["shin_L"].head_local - BONES["thigh_L"].head_local).length
L_SHIN = (BONES["foot_L"].head_local - BONES["shin_L"].head_local).length

# ---------------------------------------------------------------------------------------------------------- math helpers
def Rx(d): return Matrix.Rotation(math.radians(d), 3, 'X')
def Ry(d): return Matrix.Rotation(math.radians(d), 3, 'Y')
def Rz(d): return Matrix.Rotation(math.radians(d), 3, 'Z')
def Rax(axis, d):
    return Matrix.Rotation(math.radians(d), 3, axis) if axis.length > 1e-6 else Matrix.Identity(3)
def toward(d, target, deg):
    """Rotation that turns direction d towards `target` by deg (axis d x target)."""
    return Rax(d.cross(target).normalized(), deg)

def clamp(x, a=0.0, b=1.0): return max(a, min(b, x))
def smooth(x): x = clamp(x); return x * x * (3 - 2 * x)
def smoother(x): x = clamp(x); return x * x * x * (x * (6 * x - 15) + 10)
def hermite(p0, p1, m0, m1, s):
    s2, s3 = s * s, s * s * s
    return (2*s3 - 3*s2 + 1) * p0 + (s3 - 2*s2 + s) * m0 + (-2*s3 + 3*s2) * p1 + (s3 - s2) * m1

def pchip(keys, x, periodic=False):
    """Monotone cubic through (x, y) keys (no overshoot). periodic: keys over [0, 1) wrap around."""
    if periodic:
        k = [(a - 1, b) for a, b in keys[-2:]] + list(keys) + [(a + 1, b) for a, b in keys[:2]]
        x = x % 1.0
    else:
        k = list(keys)
        if x <= k[0][0]: return k[0][1]
        if x >= k[-1][0]: return k[-1][1]
    xs = [a for a, _ in k]; ys = [b for _, b in k]
    i = max(0, min(len(k) - 2, next(j for j in range(len(k) - 1) if xs[j + 1] >= x)))
    def slope(j):
        if j <= 0 or j >= len(k) - 1: return (ys[min(j+1, len(k)-1)] - ys[max(j-1, 0)]) / (xs[min(j+1, len(k)-1)] - xs[max(j-1, 0)])
        d0 = (ys[j] - ys[j-1]) / (xs[j] - xs[j-1]); d1 = (ys[j+1] - ys[j]) / (xs[j+1] - xs[j])
        if d0 * d1 <= 0: return 0.0
        h0, h1 = xs[j] - xs[j-1], xs[j+1] - xs[j]
        w0, w1 = 2*h1 + h0, h1 + 2*h0
        return (w0 + w1) / (w0 / d0 + w1 / d1)
    h = xs[i+1] - xs[i]
    return hermite(ys[i], ys[i+1], slope(i) * h, slope(i+1) * h, (x - xs[i]) / h)

def wave(phi, *harm):
    """Periodic smooth noise: sum of (amp, harmonic, phase) cosines over phi in [0,1)."""
    return sum(a * math.cos(TAU * (n * phi + p)) for a, n, p in harm)

def bump(t, t0, t1):
    """0 -> 1 -> 0 smooth bell between t0 and t1."""
    if t <= t0 or t >= t1: return 0.0
    return math.sin(math.pi * (t - t0) / (t1 - t0)) ** 2

def spring(xs, hz, zeta=0.75, periodic=False):
    """Critically-ish damped follower: the output trails xs (overlapping action)."""
    w = TAU * hz; dt = 1.0 / FPS / 4
    y, v = xs[0], 0.0
    out = []
    for _ in range(3 if periodic else 1):
        out = []
        for x in xs:
            for _s in range(4):
                a = w * w * (x - y) - 2 * zeta * w * v
                v += a * dt; y += v * dt
            out.append(y)
    return out

# ----------------------------------------------------------------------------------------------- base posture ("stand")
P0 = dict(px=0.0, py=0.0, pz=0.0, p_pitch=0.0, p_roll=0.0, p_yaw=0.0,
          s1_p=0.0, s1_r=0.0, s1_y=0.0, s2_p=0.0, s2_r=0.0, s2_y=0.0, s3_p=0.0, s3_r=0.0, s3_y=0.0,
          nk_p=0.0, nk_r=0.0, nk_y=0.0, hd_p=0.0, hd_r=0.0, hd_y=0.0, jaw=0.0)
for s, _m in SIDES:
    for k in ("ear_fold", "ear_up", "cl_up", "cl_fwd", "sh_fwd", "sh_out", "sh_tw", "el", "wr_fwd", "wr_curl",
              "curl", "spread", "thumb", "ft_f", "ft_x", "ft_z", "ft_pitch", "toe", "ft_yaw", "k_out"):
        P0[f"{k}_{s}"] = 0.0

def both(d, **kw):
    for k, v in kw.items():
        d[k + "_L"] = v; d[k + "_R"] = v
    return d

# Gaunt, hunched stance from the concept: knees soft, trunk tipped forward, head jutting, long arms hanging loose.
STAND = dict(P0)
STAND.update(pz=-0.045, p_pitch=6, s1_p=4, s2_p=7, s3_p=9, nk_p=14, hd_p=-30, jaw=2)
both(STAND, cl_fwd=6, cl_up=-2, sh_fwd=2, sh_out=8, sh_tw=-6, el=14, wr_fwd=4, wr_curl=8, curl=22, spread=4, thumb=10,
     ft_x=0.01, ft_yaw=7, k_out=0.15, ear_fold=-4)

def P(base=STAND, **kw):
    d = dict(base); d.update(kw); return d

# ------------------------------------------------------------------------------------------------------- pose solver
def foot_target(p, s, m, Wp):
    """Ankle position + foot rotation from foot params (ft_f forward, ft_z lift, ft_pitch toes-up +, roll around heel/ball)."""
    foot = REST["foot_" + s]
    ankle0 = foot.translation.copy()
    off = Vector((m * p["ft_x_" + s], -p["ft_f_" + s], p["ft_z_" + s]))
    pitch = p["ft_pitch_" + s]
    Yw = Rz(m * -p["ft_yaw_" + s])
    R = Yw @ Rx(-pitch)
    piv = Vector((ankle0.x, HEEL_Y if pitch > 0 else BALL_Y, 0.0))
    piv_w = ankle0 + Yw @ (piv - ankle0) + off          # the pivot turns with the toed-out foot
    ankle = piv_w + R @ (ankle0 - piv)
    return ankle, R

def frame_of(d, pole):
    x = d.normalized(); y = (pole - pole.dot(x) * x).normalized(); z = x.cross(y)
    return Matrix((x, y, z)).transposed()

def solve(p):
    """Params -> {bone: armature-space pose matrix}. Also returns IK reach ratios."""
    W, M, reach = {}, {}, {}
    m_of = {"L": 1.0, "R": -1.0}
    for n in ORDER:
        par = PARENT[n]
        Wpar = W[par] if par else Matrix.Identity(3)
        side = n[-1] if n[-2:] in ("_L", "_R") else None
        m = m_of.get(side, 1.0)
        if n == "root":
            Wn = Matrix.Identity(3)
        elif n == "pelvis":
            Wn = Rz(p["p_yaw"]) @ Ry(p["p_roll"]) @ Rx(p["p_pitch"])
        elif n in ("spine_01", "spine_02", "spine_03", "neck_01", "head"):
            k = {"spine_01": "s1", "spine_02": "s2", "spine_03": "s3", "neck_01": "nk", "head": "hd"}[n]
            Wn = Wpar @ Rz(p[k + "_y"]) @ Ry(p[k + "_r"]) @ Rx(p[k + "_p"] + (HEAD_LIFT if k == "hd" else 0.0))
        elif n == "jaw":
            Wn = Wpar @ Rx(p["jaw"])
        elif n.startswith("ear_"):
            Wn = Wpar @ Rz(m * p["ear_fold_" + side]) @ Ry(-m * p["ear_up_" + side])
        elif n.startswith("clavicle_"):
            Wn = Wpar @ Rz(-m * p["cl_fwd_" + side]) @ Ry(-m * p["cl_up_" + side])
        elif n.startswith("upperarm_"):
            Wn = Wpar @ Rx(-p["sh_fwd_" + side]) @ Ry(-m * p["sh_out_" + side]) @ Rax(bdir(n), m * p["sh_tw_" + side])
        elif n.startswith("forearm_"):
            Wn = Wpar @ toward(bdir(n), Vector((0, -1, 0)), p["el_" + side])
        elif n.startswith("hand_"):
            palm = Vector((-m, 0, 0))
            Wn = Wpar @ Rx(-p["wr_fwd_" + side]) @ toward(bdir(n), palm, p["wr_curl_" + side])
        elif n.startswith("thumb_"):
            Wn = Wpar @ toward(bdir(n), Vector((-m * 0.5, 0.2, -0.8)), p["thumb_" + side] * (0.5 if n[6] == "1" else 1.0))
        elif n.split("_")[0] in FINGERS:
            palm = Vector((-m, -0.25, 0)).normalized()
            seg = int(n.split("_")[1])
            fi = FINGERS.index(n.split("_")[0])
            Wn = Wpar @ toward(bdir(n), palm, p["curl_" + side] * (0.55, 1.0, 0.85)[seg - 1] * (0.85 + 0.1 * fi))
            if seg == 1:
                Wn = Wn @ Rax(Vector((-m, 0, 0)), (fi - 1.5) * p["spread_" + side] * m)
        elif n.startswith(("thigh_", "shin_", "foot_", "toe_")):
            Wn = None   # legs below
        else:
            Wn = Wpar
        if n.startswith("thigh_"):
            Wp = W["pelvis"]
            hip = (M["pelvis"] @ (REST["pelvis"].inverted() @ REST[n])).translation
            ankle, Rfoot = foot_target(p, side, m, Wp)
            pole = Wp @ Vector((m * p["k_out_" + side], -1, 0)).normalized()
            d = ankle - hip
            L = d.length
            reach[side] = L / (L_THIGH + L_SHIN)
            L = clamp(L, abs(L_THIGH - L_SHIN) + 1e-3, (L_THIGH + L_SHIN) * 0.9995)
            e = d.normalized()
            ca = clamp((L_THIGH**2 + L * L - L_SHIN**2) / (2 * L_THIGH * L), -1, 1)
            wv = (pole - pole.dot(e) * e).normalized()
            knee = hip + e * (L_THIGH * ca) + wv * (L_THIGH * math.sqrt(1 - ca * ca))
            pole0 = Vector((0, -1, 0))
            shin_n = "shin_" + side
            Wt = frame_of(knee - hip, pole) @ frame_of(bdir(n), pole0).transposed()
            Ws = frame_of(hip + e * L - knee, pole) @ frame_of(bdir(shin_n), pole0).transposed()
            W[n] = Wt
            M[n] = Matrix.Translation(hip) @ (Wt @ REST[n].to_3x3()).to_4x4()
            W[shin_n] = Ws
            M[shin_n] = Matrix.Translation(knee) @ (Ws @ REST[shin_n].to_3x3()).to_4x4()
            ank = knee + Ws @ (REST["foot_" + side].translation - REST[shin_n].translation)
            W["foot_" + side] = Rfoot
            M["foot_" + side] = Matrix.Translation(ank) @ (Rfoot @ REST["foot_" + side].to_3x3()).to_4x4()
            continue
        if n.startswith(("shin_", "foot_")):
            continue
        if n.startswith("toe_"):
            Wn = W["foot_" + side] @ Rx(-p["toe_" + side])
        W[n] = Wn
        if par:
            head = (M[par] @ (REST[par].inverted() @ REST[n])).translation
        else:
            head = REST[n].translation.copy()
        if n == "pelvis":
            head = head + Vector((p["px"], -p["py"], p["pz"]))
        M[n] = Matrix.Translation(head) @ (Wn @ REST[n].to_3x3()).to_4x4()
    return M, reach

def to_basis(M):
    out = {}
    for n in ORDER:
        par = PARENT[n]
        rel = REST[n] if par is None else REST[par].inverted() @ REST[n]
        Mp = Matrix.Identity(4) if par is None else M[par]
        out[n] = rel.inverted() @ Mp.inverted() @ M[n]
    return out

# ------------------------------------------------------------------------------------------------------------ gaits
class Gait:
    def __init__(self, **kw): self.__dict__.update(kw)

def gait_foot(g, phi):
    """Forward offset, lift, pitch, toe for one foot at phase phi (contact at phi = 0)."""
    D = g.v * g.T * g.duty                      # stance travel of the ground pivot
    if phi < g.duty:
        u = phi / g.duty
        f, z = D / 2 - D * u, 0.0
        sw = 0.0
    else:
        s = (phi - g.duty) / (1 - g.duty)
        mt = -D / g.duty * (1 - g.duty)         # leave and land with ground speed (no foot pop)
        f = hermite(-D / 2, D / 2, mt * g.lift_out, mt * g.land, s)
        warp = s ** (math.log(0.5) / math.log(g.lift_peak))
        z = g.lift * math.sin(math.pi * warp) ** g.lift_shape
        sw = s
    pitch = pchip(g.pitch, phi, periodic=True)
    if phi < g.duty:
        toe = max(0.0, -pitch) * 0.95 + g.toe_stance
    else:
        toe = g.toe_swing * math.sin(math.pi * sw) + max(0.0, -pitch) * (1 - smooth(sw * 4))
    return f, z, pitch, toe, sw

def locomotion(g, frames):
    N = frames
    rows = []
    for i in range(N):
        phi = i / N
        p = dict(g.base)
        for s, m, ph in (("L", 1.0, 0.0), ("R", -1.0, 0.5)):
            f, z, pitch, toe, sw = gait_foot(g, (phi + ph) % 1.0)
            p["ft_f_" + s] = f + g.foot_center
            p["ft_z_" + s] = z
            p["ft_pitch_" + s] = pitch
            p["toe_" + s] = toe
            p["ft_x_" + s] = g.base["ft_x_" + s] + g.swing_arc * math.sin(math.pi * sw)
        # pelvis: bob (2 / cycle), lateral sway over the stance foot, roll (swing hip drops), yaw (swing hip forward)
        mid = g.duty / 2
        p["pz"] += g.bob * math.cos(2 * TAU * (phi - mid)) * g.bob_sign
        p["px"] += g.sway * math.cos(TAU * (phi - mid))
        p["p_roll"] += -g.roll * math.cos(TAU * (phi - mid - 0.04))
        p["p_yaw"] += -g.yaw * math.cos(TAU * (phi - 0.02))
        p["p_pitch"] += g.pitch_osc * math.cos(2 * TAU * (phi - mid - 0.05))
        p["py"] += g.surge * math.cos(2 * TAU * (phi - mid + 0.1))
        # thorax counter-rotates against the pelvis, chest roll follows the stance side a little
        p["s2_y"] += g.yaw * g.counter * 0.4 * math.cos(TAU * (phi - 0.06))
        p["s3_y"] += g.yaw * g.counter * 0.6 * math.cos(TAU * (phi - 0.08))
        p["s2_r"] += g.roll * 0.5 * math.cos(TAU * (phi - mid - 0.1))
        p["s1_p"] += g.breathe * math.cos(2 * TAU * (phi - mid - 0.12))
        # arms swing against the legs (left arm forward when the right foot is forward, phi = 0.5)
        for s, ph in (("L", 0.5), ("R", 0.0)):
            c = math.cos(TAU * (phi - ph - 0.03))
            p["sh_fwd_" + s] += g.arm * c
            p["el_" + s] += g.elbow_osc * (0.5 + 0.5 * c)
            p["sh_out_" + s] += g.arm_out * (0.5 + 0.5 * math.cos(2 * TAU * (phi - ph)))
            p["cl_fwd_" + s] += g.arm * 0.12 * c
            p["curl_" + s] += g.curl_osc * math.cos(TAU * (phi - ph - 0.2))
        # head: hold steady against the trunk
        tz = p["p_yaw"] + p["s1_y"] + p["s2_y"] + p["s3_y"] - g.base["p_yaw"] - g.base["s2_y"] - g.base["s3_y"]
        tr = p["p_roll"] + p["s2_r"] - g.base["p_roll"] - g.base["s2_r"]
        tp = p["p_pitch"] - g.base["p_pitch"]
        p["nk_y"] -= 0.35 * tz; p["hd_y"] -= 0.55 * tz
        p["nk_r"] -= 0.3 * tr; p["hd_r"] -= 0.5 * tr
        p["hd_p"] -= 0.8 * tp + g.head_bob * math.cos(2 * TAU * (phi - mid - 0.15))
        p["ear_fold_L"] += g.ear * math.cos(2 * TAU * (phi - mid - 0.25))
        p["ear_fold_R"] += g.ear * math.cos(2 * TAU * (phi - mid - 0.25))
        rows.append(p)
    return overlap(rows, periodic=True, hz=g.lag_hz)

def overlap(rows, periodic=False, hz=(3.2, 2.6, 2.2, 2.0)):
    """Chained lag: upper arm trails the trunk, forearm/hand/fingers trail the segment above (absolute angles)."""
    keys = lambda k: [r[k] for r in rows]
    for s in ("L", "R"):
        sh = spring(keys("sh_fwd_" + s), hz[0], 0.8, periodic)
        ab_el = spring([a + b for a, b in zip(sh, keys("el_" + s))], hz[1], 0.7, periodic)
        ab_wr = spring([a + b for a, b in zip(ab_el, keys("wr_fwd_" + s))], hz[2], 0.7, periodic)
        out = spring(keys("sh_out_" + s), hz[0], 0.8, periodic)
        curl = spring(keys("curl_" + s), hz[3], 0.8, periodic)
        wc = spring(keys("wr_curl_" + s), hz[2], 0.75, periodic)
        for i, r in enumerate(rows):
            r["sh_fwd_" + s] = sh[i]
            r["el_" + s] = max(3.0, ab_el[i] - sh[i])
            r["wr_fwd_" + s] = ab_wr[i] - ab_el[i]
            r["sh_out_" + s] = out[i]
            r["curl_" + s] = curl[i]
            r["wr_curl_" + s] = wc[i]
        for k in ("ear_fold_", "ear_up_"):
            v = spring(keys(k + s), 4.0, 0.6, periodic)
            for i, r in enumerate(rows): r[k + s] = v[i]
    return rows

# Walk 2.2 m/s: brisk, stalking human gait, hunched, heel strike -> toe off.
WALK = Gait(v=2.2, T=0.8, duty=0.6, lift=0.11, lift_peak=0.38, lift_shape=1.0, lift_out=0.8, land=0.35,
            pitch=[(0.0, 16), (0.1, 0), (0.33, 0), (0.6, -38), (0.72, -28), (0.9, 8)],
            toe_stance=0.0, toe_swing=8, foot_center=-0.01, swing_arc=0.015,
            bob=0.022, bob_sign=1, sway=0.022, roll=4.5, yaw=7, pitch_osc=1.5, surge=0.01, counter=1.0, breathe=0.8,
            arm=17, elbow_osc=10, arm_out=2, curl_osc=4, head_bob=1.2, ear=3, lag_hz=(3.2, 2.6, 2.2, 2.0),
            base=both(P(pz=-0.115, p_pitch=9, s1_p=5, s2_p=8, s3_p=8, nk_p=16, hd_p=-36), sh_out=12, sh_fwd=-2,
                      wr_curl=2))
# Sneak 1.2 m/s: low, slow, toe-first, arms raised in front, fingers splayed, head level and still.
SNEAK = Gait(v=1.2, T=1.1, duty=0.72, lift=0.13, lift_peak=0.45, lift_shape=1.2, lift_out=0.6, land=0.15,
             pitch=[(0.0, -10), (0.12, 0), (0.45, 0), (0.72, -32), (0.8, -36), (0.93, -14)],
             toe_stance=0.0, toe_swing=-4, foot_center=0.02, swing_arc=0.03,
             bob=0.012, bob_sign=1, sway=0.035, roll=3.0, yaw=5, pitch_osc=1.0, surge=0.0, counter=1.0, breathe=0.6,
             arm=4, elbow_osc=3, arm_out=1, curl_osc=8, head_bob=0.6, ear=5, lag_hz=(2.4, 2.0, 1.8, 1.6),
             base=both(P(pz=-0.21, p_pitch=16, s1_p=8, s2_p=10, s3_p=8, nk_p=12, hd_p=-50, jaw=4),
                       sh_fwd=38, sh_out=14, el=58, wr_fwd=-14, wr_curl=4, curl=32, spread=10, cl_up=4, cl_fwd=12,
                       ft_x=0.03, ft_yaw=9, k_out=0.25, ear_fold=-10, ear_up=6))
# Run 5.5 m/s: forward lean, flight phase, forefoot strike, heel kick, big pumping arms with loose hands.
RUN = Gait(v=5.5, T=14 / 30, duty=0.34, lift=0.30, lift_peak=0.32, lift_shape=1.0, lift_out=0.55, land=0.45,
           pitch=[(0.0, 4), (0.07, 0), (0.17, -4), (0.34, -46), (0.48, -55), (0.75, -12), (0.92, 8)],
           toe_stance=0.0, toe_swing=12, foot_center=0.07, swing_arc=0.02,
           bob=0.045, bob_sign=-1, sway=0.012, roll=5, yaw=11, pitch_osc=3.0, surge=0.02, counter=1.1, breathe=1.5,
           arm=38, elbow_osc=22, arm_out=4, curl_osc=10, head_bob=2.5, ear=8, lag_hz=(4.5, 3.6, 3.0, 2.6),
           base=both(P(pz=-0.16, p_pitch=16, s1_p=7, s2_p=9, s3_p=6, nk_p=10, hd_p=-44, jaw=8),
                     sh_fwd=6, sh_out=8, el=62, wr_fwd=-6, curl=34, cl_fwd=4, ft_x=-0.02, ft_yaw=4, k_out=0.1,
                     ear_fold=-22))

# ------------------------------------------------------------------------------------------------- loops (in place)
def idle_rows(N, base, listen=False):
    rows = []
    for i in range(N):
        u = i / N
        p = dict(base)
        br = math.cos(TAU * 2 * u)                       # two slow breaths per loop
        p["s2_p"] -= 1.6 * br; p["s3_p"] -= 2.2 * br
        both_add(p, "cl_up", 1.4 * br)
        p["jaw"] += 1.5 * (0.5 - 0.5 * br)
        p["px"] += 0.018 * math.cos(TAU * u)             # weight shift side to side
        p["p_roll"] += 1.8 * math.cos(TAU * u + 0.3)
        p["p_yaw"] += wave(u, (1.5, 1, 0.1), (0.6, 3, 0.4))
        p["pz"] += 0.006 * math.cos(TAU * 2 * u + 1.0)
        p["s3_y"] -= 0.6 * p["p_yaw"]
        p["s2_r"] -= 1.0 * math.cos(TAU * u + 0.3)
        p["nk_y"] += wave(u, (4, 1, 0.3), (2.5, 2, 0.8), (1.0, 5, 0.2))
        p["hd_p"] += wave(u, (2.5, 1, 0.6), (1.2, 3, 0.1))
        p["hd_r"] += wave(u, (3, 1, 0.75), (1.2, 4, 0.5))
        for s, ph in (("L", 0.0), ("R", 0.37)):
            p["curl_" + s] += wave(u + ph, (6, 1, 0.2), (3, 3, 0.7))
            p["sh_fwd_" + s] += wave(u + ph, (1.5, 1, 0.5))
            p["sh_out_" + s] += 1.0 * br
        if listen:
            # slow listening sweep: tilt the head towards one side, hold, swing to the other; ears hunt for the sound
            sweep = math.sin(TAU * u)
            hold = math.copysign(abs(sweep) ** 0.5, sweep)
            p["hd_r"] += 17 * hold; p["nk_y"] += 22 * hold; p["hd_y"] += 10 * hold
            p["nk_r"] += 5 * hold; p["hd_p"] += 4 * abs(hold)
            p["s3_y"] += 5 * hold; p["s2_r"] -= 3 * hold
            p["ear_up_L"] += 10 + 8 * max(0.0, -hold); p["ear_up_R"] += 10 + 8 * max(0.0, hold)
            p["ear_fold_L"] -= 10 * max(0.0, -hold); p["ear_fold_R"] -= 10 * max(0.0, hold)
            tw = bump(u, 0.18, 0.23) - bump(u, 0.68, 0.73)
            p["ear_fold_L"] += 14 * tw; p["ear_fold_R"] -= 14 * tw
        else:
            tw = bump(u, 0.31, 0.35) + 0.6 * bump(u, 0.37, 0.40)   # one ear twitch per loop
            p["ear_fold_L"] += 16 * tw; p["ear_up_L"] += 6 * tw
            p["ear_fold_R"] += 10 * bump(u, 0.78, 0.82)
        rows.append(p)
    return overlap(rows, periodic=True, hz=(2.4, 2.0, 1.8, 1.6))

def both_add(p, k, v):
    p[k + "_L"] += v; p[k + "_R"] += v

LISTEN_BASE = both(P(pz=-0.10, p_pitch=10, s1_p=6, s2_p=8, s3_p=8, nk_p=16, hd_p=-38),
                   sh_fwd=10, sh_out=6, el=26, curl=30, spread=8, ft_x=0.04, ft_yaw=10, k_out=0.25, ear_up=4)

# ----------------------------------------------------------------------------------------------- one-shots (timelines)
def timeline(N, keys, extra=None, hz=(3.0, 2.5, 2.1, 1.9)):
    """keys: [(t seconds, params dict)]. Each param is pchip-interpolated through the keys where it changes."""
    names = list(keys[0][1].keys())
    rows = []
    for i in range(N + 1):
        t = i / FPS
        p = {k: pchip([(kt, kp[k]) for kt, kp in keys], t) for k in names}
        if extra: extra(p, t)
        rows.append(p)
    return overlap(rows, periodic=False, hz=hz)

def alert_clip():
    # Startle: flinch down, then snap up tall, head whips towards the sound, ears forward, claws open; settle tense.
    k = [(0.0, P()),
         (0.10, both(P(pz=-0.07, p_pitch=10, s3_p=12, hd_p=-26, nk_p=18), sh_out=5, el=18, curl=30, ear_fold=-6)),
         (0.30, both(P(pz=-0.005, p_pitch=-2, s1_p=-2, s2_p=-3, s3_p=2, nk_p=4, hd_p=-12, hd_y=14, nk_y=10, jaw=16,
                       s3_y=6), sh_out=16, sh_fwd=12, el=30, cl_up=8, curl=8, spread=14, wr_curl=-6,
                    ear_up=24, ear_fold=-24)),
         (0.55, both(P(pz=-0.03, p_pitch=3, s3_p=6, nk_p=10, hd_p=-22, hd_y=12, nk_y=8, jaw=10, s3_y=5),
                     sh_out=12, sh_fwd=14, el=34, cl_up=5, curl=20, spread=10, ear_up=18, ear_fold=-18)),
         (0.85, both(P(pz=-0.06, p_pitch=8, s3_p=9, nk_p=15, hd_p=-30, hd_y=8, nk_y=6, jaw=6, s3_y=3),
                     sh_out=8, sh_fwd=10, el=28, curl=28, spread=6, ear_up=12, ear_fold=-12)),
         (1.2, P())]
    def jitter(p, t):
        p["hd_r"] += 2.0 * math.sin(TAU * 9 * t) * bump(t, 0.25, 0.6)
    return timeline(36, k, jitter, hz=(4.0, 3.4, 2.8, 2.4))

def call_clip():
    # Mimicry: deep inhale (chest + clavicles rise), head tips back, jaw opens wide and the throat vibrates while the
    # fake sound is produced; fingers tremble; exhale and close.
    k = [(0.0, P()),
         (0.45, both(P(s2_p=2, s3_p=1, nk_p=6, hd_p=-38, jaw=6, pz=-0.02), cl_up=9, sh_out=7, el=18, curl=14,
                     ear_up=10, ear_fold=-14)),
         (0.75, both(P(s2_p=4, s3_p=4, nk_p=4, hd_p=-50, hd_r=-8, jaw=34, pz=-0.04), cl_up=6, sh_out=6, el=24,
                     curl=36, spread=12, ear_up=18, ear_fold=-26)),
         (1.45, both(P(s2_p=7, s3_p=8, nk_p=10, hd_p=-46, hd_r=-10, jaw=30, pz=-0.05), cl_up=3, sh_out=5, el=24,
                     curl=40, spread=12, ear_up=16, ear_fold=-24)),
         (1.8, both(P(s2_p=8, s3_p=10, nk_p=14, hd_p=-32, jaw=8), cl_up=-1, el=16, curl=26, ear_fold=-8)),
         (2.2, P())]
    def voice(p, t):
        v = bump(t, 0.7, 1.6)
        p["jaw"] += 3.0 * math.sin(TAU * 14 * t) * v
        p["hd_p"] += 1.0 * math.sin(TAU * 7 * t + 1) * v
        p["s3_p"] += 0.8 * math.sin(TAU * 7 * t) * v
        for s, ph in (("L", 0.0), ("R", 1.3)):
            p["curl_" + s] += 5 * math.sin(TAU * 11 * t + ph) * v
    return timeline(66, k, voice)

def attack_clip():
    # 1.4 s lunge-swipe; hit window around 0.5 s (AgentBody windup). Left foot steps in, right claw rakes down-across.
    k = [(0.0, P()),
         (0.32, both(P(pz=-0.14, py=-0.05, p_pitch=12, p_yaw=14, s1_p=6, s2_p=8, s2_y=10, s3_p=4, s3_y=14,
                       nk_p=10, hd_p=-34, hd_y=-12, jaw=22, ft_f_L=0.05),
                     ear_fold=-30, curl=18, spread=14)
               | dict(sh_fwd_R=60, sh_out_R=70, el_R=95, wr_fwd_R=-30, sh_tw_R=10, cl_up_R=14, cl_fwd_R=-6,
                      sh_fwd_L=22, sh_out_L=16, el_L=40, ft_pitch_L=0, k_out_L=0.15)),
         (0.50, both(P(pz=-0.16, py=0.20, p_pitch=24, p_yaw=-16, s1_p=10, s2_p=14, s2_y=-14, s3_p=10, s3_y=-18,
                       nk_p=12, hd_p=-40, hd_y=8, jaw=34, ft_f_L=0.45, ft_pitch_L=0),
                     ear_fold=-34, curl=10, spread=18)
               | dict(sh_fwd_R=86, sh_out_R=8, el_R=12, wr_fwd_R=8, sh_tw_R=-20, cl_up_R=4, cl_fwd_R=22,
                      sh_fwd_L=14, sh_out_L=28, el_L=40, ft_pitch_L=0)),
         (0.66, both(P(pz=-0.18, py=0.22, p_pitch=26, p_yaw=-22, s1_p=10, s2_p=14, s2_y=-16, s3_p=12, s3_y=-20,
                       nk_p=12, hd_p=-38, hd_y=10, jaw=20, ft_f_L=0.45),
                     ear_fold=-30, curl=44, spread=4)
               | dict(sh_fwd_R=20, sh_out_R=-18, el_R=30, wr_fwd_R=24, sh_tw_R=-26, cl_fwd_R=16,
                      sh_fwd_L=-6, sh_out_L=24, el_L=36)),
         (0.95, both(P(pz=-0.12, py=0.12, p_pitch=16, p_yaw=-8, s2_p=10, s2_y=-6, s3_p=10, s3_y=-8,
                       nk_p=14, hd_p=-34, jaw=10, ft_f_L=0.40),
                     ear_fold=-16, curl=30) | dict(sh_fwd_R=8, el_R=22, sh_fwd_L=4, sh_out_L=10)),
         (1.15, P(ft_f_L=0.12, pz=-0.06, ft_z_L=0.0)),
         (1.4, P())]
    def step(p, t):
        # left foot leaves the ground between 0.30 and 0.48 s (lands on its forefoot), and again on the recovery step
        a = bump(t, 0.30, 0.50); b = bump(t, 0.98, 1.22)
        p["ft_z_L"] += 0.10 * a + 0.07 * b
        p["ft_pitch_L"] += -14 * a - 10 * b
        p["toe_L"] = max(0.0, -p["ft_pitch_L"]) * 0.6 + 6 * (a + b)
    return timeline(42, k, step, hz=(4.2, 3.6, 3.0, 2.6))

def hit_clip():
    # 0.8 s: the impact snaps the chest back and twists it, head whips, arms fling out, knees give; damped recovery.
    k = [(0.0, P()),
         (0.08, both(P(pz=-0.07, py=-0.06, p_pitch=-4, s2_p=-6, s3_p=-12, s3_y=-14, s3_r=6, nk_p=-4, hd_p=-12,
                       hd_y=-16, hd_r=10, jaw=24), sh_out=24, sh_fwd=-8, el=8, curl=4, spread=16,
                  ear_fold=-34)),
         (0.22, both(P(pz=-0.10, py=-0.03, p_pitch=12, s2_p=12, s3_p=14, s3_y=6, s3_r=-3, nk_p=18, hd_p=-36,
                       hd_y=8, hd_r=-4, jaw=12), sh_out=10, sh_fwd=16, el=36, curl=38, ear_fold=-20)),
         (0.42, both(P(pz=-0.07, p_pitch=8, s2_p=9, s3_p=11, s3_y=-2, nk_p=15, hd_p=-31, jaw=6), sh_out=5,
                     sh_fwd=6, el=18, curl=26, ear_fold=-8)),
         (0.8, P())]
    def stagger(p, t):
        a = bump(t, 0.04, 0.34)
        p["ft_z_R"] += 0.06 * a; p["ft_f_R"] -= 0.14 * smooth(t / 0.3) * (1 - smooth((t - 0.3) / 0.4))
        p["ft_pitch_R"] -= 10 * a
    return timeline(24, k, stagger, hz=(5.0, 4.0, 3.2, 2.8))

def death_clip():
    # 2.5 s: hit jolt -> knees buckle to a kneel -> topples forward onto the chest, head turned, arms limp.
    kneel = dict(pz=-0.54, py=0.33, p_pitch=14, s1_p=8, s2_p=12, s3_p=12, nk_p=18, hd_p=-20, jaw=18)
    k = [(0.0, P()),
         (0.15, both(P(pz=-0.06, py=-0.05, p_pitch=-6, s2_p=-8, s3_p=-12, nk_p=-6, hd_p=-8, hd_y=-10, jaw=30),
                     sh_out=26, el=10, spread=18, curl=6, ear_fold=-40)),
         (0.55, both(P(pz=-0.30, py=0.12, p_pitch=4, s2_p=4, s3_p=8, nk_p=12, hd_p=-16, hd_y=-6, jaw=24,
                       ft_pitch_L=-20, ft_pitch_R=-24), sh_out=14, sh_fwd=12, el=24, curl=20, ear_fold=-40,
                     k_out=0.3)),
         (1.0, both(P(**kneel, ft_pitch_L=-62, ft_pitch_R=-66, ft_f_L=-0.05, ft_f_R=-0.08), sh_out=10, sh_fwd=10,
                    el=30, curl=30, ear_fold=-36, k_out=0.3, toe=0)),
         (1.25, both(P(pz=-0.58, py=0.40, p_pitch=30, s1_p=10, s2_p=14, s3_p=14, nk_p=16, hd_p=-12, jaw=16,
                       ft_pitch_L=-66, ft_pitch_R=-70, ft_f_L=-0.10, ft_f_R=-0.12), sh_out=12, sh_fwd=28, el=24,
                     curl=26, ear_fold=-36, k_out=0.3)),
         (1.65, both(P(pz=-0.78, py=0.62, p_pitch=72, s1_p=6, s2_p=8, s3_p=4, nk_p=-4, hd_p=-10, hd_y=60, hd_r=8,
                       jaw=20, ft_pitch_L=-86, ft_pitch_R=-90, ft_f_L=-0.36, ft_f_R=-0.40, ft_z_L=0.05,
                       ft_z_R=0.05), sh_out=26, sh_fwd=52, el=34, curl=34, ear_fold=-30, k_out=0.25)),
         (1.85, both(P(pz=-0.91, py=0.70, p_pitch=84, s1_p=0, s2_p=-2, s3_p=-6, nk_p=-34, hd_p=-4, hd_y=72, hd_r=10,
                       jaw=24, ft_pitch_L=-100, ft_pitch_R=-104, ft_f_L=-0.60, ft_f_R=-0.63, ft_z_L=0.08,
                       ft_z_R=0.08), sh_out=40, sh_fwd=0, el=24, curl=24, ear_fold=-26, k_out=0.25)),
         (2.5, both(P(pz=-0.92, py=0.71, p_pitch=86, s1_p=0, s2_p=-2, s3_p=-7, nk_p=-36, hd_p=-4, hd_y=74, hd_r=10,
                      jaw=26, ft_pitch_L=-102, ft_pitch_R=-106, ft_f_L=-0.61, ft_f_R=-0.64, ft_z_L=0.08,
                      ft_z_R=0.08), sh_out=42, sh_fwd=-6, el=18, curl=16, spread=8, ear_fold=-24, k_out=0.25))]
    def twitch(p, t):
        v = bump(t, 1.9, 2.35)
        p["curl_L"] += 14 * math.sin(TAU * 6 * t) * v
        p["hd_p"] += 1.5 * math.sin(TAU * 5 * t) * v
    return timeline(75, k, twitch, hz=(3.4, 2.8, 2.4, 2.0))

def tail_of(M, n):
    return M[n] @ Vector((0.0, BONES[n].length, 0.0))

def floor_guard(rows):
    """Lying / kneeling poses: swing the arms back and lift the head until hands, claws and skull clear the floor."""
    for p in rows:
        for _ in range(60):
            M, _r = solve(p)
            ok = True
            for s in ("L", "R"):
                low = min(tail_of(M, f"{f}_03_{s}").z for f in FINGERS)
                if low < 0.02 or M["hand_" + s].translation.z < 0.06:
                    p["sh_fwd_" + s] -= 2.0; p["sh_out_" + s] += 0.6; ok = False
            if M["head"].translation.z < 0.19 or tail_of(M, "head").z < 0.19:
                p["nk_p"] -= 2.0; p["hd_p"] -= 0.5; ok = False
            if min(M["shin_L"].translation.z, M["shin_R"].translation.z) < 0.07:
                p["pz"] += 0.01; ok = False                                   # knees rest on, not in, the floor
            if ok: break
    return rows

CLIPS = {
    "Idle":   lambda: (idle_rows(120, STAND), True),
    "Listen": lambda: (idle_rows(108, LISTEN_BASE, listen=True), True),
    "Sneak":  lambda: (locomotion(SNEAK, 33), True),
    "Walk":   lambda: (locomotion(WALK, 24), True),
    "Run":    lambda: (locomotion(RUN, 14), True),
    "Alert":  lambda: (alert_clip(), False),
    "Call":   lambda: (call_clip(), False),
    "Attack": lambda: (attack_clip(), False),
    "Hit":    lambda: (hit_clip(), False),
    "Death":  lambda: (floor_guard(death_clip()), False),
}

# ---------------------------------------------------------------------------------------------------------- writing
def write_action(name, rows, loop):
    if loop: rows = rows + [rows[0]]                    # exact period: last key == first key
    act = bpy.data.actions.get(name)
    if act: bpy.data.actions.remove(act)
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    rig.animation_data_create()
    rig.animation_data.action = act
    for pb in rig.pose.bones: pb.rotation_mode = 'QUATERNION'
    data = {n: ([], []) for n in ORDER}
    prev = {}
    worst = 0.0
    for p in rows:
        M, reach = solve(p)
        worst = max([worst] + list(reach.values()))
        for n, B in to_basis(M).items():
            loc, q, _sc = B.decompose()
            if n in prev and q.dot(prev[n]) < 0: q.negate()
            prev[n] = q
            data[n][0].append(loc); data[n][1].append(q)
    for n in ORDER:
        if n == "root": continue
        locs, quats = data[n]
        chans = [("rotation_quaternion", i, [q[i] for q in quats]) for i in range(4)]
        if n == "pelvis" or max(l.length for l in locs) > 1e-4:
            chans += [("location", i, [l[i] for l in locs]) for i in range(3)]
        for path, idx, vals in chans:
            fc = act.fcurve_ensure_for_datablock(rig, f'pose.bones["{n}"].{path}', index=idx, group_name=n)
            fc.keyframe_points.add(len(vals))
            co = []
            for f, v in enumerate(vals): co += [float(f), float(v)]
            fc.keyframe_points.foreach_set("co", co)
            fc.keyframe_points.foreach_set("interpolation", [1] * len(vals))   # LINEAR: dense keys, no overshoot
            fc.update()
    act.use_frame_range = True
    act.frame_start, act.frame_end = 0, len(rows) - 1
    act.use_cyclic = loop
    return act, worst, len(rows) - 1

def check_feet(name, rows, g):
    """Planted-foot slip vs the ground (m/s in game) and lowest toe/heel, from the solved skeleton."""
    pass

if STAGE == "anim":
    for a in [a for a in bpy.data.actions if a.name.startswith("ECHO_")]:
        bpy.data.actions.remove(a)
    which = EXTRA or list(CLIPS)
    for name in which:
        rows, loop = CLIPS[name]()
        act, worst, n = write_action(name, rows, loop)
        log(f"{name:7s} frames 0..{n} ({n / FPS:.2f}s) loop={loop} max leg reach {worst:.3f}")
    rig.animation_data.action = bpy.data.actions["Idle"]
    bpy.context.scene.render.fps = FPS
    path = os.path.join(OUT, "Mimic_anim.blend")
    bpy.ops.wm.save_as_mainfile(filepath=path, copy=False)
    log("saved", path)

# ---------------------------------------------------------------------------------- verification: slip + contact sheets
if STAGE == "check":
    import numpy as np
    for name, g in (("Sneak", SNEAK), ("Walk", WALK), ("Run", RUN)):
        act = bpy.data.actions[name]
        rig.animation_data.action = act
        n = int(act.frame_end)
        sc = bpy.context.scene
        pos = {s: [] for s in "LR"}
        for f in range(n + 1):
            sc.frame_set(f)
            for s in "LR":
                ball = rig.matrix_world @ rig.pose.bones["toe_" + s].head
                heel = rig.matrix_world @ (rig.pose.bones["foot_" + s].matrix @ (REST["foot_" + s].inverted() @ Vector((REST["foot_" + s].translation.x, HEEL_Y, 0.0))))
                tip = rig.matrix_world @ rig.pose.bones["toe_" + s].tail
                pos[s].append((ball.copy(), heel.copy(), tip.copy()))
        worst_slip, where, low = 0.0, None, 9.0
        for s in "LR":
            for f in range(n):
                for j in range(3):
                    a, b = pos[s][f][j], pos[s][f + 1][j]
                    low = min(low, a.z)
                    if a.z < 0.004 and b.z < 0.004:     # point on the ground in both frames -> should move at -v
                        vy = (b.y - a.y) * FPS           # +Y = backwards
                        e = max(abs(vy - g.v), abs(b.x - a.x) * FPS)
                        if e > worst_slip: worst_slip, where = e, (s, f, ("ball", "heel", "tip")[j], round(vy, 2))
        log(f"{name}: speed {g.v} m/s, worst planted-point slip {worst_slip:.3f} m/s at {where}, lowest foot point {low:.3f} m")

if STAGE == "render":
    import numpy as np
    sc = bpy.context.scene
    for o in bpy.data.objects:
        if o.type == 'MESH' and o.parent != rig: o.hide_render = True
        if o.type == 'LIGHT': o.hide_render = True
    try: sc.render.engine = 'BLENDER_WORKBENCH'
    except TypeError as e: log(e)
    sc.display.shading.light = 'STUDIO'; sc.display.shading.color_type = 'MATERIAL'
    sc.display.shading.show_shadows = False
    sc.render.resolution_x, sc.render.resolution_y = 320, 400
    sc.render.film_transparent = False
    bpy.ops.mesh.primitive_plane_add(size=6, location=(0, 0, 0))
    floor = bpy.context.active_object
    fm = bpy.data.materials.new("Floor"); fm.diffuse_color = (0.18, 0.2, 0.24, 1); floor.data.materials.append(fm)
    # grid lines every 0.25 m along Y so foot slip is visible
    for i in range(-12, 13):
        bpy.ops.mesh.primitive_cube_add(size=1, location=(0, i * 0.25, 0.0005))
        c = bpy.context.active_object; c.scale = (6, 0.008, 0.001); c.data.materials.append(fm if i % 4 else fm)
    cam_data = bpy.data.cameras.new("Cam"); cam_data.type = 'ORTHO'; cam_data.ortho_scale = 2.7
    cam = bpy.data.objects.new("Cam", cam_data); sc.collection.objects.link(cam); sc.camera = cam
    views = {"side": ((4.0, 0.0, 1.05), (math.radians(90), 0, math.radians(90))),
             "front": ((0.0, -4.0, 1.05), (math.radians(90), 0, 0)),
             "q34": ((2.6, -2.9, 1.5), (math.radians(78), 0, math.radians(42)))}
    which = [c for c in EXTRA if c in CLIPS] or list(CLIPS)
    vlist = [v for v in EXTRA if v in views] or ["side", "front"]
    for name in which:
        act = bpy.data.actions[name]; rig.animation_data.action = act
        n = int(act.frame_end)
        samples = 8
        fr = [round(i * n / (samples - (0 if act.use_cyclic else 1))) for i in range(samples)]
        sheet_rows = []
        for v in vlist:
            loc, rot = views[v]
            cam.location = loc; cam.rotation_euler = rot
            if name == "Death" and v == "side": cam_data.ortho_scale = 3.2; cam.location = (4.0, 0.3, 0.9)
            else: cam_data.ortho_scale = 2.7
            tiles = []
            for f in fr:
                sc.frame_set(f)
                tmp = os.path.join(OUT, "_tile.png"); sc.render.filepath = tmp
                bpy.ops.render.render(write_still=True)
                img = bpy.data.images.load(tmp, check_existing=False)
                a = np.array(img.pixels[:], dtype=np.float32).reshape(img.size[1], img.size[0], 4)
                bpy.data.images.remove(img)
                tiles.append(a)
            sheet_rows.append(np.concatenate(tiles, axis=1))
        sheet = np.concatenate(sheet_rows[::-1], axis=0)
        h, w = sheet.shape[:2]
        im = bpy.data.images.new(f"sheet_{name}", w, h, alpha=True)
        im.pixels.foreach_set(sheet.ravel())
        im.filepath_raw = os.path.join(OUT, "Previews", f"{name}.png"); im.file_format = 'PNG'
        os.makedirs(os.path.dirname(im.filepath_raw), exist_ok=True)
        im.save()
        log("sheet", im.filepath_raw, "frames", fr)

if STAGE == "export":
    rig.name = "Mimic_Rig"
    keep = {"ECHO_Torso", "ECHO_Head", "ECHO_Arm_L", "ECHO_Arm_R", "ECHO_Hand_L", "ECHO_Hand_R", "ECHO_Leg_L",
            "ECHO_Leg_R", "ECHO_Eye_L", "ECHO_Eye_R", "ECHO_Teeth_Upper", "ECHO_Teeth_Lower"}
    meshes = [o for o in rig.children if o.type == 'MESH' and o.name in keep]
    for o in meshes: o.name = "Mimic_" + o.name[len("ECHO_"):]
    ren = {"ECHO_Skin": "Mimic_Skin", "ECHO_MouthInterior": "Mimic_Mouth", "ECHO_Eye": "Mimic_Eye",
           "ECHO_Keratin": "Mimic_Teeth"}
    for old, new in ren.items():
        if old in bpy.data.materials: bpy.data.materials[old].name = new
    for a in [a for a in bpy.data.actions if a.name not in CLIPS]: bpy.data.actions.remove(a)
    rig.animation_data.action = bpy.data.actions["Idle"]
    rig.data.pose_position = 'POSE'
    bpy.context.scene.render.fps = FPS
    for o in bpy.context.view_layer.objects: o.select_set(False)
    for o in [rig] + meshes: o.hide_set(False); o.select_set(True)
    bpy.context.view_layer.objects.active = rig
    fbx = os.path.join(OUT, "Mimic.fbx")
    bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, object_types={'ARMATURE', 'MESH'},
                             use_mesh_modifiers=True, add_leaf_bones=False, use_armature_deform_only=False,
                             bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
                             bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0,
                             mesh_smooth_type='FACE', use_tspace=True, path_mode='AUTO')
    log("exported", fbx, [o.name for o in meshes], sorted(a.name for a in bpy.data.actions))

"""Listener locomotion v3: foot-locked Sneak / Walk / Run cycles that match the in-game NavMeshAgent speeds.

Why: the v2 clips stepped far slower than the agent moves (walk feet 0.27 m/s vs 2.2 m/s), so the body slid;
the walk had a different stride per leg; and cyclic actions ended one frame early, so every loop popped.

Each gait's planted-foot travel is computed from the target speed:
    stride S (Blender units) = speed (m/s) * UNIT * duty * frames / FPS
so a foot on the ground moves backwards exactly as fast as the agent moves forwards in Unity.

Uses the rig solver / helpers of the source scripts (read-only):
    D:/Blender Projects/echo-creature/Listener/scripts/listener_anim_lib.py, listener_actions.py, listener_actions_more.py
Run on the export work file (never on the source .blend), then run bake_listener_v2.py "export":
    blender.exe -b <Exports>/Listener_textured.blend --python listener_locomotion.py -- <scripts_dir> [save]
Without "save" it only builds + reports (dry run).
"""
import bpy, math, os, sys

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
SCRIPTS = argv[0] if argv else r"D:\Blender Projects\echo-creature\Listener\scripts"
SAVE = "save" in argv

ns = {"__name__": "listener_anim"}
for fn in ("listener_anim_lib.py", "listener_actions.py", "listener_actions_more.py"):
    exec(open(os.path.join(SCRIPTS, fn)).read(), ns)
globals().update({k: ns[k] for k in ("TAU", "clamp", "pchip", "pbell", "pnoise", "wave", "foot_cycle",
                                     "with_base", "BASE", "build_action", "set_rotation_modes", "ARM")})

FPS = 24
MODEL_SCALE = 0.28                 # AgentAssetBuilder Listener.ModelScale
UNIT = 1.0 / MODEL_SCALE           # Blender units per metre in game


def stride(speed, duty, frames):
    return speed * UNIT * duty * frames / FPS


def legs(u, S, duty, lift, pk, zk, center=0.0, width=0.0, arc=0.06, toe_curl=10.0):
    """Both legs, same stride (any difference makes one foot slide). Left contacts at u=0, right at u=0.5."""
    c = {}
    for s, ph in (("L", 0.0), ("R", 0.5)):
        phi = (u + ph) % 1.0
        y, z, p = foot_cycle(phi, S, duty, lift, pk, zk)
        sw = clamp((phi - duty) / (1 - duty))
        c["ft_y_" + s] = y + center
        c["ft_z_" + s] = z
        c["ft_pitch_" + s] = p
        c["ft_x_" + s] = width + arc * math.sin(math.pi * sw)
        c["toe_" + s] = toe_curl * math.sin(math.pi * sw) ** 2
    return c


def stabilise_head(c, yaw=0.85, roll=0.7, pitch=0.6):
    """Predators keep the head steady while the body rocks: the neck/head cancel most of the trunk's sway."""
    tz = c.get("pel_rz", 0) + c.get("sp_rz", 0) + c.get("ch_rz", 0)
    ty = c.get("pel_ry", 0) + c.get("sp_ry", 0) + c.get("ch_ry", 0)
    tx = c.get("pel_rx_osc", 0) + c.get("ch_rx_osc", 0)
    c["nk_rz"] = c.get("nk_rz", 0) - 0.4 * yaw * tz
    c["hd_rz"] = c.get("hd_rz", 0) - 0.6 * yaw * tz
    c["nk_ry"] = c.get("nk_ry", 0) - 0.4 * roll * ty
    c["hd_ry"] = c.get("hd_ry", 0) - 0.6 * roll * ty
    c["hd_rx"] = c.get("hd_rx", 0) - pitch * tx
    c.pop("pel_rx_osc", None); c.pop("ch_rx_osc", None)
    return c


def arms(u, swing, lag_fa, fa_amp, ha_amp, cu_amp, out_amp=3.0):
    """Arms swing against the legs (left arm forward when the right foot is forward, u=0.5); forearm, hand and
    claws trail behind (overlap) so the limbs read as heavy instead of stiff."""
    c = {}
    for s, ph in (("L", 0.5), ("R", 0.0)):
        c["ua_fwd_" + s] = swing * math.cos(TAU * (u - ph - 0.04))
        c["fa_flex_" + s] = fa_amp * math.cos(TAU * (u - ph - 0.04 - lag_fa))
        c["ha_flex_" + s] = ha_amp * math.cos(TAU * (u - ph - 0.04 - 2 * lag_fa))
        c["cu_" + s] = cu_amp * math.cos(TAU * (u - ph - 0.04 - 3 * lag_fa))
        c["cl_fwd_" + s] = 0.25 * swing * math.cos(TAU * (u - ph - 0.02))
        c["ua_out_" + s] = out_amp * (1 + math.cos(2 * TAU * (u - ph)))
    return c


# ------------------------------------------------------------------ SNEAK (creep): low, slow, toe-first, arms raised
SNEAK_SPEED, SNEAK_L, SNEAK_DUTY = 1.2, 28, 0.68
SNEAK_BASE = with_base({"pel_z": -0.95, "pel_y": -0.15, "pel_rx": 16, "sp_rx": 10, "ch_rx": 12, "nk_rx": -30,
                        "hd_rx": -12, "ua_fwd": 22, "ua_out": 10, "fa_flex": 42, "ha_flex": -10, "cu": -12,
                        "cl_fwd": 8, "cl_up": 6, "jaw": 4, "pk_x": 0.35})

def sneak(f, L=SNEAK_L):
    u = f / L
    duty = SNEAK_DUTY
    S = stride(SNEAK_SPEED, duty, L)
    # toe touches first and rolls down (pitch > 0 pivots on the ball), long careful lift
    pk = [(0.0, 10), (0.10, 0), (0.48, 0), (0.60, 14), (0.68, 34), (0.76, 28), (0.88, 18), (0.96, 12), (1.0, 10)]
    zk = [(0.0, 0), (0.66, 0), (0.68, 0.03), (0.76, 0.75), (0.84, 1.0), (0.93, 0.45), (0.985, 0.04), (1.0, 0)]
    c = legs(u, S, duty, 0.7, pk, zk, center=0.25, width=0.08, arc=0.1, toe_curl=14)
    m = duty / 2
    c.update({
        "pel_z": 0.07 * math.cos(2 * TAU * (u - m)),
        "pel_x": -0.13 * math.cos(TAU * (u - m)),
        "pel_ry": 3.0 * math.cos(TAU * (u - m)),
        "pel_rz": 6.0 * math.cos(TAU * u),
        "sp_rz": -2.0 * math.cos(TAU * (u - 0.03)),
        "ch_rz": -5.0 * math.cos(TAU * (u - 0.06)),
        "ch_ry": -2.0 * math.cos(TAU * (u - m - 0.05)),
        "pel_rx_osc": 1.2 * math.cos(2 * TAU * (u - m)),
        # slow scanning head on top of the stabilisation (one sweep per cycle loops cleanly)
        "hd_rz": 9 * math.sin(TAU * (u + 0.1)) + pnoise(u, 41, 2),
        "hd_ry": 3 * math.sin(TAU * (u + 0.35)),
        "jaw": 1.5 * wave(u, 2, 0.2),
    })
    c.update(arms(u, 6.0, 0.06, 6.0, 6.0, 8.0, out_amp=1.5))
    return with_base(stabilise_head(c), SNEAK_BASE)


# ------------------------------------------------------------------ WALK (stalk): heel-toe, heavy, hunched
WALK_SPEED, WALK_L, WALK_DUTY = 2.2, 20, 0.6
WALK2_BASE = with_base({"pel_z": -0.62, "pel_y": -0.1, "pel_rx": 10, "sp_rx": 6, "ch_rx": 8, "nk_rx": -18,
                        "hd_rx": -6, "ua_fwd": -4, "fa_flex": 14, "cu": 4, "jaw": 3, "pk_x": 0.2})

def walk(f, L=WALK_L):
    u = f / L
    duty = WALK_DUTY
    S = stride(WALK_SPEED, duty, L)
    # heel strike -> flat -> heel peel -> toe-off -> toes clear -> reach
    pk = [(0.0, -14), (0.07, 0), (0.36, 0), (0.48, 10), (0.60, 40), (0.68, 30), (0.80, 6), (0.92, -12), (1.0, -14)]
    zk = [(0.0, 0), (0.58, 0), (0.60, 0.03), (0.68, 0.7), (0.78, 1.0), (0.89, 0.45), (0.97, 0.05), (1.0, 0)]
    c = legs(u, S, duty, 0.52, pk, zk, center=0.2, width=0.02, arc=0.07)
    m = duty / 2
    c.update({
        "pel_z": 0.12 * math.cos(2 * TAU * (u - m)),          # highest over the planted foot
        "pel_x": -0.12 * math.cos(TAU * (u - m)),             # weight shifts over the stance leg
        "pel_ry": 4.0 * math.cos(TAU * (u - m)),              # swing-side hip drops
        "pel_rz": 8.0 * math.cos(TAU * u),                    # hip reaches with the leading leg
        "sp_rz": -3.0 * math.cos(TAU * (u - 0.02)),
        "ch_rz": -10.0 * math.cos(TAU * (u - 0.05)),          # shoulders counter-rotate
        "sp_ry": -1.5 * math.cos(TAU * (u - m - 0.03)),
        "ch_ry": -2.5 * math.cos(TAU * (u - m - 0.06)),
        "pel_rx_osc": 2.0 * math.cos(2 * TAU * (u - 0.05)),    # loading response nod
        "ch_rx_osc": 1.2 * math.cos(2 * TAU * (u - 0.12)),
        "hd_rz": pnoise(u, 7, 2.5), "hd_ry": pnoise(u, 8, 1.5),
        "jaw": 2 * wave(u, 2, 0.1),
    })
    c["pel_rx"] = c["pel_rx_osc"]; c["ch_rx"] = c["ch_rx_osc"]
    c.update(arms(u, 16.0, 0.07, 10.0, 8.0, 8.0))
    return with_base(stabilise_head(c), WALK2_BASE)


# ------------------------------------------------------------------ RUN (sprint): ball strike, flight, clawing arms
RUN_SPEED, RUN_L, RUN_DUTY = 6.0, 15, 0.32
RUN2_BASE = with_base({"pel_z": -0.7, "pel_y": 0.05, "pel_rx": 22, "sp_rx": 12, "ch_rx": 8, "nk_rx": -36,
                       "hd_rx": -14, "ua_fwd": -10, "ua_out": 8, "fa_flex": 55, "ha_flex": -6, "cu": 10, "jaw": 10,
                       "pk_x": 0.4})

def run(f, L=RUN_L):
    u = f / L
    duty = RUN_DUTY
    S = stride(RUN_SPEED, duty, L)
    # lands on the ball, heel kisses the floor, explosive toe-off, heel kicks up behind in swing
    pk = [(0.0, 8), (0.08, 0), (0.18, 4), (0.32, 48), (0.45, 75), (0.62, 55), (0.80, 14), (0.93, 6), (1.0, 8)]
    zk = [(0.0, 0), (0.30, 0), (0.32, 0.04), (0.42, 0.6), (0.55, 1.0), (0.70, 0.8), (0.85, 0.3), (0.96, 0.03), (1.0, 0)]
    c = legs(u, S, duty, 1.25, pk, zk, center=0.45, width=-0.04, arc=0.08, toe_curl=18)
    m = duty / 2
    c.update({
        "pel_z": -0.16 * math.cos(2 * TAU * (u - m)) - 0.04,   # compresses mid-stance, rises in flight
        "pel_y": 0.06 * math.cos(2 * TAU * (u - m - 0.1)),
        "pel_x": -0.06 * math.cos(TAU * (u - m)),
        "pel_ry": 4.0 * math.cos(TAU * (u - m)),
        "pel_rz": 10.0 * math.cos(TAU * u),
        "sp_rz": -5.0 * math.cos(TAU * (u - 0.03)),
        "ch_rz": -13.0 * math.cos(TAU * (u - 0.06)),
        "ch_ry": -3.0 * math.cos(TAU * (u - m - 0.05)),
        "pel_rx_osc": 3.0 * math.cos(2 * TAU * (u - m)),
        "ch_rx_osc": 2.5 * math.cos(2 * TAU * (u - m - 0.06)),
        "hd_rz": pnoise(u, 11, 1.5), "hd_ry": pnoise(u, 12, 1.5),
        "jaw": 5 * math.cos(2 * TAU * (u - m - 0.1)),
    })
    c["pel_rx"] = c["pel_rx_osc"]; c["ch_rx"] = c["ch_rx_osc"]
    c.update(arms(u, 34.0, 0.08, 18.0, 14.0, 14.0, out_amp=4.0))
    return with_base(stabilise_head(c, yaw=0.8, roll=0.6, pitch=0.7), RUN2_BASE)


GAITS = [
    # action, frames, fn, speed (m/s), duty
    ("Listener_Sneak_Creep", SNEAK_L, sneak, SNEAK_SPEED, SNEAK_DUTY),
    ("Listener_Walk_Stalk", WALK_L, walk, WALK_SPEED, WALK_DUTY),
    ("Listener_Run_Sprint", RUN_L, run, RUN_SPEED, RUN_DUTY),
]


def build():
    meshes = [o for o in bpy.data.objects if o.type == 'MESH' and o.parent == ARM]
    saved = [(m, m.show_viewport) for o in meshes for m in o.modifiers]
    set_rotation_modes()
    if ARM.animation_data:
        ARM.animation_data.action = None
    acts = []
    for name, L, fn, _, _ in GAITS:
        act = build_action(name, L, fn, cyclic=True, step=1)
        # The last key (frame L+1) equals the first: include it so the exported clip is exactly one period long.
        # (Ending at frame L made Unity skip one frame on every loop.)
        act.frame_end = L + 1
        acts.append(act)
    for m, v in saved:
        m.show_viewport = v
    return acts


# ------------------------------------------------------------------ verification
def measure(act, L, speed, duty):
    """Planted-foot slip (m/s, game scale), ankle IK error (m) and leg extension, from the evaluated deform bones."""
    ad = ARM.animation_data or ARM.animation_data_create()
    ad.action = act
    if act.slots:
        ad.action_slot = act.slots[0]
    ARM.data.pose_position = 'POSE'          # the bake work file is saved in Rest Position
    sc = bpy.context.scene
    mw = ARM.matrix_world
    P = ARM.pose.bones
    leg_len = {s: ARM.data.bones["Thigh." + s].length + ARM.data.bones["Shin." + s].length for s in "LR"}
    ball, err, ext = {s: [] for s in "LR"}, 0.0, 0.0
    for fr in range(1, L + 2):
        sc.frame_set(fr)
        for s in "LR":
            ball[s].append(mw @ P["Foot." + s].tail)
            err = max(err, (P["Foot." + s].head - P["IK_Foot." + s].head).length)
            ext = max(ext, (P["Shin." + s].tail - P["Thigh." + s].head).length / leg_len[s])
    v_bu = speed * UNIT / FPS
    slip, low, high = 0.0, 1e9, -1e9
    for s, ph in (("L", 0.0), ("R", 0.5)):
        for i in range(L):
            phi = (i / L + ph) % 1.0
            if 0.12 < phi < duty - 0.04:        # foot flat on the floor
                d = ball[s][i + 1] - ball[s][i]
                slip = max(slip, math.hypot(d.y - v_bu, d.x) * FPS * MODEL_SCALE)
                low, high = min(low, ball[s][i].z), max(high, ball[s][i].z)
    loop = max((ball[s][0] - ball[s][L]).length for s in "LR") * MODEL_SCALE
    print(f"[loco] {act.name:24s} {L:3d}f {L / FPS:.2f}s  speed {speed} m/s  stride {stride(speed, duty, L) * MODEL_SCALE:.2f} m"
          f"  max slip {slip:.3f} m/s  ball height var {(high - low) * MODEL_SCALE * 100:.1f} cm"
          f"  ankle IK err {err * MODEL_SCALE * 100:.1f} cm  leg ext {ext:.2f}  loop gap {loop * 100:.2f} cm", flush=True)
    return slip, err, ext


acts = build()
ok = True
for act, (name, L, fn, speed, duty) in zip(acts, GAITS):
    slip, err, ext = measure(act, L, speed, duty)
    ok &= slip < 0.15 and err * MODEL_SCALE < 0.02 and ext < 0.995
idle = bpy.data.actions.get("Listener_Idle_Breathe")
ARM.animation_data.action = idle
if idle and idle.slots:
    ARM.animation_data.action_slot = idle.slots[0]
ARM.data.pose_position = 'REST'
print("[loco] all within limits" if ok else "[loco] LIMITS EXCEEDED", flush=True)
if SAVE:
    bpy.ops.wm.save_mainfile()
    print("[loco] saved", bpy.data.filepath, flush=True)

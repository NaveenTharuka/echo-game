"""Bake the Listener's procedural skin + high-poly detail onto UV-unwrapped low-poly meshes, then export an FBX.
Run:  blender.exe -b Untitled.blend --python bake_listener.py -- <out_dir> <res>
Never saves over the source file: writes ECHO_stage8_textured.blend next to it.
"""
import bpy, os, sys, time, math

argv = sys.argv[sys.argv.index("--") + 1:]
OUT = argv[0]; RES = int(argv[1]); STAGE = argv[2]
os.makedirs(os.path.join(OUT, "Textures"), exist_ok=True)
SRC_DIR = os.path.dirname(bpy.data.filepath)
STAGE8 = os.path.join(SRC_DIR, "ECHO_stage8_textured.blend")
t0 = time.time()
def log(*a): print(f"[bake {time.time()-t0:6.1f}s]", *a, flush=True)

scene = bpy.context.scene
PIECES = ["ECHO_Torso", "ECHO_Head", "ECHO_Arm_L", "ECHO_Arm_R", "ECHO_Hand_L", "ECHO_Hand_R", "ECHO_Leg_L", "ECHO_Leg_R"]
LP = [bpy.data.objects[n] for n in PIECES]
HP = [bpy.data.objects[n + "_HP"] for n in PIECES]
rig = bpy.data.objects["ECHO_Rig"]

# ---- render device: GPU if possible
scene.render.engine = 'CYCLES'
try:
    prefs = bpy.context.preferences.addons['cycles'].preferences
    for dev_type in ('OPTIX', 'CUDA', 'HIP', 'ONEAPI'):
        try:
            prefs.compute_device_type = dev_type
            prefs.get_devices()
            gpus = [d for d in prefs.devices if d.type == dev_type and 'NVIDIA' in d.name]
            if gpus:
                for d in prefs.devices: d.use = False
                gpus[0].use = True
                scene.cycles.device = 'GPU'
                log("GPU", dev_type, [d.name for d in gpus]); break
        except TypeError:
            continue
except Exception as e:
    log("GPU setup failed, CPU:", e)
scene.cycles.samples = 1
scene.cycles.use_denoising = False

# ---- the high-poly collection is usually excluded from the view layer while working: include it for the bake
def include(layer_coll, name):
    for c in layer_coll.children:
        if c.name == name:
            c.exclude = False; c.hide_viewport = False; c.collection.hide_render = False; c.collection.hide_viewport = False
            return True
        if include(c, name): return True
    return False
log("include ECHO_HighPoly:", include(bpy.context.view_layer.layer_collection, "ECHO_HighPoly"))
for o in HP: o.hide_set(False); o.hide_viewport = False

# ---- rest pose so low-poly and high-poly line up
rig.data.pose_position = 'REST'
if rig.animation_data: rig.animation_data.action = None
bpy.context.view_layer.update()

# ---- 1. UVs (prep stage only): all skin pieces packed into one atlas, then save stage8 and stop
if STAGE == "prep":
  for o in bpy.context.view_layer.objects: o.select_set(False)
  for o in LP: o.hide_set(False); o.select_set(True)
  bpy.context.view_layer.objects.active = LP[0]
  bpy.ops.object.mode_set(mode='EDIT')
  bpy.ops.mesh.select_all(action='SELECT')
  bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.004)
  bpy.ops.uv.pack_islands(margin=0.006, rotate=True)
  bpy.ops.object.mode_set(mode='OBJECT')
  log("UVs:", [(o.name, len(o.data.uv_layers)) for o in LP])
  bpy.ops.wm.save_as_mainfile(filepath=STAGE8, copy=True)
  log("saved", STAGE8); sys.exit(0)

# ---- images
def image(name, color, non_color):
    img = bpy.data.images.new(name, RES, RES, alpha=False, float_buffer=False)
    img.filepath_raw = os.path.join(OUT, "Textures", name + ".png"); img.file_format = 'PNG'
    img.generated_color = color
    if non_color: img.colorspace_settings.name = 'Non-Color'
    return img
imgs = {
    "BaseColor": image("Listener_BaseColor", (0, 0, 0, 1), False),
    "Normal": image("Listener_Normal", (0.5, 0.5, 1, 1), True),
    "Roughness": image("Listener_Roughness", (0.6, 0.6, 0.6, 1), True),
}

# ---- material helpers: an (unconnected) active image node is the bake target; emission carries a channel
MATS = [bpy.data.materials["ECHO_Skin"], bpy.data.materials["ECHO_MouthInterior"]]
state = {}
for m in MATS:
    nt = m.node_tree
    out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output)
    bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
    tex = nt.nodes.new('ShaderNodeTexImage'); tex.name = "BAKE_TARGET"
    emit = nt.nodes.new('ShaderNodeEmission'); emit.name = "BAKE_EMIT"
    state[m.name] = dict(nt=nt, out=out, bsdf=bsdf, tex=tex, emit=emit,
                         surface=out.inputs['Surface'].links[0].from_socket if out.inputs['Surface'].is_linked else None)

def target(img):
    for s in state.values():
        s["tex"].image = img
        s["nt"].nodes.active = s["tex"]

def emit_channel(socket_name):
    for s in state.values():
        nt, emit, inp = s["nt"], s["emit"], s["bsdf"].inputs[socket_name]
        for l in list(emit.inputs['Color'].links): nt.links.remove(l)
        if inp.is_linked:
            nt.links.new(inp.links[0].from_socket, emit.inputs['Color'])
        else:
            v = inp.default_value
            emit.inputs['Color'].default_value = (v, v, v, 1) if isinstance(v, float) else tuple(v)
        nt.links.new(emit.outputs['Emission'], s["out"].inputs['Surface'])

def restore_surface():
    for s in state.values():
        if s["surface"] is not None: s["nt"].links.new(s["surface"], s["out"].inputs['Surface'])

# ---- bake settings: high-poly -> low-poly
bake = scene.render.bake
bake.use_selected_to_active = True
bake.use_cage = False
bake.cage_extrusion = 0.02
bake.max_ray_distance = 0.08
bake.margin = 6
bake.use_clear = False
bake.target = 'IMAGE_TEXTURES'
bake.normal_space = 'TANGENT'
bake.normal_r, bake.normal_g, bake.normal_b = 'POS_X', 'POS_Y', 'POS_Z'   # OpenGL (Unity)

def bake_all(kind, img, **kw):
    target(img)
    everything = list(bpy.context.view_layer.objects)
    was_hidden = {o.name: o.hide_render for o in everything}
    for lp, hp in zip(LP, HP):
        # Only this pair is renderable: Cycles then syncs ~1 sculpt instead of 8 (memory).
        for o in everything: o.hide_render = o not in (lp, hp)
        for o in bpy.context.view_layer.objects: o.select_set(False)
        hp.hide_set(False); hp.hide_render = False
        hp.select_set(True); lp.select_set(True)
        bpy.context.view_layer.objects.active = lp
        bpy.ops.object.bake(type=kind, **kw)
        log("  ", kind, lp.name)
    for o in everything: o.hide_render = was_hidden[o.name]
    log("baked", kind, "->", img.name)

if STAGE == "normal":
    restore_surface(); bake_all('NORMAL', imgs["Normal"]); imgs["Normal"].save(); log("saved Normal"); sys.exit(0)
if STAGE == "color":
    emit_channel('Base Color'); bake_all('EMIT', imgs["BaseColor"]); imgs["BaseColor"].save(); log("saved BaseColor"); sys.exit(0)
if STAGE == "rough":
    emit_channel('Roughness'); bake_all('EMIT', imgs["Roughness"]); imgs["Roughness"].save(); log("saved Roughness"); sys.exit(0)

# ---- clean the bake helpers out of the materials, keep the low-poly UVs
for s in state.values():
    s["nt"].nodes.remove(s["emit"]); s["nt"].nodes.remove(s["tex"])

# ---- export FBX: same objects / names / clips as the original ECHO_Creature.fbx
rig.data.pose_position = 'POSE'
root = bpy.data.objects["ECHO_Creature"]
export = [root, rig] + [o for o in rig.children if o.type == 'MESH']
for o in bpy.context.view_layer.objects: o.select_set(False)
for o in export: o.hide_set(False); o.select_set(True)
bpy.context.view_layer.objects.active = rig
fbx = os.path.join(OUT, "ECHO_Creature_Textured.fbx")
bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, object_types={'EMPTY', 'ARMATURE', 'MESH'},
                         add_leaf_bones=False, bake_anim=True, bake_anim_use_all_actions=True,
                         bake_anim_use_nla_strips=False, bake_anim_force_startend_keying=True,
                         mesh_smooth_type='OFF', path_mode='AUTO')
log("exported", fbx, [o.name for o in export])

log("export done")

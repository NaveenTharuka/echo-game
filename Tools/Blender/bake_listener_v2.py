"""Listener v2 (echo-creature/Listener/echo_creature.blend): bake the procedural skin into one texture atlas, export an FBX.
Run each stage in its own Blender process (the PC has ~7 GB RAM):
  blender.exe -b <echo_creature.blend>           --python bake_listener_v2.py -- <out_dir> 2048 prep
  blender.exe -b <out_dir>/Listener_textured.blend --python bake_listener_v2.py -- <out_dir> 2048 normal|color|rough
  blender.exe -b <out_dir>/Listener_textured.blend --python bake_listener_v2.py -- <out_dir> 2048 export
Never saves over the source file: "prep" writes <out_dir>/Listener_textured.blend.
"""
import bpy, os, sys, time, math

argv = sys.argv[sys.argv.index("--") + 1:]
OUT = argv[0]; RES = int(argv[1]); STAGE = argv[2]
os.makedirs(os.path.join(OUT, "Textures"), exist_ok=True)
WORK = os.path.join(OUT, "Listener_textured.blend")
t0 = time.time()
def log(*a): print(f"[bake {time.time()-t0:6.1f}s]", *a, flush=True)

scene = bpy.context.scene
rig = bpy.data.objects.get("Listener | Pose controls") or bpy.data.objects["Listener_Rig"]
# Everything skinned to the rig: body sculpt, refined head, talons, foot claws. The ARCHIVE meshes are not parented.
MESHES = [o for o in rig.children if o.type == 'MESH']
log("meshes:", [o.name for o in MESHES])

rig.data.pose_position = 'REST'
bpy.context.view_layer.update()

# ---- 1. prep: one shared UV atlas for every mesh (the body's own UV map is overlapping / out of 0..1)
if STAGE == "prep":
    for o in MESHES:
        for uv in list(o.data.uv_layers): o.data.uv_layers.remove(uv)
        o.data.uv_layers.new(name="UVMap")
    for o in bpy.context.view_layer.objects: o.select_set(False)
    for o in MESHES: o.hide_set(False); o.select_set(True)
    bpy.context.view_layer.objects.active = MESHES[0]
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.003)
    bpy.ops.uv.pack_islands(margin=0.004, rotate=True)
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.ops.wm.save_as_mainfile(filepath=WORK, copy=True)
    log("saved", WORK); sys.exit(0)

# ---- render device: one NVIDIA GPU (enabling every device crashed here before)
scene.render.engine = 'CYCLES'
try:
    prefs = bpy.context.preferences.addons['cycles'].preferences
    for dev_type in ('OPTIX', 'CUDA'):
        try:
            prefs.compute_device_type = dev_type
            prefs.get_devices()
            gpus = [d for d in prefs.devices if d.type == dev_type and 'NVIDIA' in d.name]
            if gpus:
                for d in prefs.devices: d.use = False
                gpus[0].use = True
                scene.cycles.device = 'GPU'
                log("GPU", dev_type, gpus[0].name); break
        except TypeError:
            continue
except Exception as e:
    log("GPU setup failed, CPU:", e)
scene.cycles.samples = 4
scene.cycles.use_denoising = False

MATS = sorted({s.material for o in MESHES for s in o.material_slots if s.material}, key=lambda m: m.name)

# ---- 2. bakes: an (unconnected) active image node in every material is the target; emission carries a channel
if STAGE in ("normal", "color", "rough"):
    def image(name, color, non_color):
        img = bpy.data.images.new(name, RES, RES, alpha=False, float_buffer=False)
        img.filepath_raw = os.path.join(OUT, "Textures", name + ".png"); img.file_format = 'PNG'
        img.generated_color = color
        if non_color: img.colorspace_settings.name = 'Non-Color'
        return img
    img = {"normal": lambda: image("Listener_Normal", (0.5, 0.5, 1, 1), True),
           "color": lambda: image("Listener_BaseColor", (0, 0, 0, 1), False),
           "rough": lambda: image("Listener_Roughness", (0.6, 0.6, 0.6, 1), True)}[STAGE]()

    state = []
    for m in MATS:
        nt = m.node_tree
        out = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output)
        bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
        tex = nt.nodes.new('ShaderNodeTexImage'); tex.image = img; nt.nodes.active = tex
        state.append((m, nt, out, bsdf))

    def emit_channel(socket_name):
        for m, nt, out, bsdf in state:
            emit = nt.nodes.new('ShaderNodeEmission')
            inp = bsdf.inputs[socket_name]
            if inp.is_linked:
                nt.links.new(inp.links[0].from_socket, emit.inputs['Color'])
            else:
                v = inp.default_value
                emit.inputs['Color'].default_value = (v, v, v, 1) if isinstance(v, float) else tuple(v)
            nt.links.new(emit.outputs['Emission'], out.inputs['Surface'])

    bake = scene.render.bake
    bake.use_selected_to_active = False
    bake.margin = 8
    bake.use_clear = True
    bake.target = 'IMAGE_TEXTURES'
    bake.normal_space = 'TANGENT'
    bake.normal_r, bake.normal_g, bake.normal_b = 'POS_X', 'POS_Y', 'POS_Z'   # OpenGL (Unity)

    for o in bpy.context.view_layer.objects:
        o.select_set(False)
        o.hide_render = o not in MESHES
    for o in MESHES: o.hide_set(False); o.select_set(True)
    bpy.context.view_layer.objects.active = MESHES[0]

    if STAGE == "normal":
        bpy.ops.object.bake(type='NORMAL')
    else:
        emit_channel('Base Color' if STAGE == "color" else 'Roughness')
        bpy.ops.object.bake(type='EMIT')
    img.save()
    log("saved", img.filepath_raw); sys.exit(0)

# ---- 3. export: clean names, one material, deform bones only, every action baked (IK constraints -> FK keys)
if STAGE == "export":
    skin = bpy.data.materials.new("Listener_Skin")
    for o in MESHES:
        o.data.materials.clear()
        o.data.materials.append(skin)
        o.name = "Listener_" + o.name.replace("Listener | ", "").replace(" ", "_")
    rig.name = "Listener_Rig"
    for a in bpy.data.actions:
        if a.name.startswith("Listener_"): a.name = a.name[len("Listener_"):]   # "Walk_Stalk"
    # The armature only needs one action assigned for the exporter to find compatible ones.
    rig.data.pose_position = 'POSE'
    for o in bpy.context.view_layer.objects: o.select_set(False)
    for o in [rig] + MESHES: o.hide_set(False); o.select_set(True)
    bpy.context.view_layer.objects.active = rig
    fbx = os.path.join(OUT, "Listener.fbx")
    bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, object_types={'ARMATURE', 'MESH'},
                             use_mesh_modifiers=True, use_armature_deform_only=True, add_leaf_bones=False,
                             bake_anim=True, bake_anim_use_all_actions=True, bake_anim_use_nla_strips=False,
                             bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0,   # exact keys: simplifying lets planted feet drift
                             mesh_smooth_type='FACE', use_tspace=True, path_mode='AUTO')
    log("exported", fbx, [o.name for o in MESHES], [a.name for a in bpy.data.actions])

"""Upstream UniMate's reconstruction of generated motion onto the ORIGINAL rigged model -> C# fixtures.

Runs inside Blender (upstream's animate_motion path is a Blender pipeline):

  blender -b --factory-startup --python dev/tools/upstream_prep/reconstruct_upstream.py -- <fixture_dir> <tag>=<model path> ...

For each sample_<tag>.npz written by make_sample_fixtures.py (which saved the transforms_local matrices of
upstream's recover_unimate_anim_from_rot + scale_anim, exactly what animate_motion builds), this loads the
original model with upstream's load_character, then runs upstream's drive path unmodified -
sync_armature_bones('merge'), compute_bone_keyframes (with the T-pose global rotations), and the keyed pose values
rebuild_action_from_data would write - and evaluates the world transform of every kept bone on every frame.
"""
import os
import sys
import types

sys.path[:0] = [r"D:\99-scratch\unimate", r"D:\99-scratch\Motion"]


class _Log:
    def __getattr__(self, _):
        return lambda *a, **k: None


sys.modules["loguru"] = types.SimpleNamespace(logger=_Log())
sys.modules["tqdm"] = types.SimpleNamespace(tqdm=lambda x, **k: x)
_plot = types.ModuleType("data_process.utils.plotting")
for _n in ("save_skeleton_motion", "save_skeleton_motion_ground", "save_skeleton_motion_spectral",
           "save_skeleton_tpose_ground", "save_skeleton_tpose"):
    setattr(_plot, _n, lambda *a, **k: None)
sys.modules["data_process.utils.plotting"] = _plot

import bpy  # noqa: E402
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from fbx_ascii_import import import_fbx_ascii, is_ascii_fbx  # noqa: E402


def importer_for(path):
    """upstream's importers; ASCII FBX (which Blender refuses) through fbx_ascii_import"""
    if path.lower().endswith((".glb", ".gltf")):
        return be.import_gltf
    return import_fbx_ascii if is_ascii_fbx(path) else be.import_fbx
import json  # noqa: E402
import numpy as np  # noqa: E402
from data_process.utils import blender_export as be  # noqa: E402
from data_process.utils.blender_rig import compute_bone_keyframes, set_scene_timing, sync_armature_bones  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1:]
fix = args[0]
for spec in args[1:]:
    tag, path = spec.split("=", 1)
    z = np.load(os.path.join(fix, f"sample_{tag}.npz"))
    prep = json.load(open(os.path.join(fix, f"prep_{tag}.json")))
    names = [prep["raw"][i]["name"] for i in z["src_bone"]]        # BFS joint -> bone name
    # load the character the way prep fixtures did (upstream load_scene picks the main armature)
    armature, _ = be.load_scene(importer_for(path), path)
    anim_local, rest_local = z["anim_local_mat"], z["rest_local_mat"]
    sync_armature_bones(armature, names, extra_bones_strategy="merge")
    set_scene_timing(anim_local.shape[0], 30)
    keyframes = compute_bone_keyframes(rest_local, anim_local, names, z["tpos_global_rot"])
    # upstream's rebuild_action_from_data keys pose_bone.location / rotation_quaternion with these values; Blender 5
    # removed the Action.fcurves API it uses, so the same values are applied per frame (identical at whole frames)
    if armature.animation_data is not None:
        armature.animation_data.action = None
    # upstream keys location + rotation only; a pose scale left in the file (the snake's root carries 0.33,1,1)
    # would shear everything below it, so the reference is evaluated on an unscaled pose
    for pb in armature.pose.bones:
        pb.scale = (1.0, 1.0, 1.0)
    unit = armature.matrix_world.to_scale()[0]
    T, J = anim_local.shape[:2]
    pos = np.zeros((T, J, 3)); rot = np.zeros((T, J, 4))
    for f in range(T):
        for n, data in keyframes.items():
            pb = armature.pose.bones[n]
            pb.rotation_mode = "QUATERNION"
            # the sample script built the rest pose from world units; upstream's exporter stores armature-local
            # units (root divided by the armature scale), which is what pose locations are in
            pb.location = data["location"][f][1] / unit
            pb.rotation_quaternion = data["rotation"][f][1]
        bpy.context.view_layer.update()
        for j, n in enumerate(names):
            m = armature.matrix_world @ armature.pose.bones[n].matrix
            pos[f, j] = list(m.to_translation())
            q = m.to_quaternion(); rot[f, j] = [q.w, q.x, q.y, q.z]
    np.savez(os.path.join(fix, f"recon_{tag}.npz"), world_pos=pos, world_rot=rot)
    print(f"RECON {tag}: {J} bones x {T} frames")

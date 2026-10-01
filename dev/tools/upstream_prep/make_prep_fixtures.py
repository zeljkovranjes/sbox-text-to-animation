"""Upstream UniMate skeleton preprocessing on real FBX rigs -> JSON fixtures for the C# port.

Runs INSIDE Blender (upstream's exporter is a Blender pipeline):

  blender -b --factory-startup --python dev/tools/upstream_prep/make_prep_fixtures.py -- <out_dir> <rig.fbx|.glb> ...

ASCII and FBX 6 files (which Blender can't import) go through FBX2glTF first; upstream imports glTF the same way.

For each rig it records the RAW input the C# side also gets (every bone: name, parent, rest world transform,
skin weight sum) and then calls upstream's own code, unmodified:

  data_process.utils.blender_export.load_scene / prepare_skeleton   (secondary roots, arrays, Z-up -> Y-up)
  data_process.utils.blender_export.prune_skeleton_shared             (with the rest pose as the only clip:
                                                                       a rig to animate has no clips yet)
  data_process.joint_annotation.names_clean_rule                       (clean joint names)
  data_process.joint_annotation.face_select_rule                       (facing joint pair / body axis)
  data_process.utils.motion_features.process_tpose / build_topology_cond

Only two unrelated imports are stubbed (Blender's Python has neither): loguru (logging) and the matplotlib
plotting module (previews).
"""
import json
import os
import sys
import types

UPSTREAM = r"D:\99-scratch\unimate"
MOTION = r"D:\99-scratch\Motion"
sys.path[:0] = [UPSTREAM, MOTION]


class _Log:
    def __getattr__(self, _):
        return lambda *a, **k: None


sys.modules["loguru"] = types.SimpleNamespace(logger=_Log())
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
import numpy as np  # noqa: E402
from data_process.utils import blender_export as be  # noqa: E402
from data_process.joint_annotation.names_clean_rule import clean_joint_name, post_process  # noqa: E402
from data_process.joint_annotation.face_select_rule import resolve_face_joints  # noqa: E402
from data_process.utils.motion_features import process_tpose, build_topology_cond, DegenerateSkeletonError  # noqa: E402
from Animation import positions_global, rotations_global  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1:]
out_dir, fbx_paths = args[0], args[1:]
os.makedirs(out_dir, exist_ok=True)


def q_wxyz(m):
    q = m.to_quaternion()
    return [q.w, q.x, q.y, q.z]


for fbx in fbx_paths:
    tag = os.path.splitext(os.path.basename(fbx))[0].lower()
    try:
        armature, mesh = be.load_scene(importer_for(fbx), fbx)
    except (RuntimeError, AssertionError) as e:  # ASCII FBX (Blender refuses it), no armature
        print(f"SKIP {tag}: {str(e).splitlines()[0]}")
        continue

    # ---- raw input (before any upstream processing): what the engine/C# side sees
    bones = armature.data.bones
    weights = be.compute_bone_weight_sums(mesh, armature) if mesh is not None else {}
    # largest single vertex weight per bone: upstream's "skinned" test is skin_matrix[:, j].max() >= eps
    skin_max = {b.name: 0.0 for b in armature.data.bones}
    if mesh is not None:
        vg_to_bone = be.build_vgroup_to_bone_mapping(mesh, armature)
        for v in mesh.data.vertices:
            for g in v.groups:
                bname = vg_to_bone.get(g.group)
                if bname and g.weight > skin_max[bname]:
                    skin_max[bname] = float(g.weight)
    raw = []
    for b in bones:
        w = armature.matrix_world @ b.matrix_local
        raw.append(dict(name=b.name, parent=-1 if b.parent is None else bones.find(b.parent.name),
                        pos=list(w.to_translation()), rot=q_wxyz(w), weight=float(weights.get(b.name, 0.0)),
                        skin_max=skin_max[b.name]))

    # ---- upstream: secondary roots, arrays, Z-up -> Y-up, pruning with the rest pose as the clip
    skel = be.prepare_skeleton(armature, mesh)
    rest = skel["rest_anim_shared"]
    names = list(skel["bone_names"])
    anims, rest_p, names_p, skin_p = be.prune_skeleton_shared([rest.copy()], rest.copy(), names, skel["skin_matrix"].copy())
    rest_local_pos = np.array(rest_p.positions[0])
    rest_local_rot = np.array(rest_p.rotations.qs[0])
    parents_p = [int(p) for p in rest_p.parents]

    # ---- upstream: clean names, facing pair (on the pruned skeleton, export order)
    clean = [post_process(clean_joint_name(n, tag)) for n in names_p]
    face = resolve_face_joints(clean, names_p)

    # ---- upstream: T-pose canonicalization + topology conditioning
    tpos_data = dict(names=np.array(names_p), parents=np.array(parents_p), fps=np.array(30),
                     rest_local_pos=rest_local_pos, rest_local_rot=rest_local_rot)
    try:
        (tpos_anim, offsets, scale, ground, parents_bfs, names_bfs, fps, bfs_order,
         face_idxs, body_axis) = process_tpose(tpos_data, face_joints=face if face.get("source") != "empty" else None)
    except DegenerateSkeletonError as e:  # upstream skips such an object type; the port must refuse it too
        with open(os.path.join(out_dir, f"prep_{tag}.json"), "w") as f:
            json.dump(dict(rig=tag, raw=raw, pruned=dict(names=names_p, parents=parents_p), degenerate=str(e)), f)
        print(f"PREP {tag}: raw {len(raw)} bones -> {len(names_p)} joints; DEGENERATE ({e})")
        continue
    clean_bfs = [clean[i] for i in bfs_order]
    cond = build_topology_cond(tag, parents_bfs, offsets, names_bfs, clean_bfs,
                               positions_global(tpos_anim)[0], tpos_anim.rotations.qs[0],
                               rotations_global(tpos_anim).qs[0], face_joint_idxs=face_idxs,
                               body_axis=body_axis, scale_factor=scale)

    fixture = dict(
        rig=tag, raw=raw,
        pruned=dict(names=names_p, parents=parents_p, skinned=[bool(skin_p[:, j].max() >= be._SKINNING_WEIGHT_EPS) for j in range(len(names_p))]),
        clean_names=clean, face=face,
        bfs=dict(order=[int(i) for i in bfs_order], names=list(names_bfs), parents=[int(p) for p in parents_bfs],
                 clean_names=clean_bfs, face_idxs=[int(i) for i in face_idxs], body_axis=bool(body_axis),
                 scale=float(scale), tpos=np.asarray(cond["tpos_first_frame"]).tolist(), offsets=np.asarray(offsets).tolist(),
                 relations=np.asarray(cond["joint_relations"]).tolist(), graph_dists=np.asarray(cond["joint_graph_dists"]).tolist(),
                 depths=np.asarray(cond["joint_depths"]).tolist(), spectral=np.asarray(cond["spectral_feats"]).tolist()),
    )
    with open(os.path.join(out_dir, f"prep_{tag}.json"), "w") as f:
        json.dump(fixture, f)
    print(f"PREP {tag}: raw {len(raw)} bones -> {len(names_p)} joints; face {face.get('source')} "
          f"({face['r_hip']['raw']} / {face['l_hip']['raw']}){' body-axis' if body_axis else ''}")

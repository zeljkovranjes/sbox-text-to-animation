"""Skinned test rigs for skeleton families no downloaded model covers properly -> binary glTF.

Runs inside Blender:

  blender -b --factory-startup --python dev/tools/upstream_prep/make_test_rigs.py -- <out_dir>

(<out_dir>/<rig>.glb for the upstream fixtures, <out_dir>/fbx/<rig>.fbx for the editor gate)

  arachnid     cephalothorax, abdomen, fangs, pedipalps, eight 4-segment legs (41 bones + unskinned tips)
  bird         pelvis to beak, two 4-segment wings, legs with toes, tail
  insect       head, antennae, mandibles, 3-segment abdomen, six legs, four 2-segment wings
  robot_arm    an articulated industrial arm: base, turret, shoulder, elbow, wrist pitch/roll, two gripper fingers

Every bone owns a tube of the mesh at full weight (the way a rigid/segmented model is skinned); each limb chain
ends in an unskinned tip bone, as exported rigs usually do, which upstream's pruning removes. Names are plain
descriptive names - nothing the port special-cases (it special-cases nothing).
"""
import math
import os
import sys

import bpy
from mathutils import Vector

out_dir = sys.argv[sys.argv.index("--") + 1]
os.makedirs(os.path.join(out_dir, "fbx"), exist_ok=True)


def chain(bones, names, parent, points, tip=True):
    """bones along points[0] -> points[1] -> ...; an unskinned tip bone after the last when tip"""
    for i, n in enumerate(names):
        bones.append((n, parent, points[i], points[i + 1], True))
        parent = n
    if tip:
        a, b = Vector(points[-2]), Vector(points[-1])
        bones.append((names[-1] + "_end", parent, tuple(b), tuple(b + (b - a) * 0.3), False))
    return parent


def spider():
    b = []
    b.append(("cephalothorax", None, (0, 0, 0.30), (0, -0.25, 0.30), True))
    chain(b, ["abdomen", "spinneret"], "cephalothorax", [(0, 0.02, 0.30), (0, 0.45, 0.36), (0, 0.55, 0.33)])
    for side, s in (("L", 1), ("R", -1)):
        chain(b, [f"fang_{side}"], "cephalothorax", [(s * 0.04, -0.25, 0.28), (s * 0.05, -0.32, 0.20)])
        chain(b, [f"pedipalp_base_{side}", f"pedipalp_tip_{side}"], "cephalothorax",
              [(s * 0.07, -0.24, 0.30), (s * 0.12, -0.36, 0.33), (s * 0.13, -0.45, 0.25)])
        for i in range(4):
            y = -0.20 + 0.075 * i
            dy = (-0.10, -0.03, 0.03, 0.10)[i]
            base = Vector((s * 0.08, y, 0.30))
            pts = [base, base + Vector((s * 0.06, dy * 0.3, 0.0)), base + Vector((s * 0.22, dy, 0.14)),
                   base + Vector((s * 0.42, dy * 1.8, 0.06)), base + Vector((s * 0.55, dy * 2.4, -0.28))]
            chain(b, [f"leg{i + 1}_coxa_{side}", f"leg{i + 1}_femur_{side}", f"leg{i + 1}_tibia_{side}",
                      f"leg{i + 1}_tarsus_{side}"], "cephalothorax", [tuple(p) for p in pts])
    return b


def bird():
    b = []
    b.append(("pelvis", None, (0, 0.05, 0.45), (0, -0.05, 0.50), True))
    chain(b, ["spine", "chest", "neck_lower", "neck_upper", "head", "beak"], "pelvis",
          [(0, -0.05, 0.50), (0, -0.15, 0.55), (0, -0.25, 0.58), (0, -0.30, 0.70), (0, -0.32, 0.82),
           (0, -0.40, 0.86), (0, -0.52, 0.84)])
    chain(b, ["tail_base", "tail_feathers"], "pelvis", [(0, 0.05, 0.45), (0, 0.20, 0.48), (0, 0.40, 0.46)])
    for side, s in (("L", 1), ("R", -1)):
        chain(b, [f"wing_shoulder_{side}", f"wing_upper_{side}", f"wing_lower_{side}", f"wing_hand_{side}"], "chest",
              [(s * 0.05, -0.20, 0.60), (s * 0.12, -0.20, 0.62), (s * 0.40, -0.18, 0.64), (s * 0.70, -0.15, 0.62),
               (s * 1.00, -0.10, 0.60)])
        chain(b, [f"thigh_{side}", f"shin_{side}", f"tarsus_{side}", f"toe_{side}"], "pelvis",
              [(s * 0.08, 0.0, 0.45), (s * 0.09, -0.05, 0.30), (s * 0.09, 0.02, 0.15), (s * 0.09, 0.0, 0.02),
               (s * 0.09, -0.10, 0.0)])
    return b


def insect():
    b = []
    b.append(("thorax", None, (0, 0.05, 0.20), (0, -0.12, 0.21), True))
    chain(b, ["head"], "thorax", [(0, -0.12, 0.21), (0, -0.22, 0.20)])
    chain(b, ["abdomen_1", "abdomen_2", "abdomen_3"], "thorax",
          [(0, 0.05, 0.20), (0, 0.17, 0.19), (0, 0.29, 0.17), (0, 0.40, 0.14)])
    for side, s in (("L", 1), ("R", -1)):
        chain(b, [f"antenna_base_{side}", f"antenna_tip_{side}"], "head",
              [(s * 0.02, -0.20, 0.23), (s * 0.08, -0.32, 0.32), (s * 0.14, -0.45, 0.36)])
        chain(b, [f"mandible_{side}"], "head", [(s * 0.02, -0.22, 0.18), (s * 0.01, -0.27, 0.16)])
        for i, name in enumerate(("front", "middle", "hind")):
            y = -0.08 + 0.08 * i
            base = Vector((s * 0.04, y, 0.18))
            dy = (-0.08, 0.0, 0.10)[i]
            pts = [base, base + Vector((s * 0.12, dy * 0.5, 0.06)), base + Vector((s * 0.24, dy, -0.08)),
                   base + Vector((s * 0.30, dy * 1.5, -0.18))]
            chain(b, [f"{name}_femur_{side}", f"{name}_tibia_{side}", f"{name}_tarsus_{side}"], "thorax", [tuple(p) for p in pts])
        for i, name in enumerate(("forewing", "hindwing")):
            base = Vector((s * 0.03, -0.04 + 0.08 * i, 0.24))
            chain(b, [f"{name}_inner_{side}", f"{name}_outer_{side}"], "thorax",
                  [tuple(base), tuple(base + Vector((s * 0.20, 0.04, 0.03))), tuple(base + Vector((s * 0.42, 0.10, 0.02)))])
    return b


def robot_arm():
    b = []
    b.append(("base", None, (0, 0, 0), (0, 0, 0.15), True))
    chain(b, ["turret", "shoulder", "upper_arm", "elbow", "forearm", "wrist_pitch", "wrist_roll"], "base",
          [(0, 0, 0.15), (0, 0, 0.30), (0, 0.05, 0.40), (0, 0.05, 0.95), (0, 0.05, 1.05), (0, 0.55, 1.10),
           (0, 0.65, 1.10), (0, 0.72, 1.10)], tip=False)
    for side, s in (("L", 1), ("R", -1)):
        chain(b, [f"gripper_finger_{side}"], "wrist_roll", [(s * 0.04, 0.72, 1.10), (s * 0.04, 0.85, 1.10)])
    return b


def build(name, bones):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    arm_data = bpy.data.armatures.new(name + "_armature")
    arm = bpy.data.objects.new(name + "_armature", arm_data)
    bpy.context.scene.collection.objects.link(arm)
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    for n, parent, head, tail, _ in bones:
        eb = arm_data.edit_bones.new(n)
        eb.head, eb.tail = head, tail
        if parent:
            eb.parent = arm_data.edit_bones[parent]
    bpy.ops.object.mode_set(mode="OBJECT")

    verts, faces, owner = [], [], []
    for bi, (n, parent, head, tail, skinned) in enumerate(bones):
        if not skinned:
            continue
        h, t = Vector(head), Vector(tail)
        axis = (t - h)
        r = max(axis.length * 0.18, 0.012)
        side = axis.orthogonal().normalized() * r
        up = axis.normalized().cross(side).normalized() * r
        base = len(verts)
        rings, sides = 6, 12  # a tube per bone, dense like a real mesh
        for k in range(rings):
            p = h + axis * (k / (rings - 1))
            for a in range(sides):
                ang = 2 * math.pi * a / sides
                verts.append(p + side * math.cos(ang) + up * math.sin(ang))
                owner.append(n)
        for k in range(rings - 1):
            for a in range(sides):
                i0, i1 = base + k * sides + a, base + k * sides + (a + 1) % sides
                faces.append((i0, i1, i1 + sides, i0 + sides))
    me = bpy.data.meshes.new(name)
    me.from_pydata([tuple(v) for v in verts], [], [tuple(f) for f in faces])
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    ob.parent = arm
    groups = {}
    for i, n in enumerate(owner):
        if n not in groups:
            groups[n] = ob.vertex_groups.new(name=n)
        groups[n].add([i], 1.0, "REPLACE")
    ob.modifiers.new("Armature", "ARMATURE").object = arm
    path = os.path.join(out_dir, name + ".glb")
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_skins=True, export_animations=False)
    # the same rig as binary FBX, for the editor gate (s&box imports FBX; T2A_GATE_EXTRA takes a folder of them)
    bpy.ops.export_scene.fbx(filepath=os.path.join(out_dir, "fbx", name + ".fbx"), add_leaf_bones=False, bake_anim=False)
    print(f"RIG {name}: {len(bones)} bones ({sum(1 for x in bones if not x[4])} unskinned tips) -> {path}")


for name, make in (("arachnid", spider), ("bird", bird), ("insect", insect), ("robot_arm", robot_arm)):
    build(name, make())

"""Blender scene from an ASCII FBX (6.x or 7.x), for rigs Blender's own importer refuses.

Blender imports only binary FBX 7.1+, and converting through FBX2glTF drops skin weights on some rigs (the spider
loses all but a few), which changes what upstream's pruning keeps. This builds the scene upstream's exporter
expects - one armature with every bone at its bind-pose world transform, and every skinned mesh with one vertex
group per cluster - straight from the file, so upstream's load_scene / prepare_skeleton / pruning run unmodified:

  armature, mesh = be.load_scene(import_fbx_ascii, path)

Bone rest transforms come from the BindPose (the cluster TransformLink when a bone is missing from it), axes as
Blender's importer sets them up: the file's up axis to Blender's +Z, UnitScaleFactor to metres.
"""
import re

import bpy
import numpy as np
from mathutils import Matrix


def is_ascii_fbx(path):
    with open(path, "rb") as f:
        return f.read(5) == b"; FBX"


# ---- a minimal ASCII FBX tree: name, values, children
_TOKEN = re.compile(r'"(?:[^"\\]|\\.)*"|[{}]|[^\s,{}:]+:|[^\s,{}]+|,')


class Node:
    __slots__ = ("name", "values", "children")

    def __init__(self, name):
        self.name, self.values, self.children = name, [], []

    def find(self, name):
        return next((c for c in self.children if c.name == name), None)

    def all(self, name):
        return [c for c in self.children if c.name == name]

    def numbers(self):
        """Values as floats; FBX 7 wraps arrays in a child 'a:' node."""
        a = self.find("a")
        vals = (a or self).values
        return np.array([float(v) for v in vals if not (isinstance(v, str) and v.startswith("*"))], float)


def parse(text):
    text = "\n".join(line for line in text.splitlines() if not line.lstrip().startswith(";"))
    root, stack, current = Node(""), [], None
    stack.append(root)
    for tok in _TOKEN.findall(text):
        if tok == ",":
            continue
        if tok == "{":
            stack.append(current)
        elif tok == "}":
            stack.pop()
        elif tok.endswith(":") and not tok.startswith('"'):
            current = Node(tok[:-1])
            stack[-1].children.append(current)
        else:
            v = tok[1:-1] if tok.startswith('"') else tok
            current.values.append(v)
    return root


def _short(name):
    """'Model::Bone' (FBX 6) / 'Bone\\x00\\x01Model' style names -> 'Bone'."""
    return name.split("::", 1)[1] if "::" in name else name


def _matrix(vals):
    # FBX stores 4x4 matrices column-major (translation in 12..14)
    return np.asarray(vals, float).reshape(4, 4).T


def load(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        root = parse(f.read())
    objects = root.find("Objects")
    fbx7 = root.find("FBXHeaderExtension") is not None and any(c.name == "C" for c in (root.find("Connections") or Node("")).children)

    # ids: FBX 7 numeric ids; FBX 6 the "Class::Name" strings
    def key(node):
        return node.values[0]

    models = {key(m): m for m in objects.all("Model")}
    geoms = {key(g): g for g in objects.all("Geometry")}
    deformers = {key(d): d for d in objects.all("Deformer")}
    poses = objects.all("Pose")

    def mname(k):
        m = models[k]
        return _short(m.values[1] if fbx7 else m.values[0])

    def mtype(k):
        return models[k].values[-1]

    links = []  # (child, parent)
    for c in (root.find("Connections") or Node("")).children:
        if c.name in ("C", "Connect") and c.values and c.values[0] == "OO":
            links.append((c.values[1], c.values[2]))
    parent_of = {}
    for ch, pa in links:
        if ch in models and pa in models:
            parent_of[ch] = pa

    bones = [k for k in models if mtype(k) in ("LimbNode", "Limb", "Root")]
    bone_set = set(bones)

    # bind pose world matrices
    world = {}
    for pose in poses:
        for pn in pose.all("PoseNode"):
            node = pn.find("Node").values[0]
            world.setdefault(node, _matrix(pn.find("Matrix").numbers()))

    # skin: cluster -> bone, cluster -> skin -> geometry
    cluster_bone, cluster_skin, skin_geom, geom_model = {}, {}, {}, {}
    for ch, pa in links:
        if ch in models and pa in deformers:
            cluster_bone[pa] = ch
        elif ch in deformers and pa in deformers:
            cluster_skin[ch] = pa
        elif ch in deformers and pa in geoms:
            skin_geom[ch] = pa
        elif ch in deformers and pa in models:  # FBX 6: skin deformer on the mesh model
            skin_geom[ch] = pa
        elif ch in geoms and pa in models:
            geom_model[ch] = pa
    for cl, b in cluster_bone.items():
        if b not in world and deformers[cl].find("TransformLink") is not None:
            world[b] = _matrix(deformers[cl].find("TransformLink").numbers())

    # meshes: FBX 7 vertices on the Geometry; FBX 6 on the Mesh model itself
    meshes = {}  # mesh key -> (vertex count, {bone: [(index, weight)]})
    for k, m in list(models.items()) + list(geoms.items()):
        verts = m.find("Vertices")
        if verts is None or (k in models and mtype(k) != "Mesh"):
            continue
        meshes[k] = [len(verts.numbers()) // 3, {}]
    for cl, b in cluster_bone.items():
        owner = skin_geom.get(cluster_skin.get(cl))
        if owner not in meshes:
            continue
        d = deformers[cl]
        idx, w = d.find("Indexes"), d.find("Weights")
        if idx is None or w is None:
            continue
        meshes[owner][1].setdefault(b, []).extend(zip(idx.numbers().astype(int), w.numbers()))

    settings = root.find("GlobalSettings")
    up, unit = 1, 1.0
    if settings is not None:
        for p in settings.find("Properties70").children if settings.find("Properties70") else settings.find("Properties60").children:
            if p.values and p.values[0] == "UpAxis":
                up = int(float(p.values[-1]))
            if p.values and p.values[0] == "UnitScaleFactor":
                unit = float(p.values[-1])
    return dict(bones=bones, name={k: mname(k) for k in models}, parent=parent_of, world=world, meshes=meshes,
                up=up, unit=unit, bone_set=bone_set)


def import_fbx_ascii(path):
    """load_scene import function: builds the armature and skinned meshes of an ASCII FBX."""
    fbx = load(path)
    bones, name, world = fbx["bones"], fbx["name"], fbx["world"]
    missing = [name[b] for b in bones if b not in world]
    if not bones:
        raise RuntimeError(f"No armature in {path}")
    if missing:
        raise RuntimeError(f"No rest transform for {missing}")
    # file axes -> Blender (Z up), file units -> metres
    to_z = np.eye(4)
    if fbx["up"] == 1:  # Y up: +90 deg about X
        to_z[:3, :3] = [[1, 0, 0], [0, 0, -1], [0, 1, 0]]
    elif fbx["up"] == 0:
        to_z[:3, :3] = [[0, 0, 1], [1, 0, 0], [0, 1, 0]]
    scale = fbx["unit"] / 100.0

    arm_data = bpy.data.armatures.new("Armature")
    arm = bpy.data.objects.new("Armature", arm_data)
    bpy.context.scene.collection.objects.link(arm)
    arm.matrix_world = Matrix((to_z @ np.diag([scale, scale, scale, 1.0])).tolist())
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    heads = {b: world[b][:3, 3] for b in bones}
    edit = {}
    for b in bones:
        kids = [c for c in bones if fbx["parent"].get(c) == b]
        lengths = [np.linalg.norm(heads[c] - heads[b]) for c in kids]
        length = max([l for l in lengths if l > 1e-6], default=0.0)
        if length <= 1e-6:
            p = fbx["parent"].get(b)
            length = 0.5 * np.linalg.norm(heads[b] - heads[p]) if p in heads else 0.0
        eb = arm_data.edit_bones.new(name[b])
        eb.head, eb.tail = (0, 0, 0), (0, max(length, 1e-3), 0)
        m = world[b].copy()
        m[:3, :3] /= np.linalg.norm(m[:3, :3], axis=0)  # rotation only (bone scale is not part of a rest pose)
        eb.matrix = Matrix(m.tolist())
        edit[b] = eb
    for b in bones:
        p = fbx["parent"].get(b)
        while p is not None and p not in edit:  # a non-bone (Null) between bones: skip to the next bone up
            p = fbx["parent"].get(p)
        if p is not None:
            edit[b].parent = edit[p]
    bpy.ops.object.mode_set(mode="OBJECT")

    for k, (count, groups) in fbx["meshes"].items():
        me = bpy.data.meshes.new(name[k] if k in name else "Mesh")
        me.vertices.add(count)
        ob = bpy.data.objects.new(me.name, me)
        bpy.context.scene.collection.objects.link(ob)
        ob.parent = arm
        for b, entries in groups.items():
            vg = ob.vertex_groups.new(name=name[b])
            for i, w in entries:
                vg.add([int(i)], float(w), "REPLACE")
        ob.modifiers.new("Armature", "ARMATURE").object = arm

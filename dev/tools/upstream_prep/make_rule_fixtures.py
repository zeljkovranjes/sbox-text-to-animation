"""Upstream joint-name cleaning and facing-pair rules over every skeleton UniMate published -> C# fixture.

Input: UniML3D's export/objaverse/joint_names.json (the raw joint names of every Objaverse object type, after
upstream's export pruning) - thousands of real rigs from all body plans. Output: for each object, upstream's
rule-based clean names (names_clean_rule) and facing pair (face_select_rule), computed by upstream's own code.

Run: D:\\99-scratch\\unimate\\.venv\\Scripts\\python.exe make_rule_fixtures.py <joint_names.json> <out.json>
"""
import json
import sys

sys.path.insert(0, r"D:\99-scratch\unimate")
from data_process.joint_annotation.names_clean_rule import clean_joint_name, post_process  # noqa: E402
from data_process.joint_annotation.face_select_rule import resolve_face_joints  # noqa: E402

src, out = sys.argv[1], sys.argv[2]
data = json.load(open(src))
fixture = {}
for obj, raw in data.items():
    clean = [post_process(clean_joint_name(n, obj)) for n in raw]
    face = resolve_face_joints(clean, raw)
    r = raw.index(face["r_hip"]["raw"]) if face["r_hip"]["raw"] else -1
    l = raw.index(face["l_hip"]["raw"]) if face["l_hip"]["raw"] else -1
    fixture[obj] = dict(raw=raw, clean=clean, r=r, l=l, body_axis=bool(face.get("body_axis", False)), source=face["source"])
json.dump(fixture, open(out, "w"))
print(f"{len(fixture)} objects, {sum(len(v['raw']) for v in fixture.values())} joint names")

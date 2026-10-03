"""Expected output of UniMate's own clean-up (its Blender add-on's backend: timeline.finish_motion joins, then
collision.cleanup) for each <rig>_input.json written by UpstreamCleanupTests.MakeInput.

Run: D:\\99-scratch\\unimate\\.venv\\Scripts\\python.exe make_cleanup_fixtures.py <fixture dir>
"""
import glob
import json
import os
import sys

import numpy as np

B3D = r"D:\99-scratch\UniMate-B3D\backend"
sys.path.insert(0, B3D)
import timeline  # noqa: E402
import collision  # noqa: E402
import ground  # noqa: E402
from timeline import finish_motion, to_local  # noqa: E402
from collision import cleanup  # noqa: E402
from scipy.spatial.transform import Rotation  # noqa: E402

# The add-on composes rotation matrices without re-orthonormalising them; on a deep chain (a hand's fingers) the
# drift doubles with every conjugated update until the pose blows up (the s&box human's fingers diverge in 19
# iterations). The reference keeps its algorithm and only orthonormalises what its forward kinematics consumes.
_fk = timeline.forward_kinematics
def _exact_fk(root, local, skeleton):
    return _fk(root, Rotation.from_matrix(local.reshape(-1, 3, 3)).as_matrix().reshape(local.shape), skeleton)
collision.forward_kinematics = _exact_fk
ground.forward_kinematics = _exact_fk

FIX = sys.argv[1]
for path in sorted(glob.glob(os.path.join(FIX, "*_input.json"))):
    data = json.load(open(path))
    skeleton = {k: data[k] for k in ("parents", "heads", "labels", "bone_names", "rest_matrices", "collision_capsules", "foot_profiles")}
    positions = np.asarray(data["positions"], dtype=np.float64)
    rotations = np.asarray(data["rotations"], dtype=np.float64)
    clips = data["clips"]
    local = to_local(rotations, skeleton["parents"])
    root = positions[:, 0].copy()
    pos, rot = finish_motion(root, local, clips, skeleton, 12, 60)
    pos, rot, report = cleanup(pos, rot, skeleton, data["ground"])
    summary = {
        "initial": report.get("initial_collision_pass"),
        "ground": {k: v for k, v in report.get("ground_contact", {}).items() if k not in ("foot_limits",)},
        "final": {k: v for k, v in report.items() if k in ("max_penetration_before", "max_penetration_after", "pairs", "frames_with_remaining_contacts")},
    }
    out = path.replace("_input.json", "_expected.json")
    json.dump({"positions": pos.tolist(), "rotations": rot.tolist(), "report": json.dumps(summary, default=str)}, open(out, "w"))
    print(os.path.basename(out), json.dumps(summary, default=str)[:600])

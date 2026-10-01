"""End-to-end upstream UniMate sampling on the real rigs prepared by make_prep_fixtures.py -> C# fixtures.

For every prepared rig (prep_<rig>.json): conditioning from upstream's prepared skeleton, the released
uniml3d_f60_v2 checkpoint sampled with fixed noise (Euler, CFG 3), decoding with upstream's own recovery
functions (unimate.utils.motion_utils), and the motion mapped back onto the rig's original bones.

Run: D:\\99-scratch\\unimate\\.venv\\Scripts\\python.exe make_sample_fixtures.py <fixture_dir>
"""
import glob
import json
import os
import sys

sys.path.insert(0, r"D:\99-scratch\unimate-fixtures")
from unimate_ref import *  # noqa: E402,F401  (build_cond, euler_sample, to_engine, quaternion helpers, TextEncoder)
from Quaternions import Quaternions  # noqa: E402
from data_process.utils.skeleton import get_root_facing_quat  # noqa: E402
from unimate.configs.schema import MainConfig  # noqa: E402
from unimate.models.factory import create_model  # noqa: E402
from unimate.training.ema import EMAModel  # noqa: E402
from unimate.utils.motion_utils import hml_rotations_to_bvh_quaternions, recover_unimate_root_quat_and_pos  # noqa: E402
import types  # noqa: E402
sys.modules.setdefault("data_process.utils.plotting", types.SimpleNamespace(**{n: (lambda *a, **k: None) for n in (
    "save_skeleton_motion", "save_skeleton_motion_ground", "save_skeleton_motion_spectral", "save_skeleton_tpose_ground")}))
from data_process.utils.motion_features import process_tpose  # noqa: E402
from data_process.joint_annotation.names_clean_rule import clean_joint_name, post_process  # noqa: E402
from data_process.joint_annotation.face_select_rule import resolve_face_joints  # noqa: E402


def trim_to(names, parents, world, max_joints):
    """The port's one extension (independent re-implementation): over the model's joint limit, remove leaves
    shortest bone first, the last in order on ties."""
    names, parents, world = list(names), list(parents), np.array(world)
    trimmed = 0
    while len(names) > max_joints:
        kids = {p for p in parents if p >= 0}
        cand = [(np.linalg.norm(world[i] - world[parents[i]]), -i, i) for i in range(len(names)) if parents[i] >= 0 and i not in kids]
        i = min(cand)[2]
        keep = [k for k in range(len(names)) if k != i]
        remap = {o: n for n, o in enumerate(keep)}
        parents = [-1 if parents[k] < 0 else remap[parents[k]] for k in keep]
        names = [names[k] for k in keep]; world = world[keep]; trimmed += 1
    return names, parents, world, trimmed


def upstream_prepare(names, parents, world_pos, world_rot_wxyz, tag):
    """Upstream process_tpose on a skeleton given as Blender-world rest transforms (local Y-up arrays, as upstream's
    exporter writes them), with upstream's rule names and facing."""
    UPq = mat_to_q(UP[None])[0]
    pos = np.asarray(world_pos) @ UP.T
    rot = qmul(UPq[None], np.asarray(world_rot_wxyz))
    lp = pos.copy(); lr = rot.copy()
    for j, p in enumerate(parents):
        if p >= 0:
            lp[j] = qrot(qinv(rot[p])[None], (pos[j] - pos[p])[None])[0]
            lr[j] = qmul(qinv(rot[p])[None], rot[j][None])[0]
    clean = [post_process(clean_joint_name(n, tag)) for n in names]
    face = resolve_face_joints(clean, list(names))
    data = dict(names=np.array(names), parents=np.array(parents), fps=np.array(30), rest_local_pos=lp, rest_local_rot=lr)
    out = process_tpose(data, face_joints=face if face.get("source") != "empty" else None)
    return out, clean

FIX = sys.argv[1]
CACHE = r"P:\02-projects\unimate-cache"
EXP = "unimate_uniml3d_f60_v2"
UP = np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], float)  # Blender Z-up -> Y-up (upstream apply_zup_to_yup)
PROMPT = "An object walks forward."
STEPS, CFG = 8, 3.0

cfg = MainConfig.from_json(os.path.join(CACHE, EXP, "config.json"))
model = create_model(cfg.dataset, cfg.model)
sd = torch.load(os.path.join(CACHE, EXP, "checkpoints", "checkpoint_step_100000.pt"), map_location="cpu", weights_only=False)
model.load_state_dict(sd["model_state_dict"])
ema = EMAModel(parameters=model.parameters(), decay=0.9999, use_ema_warmup=True)
ema.load_state_dict(sd["ema_state_dict"]); ema.copy_to(model.parameters())
model.eval()
stats_all = np.load(os.path.join(CACHE, EXP, "dataset_stats.npy"), allow_pickle=True).item()
MJ, MD, T = cfg.dataset.max_joints, cfg.dataset.max_depth, cfg.dataset.max_motion_length
te = TextEncoder()

for path in sorted(glob.glob(os.path.join(FIX, "prep_*.json"))):
    fx = json.load(open(path))
    rig = fx["rig"]
    if "degenerate" in fx:
        continue
    raw = fx["raw"]
    idx = {b["name"]: i for i, b in enumerate(raw)}
    names_pruned = fx["pruned"]["names"]
    kept_raw = [idx[n] for n in names_pruned]                       # pruned (export) order -> raw bone
    if len(kept_raw) < cfg.dataset.min_joints:
        print(f"SKIP {rig}: {len(kept_raw)} joints (the model takes at least {cfg.dataset.min_joints})")
        continue
    # over the joint limit: the port's leaf trim, then upstream's own canonicalization of what remains
    names_t, parents_t, _, trimmed = trim_to(names_pruned, fx["pruned"]["parents"], [raw[i]["pos"] for i in kept_raw], MJ)
    kept_raw = [idx[n] for n in names_t]
    (tpos_anim, _, _, _, parents_bfs, names_bfs, _, order, face_idxs, body_axis), clean = upstream_prepare(
        names_t, parents_t, [raw[i]["pos"] for i in kept_raw], [raw[i]["rot"] for i in kept_raw], rig)
    if not trimmed:
        assert list(order) == fx["bfs"]["order"], rig           # same skeleton as the preparation fixture
    from Animation import positions_global
    bfs = dict(face_idxs=[int(i) for i in face_idxs], body_axis=bool(body_axis), clean_names=[clean[o] for o in order],
               tpos=positions_global(tpos_anim)[0].tolist())
    parents = np.array(parents_bfs)
    J = len(parents)
    src = [kept_raw[o] for o in order]                              # BFS joint -> raw bone
    world = np.array([raw[i]["pos"] for i in src], float)
    rest_rot = np.array([raw[i]["rot"] for i in src], float)        # wxyz, Blender world

    # canonical frame exactly as upstream's canonicalize_anim (facing quat, center, diameter, ground)
    p = world @ UP.T
    face = bfs["face_idxs"]
    q = get_root_facing_quat(p[None], face, body_axis=bfs["body_axis"]).qs[0]
    M = q_to_mat(q[None])[0] @ UP
    pc = world @ M.T
    scale = 2.0 / tree_diameter(parents, pc)
    origin = np.array([pc[0, 0], pc[:, 1].min(), pc[0, 2]])
    tpos = (pc - origin) * scale
    assert np.abs(tpos - np.array(bfs["tpos"])).max() < 1e-5, rig
    canon = dict(tpos=tpos, M=M, origin=origin, scale=scale)

    # conditioning: upstream clean names (T5, mean-pooled), the rig's own statistics family
    family = "mixamo" if rig.startswith("citizen") else "truebones"  # an input both sides share (stored below)
    uniq = sorted(set(bfs["clean_names"]))
    _, pooled, _ = te.encode([PROMPT] + uniq)
    emb = {n: pooled[1 + i] for i, n in enumerate(uniq)}
    joint_emb = np.stack([emb[n] for n in bfs["clean_names"]])
    cond, parts = build_cond(tpos, parents, joint_emb, pooled[0], stats_all[family], MJ, MD)

    noise = torch.randn((1, MJ, 12, T), generator=torch.Generator().manual_seed(1234))
    x = euler_sample(model, cond, noise, cfg=CFG, steps=STEPS)
    feat = x[0, :J].permute(2, 0, 1).numpy() * parts["std"][None] + parts["mean"][None]

    # upstream's own recovery: HML -> BVH rotations, integrated root trajectory
    bvh = hml_rotations_to_bvh_quaternions(feat[:, :, 3:9], parents).qs
    _, root_pos = recover_unimate_root_quat_and_pos(feat[:, 0])
    L, G, root = to_engine(bvh, root_pos, canon, rest_rot, parents)

    np.savez(os.path.join(FIX, f"sample_{rig}.npz"), noise=noise.numpy(), caption_emb=pooled[0], x_final=x.numpy(),
             features=feat, bvh_local_q=bvh, root_pos_canon=root_pos, engine_world_q=G, engine_root_pos=root,
             spectral=parts["spec"], src_bone=np.array(src), stats=np.array(family))
    np.savez(os.path.join(FIX, f"sample_{rig}_kept.npz"), names=np.array([raw[i]["name"] for i in src]))
    print(f"SAMPLE {rig}: {J} joints{f' ({trimmed} leaves trimmed)' if trimmed else ''}, stats {family}")

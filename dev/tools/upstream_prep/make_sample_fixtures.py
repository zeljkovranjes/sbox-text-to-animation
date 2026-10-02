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
from unimate.models.factory import create_model, create_transport  # noqa: E402
from unimate.models.flow.transport import Sampler  # noqa: E402
from unimate.inference.generate import ClassifierFreeSampleModel  # noqa: E402
from unimate.training.ema import EMAModel  # noqa: E402
from unimate.utils.motion_utils import hml_rotations_to_bvh_quaternions, recover_unimate_root_quat_and_pos  # noqa: E402
import types  # noqa: E402
sys.modules.setdefault("data_process.utils.plotting", types.SimpleNamespace(**{n: (lambda *a, **k: None) for n in (
    "save_skeleton_motion", "save_skeleton_motion_ground", "save_skeleton_motion_spectral", "save_skeleton_tpose_ground")}))
from data_process.utils.motion_features import process_tpose  # noqa: E402
import data_process.utils.skeleton as _skel  # noqa: E402
from collections import deque  # noqa: E402


def _bfs_with_tie_clusters(parents, offsets=None):
    """upstream bfs_reorder_joints with the port's tie convention: sibling bone lengths equal up to rounding
    (consecutive lengths <= 1e-6 relative apart) form one cluster, kept in file order. Upstream compares raw floats,
    so its order inside such ties is rounding noise; both sides of the comparison use this rule."""
    n = len(parents)
    length = np.linalg.norm(offsets, axis=-1) if offsets is not None else np.zeros(n)
    children = [[] for _ in range(n)]; root = None
    for j, p in enumerate(parents):
        if p == -1: root = j
        else: children[p].append(j)
    topo, q = [], deque([root])
    while q:
        u = q.popleft(); topo.append(u); q.extend(children[u])
    size = [1] * n
    for u in reversed(topo):
        for c in children[u]: size[u] += size[c]
    for u in range(n):
        by_len = sorted(children[u], key=lambda c: length[c])
        cl = {}
        for k, c in enumerate(by_len):
            prev = by_len[k - 1] if k else None
            cl[c] = cl[prev] if prev is not None and length[c] - length[prev] <= 1e-6 * length[c] else length[c]
        children[u] = sorted(children[u], key=lambda c: (-size[c], cl[c], children[u].index(c)))
    order, q = [], deque([root])
    while q:
        u = q.popleft(); order.append(u); q.extend(children[u])
    old2new = {o: i for i, o in enumerate(order)}
    return order, [-1 if parents[o] == -1 else old2new[parents[o]] for o in order]


_skel.bfs_reorder_joints = _bfs_with_tie_clusters
from data_process.joint_annotation.names_clean_rule import clean_joint_name, post_process  # noqa: E402
from data_process.joint_annotation.face_select_rule import resolve_face_joints  # noqa: E402
from data_process.utils.skeleton import scale_anim  # noqa: E402
from Animation import Animation  # noqa: E402
from unimate.dataset.transforms import extract_conditions, apply_normalization, apply_padding, build_parent_features  # noqa: E402
from unimate.dataset.mixture.collate import mixture_batch_collate  # noqa: E402
from unimate.utils.topology_utils import (compute_edge_relations_and_distances, compute_joint_depths,  # noqa: E402
                                          compute_laplacian_eigenvectors, compute_edge_indexs)
from unimate.utils.motion_utils import recover_unimate_joint_pos_from_rot, recover_unimate_anim_from_rot  # noqa: E402


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
GEN = Sampler(create_transport(training_config=cfg.training))
stats_all = np.load(os.path.join(CACHE, EXP, "dataset_stats.npy"), allow_pickle=True).item()
MJ, MD, T = cfg.dataset.max_joints, cfg.dataset.max_depth, cfg.dataset.max_motion_length
te = TextEncoder()

ONLY = [r for r in os.environ.get("T2A_RIGS", "").split(",") if r]  # restrict to these rigs (others keep their files)
for path in sorted(glob.glob(os.path.join(FIX, "prep_*.json"))):
    fx = json.load(open(path))
    tag = os.path.basename(path)[len("prep_"):-len(".json")]  # the fixture name (prep_<tag>.json)
    rig = fx["rig"]                                           # the object type upstream's name rule sees
    if ONLY and tag not in ONLY:
        continue
    if "degenerate" in fx:
        continue
    raw = fx["raw"]
    idx = {b["name"]: i for i, b in enumerate(raw)}
    names_pruned = fx["pruned"]["names"]
    kept_raw = [idx[n] for n in names_pruned]                       # pruned (export) order -> raw bone
    if len(kept_raw) < cfg.dataset.min_joints:
        print(f"SKIP {rig}: {len(kept_raw)} joints (the model takes at least {cfg.dataset.min_joints})")
        continue
    (tpos_anim, offsets_cond, scale_factor, _, parents_bfs, names_bfs, _, order, face_idxs, body_axis), clean = upstream_prepare(
        names_pruned, fx["pruned"]["parents"], [raw[i]["pos"] for i in kept_raw], [raw[i]["rot"] for i in kept_raw], rig)
    # (the joint ORDER may differ from the Blender run on exact ties: upstream breaks them by float rounding)
    from Animation import positions_global, rotations_global, offsets_from_positions, transforms_local
    parents = np.array(parents_bfs)
    J = len(parents)
    clean_bfs = [clean[o] for o in order]
    tpos = positions_global(tpos_anim)[0]
    src = [kept_raw[o] for o in order]                              # BFS joint -> raw bone
    world = np.array([raw[i]["pos"] for i in src], float)
    rest_rot = np.array([raw[i]["rot"] for i in src], float)        # wxyz, Blender world

    # the canonical frame as a source->canonical map, for mapping the result back (checked against upstream's T-pose)
    p = world @ UP.T
    q = get_root_facing_quat(p[None], [int(i) for i in face_idxs], body_axis=bool(body_axis)).qs[0]
    M = q_to_mat(q[None])[0] @ UP
    pc = world @ M.T
    scale = 2.0 / tree_diameter(parents, pc)
    origin = np.array([pc[0, 0], pc[:, 1].min(), pc[0, 2]])
    assert np.abs((pc - origin) * scale - tpos).max() < 1e-5, rig
    canon = dict(tpos=tpos, M=M, origin=origin, scale=scale)

    # ---- conditioning with upstream's own dataset functions (create_sample_condition's path)
    # stage fixtures: one fixed statistics set on both sides (Objaverse). "As shipped" fixtures (prep_<rig>_asshipped):
    # the statistics the editor picks for the rig (T2A_FAMILY) and upstream's own sampler (dopri5)
    as_shipped = tag.endswith("_asshipped")
    family = os.environ.get("T2A_FAMILY", "objaverse") if as_shipped else "objaverse"
    stats = stats_all[family]
    mj = max(MJ, J)                                                 # the network has no per-joint weights: pad to J
    for m in model.modules():
        if hasattr(m, "max_joints"): m.max_joints = mj
        if type(m).__name__ == "FinalLayer": m.joint = mj
    tpos12 = np.concatenate([tpos, np.zeros((J, 9))], -1)
    tpos12[:, 3:9] = Quaternions.id(1).rotation_matrix(cont6d=True)[0]
    conds = extract_conditions(np.zeros((T, J, 12)), tpos12, "tpos")
    mean = np.zeros((J, 12)); std = np.zeros((J, 12))
    mean[0], std[0] = stats["mean_root"], stats["std_root"]
    mean[1:], std[1:] = stats["mean_local"], stats["std_local"]
    tpos_n = apply_normalization(conds["tpos_first_frame"], mean, std)
    motion_n, _ = apply_padding(apply_normalization(conds["motion"], mean, std), T)
    rel, dist = compute_edge_relations_and_distances(parents, max_path_len=5)
    depth = compute_joint_depths(parents)
    spec, _ = compute_laplacian_eigenvectors(parents, max_freqs=8)
    uniq = sorted(set(clean_bfs))
    _, pooled, _ = te.encode([PROMPT] + uniq)
    emb = {n: pooled[1 + i] for i, n in enumerate(uniq)}
    joint_emb = np.stack([emb[n] for n in clean_bfs]).astype(np.float32)
    batch = dict(motion=motion_n, max_motion_length=T, motion_length=T, max_joints=mj, parents=parents,
                 edge_indexs=compute_edge_indexs(parents), tpos_first_frame=tpos_n,
                 tpos_first_frame_parents=build_parent_features(tpos_n, parents)["tpos_first_frame_parents"],
                 offsets=offsets_cond, joint_graph_dist=dist, joint_relations=rel, joint_depths=depth,
                 spectral_feats=spec, joint_names_emb=joint_emb, object_type=rig, start_idx=0, mean=mean, std=std,
                 split_tag="train", caption=PROMPT, caption_emb=pooled[0])
    _, cond = mixture_batch_collate([batch])

    noise = torch.randn((1, mj, 12, T), generator=torch.Generator().manual_seed(1234))
    if as_shipped:
        with torch.no_grad():
            x = GEN.sample_ode()(noise, ClassifierFreeSampleModel(model, cfg_scale=CFG), cond=cond)[-1]
    else:
        x = euler_sample(model, cond, noise, cfg=CFG, steps=STEPS)
    feat = x[0, :J].permute(2, 0, 1).numpy() * std[None] + mean[None]

    # ---- upstream's own recovery: BVH rotations + root (motion_utils), FK positions, rig-driving matrices
    bvh = hml_rotations_to_bvh_quaternions(feat[:, :, 3:9], parents).qs
    _, root_pos = recover_unimate_root_quat_and_pos(feat[:, 0])
    fk_pos = recover_unimate_joint_pos_from_rot(feat, parents, offsets_from_positions(tpos, parents))
    tpos_offsets = offsets_from_positions(tpos, parents)
    anim = scale_anim(recover_unimate_anim_from_rot(feat, parents, tpos_offsets), 1.0 / scale_factor)
    rest = Animation(Quaternions.id((1, J)), (tpos_offsets / scale_factor)[None], Quaternions.id(J), tpos_offsets / scale_factor, parents)
    L, G, root = to_engine(bvh, root_pos, canon, rest_rot, parents)

    np.savez(os.path.join(FIX, f"sample_{tag}.npz"), noise=noise.numpy()[:, :J], caption_emb=pooled[0], x_final=x.numpy()[:, :J],
             features=feat, bvh_local_q=bvh, root_pos_canon=root_pos, fk_pos=fk_pos, engine_world_q=G, engine_root_pos=root,
             spectral=spec, src_bone=np.array(src),
             cond_tpos=cond["tpos_first_frame"][0, :J].numpy(), cond_tpos_parents=cond["tpos_first_frame_parents"][0, :J].numpy(),
             cond_relations=cond["joint_relations"][0, :J, :J].numpy(), cond_graph_dist=cond["graph_dist"][0, :J, :J].numpy(),
             cond_depths=cond["joint_depths"][0, :J].numpy(), cond_spectral=cond["spectral_feats"][0, :J].numpy(),
             cond_name_emb=cond["joint_names_emb"][0, :J].numpy(),
             anim_local_mat=transforms_local(anim), rest_local_mat=transforms_local(rest)[0],
             tpos_global_rot=rotations_global(tpos_anim).qs[0])
    print(f"SAMPLE {tag}: {J} joints, stats {family}, {'dopri5' if as_shipped else f'euler {STEPS}'}")

"""UniMate's own text-to-motion on skeletons from its own datasets -> reference motions for the C# port.

Unlike make_sample_fixtures.py (which reproduces the port's choices on both sides to check its arithmetic), this
lets upstream decide everything the way its sampler does (unimate.inference.sample -> create_sample_condition):
the skeleton's conditioning from the dataset's cond.npy (T-pose, topology, sign-pinned spectral features, the
dataset's clean joint names), normalisation with the statistics of the skeleton's own dataset (Mixamo, Truebones,
Objaverse), fixed noise, the released checkpoint, upstream's motion recovery. The C# test builds an ordinary engine
rig from the same skeleton, lets the port choose, and compares the motion.

Run: D:\\99-scratch\\unimate\\.venv\\Scripts\\python.exe make_dataset_fixtures.py <fixture_dir>
"""
import json
import os
import sys
import time

sys.path.insert(0, r"D:\99-scratch\unimate-fixtures")
from unimate_ref import *  # noqa: E402,F401  (euler_sample, TextEncoder, quaternion helpers)
from Quaternions import Quaternions  # noqa: E402
from unimate.configs.schema import MainConfig  # noqa: E402
from unimate.models.factory import create_model, create_transport  # noqa: E402
from unimate.models.flow.transport import Sampler  # noqa: E402
from unimate.inference.generate import ClassifierFreeSampleModel  # noqa: E402
from unimate.training.ema import EMAModel  # noqa: E402
from unimate.utils.motion_utils import recover_unimate_joint_pos_from_rot  # noqa: E402
from unimate.dataset.transforms import extract_conditions, apply_normalization, apply_padding, build_parent_features  # noqa: E402
from unimate.dataset.mixture.collate import mixture_batch_collate  # noqa: E402
from unimate.utils.topology_utils import compute_edge_indexs  # noqa: E402
from Animation import offsets_from_positions  # noqa: E402

FIX = sys.argv[1]
DATA = r"D:\99-scratch\uniml3d"
CACHE = r"P:\02-projects\unimate-cache"
EXP = "unimate_uniml3d_f60_v2"
CFG, SEED = 3.0, 4321
CASES = [  # (dataset, object type, prompt)
    ("mixamo", "mixamo", "An object walks forward."),
    ("mixamo", "mixamo", "An object jumps."),
    ("mixamo", "mixamo", "An object punches forward."),
    ("truebones", "Alligator", "An object walks forward."),
    ("truebones", "Crocodile", "An object walks forward."),
    ("truebones", "Dog", "An object walks forward."),
    ("truebones", "Horse", "An object walks forward."),
    ("truebones", "Spider", "An object walks forward."),
    ("truebones", "Bird", "An object flaps its wings."),
    ("truebones", "Anaconda", "An object slithers forward."),
    ("objaverse", "669a666757bbceb71e5c0b1f_fbx", "An object walks forward."),
    ("objaverse", "44e11eaa8905483b8a4da1aba6dec9c5", "An object walks forward."),
]

cfg = MainConfig.from_json(os.path.join(CACHE, EXP, "config.json"))
model = create_model(cfg.dataset, cfg.model)
sd = torch.load(os.path.join(CACHE, EXP, "checkpoints", "checkpoint_step_100000.pt"), map_location="cpu", weights_only=False)
model.load_state_dict(sd["model_state_dict"])
ema = EMAModel(parameters=model.parameters(), decay=0.9999, use_ema_warmup=True)
ema.load_state_dict(sd["ema_state_dict"]); ema.copy_to(model.parameters())
model.eval()
gen_diffusion = Sampler(create_transport(training_config=cfg.training))  # sample.py's _build_diffusion for 'flow'
stats_all = np.load(os.path.join(CACHE, EXP, "dataset_stats.npy"), allow_pickle=True).item()
MJ, T = cfg.dataset.max_joints, cfg.dataset.max_motion_length
te = TextEncoder()
conds = {ds: np.load(os.path.join(DATA, f"features__{ds}__cond.npy"), allow_pickle=True).item() for ds in {c[0] for c in CASES}}

ONLY = [o for o in os.environ.get("T2A_CASES", "").split(",") if o]  # restrict to these object types (or prompts)
for dataset, object_type, prompt in CASES:
    if ONLY and object_type not in ONLY and prompt not in ONLY:
        continue
    c = conds[dataset][object_type]
    parents = np.array(c["parents"])
    J = len(parents)
    tpos = np.array(c["tpos_first_frame"], float)
    stats = stats_all[dataset]  # create_sample_condition: the clip's own dataset_type
    mj = max(MJ, J)
    for m in model.modules():
        if hasattr(m, "max_joints"): m.max_joints = mj
        if type(m).__name__ == "FinalLayer": m.joint = mj
    tpos12 = np.concatenate([tpos, np.zeros((J, 9))], -1)
    tpos12[:, 3:9] = Quaternions.id(1).rotation_matrix(cont6d=True)[0]
    ex = extract_conditions(np.zeros((T, J, 12)), tpos12, "tpos")
    mean = np.zeros((J, 12)); std = np.zeros((J, 12))
    mean[0], std[0] = stats["mean_root"], stats["std_root"]
    mean[1:], std[1:] = stats["mean_local"], stats["std_local"]
    tpos_n = apply_normalization(ex["tpos_first_frame"], mean, std)
    motion_n, _ = apply_padding(apply_normalization(ex["motion"], mean, std), T)
    clean = list(c["clean_joint_names"])
    uniq = sorted(set(clean))
    _, pooled, _ = te.encode([prompt] + uniq)
    emb = {n: pooled[1 + i] for i, n in enumerate(uniq)}
    joint_emb = np.stack([emb[n] for n in clean]).astype(np.float32)
    batch = dict(motion=motion_n, max_motion_length=T, motion_length=T, max_joints=mj, parents=parents,
                 edge_indexs=compute_edge_indexs(parents), tpos_first_frame=tpos_n,
                 tpos_first_frame_parents=build_parent_features(tpos_n, parents)["tpos_first_frame_parents"],
                 offsets=np.array(c["offsets"]), joint_graph_dist=np.array(c["joint_graph_dists"]),
                 joint_relations=np.array(c["joint_relations"]), joint_depths=np.array(c["joint_depths"]),
                 spectral_feats=np.array(c["spectral_feats"]), joint_names_emb=joint_emb, object_type=object_type,
                 start_idx=0, mean=mean, std=std, split_tag="train", caption=prompt, caption_emb=pooled[0])
    _, cond = mixture_batch_collate([batch])

    noise = torch.randn((1, mj, 12, T), generator=torch.Generator().manual_seed(SEED))
    # upstream's own sampling call (generate_samples, text-to-motion): dopri5 through the CFG wrapper
    with torch.no_grad():
        cfg_model = ClassifierFreeSampleModel(model, cfg_scale=CFG)
        calls = [0]
        def counted(x, t, cond=None):
            calls[0] += 1
            return cfg_model(x, t, cond=cond)
        counted.cond_mask_prob = model.cond_mask_prob
        t_start = time.time()
        x = gen_diffusion.sample_ode()(noise, counted, cond=cond)[-1]
        print(f"  dopri5: {calls[0]} network calls, {time.time() - t_start:.1f} s", flush=True)
    feat = x[0, :J].permute(2, 0, 1).numpy() * std[None] + mean[None]
    pos = recover_unimate_joint_pos_from_rot(feat, parents, offsets_from_positions(tpos, parents))

    tag = f"{dataset}_{object_type}_{prompt.split()[2].strip('.').lower()}"[:60]
    # noise for every slot: upstream pads to max_joints and the padded slots enter dopri5's error norm
    np.savez(os.path.join(FIX, f"ds_{tag}.npz"), noise=noise.numpy(), caption_emb=pooled[0], x_final=x.numpy()[:, :J],
             features=feat, fk_pos=pos, tpos=tpos, tpos_global_rot=np.array(c["tpos_global_rotations"], float),
             spectral=np.array(c["spectral_feats"], float), name_emb=joint_emb,
             cond_tpos=cond["tpos_first_frame"][0, :J].numpy())
    with open(os.path.join(FIX, f"ds_{tag}.json"), "w") as f:
        json.dump(dict(dataset=dataset, object_type=object_type, prompt=prompt, sampler="dopri5", cfg=CFG,
                       parents=[int(p) for p in parents], joint_names=list(c["joint_names"]), clean_joint_names=clean,
                       face=dict(r=int(c["face_joint_idxs"]["r_hip"]), l=int(c["face_joint_idxs"]["l_hip"]),
                                 body_axis=bool(c["face_joint_idxs"]["body_axis"]))), f)
    ground = pos[:, :, 1].min(1)
    print(f"DATASET {tag}: {J} joints, stats {dataset}; lowest joint per frame {ground.min():.3f}..{ground.max():.3f} (canonical, diameter 2)")

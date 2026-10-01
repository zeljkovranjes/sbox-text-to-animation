# Text to Animation

A text-to-animation and animation editing tool for the [s&box](https://sbox.game) editor,
powered by [UniMate](https://github.com/Friedrich-M/UniMate).

- Describe a motion and generate it on any humanoid VMDL ("walk cautiously forward, look behind, then run").
- Chain several prompts into one motion, fill the frames between pinned poses, regenerate unlocked
  bones from a new prompt, or make variations of an existing animation.
- Lock the selected bones, a bone and its children, or everything except the selection.
  Locked bones and pinned poses come back exactly as they were.
- Import existing sequences from the model, as many times as you like; originals are never touched.
- Trim, crop, split, delete or repeat sections, reverse, change speed and frame rate.
- Make seamless loops and set root motion: in place, remove drift, offset or bend the path,
  with the trajectory shown in the view.
- Pose bones in the view with keys that blend into the motion, and copy or paste poses.
- Fix foot sliding, ground the feet and detect footsteps. A quality check flags scale, axis,
  root-motion, sliding, discontinuity and loop-seam problems, and offers fixes.
- Save to VMDL with automatic compile and playback verification; the previous file is backed up and
  restored on failure. Also offers replace-existing, export, and additive, mirrored or in-place variants.
- Each model is a workspace that keeps its animations, undo history and settings between sessions.

Add the library to your s&box project and open **View → Text to Animation**, or right-click a
model asset and choose **Animate with Text to Animation**. Keep only one copy of the library installed.

1. Choose a humanoid model (.vmdl).
2. On the **Generate** tab click **Download** once to fetch the model (769 MB, verified, resumable).
3. Describe the motion and click **Generate**. Each take becomes a new animation in the left list.
4. Refine it on the **Edit** and **Pose** tabs. Shift+drag on the timeline selects a range;
   double-clicking the Pinned lane pins a frame.
5. On the **Save** tab click **Save to VMDL**, or **Replace…** to overwrite an existing sequence.

Generation runs entirely in C# inside the editor (no Python, no external process) on the CPU,
and never blocks the editor; it can be cancelled at any time. A 2-second motion takes roughly
10–20 seconds on a modern desktop CPU at Standard quality. Model files are stored in
`%LOCALAPPDATA%\TextToAnimation\Models` (override with `TEXT_TO_ANIMATION_MODEL_ROOT`), so
they are shared by all projects. The editor works offline once they are downloaded.

UniMate generates body motion only: fingers and helper bones keep their pose from the source or
rest pose, and facial animation is not generated. Models that are not humanoid, or whose
skeleton cannot be recognised, are not supported. Generated motion can need cleanup; check it
in the preview before saving.

UniMate's code is MIT licensed. Its weights are trained on Mixamo and Objaverse-XL data, which
remain governed by their original licenses; review them before commercial use. The text encoder
is [flan-t5-base](https://huggingface.co/google/flan-t5-base) (Apache 2.0).

Package ident: `notpointless.chomnr_text_to_animation`

## Development

- `dotnet test dev/Tests` runs the unit tests. These cover the clip operations, workspace storage,
  VMDL writing, the ONNX runtime, and a numerical check of the UniMate port against the reference implementation.
- `dev/editor-rig/run_gate.ps1` runs the end-to-end gate in a real `sbox-dev.exe`: 37 checks covering
  open, import, edit, undo, posing, locks, every generation mode, cancellation, save with playback
  verification, replace, export and reload. `-Capture` also saves screenshots of the window.

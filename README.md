# Text to Animation

Text-to-animation and animation editing inside the s&box editor, powered by
[UniMate](https://github.com/Friedrich-M/UniMate). Describe a motion ("walk cautiously forward, look behind,
then run") and get a real, compiled animation on your own rigged model, then trim, loop, fix and pose it
before saving it back into the VMDL. For anyone animating characters or creatures in s&box without a DCC tool.

- Generate motion from text, chain prompts (one step per line), in-between pinned poses, edit with bone locks, make variations.
- Trim, split, reverse, change speed, loop, fix root motion and foot sliding, and pose bones with keys.
- Save to VMDL with compile and playback verification, replace existing sequences, or export FBX.

## Requirements

- The s&box editor (this is an editor tool).
- The UniMate model weights: 769 MB, downloaded once from the Generate tab.
- No Python or external tools. A GPU is used when available; otherwise everything runs on the CPU.

## Install

Search for **Text to Animation** in the s&box library manager, or add `notpointless.chomnr_text_to_animation`.

## Quick start

### In the editor

1. Open **View → Text to Animation**, or right-click a model and choose **Animate with Text to Animation**.
2. Choose or drop a rigged VMDL, or start fresh with **New from Citizen** / **New from Citizen Human** (copies that VMDL into your project).
3. Click **Download** on the Generate tab (769 MB, once).
4. Describe the motion and click **Generate**.
5. Refine it on the Edit and Pose tabs, then click **Save to VMDL**.

### In code

Editor-only, no code API.

## Options

None: everything is chosen in the window as you work. Generated motion is cleaned by default (jitter
smoothed, planted feet locked); the raw model output is one click away.

## How it works

The editor window drives a pure C# port of UniMate: a T5 text encoder and the UniMate motion network run on a
small managed ONNX-style runtime (CPU kernels, plus compute shaders in `Assets/shaders/t2a` for the GPU). Your
model's skeleton is prepared and named the way UniMate's own pipeline does it, so people, animals, creatures and
props all get the statistics of the data they resemble. The generated motion is cleaned up, retargeted onto the
rig and written into the VMDL as a sequence, which is then compiled and played back to verify it. Clip editing
and the rig/animation maths live in the runtime assembly as plain C# (`Code/TextToAnimation/Core`).

## Multiplayer

Not applicable: an editor tool. The animations it saves are ordinary model sequences and network like any other.

## Limitations

- Editor-only: nothing generates animation at runtime in a game.
- The first generation needs the 769 MB download; generation takes seconds per clip, longer on the CPU.
- Motion quality is UniMate's: unusual rigs may need cleanup on the Edit and Pose tabs.
- UniMate code is MIT; its weights are trained on Mixamo and Objaverse-XL data, so check those licenses before commercial use.

## Development

- `sbox-check` (structure, docs, and builds `dev\TextToAnimation.Core.csproj` as plain .NET).
- `dotnet test tests\TextToAnimation.Tests` (254 tests; fixtures in `tests\TextToAnimation.Tests\fixtures`).
- Offline compile checks: `dotnet build dev\editor-rig\CodeCompileCheck.csproj` and `dev\editor-rig\EditorCompileCheck.csproj`.
- End-to-end gate in a real editor (whitelist included): `dev\editor-rig\run_gate.ps1`.
- Fixture generators (upstream UniMate in Python/Blender): `dev\tools`.

## License

UniMate code is MIT. Check the Mixamo and Objaverse-XL data licenses for the model weights before commercial use.

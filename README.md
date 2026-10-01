# Text to Animation

Text-to-animation and animation editing for the [s&box](https://sbox.game) editor, powered by
[UniMate](https://github.com/Friedrich-M/UniMate).

- Generate motion from text, chain prompts, in-between pinned poses, edit with bone locks, make variations.
- Trim, split, reverse, change speed, loop, fix root motion and foot sliding, and pose bones with keys.
- Save to VMDL with compile and playback verification, replace existing sequences, or export.

Open **View → Text to Animation**, or right-click a model and choose **Animate with Text to Animation**.

1. Choose or drop a humanoid VMDL, or start fresh with **New from Citizen** / **New from Citizen Human** (copies that VMDL into your project).
2. Click **Download** on the Generate tab (769 MB, once).
3. Describe the motion and click **Generate**.
4. Refine it on the Edit and Pose tabs, then click **Save to VMDL**.

Runs fully in C# on the CPU. UniMate code is MIT; its weights are trained on Mixamo and
Objaverse-XL data, so check those licenses before commercial use.

Package ident: `notpointless.chomnr_text_to_animation`

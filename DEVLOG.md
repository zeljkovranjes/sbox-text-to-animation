# Text to Animation

Journal for this library. Keep it current: decisions, engine gotchas, what failed and why, the next step.

## Package

| | |
|---|---|
| Ident | `notpointless.chomnr_text_to_animation` |
| Type | library (editor tool) |
| Root namespace | `TextToAnimation` |
| Depends on | none |
| Published | |

## Decisions

- All of `Code/TextToAnimation` is plain C# (it already compiled in the plain .NET test project), so the whole
  runtime assembly is `Core`. There are no Components, Systems or Resources: the product is the editor window.
- Engine-free editor code (Inference, Formats/Fbx, Generation, Mapping, Workspace, `Engine/FbxSkin.cs`) stays in
  `Editor/` rather than moving to Core: it is editor-only, compiles with unsafe code (`FastKernels`) and would
  otherwise ship in the runtime package under the stricter runtime whitelist. The test project compiles it
  directly by path.
- `Editor/Mapping` is a vendored copy of humanoid-retargeter's mapping code and duplicates
  `Core/Mapping` under another namespace; kept as found.
- `FastKernels` is `unsafe` per method instead of per class: sbox-check's file/type rule does not recognise
  the `unsafe` class modifier. Same code, same compile.

## Log

- 2026-10-06: brought under the workspace standard. Runtime code moved to `Code/TextToAnimation/Core`
  (namespaces `TextToAnimation.Core.*`), editor code from `Editor/TextToAnimation/*` to `Editor/*`
  (namespaces `TextToAnimation.EditorTools.*`), tests from `dev/Tests` to `tests/TextToAnimation.Tests`,
  multi-type files split one type per file, icon to `Assets/ui`. Tests 254/254 before and after; Code, Editor,
  both compile checks and the Core harness build. Editor gate (`run_gate.ps1`, the whitelist proof) not re-run.
  Next: run the gate in the real editor once to confirm the whitelist after the move.

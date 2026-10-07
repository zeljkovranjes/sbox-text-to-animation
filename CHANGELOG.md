# Changelog

## 2026-10-06
### Breaking
- Namespaces moved (type names are unchanged; only code that calls the library's types directly is affected, update its `using` lines):
  - `TextToAnimation.Animation` -> `TextToAnimation.Core.Animation`
  - `TextToAnimation.Formats` -> `TextToAnimation.Core.Formats`
  - `TextToAnimation.Generation` -> `TextToAnimation.Core.Generation`
  - `TextToAnimation.Mapping` -> `TextToAnimation.Core.Mapping`
  - `TextToAnimation.Maths` -> `TextToAnimation.Core.Maths`
  - `TextToAnimation.Processing` -> `TextToAnimation.Core.Processing`
  - `TextToAnimation.Rig` -> `TextToAnimation.Core.Rig`
  - `TextToAnimation.Vmdl` -> `TextToAnimation.Core.Vmdl`
  - `TextToAnimation.Workspace` -> `TextToAnimation.Core.Workspace`
  - `TextToAnimation.Editor.*` -> `TextToAnimation.EditorTools.*` (Engine, Formats.Fbx, Generation, Inference.*, Mapping, Session, Testing, UI, Workspace)
  - `TextToAnimation.Editor.UniMate` (UniMateBackend) -> `TextToAnimation.EditorTools.Generation.UniMate`
### Changed
- The package follows the workspace layout: runtime code in `Code/TextToAnimation/Core`, editor code directly under `Editor/`, tests in `tests/TextToAnimation.Tests`, one type per file. Behaviour is unchanged.
- The package icon moved to `Assets/ui`.
- Package summary, description and tags rewritten; README rewritten in the standard format.

using System;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Editor.Generation;
using TextToAnimation.Generation;

namespace TextToAnimation.Editor.UniMate;

/// <summary>
/// UniMate (SIGGRAPH Asia 2026) as the editor's motion model: downloads the official checkpoint,
/// converts it to ONNX on this machine and runs it with the managed ONNX runtime.
/// </summary>
public sealed partial class UniMateBackend : IGeneratorBackend
{
	public string Name => "UniMate";
	public string Description => "Text-to-motion for any skeleton (Princeton, SIGGRAPH Asia 2026).";
}

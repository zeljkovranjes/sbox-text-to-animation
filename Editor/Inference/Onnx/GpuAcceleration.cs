using System;
using System.Collections.Generic;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>
/// Where graph runs may go to the GPU. The inference code has no engine dependency: the editor registers a
/// <see cref="Compiler"/> (compute shaders); without one, or when it fails, everything runs on the CPU.
/// </summary>
public static class GpuAcceleration
{
	/// <summary>Loads a plan onto the GPU (null when there is no GPU path, e.g. in tests).</summary>
	public static Func<GpuPlan, OnnxSession, IGpuProgram> Compiler { get; set; }

	/// <summary>Off by user choice or after a failure.</summary>
	public static bool Enabled { get; set; } = true;

	/// <summary>Why the GPU isn't used (null while it is, or before it was tried).</summary>
	public static string Status { get; set; }

	/// <summary>A GPU result must match the CPU on the traced run at least this closely before it is trusted.</summary>
	public const float AgreementTolerance = 2e-3f;
}

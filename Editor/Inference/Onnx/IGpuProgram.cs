using System;
using System.Collections.Generic;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>A <see cref="GpuPlan"/> loaded on a device: weights resident, ready to run.</summary>
public interface IGpuProgram : IDisposable
{
	/// <summary>Runs the plan on <paramref name="feed"/> (the graph inputs) and returns the graph outputs.</summary>
	Dictionary<string, float[]> Run( IReadOnlyDictionary<string, Tensor> feed );

	/// <summary>
	/// Runs launch by launch, comparing each node's result with the CPU's (<paramref name="cpu"/>, by value name):
	/// a description of the first that differs, or null.
	/// </summary>
	string Diagnose( IReadOnlyDictionary<string, Tensor> feed, IReadOnlyDictionary<string, float[]> cpu );
}

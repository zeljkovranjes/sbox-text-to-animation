using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>Per-run execution context (caches derived constants such as transposed weights).</summary>
public sealed class ExecContext
{
	public readonly Dictionary<Tensor, Tensor> TransposedCache = new( ReferenceEqualityComparer.Instance );
	/// <summary>Packed GEMM panels of constant matrices (weights), built on first use.</summary>
	public readonly Dictionary<Tensor, FastKernels.PackedMatrix> Packed = new( ReferenceEqualityComparer.Instance );
	/// <summary>Tensors that never change between runs (initializers).</summary>
	public readonly HashSet<Tensor> Constants = new( ReferenceEqualityComparer.Instance );
	/// <summary>Buffers of intermediate tensors, reused between nodes and runs.</summary>
	public readonly TensorPool Pool = new();
	/// <summary>The context of the run on this thread (kernels rent their outputs from its pool).</summary>
	[ThreadStatic] public static ExecContext Current;
	/// <summary>An output buffer with undefined contents: pooled while a session runs, fresh otherwise.</summary>
	public static float[] Alloc( int n ) => Current?.Pool.Rent( n ) ?? new float[n];
	/// <summary>A zero-filled output buffer.</summary>
	public static float[] AllocZeroed( int n ) => Current?.Pool.RentZeroed( n ) ?? new float[n];
	// two cores stay free: the editor's main and render threads keep running smoothly while a model runs on the CPU
	public int MaxThreads = Math.Max( 1, Environment.ProcessorCount - 2 );
	public ParallelOptions Parallel => new() { MaxDegreeOfParallelism = MaxThreads };
}

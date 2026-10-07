using System;
using System.Collections.Generic;

namespace TextToAnimation.EditorTools.Inference.Onnx;

/// <summary>
/// Reuses the float buffers of intermediate tensors between nodes and between runs. A transformer step builds
/// hundreds of same-sized activations; allocating each fresh (zeroed, on the large-object heap) cost more than
/// a third of a step in allocation and full garbage collections. The session returns a buffer once the last
/// tensor using it is dead; only buffers this pool handed out ever come back to it.
/// </summary>
public sealed class TensorPool
{
	readonly Dictionary<int, Stack<float[]>> _free = new();
	readonly HashSet<float[]> _owned = new( ReferenceEqualityComparer.Instance );
	readonly object _lock = new();

	/// <summary>A buffer of exactly <paramref name="length"/> floats with undefined contents.</summary>
	public float[] Rent( int length )
	{
		if ( length <= 0 ) return Array.Empty<float>();
		lock ( _lock )
		{
			if ( _free.TryGetValue( length, out var stack ) && stack.Count > 0 )
			{
				var reused = stack.Pop();
				_owned.Add( reused );
				return reused;
			}
		}
		var fresh = GC.AllocateUninitializedArray<float>( length );
		lock ( _lock ) _owned.Add( fresh );
		return fresh;
	}

	/// <summary>A zero-filled buffer (for kernels that accumulate into their output).</summary>
	public float[] RentZeroed( int length )
	{
		var a = Rent( length );
		Array.Clear( a );
		return a;
	}

	/// <summary>True for buffers this pool handed out that are still in use.</summary>
	public bool Owns( float[] a )
	{
		lock ( _lock ) return _owned.Contains( a );
	}

	/// <summary>Takes a buffer back for reuse (ignored for buffers the pool doesn't own).</summary>
	public void Return( float[] a )
	{
		if ( a is null || a.Length == 0 ) return;
		lock ( _lock )
		{
			if ( !_owned.Remove( a ) ) return;
			if ( !_free.TryGetValue( a.Length, out var stack ) ) _free[a.Length] = stack = new Stack<float[]>();
			stack.Push( a );
		}
	}

	/// <summary>Gives a buffer away for good (a session output now owned by the caller).</summary>
	public void Release( float[] a )
	{
		if ( a is null ) return;
		lock ( _lock ) _owned.Remove( a );
	}

	/// <summary>Drops every free buffer (memory back to the GC).</summary>
	public void Trim()
	{
		lock ( _lock ) _free.Clear();
	}
}

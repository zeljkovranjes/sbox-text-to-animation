using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TextToAnimation.Editor.Inference.Onnx;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>
/// flan-t5-base encoder (ONNX, run by the managed runtime) with UniMate's pooling: the mean of
/// <c>last_hidden_state</c> over the non-padding tokens. The empty prompt pools to zero, which is the
/// unconditional (classifier-free) branch. Results are cached per text.
/// </summary>
public sealed class T5TextEncoder
{
	public const int Width = 768;
	readonly OnnxSession _session;
	readonly T5Tokenizer _tokenizer;
	readonly ConcurrentDictionary<string, float[]> _cache = new( StringComparer.Ordinal );
	readonly object _runLock = new();

	public T5TextEncoder( OnnxSession session, T5Tokenizer tokenizer )
	{
		_session = session;
		_tokenizer = tokenizer;
	}

	public T5Tokenizer Tokenizer => _tokenizer;

	/// <summary>Pooled 768-d embedding of a text.</summary>
	public float[] Encode( string text, CancellationToken token = default )
	{
		text ??= "";
		if ( text.Length == 0 ) return new float[Width];
		if ( _cache.TryGetValue( text, out var cached ) ) return cached;
		var ids = _tokenizer.Encode( text );
		var input = Tensor.Int64( new[] { 1, ids.Count }, ids.Select( i => (long)i ).ToArray() );
		var mask = Tensor.Int64( new[] { 1, ids.Count }, Enumerable.Repeat( 1L, ids.Count ).ToArray() );
		Tensor hidden;
		lock ( _runLock )
			hidden = _session.Run( new Dictionary<string, Tensor> { ["input_ids"] = input, ["attention_mask"] = mask }, token )["last_hidden_state"];
		var pooled = new float[Width];
		for ( var t = 0; t < ids.Count; t++ )
			for ( var c = 0; c < Width; c++ ) pooled[c] += hidden.F[t * Width + c];
		for ( var c = 0; c < Width; c++ ) pooled[c] /= ids.Count;
		_cache[text] = pooled;
		return pooled;
	}
}

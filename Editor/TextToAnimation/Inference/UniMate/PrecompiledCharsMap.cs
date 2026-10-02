using System;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>
/// SentencePiece's precompiled normalisation (the "Precompiled" normalizer of a Hugging Face tokenizer.json, as the
/// tokenizers library applies it): a darts-clone double-array trie from UTF-8 sequences to their replacements (NFKC
/// plus SentencePiece's own rules: control characters, invisible joiners, odd spaces ...). Each grapheme cluster
/// shorter than 6 bytes is looked up whole; otherwise, or when it has no entry, each of its characters is.
/// </summary>
public sealed class PrecompiledCharsMap
{
	readonly uint[] _array;
	readonly byte[] _normalized;

	public PrecompiledCharsMap( byte[] blob )
	{
		var trieSize = (int)BitConverter.ToUInt32( blob, 0 );
		if ( trieSize % 4 != 0 || 4 + trieSize > blob.Length ) throw new InvalidOperationException( "Invalid precompiled charsmap." );
		_array = new uint[trieSize / 4];
		for ( var i = 0; i < _array.Length; i++ ) _array[i] = BitConverter.ToUInt32( blob, 4 + i * 4 );
		_normalized = blob[(4 + trieSize)..];
	}

	/// <summary>The map of a tokenizer.json's normalizer (directly or inside a Sequence), or null when it has none.</summary>
	public static PrecompiledCharsMap FromTokenizer( JsonElement root )
	{
		if ( !root.TryGetProperty( "normalizer", out var normalizer ) || normalizer.ValueKind != JsonValueKind.Object ) return null;
		return Find( normalizer );

		static PrecompiledCharsMap Find( JsonElement n )
		{
			var type = n.TryGetProperty( "type", out var t ) ? t.GetString() : null;
			if ( type == "Precompiled" && n.TryGetProperty( "precompiled_charsmap", out var map ) && map.ValueKind == JsonValueKind.String )
				return new PrecompiledCharsMap( Convert.FromBase64String( map.GetString() ) );
			if ( type == "Sequence" && n.TryGetProperty( "normalizers", out var list ) )
				foreach ( var inner in list.EnumerateArray() )
					if ( Find( inner ) is { } found ) return found;
			return null;
		}
	}

	public string Normalize( string text )
	{
		var sb = new StringBuilder( text.Length );
		var bytes = new byte[64];
		var elements = StringInfo.GetTextElementEnumerator( text );
		while ( elements.MoveNext() )
		{
			var grapheme = (string)elements.Current;
			if ( Encoding.UTF8.GetByteCount( grapheme ) < 6 && Transform( grapheme ) is { } whole )
			{
				sb.Append( whole );
				continue;
			}
			foreach ( var rune in grapheme.EnumerateRunes() )
			{
				var part = rune.ToString();
				sb.Append( Transform( part ) ?? part );
			}
		}
		return sb.ToString();
	}

	/// <summary>The replacement for a whole chunk: the value of the trie's first (shortest) key that prefixes it.</summary>
	string Transform( string chunk )
	{
		var key = Encoding.UTF8.GetBytes( chunk );
		var pos = 0;
		var unit = _array[0];
		pos ^= Offset( unit );
		foreach ( var c in key )
		{
			if ( c == 0 ) break;
			pos ^= c;
			if ( (uint)pos >= (uint)_array.Length ) return null;
			unit = _array[pos];
			if ( Label( unit ) != c ) return null;
			pos ^= Offset( unit );
			if ( HasLeaf( unit ) )
			{
				if ( (uint)pos >= (uint)_array.Length ) return null;
				var start = (int)Value( _array[pos] );
				var end = start;
				while ( end < _normalized.Length && _normalized[end] != 0 ) end++;
				return Encoding.UTF8.GetString( _normalized, start, end - start );
			}
		}
		return null;
	}

	static bool HasLeaf( uint unit ) => ((unit >> 8) & 1) == 1;
	static uint Value( uint unit ) => unit & ((1u << 31) - 1);
	static uint Label( uint unit ) => unit & ((1u << 31) | 0xFF);
	static int Offset( uint unit ) => (int)((unit >> 10) << (int)((unit & (1u << 9)) >> 6));
}

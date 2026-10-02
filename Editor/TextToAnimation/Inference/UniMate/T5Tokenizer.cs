using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TextToAnimation.Editor.Inference.UniMate;

/// <summary>
/// SentencePiece-unigram tokenizer for flan-t5, read from the Hugging Face <c>tokenizer.json</c>: its precompiled
/// charsmap normalisation, whitespace split and Metaspace pre-tokenisation ("▁" word prefix),
/// Viterbi segmentation by piece log-probability, then <c>&lt;/s&gt;</c> appended.
/// </summary>
public sealed class T5Tokenizer
{
	public const int PadId = 0;
	public const int EosId = 1;
	public const int UnkId = 2;
	const char Space = '▁';

	readonly Dictionary<string, (int Id, double Score)> _pieces = new( StringComparer.Ordinal );
	readonly int _maxPieceLength;
	readonly double _unkScore;

	public int VocabSize { get; }

	public T5Tokenizer( string tokenizerJson )
	{
		using var doc = JsonDocument.Parse( tokenizerJson );
		var model = doc.RootElement.GetProperty( "model" );
		if ( model.GetProperty( "type" ).GetString() != "Unigram" )
			throw new InvalidDataException( "tokenizer.json is not a Unigram tokenizer." );
		var id = 0;
		var min = double.MaxValue;
		foreach ( var entry in model.GetProperty( "vocab" ).EnumerateArray() )
		{
			var piece = entry[0].GetString() ?? "";
			var score = entry[1].GetDouble();
			_pieces.TryAdd( piece, (id, score) );
			min = Math.Min( min, score );
			id++;
		}
		VocabSize = id;
		_maxPieceLength = _pieces.Keys.Max( k => k.Length );
		_unkScore = min - 10.0;
		_charsMap = PrecompiledCharsMap.FromTokenizer( doc.RootElement );
	}

	readonly PrecompiledCharsMap _charsMap;

	public static T5Tokenizer Load( string path ) => new( File.ReadAllText( path ) );

	/// <summary>
	/// Token ids for a text, ending with &lt;/s&gt;, as transformers' T5Tokenizer gives them: the normalizer, then its
	/// pre-tokenizers WhitespaceSplit (words between Unicode whitespace) and Metaspace (each word prefixed with "▁"
	/// unless it starts with one, and split before every "▁"), then each piece segmented.
	/// </summary>
	public List<int> Encode( string text )
	{
		var ids = new List<int>();
		var normalized = Normalize( text );
		var start = -1;
		for ( var i = 0; i <= normalized.Length; i++ )
		{
			if ( i < normalized.Length && !char.IsWhiteSpace( normalized[i] ) )
			{
				if ( start < 0 ) start = i;
				continue;
			}
			if ( start >= 0 ) Metaspace( normalized[start..i], ids );
			start = -1;
		}
		ids.Add( EosId );
		return ids;
	}

	void Metaspace( string word, List<int> ids )
	{
		if ( word[0] != Space ) word = Space + word;
		var from = 0;
		for ( var i = 1; i <= word.Length; i++ )
		{
			if ( i < word.Length && word[i] != Space ) continue;
			Segment( word[from..i], ids );
			from = i;
		}
	}

	/// <summary>tokenizer.json's normalizer: the SentencePiece precompiled charsmap (its space collapsing is moot once words are split on whitespace).</summary>
	string Normalize( string text ) => _charsMap is null ? (text ?? "").Normalize( NormalizationForm.FormKC ) : _charsMap.Normalize( text ?? "" );

	/// <summary>Viterbi: best-scoring segmentation of one word into vocabulary pieces.</summary>
	void Segment( string word, List<int> ids )
	{
		var n = word.Length;
		var best = new double[n + 1];
		var from = new int[n + 1];
		var piece = new int[n + 1];
		for ( var i = 1; i <= n; i++ ) best[i] = double.NegativeInfinity;
		for ( var end = 1; end <= n; end++ )
		{
			for ( var len = 1; len <= Math.Min( _maxPieceLength, end ); len++ )
			{
				var start = end - len;
				if ( double.IsNegativeInfinity( best[start] ) ) continue;
				if ( _pieces.TryGetValue( word.Substring( start, len ), out var p ) )
				{
					var score = best[start] + p.Score;
					if ( score > best[end] ) { best[end] = score; from[end] = start; piece[end] = p.Id; }
				}
			}
			if ( double.IsNegativeInfinity( best[end] ) )
			{
				// no piece ends here: consume one character as <unk>
				best[end] = best[end - 1] + _unkScore;
				from[end] = end - 1;
				piece[end] = UnkId;
			}
		}
		var result = new List<int>();
		for ( var i = n; i > 0; i = from[i] ) result.Add( piece[i] );
		result.Reverse();
		// merge consecutive unknowns like SentencePiece
		for ( var i = 0; i < result.Count; i++ )
			if ( !(result[i] == UnkId && ids.Count > 0 && ids[^1] == UnkId && i > 0) ) ids.Add( result[i] );
	}
}

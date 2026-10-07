namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>Prompts as UniMate takes them, and clip names from them.</summary>
public static class UniMatePrompt
{
	/// <summary>
	/// The caption UniMate is conditioned on: the prompt as written, trimmed - as upstream (inference/sample.py encodes
	/// <c>prompt.strip()</c> whole, with no rewriting or splitting; FLAN-T5 reads the wording).
	/// </summary>
	public static string Caption( string prompt )
	{
		var s = prompt ?? "";
		int start = 0, end = s.Length;
		while ( start < end && IsPythonSpace( s[start] ) ) start++;
		while ( end > start && IsPythonSpace( s[end - 1] ) ) end--;
		return s[start..end];
	}

	/// <summary>Python's <c>str.isspace</c>: .NET's whitespace plus the information separators U+001C..U+001F.</summary>
	static bool IsPythonSpace( char c ) => char.IsWhiteSpace( c ) || c is >= '\u001c' and <= '\u001f';

	/// <summary>
	/// A clip name from a prompt: the prompt itself on one line, capitalised, without a leading "An object"; only a
	/// very long one is shortened, at a word boundary.
	/// </summary>
	public static string ClipName( string prompt )
	{
		var words = Caption( prompt ).Split( (char[])null, System.StringSplitOptions.RemoveEmptyEntries );
		// UniMate's captions all open with "An object" (its README's advice for prompts): the name is what it does
		if ( words.Length > 2 && words[0].Equals( "an", System.StringComparison.OrdinalIgnoreCase ) && words[1].Equals( "object", System.StringComparison.OrdinalIgnoreCase ) )
			words = words[2..];
		var text = string.Join( ' ', words ).TrimEnd( '.', '!', '?' ).Trim();
		const int MaxLength = 48;
		if ( text.Length > MaxLength )
		{
			var space = text.LastIndexOf( ' ', MaxLength );
			var cut = space > 0 ? space : MaxLength;
			if ( char.IsHighSurrogate( text[cut - 1] ) ) cut--; // never half a character (emoji)
			text = text[..cut].Trim();
		}
		if ( text.Length == 0 ) return "Generated";
		return char.ToUpperInvariant( text[0] ) + text[1..];
	}
}

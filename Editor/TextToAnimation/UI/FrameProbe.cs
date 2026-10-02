using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// Where the editor's frame time goes (the gate reports it): milliseconds per labelled piece of work. Off until
/// <see cref="Reset"/> is called.
/// </summary>
public static class FrameProbe
{
	static bool _enabled;

	/// <summary>True once the gate turned the probe on.</summary>
	public static bool Enabled => _enabled;

	static readonly Dictionary<string, List<double>> _samples = new();
	static readonly Stopwatch _clock = Stopwatch.StartNew();
	static double _lastFrame = -1;

	public static void Add( string label, double ms )
	{
		if ( !_enabled ) return;
		if ( !_samples.TryGetValue( label, out var list ) ) _samples[label] = list = new List<double>();
		if ( list.Count < 20000 ) list.Add( ms );
	}

	public static double Now => _clock.Elapsed.TotalMilliseconds;

	/// <summary>Called once per editor frame: records the interval since the previous one.</summary>
	public static void FrameTick()
	{
		if ( !_enabled ) return;
		var now = Now;
		if ( _lastFrame >= 0 ) Add( "frame interval", now - _lastFrame );
		_lastFrame = now;
	}

	static TimeSpan _gcPause;
	static int _gen2;

	public static void Reset() { _enabled = true; _samples.Clear(); _lastFrame = -1; _gcPause = GC.GetTotalPauseDuration(); _gen2 = GC.CollectionCount( 2 ); }

	/// <summary>Times <paramref name="work"/> under <paramref name="label"/>.</summary>
	public static void Time( string label, Action work )
	{
		var started = Now;
		try { work(); }
		finally { Add( label, Now - started ); }
	}

	public static string Report() => $"GC pauses {(GC.GetTotalPauseDuration() - _gcPause).TotalMilliseconds:0} ms ({GC.CollectionCount( 2 ) - _gen2} full); heap {GC.GetTotalMemory( false ) / 1048576} MB; " + string.Join( "; ", _samples.Select( kv =>
	{
		var s = kv.Value.OrderBy( x => x ).ToList();
		double P( double q ) => s.Count == 0 ? 0 : s[Math.Min( s.Count - 1, (int)(q * s.Count) )];
		return $"{kv.Key}: n {s.Count}, median {P( .5 ):0.0} ms, p95 {P( .95 ):0.0}, max {(s.Count == 0 ? 0 : s[^1]):0.0}";
	} ) );
}

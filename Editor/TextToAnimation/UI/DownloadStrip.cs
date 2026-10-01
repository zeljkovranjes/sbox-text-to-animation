using System;
using System.Text.RegularExpressions;
using Editor;
using Sandbox;
using TextToAnimation.Editor.Generation;
using TextToAnimation.Editor.Session;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// Shown only while the UniMate model is missing: what it is and a Download button; during the download a
/// progress bar with the file and what is left, and a pause button. Hides itself once the model is installed.
/// </summary>
public sealed class DownloadStrip : Widget
{
	static readonly Regex Line = new( @"^Downloading (?<name>.+?) · (?<percent>\d+)%(?: · (?<left>.+) left)?$", RegexOptions.CultureInvariant );

	readonly GenerationFlow _flow;
	readonly Label _text;
	readonly Button _download;
	readonly TaButton _pause;
	float? _fraction;
	bool _downloading;

	public DownloadStrip( Widget parent, GenerationFlow flow ) : base( parent )
	{
		_flow = flow;
		FixedHeight = 48;
		Layout = Layout.Row();
		Layout.Margin = new Sandbox.UI.Margin( 44, 6, 8, 10 );
		Layout.Spacing = 8;
		_text = Layout.Add( new Label( "", this ) { WordWrap = true }, 1 );
		_download = Layout.Add( new Button.Primary( "Download" ) { Icon = "download", Tint = TaStyle.Accent, FixedHeight = 28 } );
		_download.Clicked = () => _ = DownloadAsync();
		_pause = Layout.Add( new TaButton( this, "Pause", "pause", () => _flow.Cancel(), "Stop for now; it resumes where it stopped", 28 ) );
		_flow.Progress += OnProgress;
		GeneratorService.Instance.StateChanged += () => MainThread.Queue( Refresh );
		Refresh();
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		_flow.Progress -= OnProgress;
	}

	async System.Threading.Tasks.Task DownloadAsync()
	{
		_downloading = true;
		_fraction = 0;
		Refresh();
		try { await _flow.DownloadAsync(); }
		finally { _downloading = false; _fraction = null; Refresh(); }
	}

	void OnProgress( string line )
	{
		if ( !_downloading || !this.IsValid() ) return;
		if ( Line.Match( line ) is { Success: true } m )
		{
			_fraction = Math.Clamp( int.Parse( m.Groups["percent"].Value ) / 100f, 0, 1 );
			_text.Text = m.Groups["left"].Success ? $"Downloading {m.Groups["name"].Value} · {m.Groups["left"].Value} left" : $"Downloading {m.Groups["name"].Value}";
		}
		else _text.Text = line;
		Update();
	}

	public void Refresh()
	{
		if ( !this.IsValid() ) return;
		var service = GeneratorService.Instance;
		var installed = service.State is ModelState.Ready or ModelState.Loading;
		Visible = !installed || _downloading;
		_download.Visible = !_downloading;
		_pause.Visible = _downloading;
		if ( !_downloading )
		{
			_download.Text = service.State == ModelState.Incomplete ? "Resume download" : service.State == ModelState.Failed ? "Try again" : "Download";
			_text.Text = service.State == ModelState.Failed && !string.IsNullOrEmpty( service.LastError )
				? $"The download failed: {service.LastError}"
				: $"Text to animation needs the UniMate model ({service.Backend.DownloadBytes / 1e6:0} MB, downloaded once). It runs on your computer.";
		}
		Update();
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.SetPen( TaStyle.Accent.WithAlpha( .45f ), 1 );
		Paint.SetBrush( TaStyle.Accent.WithAlpha( .08f ) );
		Paint.DrawRect( LocalRect.Shrink( 1 ), 6 );
		Paint.SetPen( TaStyle.AccentLight );
		Paint.DrawIcon( new Rect( 12, 0, 22, Height - 4 ), "download", 20, TextFlag.Center );
		if ( _fraction is float f && _downloading )
		{
			var bar = new Rect( 44, Height - 9, Width - 56, 4 );
			Paint.ClearPen();
			Paint.SetBrush( Color.White.WithAlpha( .08f ) );
			Paint.DrawRect( bar, 2 );
			Paint.SetBrush( TaStyle.Accent );
			Paint.DrawRect( new Rect( bar.Left, bar.Top, MathF.Max( 4, bar.Width * f ), bar.Height ), 2 );
		}
	}
}

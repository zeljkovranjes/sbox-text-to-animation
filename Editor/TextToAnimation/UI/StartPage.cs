using System;
using System.Collections.Generic;
using Editor;
using Sandbox;
using TextToAnimation.Editor.Session;

namespace TextToAnimation.Editor.UI;

/// <summary>
/// First-load surface (the Weapon Importer's drop area): a dark-gray rounded box with a centered prompt,
/// a blue choose button and buttons to start from a stock character. Accepts .vmdl files from the OS and
/// models from the asset browser.
/// </summary>
public sealed class StartPage : Widget
{
	readonly Action<IReadOnlyList<string>> _open;
	int _hover;

	public StartPage( Widget parent, Action<IReadOnlyList<string>> open, Action chooseFile, Action chooseFromDisk,
		Action newFromCitizen, Action newFromCitizenHuman, GenerationFlow flow ) : base( parent )
	{
		_open = open;
		AcceptDrops = true;
		Layout = Layout.Column();
		Layout.Margin = 12;
		Layout.Spacing = 8;
		Layout.AddStretchCell();
		var row = Layout.AddRow();
		row.AddStretchCell();
		var center = row.AddColumn();
		center.Spacing = 12;
		center.Add( new BigIcon( this ) );
		center.Add( new Label.Subtitle( "Drop a model", this ) { Alignment = TextFlag.Center } );
		center.Add( TaStyle.Muted( new Label( "VMDL or rigged FBX", this ) { Alignment = TextFlag.Center } ) );
		center.Add( TaStyle.Muted( new Label( "Drop a rigged model to animate it, or start fresh from a copy of an s&box character.", this )
			{ Alignment = TextFlag.Center, WordWrap = true, MaximumWidth = 440 }, small: true ) );
		center.AddSpacingCell( 4 );
		var choice = center.AddRow();
		choice.Spacing = 8;
		choice.AddStretchCell();
		choice.Add( new Button.Primary( "Choose VMDL" ) { Icon = "folder_open", Tint = TaStyle.Accent, MinimumWidth = 140, FixedHeight = 32, Clicked = chooseFile, ToolTip = "Pick a model from the asset browser" } );
		choice.Add( new TaButton( this, "From disk…", "file_open", chooseFromDisk, "Pick a .vmdl or a rigged .fbx (copied into the project; an FBX gets its textures, materials and a vmdl)", 32 ) );
		choice.AddStretchCell();
		var fresh = center.AddRow();
		fresh.Spacing = 8;
		fresh.AddStretchCell();
		fresh.Add( new TaButton( this, "New from Citizen", "person_add", newFromCitizen, "Start fresh: copies citizen.vmdl into your project", 32 ) );
		fresh.Add( new TaButton( this, "New from Citizen Human", "person_add", newFromCitizenHuman, "Start fresh: copies citizen_human_male.vmdl into your project", 32 ) );
		fresh.AddStretchCell();
		center.AddSpacingCell( 8 );
		center.Add( new DownloadStrip( this, flow ) { FixedWidth = 560 } );
		row.AddStretchCell();
		Layout.AddStretchCell();
	}

	public override void OnDragHover( DragEvent e )
	{
		var valid = TaDrop.Paths( e.Data, TaDrop.ModelExtensions ).Count > 0;
		_hover = valid ? 1 : -1;
		if ( valid )
			e.Action = DropAction.Link;
		Update();
	}

	public override void OnDragDrop( DragEvent e )
	{
		_hover = 0;
		var paths = TaDrop.Paths( e.Data, TaDrop.ModelExtensions );
		if ( paths.Count > 0 )
		{
			e.Action = DropAction.Link;
			_open( paths );
		}
		Update();
	}

	public override void OnDragLeave()
	{
		_hover = 0;
		Update();
	}

	protected override void OnPaint()
	{
		Paint.Antialiasing = true;
		Paint.SetPen( _hover == 1 ? TaStyle.Accent : _hover < 0 ? Theme.Red : Theme.ControlBackground.Lighten( .2f ), _hover == 0 ? 1 : 2 );
		Paint.SetBrush( _hover == 1 ? TaStyle.Accent.WithAlpha( .06f ) : _hover < 0 ? Theme.Red.WithAlpha( .05f ) : Paint.HasMouseOver ? Theme.ControlBackground.Lighten( .3f ) : Theme.ControlBackground );
		Paint.DrawRect( LocalRect.Shrink( 1 ), 6 );
	}

	sealed class BigIcon : Widget
	{
		public BigIcon( Widget parent ) : base( parent )
		{
			FixedHeight = 56;
		}

		protected override void OnPaint()
		{
			Paint.SetPen( Theme.TextLight );
			Paint.DrawIcon( new Rect( (Width - 48) * .5f, 4, 48, 48 ), "directions_run", 48 );
		}
	}
}

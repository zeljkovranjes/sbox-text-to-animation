using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TextToAnimation.EditorTools.Engine;
using TextToAnimation.EditorTools.Session;

namespace TextToAnimation.EditorTools.UI;

/// <summary>Pick existing animations to bring into the workspace (from this model, or another one).</summary>
public sealed class ImportDialog : Dialog
{
	readonly EditorSession _session;
	readonly Widget _list;
	readonly LineEdit _filter;
	readonly Label _source;
	readonly Button _import;
	readonly HashSet<string> _checked = new( StringComparer.Ordinal );
	List<SequenceInfo> _sequences;
	Model _otherModel;

	public ImportDialog( Widget parent, EditorSession session ) : base( parent )
	{
		_session = session;
		Window.WindowTitle = "Import existing animation";
		Window.SetWindowIcon( "download" );
		Window.SetModal( true, true );
		Window.Size = new Vector2( 520, 600 );
		Layout = Layout.Column();
		Layout.Margin = 10;
		Layout.Spacing = 8;

		var card = Layout.Add( new TaCard( this ), 1 );
		var header = card.Header( "download", "Animations" );
		header.AddStretchCell();
		header.Add( new TaButton( card, "From another model…", "view_in_ar", PickOtherModel, "Import animations from a different model that shares bone names" ) );
		_source = card.Layout.Add( TaStyle.Muted( new Label( "", card ) { WordWrap = true }, small: true ) );
		_filter = card.Layout.Add( TaStyle.Framed( new LineEdit( card ) { PlaceholderText = "Filter…" } ) );
		_filter.TextEdited += _ => Rebuild();
		var scroll = card.Layout.Add( new ScrollArea( card ), 1 );
		scroll.HorizontalScrollbarMode = ScrollbarMode.Off;
		scroll.SetStyles( "background-color: transparent;" );
		_list = new Widget( scroll ) { Layout = Layout.Column() };
		_list.Layout.Spacing = 2;
		_list.Layout.Margin = new Sandbox.UI.Margin( 0, 0, 8, 0 );
		scroll.Canvas = _list;

		var footer = Layout.AddRow();
		footer.Spacing = 8;
		footer.Add( TaStyle.Muted( new Label( "Imported copies are edited in the workspace; the model is unchanged until you save.", this ) { WordWrap = true }, small: true ), 1 );
		footer.Add( new TaButton( this, "Cancel", null, Close, null, 28 ) );
		_import = footer.Add( new Button.Primary( "Import" ) { Icon = "download", Tint = TaStyle.Accent, FixedHeight = 28 } );
		_import.Clicked = () => _ = ImportAsync();

		ShowModel( null );
	}

	void ShowModel( Model other )
	{
		_otherModel = other;
		_checked.Clear();
		_sequences = other is null
			? _session.Sequences
			: other.AnimationNames.Select( n => new SequenceInfo( n, false, "", false, false, false ) ).ToList();
		_source.Text = other is null
			? $"From {_session.ModelAsset?.Name}. \"In model\" animations can later be replaced in place; inherited ones come from its base model."
			: $"From {other.ResourcePath}. Bones are matched by name.";
		Rebuild();
	}

	void Rebuild()
	{
		_list.Layout.Clear( true );
		var filter = _filter.Text?.Trim() ?? "";
		var shown = _sequences.Where( s => filter.Length == 0 || s.Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) ).ToList();
		if ( shown.Count == 0 )
			_list.Layout.Add( TaStyle.Muted( new Label( _sequences.Count == 0 ? "This model has no animations." : "Nothing matches the filter.", _list ) ) );
		foreach ( var seq in shown.Take( 400 ) )
		{
			var row = _list.Layout.AddRow();
			row.Spacing = 6;
			var name = seq.Name;
			var box = TaStyle.Check( row, name, _checked.Contains( name ), v => { if ( v ) _checked.Add( name ); else _checked.Remove( name ); UpdateButton(); } );
			row.AddStretchCell();
			if ( _otherModel is null )
				row.Add( new TaPill( _list, seq.DefinedInModel ? "IN MODEL" : "INHERITED", seq.DefinedInModel ? TaStyle.Accent : Theme.TextLight ) );
		}
		_list.Layout.AddStretchCell();
		UpdateButton();
	}

	void UpdateButton()
	{
		_import.Enabled = _checked.Count > 0;
		_import.Text = _checked.Count <= 1 ? "Import" : $"Import {_checked.Count}";
	}

	void PickOtherModel()
	{
		var picker = AssetPicker.Create( this, AssetType.Model );
		picker.Window.Title = "Import animations from…";
		picker.OnAssetPicked = assets =>
		{
			var asset = assets.FirstOrDefault();
			if ( asset is null ) return;
			var model = Model.Load( asset.Path );
			if ( model is null || model.IsError ) return;
			var matched = _session.Rig.Skeleton.Bones.Count( b => model.Bones.GetBone( b.Name ) is not null );
			if ( matched < _session.Rig.Skeleton.Count * 0.5f )
			{
				_session.SetStatus( $"{asset.Name} shares only {matched} of {_session.Rig.Skeleton.Count} bones with this model - its animations wouldn't fit.", Tone.Amber );
				return;
			}
			ShowModel( model );
		};
		picker.Show();
	}

	async Task ImportAsync()
	{
		var names = _sequences.Select( s => s.Name ).Where( _checked.Contains ).ToList();
		var other = _otherModel;
		Close();
		_session.SetBusy( true, "Importing" );
		try
		{
			foreach ( var name in names )
			{
				_session.SetStatus( $"Importing {name}…" );
				await _session.ImportSequenceAsync( name, other );
			}
			_session.SetStatus( names.Count == 1 ? $"Imported \"{names[0]}\"." : $"Imported {names.Count} animations.", Tone.Accent );
		}
		catch ( Exception e )
		{
			_session.SetStatus( $"Import failed: {e.Message}", Tone.Red );
		}
		finally
		{
			_session.SetBusy( false );
		}
	}
}

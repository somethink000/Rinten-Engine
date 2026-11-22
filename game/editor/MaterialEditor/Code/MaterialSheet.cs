using Rinten.UI;
using System.Collections.Generic;
using System.Linq;

using UILabel = Rinten.UI.Label;

namespace Editor.Materials;

//The dock is the only widget here; everything in it is panels. A dock slot has to be a widget because that is what the dock manager arranges - see PanelDockHost - and nothing below it needs to be.
/// <summary>The dock down the left of the material editor: every control the material has.</summary>
public sealed class MaterialSheetDock : PanelDockHost
{
	public MaterialSheetPanel Sheet { get; }

	public MaterialSheetDock( IMaterialDocument document ) : base( null )
	{
		Name = "Material";
		WindowTitle = "Material";
		SetWindowIcon( "tune" );
		MinimumWidth = 300;

		Sheet = new MaterialSheetPanel( document );

		SetContent( Sheet );
	}
}

/// <summary>Material controls as cards: header, ungated block, then one card per feature; partial rebuilds</summary>
[StyleSheet( "MaterialSheet.cs.scss" )]
public sealed class MaterialSheetPanel : EditorPanel
{
	readonly IMaterialDocument _document;

	readonly Panel _scroll;
	readonly Panel _head;
	readonly UILabel _title;
	readonly UILabel _path;
	readonly ToolbarButton _reveal;
	readonly ToolbarButton _recompile;
	readonly Panel _cards;
	readonly UILabel _note;

	//Where the shader and the material's own settings go: the first card, or the asset's header in its place.
	Panel _fields;

	MaterialProperties _properties;
	Inspectors.ControlSheet _shader;
	SurfaceSection _surface;
	readonly List<FeatureCard> _features = new();

	//What the features were worth when their cards were last built.
	string _featureSignature;

	public MaterialSheetPanel( IMaterialDocument document )
	{
		_document = document;

		AddClass( "material-sheet" );

		_scroll = AddChild<Panel>( "scroll" );

		//The first card, a GameObject's name and tags in its inspector: what is open, the file's folder and a compile from
		//scratch. No Save - a material is written out in the frame it was edited in, so there is nothing here for one to do.
		_head = _scroll.AddChild<Panel>( "inspector-head" );

		var top = _head.AddChild<Panel>( "top-row" );
		var words = top.AddChild<Panel>( "words" );
		_title = words.AddChild<UILabel>( "title" );
		_path = words.AddChild<UILabel>( "path" );
		_title.Tokenize = false;
		_path.Tokenize = false;

		_reveal = top.AddChild<ToolbarButton>();
		_reveal.Icon = "folder_open";
		_reveal.ToolTip = "Show in Explorer";
		_reveal.OnPressed = _document.Reveal;

		_recompile = top.AddChild<ToolbarButton>();
		_recompile.Icon = "restart_alt";
		_recompile.ToolTip = "Full Recompile";
		_recompile.OnPressed = _document.Recompile;

		_fields = _head;

		_cards = _scroll.AddChild<Panel>( "cards" );

		_note = _scroll.AddChild<UILabel>( "note" );
		_note.Text = "Pick a material in the browser.";

		_document.Opened += Rebuild;
		_document.Changed += OnChanged;

		Rebuild();
	}

	public override void OnDeleted()
	{
		_document.Opened -= Rebuild;
		_document.Changed -= OnChanged;

		base.OnDeleted();
	}

	//Start again from the definition: a material was opened, or put back to an earlier version.
	void Rebuild()
	{
		_shader?.Delete( true );
		_surface?.Delete( true );
		_shader = null;
		_surface = null;
		_properties = null;
		_cards.DeleteChildren( true );
		_features.Clear();

		var open = _document.IsOpen;

		_title.Text = open ? _document.Asset.Name : "Material";
		_path.Text = open ? _document.Asset.Path : "";
		_note.SetClass( "hidden", open );
		_reveal.Disabled = !open;
		_recompile.Disabled = !_document.CanRecompile;

		if ( !open ) return;

		_properties = new MaterialProperties( _document.Definition, _document.Serialized, _document.NoteEdited );
		_properties.Refresh();

		//The shader and the material's own settings in the first card, under its name; nothing the shader declares stands there.
		_shader = _fields.AddChild( new Inspectors.ControlSheet() );
		_shader.AddPropertyRow( _properties.Shader );

		_surface = _fields.AddChild( new SurfaceSection( _properties ) );
		_surface.Rebuild();

		BuildCards();
	}

	/// <summary>Stand under an asset's header instead of a first card of its own: the buttons go beside the file's name there, the shader and the material's settings under it - see <see cref="Inspectors.IInspectorHead"/>.</summary>
	public void FillHead( Inspectors.AssetHeaderPanel head )
	{
		head.Tools.AddChild( _reveal );
		head.Tools.AddChild( _recompile );

		_fields = head.Fields;
		if ( _shader is not null ) _fields.AddChild( _shader );
		if ( _surface is not null ) _fields.AddChild( _surface );

		_head.AddClass( "hidden" );
	}

	//A card for everything the shader reads: what nothing switches off, then every feature it declares.
	void BuildCards()
	{
		_cards.DeleteChildren( true );
		_features.Clear();

		foreach ( var block in _properties.SurfaceBlocks() )
		{
			var card = _cards.AddChild( MaterialCard.Make( block.Title, MaterialCard.Glyph( block.Slots.Count > 0 ) ) );
			MaterialCard.Fill( card.Body, _properties, block );
			card.Refold();
		}

		foreach ( var (feature, property) in _properties.Features() )
			_features.Add( _cards.AddChild( new FeatureCard( _properties, feature, property ) ) );

		_featureSignature = _properties.FeatureSignature();
	}

	void OnChanged( MaterialEditKind kind )
	{
		if ( _properties is null ) return;

		switch ( kind )
		{
			//A different shader declares different features, which decide which maps there are, which decide where the numbers
			//stand - so all of it is built again, in that order. The shader row itself stays: it is the control being used.
			case MaterialEditKind.Shader:
				_properties.Refresh();
				_surface.Rebuild();
				BuildCards();
				break;

			//Feature or map edits rebuild only cards whose feature value changed, so pickers stay open
			case MaterialEditKind.Feature:
			case MaterialEditKind.Map:
			{
				var signature = _properties.FeatureSignature();
				if ( signature == _featureSignature ) break;

				_featureSignature = signature;
				_properties.Refresh();

				foreach ( var card in _features )
					card.Refresh();

				break;
			}
		}
	}
}

//What goes into the material's cards - see Inspectors.InspectorCard.
static class MaterialCard
{
	/// <summary>A card of the material's, folded under its title for the project.</summary>
	public static Inspectors.InspectorCard Make( string title, string icon, SerializedProperty toggle = null )
	{
		var card = new Inspectors.InspectorCard( title, icon, toggle, $"material.{title}" );
		card.AddClass( "material-card" );
		return card;
	}

	/// <summary>The glyph in a card's bar: a picture where the card is over maps or masks, sliders where it has numbers, a switch where the switch is all there is (two-sided lighting) - so the kinds read apart down the sheet before anything is opened.</summary>
	public static string Glyph( bool maps, bool numbers = true ) => maps ? "texture" : numbers ? "tune" : "toggle_on";

	/// <summary>A block's pictures and numbers into a card: each picture with the numbers beside the first, or the numbers on their own where it has none.</summary>
	public static void Fill( Panel into, MaterialProperties properties, MaterialProperties.Block block )
	{
		if ( block.Slots.Count == 0 )
		{
			if ( block.Numbers.Count == 0 ) return;

			var sheet = into.AddChild( new Inspectors.ControlSheet() );

			foreach ( var property in block.Numbers )
				sheet.AddPropertyRow( property );

			return;
		}

		for ( var i = 0; i < block.Slots.Count; i++ )
		{
			var slot = block.Slots[i];

			if ( properties.Path( slot ) is not { } path ) continue;

			into.AddChild( new MaterialMapPanel( slot.Title, path,
				i == 0 ? block.Numbers : new List<SerializedProperty>(), slot.IsColour ) );
		}
	}
}

//One feature of the shader as a card: its switch in the bar, and what it is about under it while it is on. A feature with more than two values - a
//list rather than a tick - has no switch in the bar; its list is the first row of the card, and what the value it is on is about stands under it.
sealed class FeatureCard : Inspectors.InspectorCard
{
	readonly MaterialProperties _properties;
	readonly MaterialDefinition.ShaderFeature _feature;

	//What the feature was on when the card's body was last built.
	int _built;

	public FeatureCard( MaterialProperties properties, MaterialDefinition.ShaderFeature feature, SerializedProperty property )
		: base( feature.Title, MaterialCard.Glyph( properties.HasMaps( feature ), properties.HasNumbers( feature ) ), IsTick( feature ) ? property : null,
			$"material.{feature.Title}" )
	{
		AddClass( "material-card" );

		_properties = properties;
		_feature = feature;

		if ( !IsTick( feature ) )
		{
			var list = new Inspectors.ControlSheet();
			list.AddPropertyRow( property );

			Content.AddChild( list );
			Content.SetChildIndex( list, 0 );
		}

		Build();
	}

	static bool IsTick( MaterialDefinition.ShaderFeature feature ) => feature.Least == 0 && feature.Most == 1;

	/// <summary>Build the body again where the feature is not what it was when it was built.</summary>
	public void Refresh()
	{
		if ( _properties.FeatureValue( _feature.Name ) == _built ) return;

		Build();
	}

	void Build()
	{
		Body.DeleteChildren( true );

		_built = _properties.FeatureValue( _feature.Name );

		MaterialCard.Fill( Body, _properties, _properties.FeatureBlock( _feature ) );
		Refold();
	}
}

//One part of the sheet, built from the declarations and hidden while there is nothing in it.
abstract class MaterialSection : Panel
{
	protected MaterialProperties Properties { get; }

	protected MaterialSection( MaterialProperties properties, string name )
	{
		Properties = properties;

		AddClass( "section column" );
		AddClass( name );
	}

	/// <summary>Empty it and build it again from what the declarations say now.</summary>
	public void Rebuild()
	{
		DeleteChildren( true );

		SetClass( "hidden", !Build() );
	}

	//Build the section's contents; false when there was nothing to show.
	protected abstract bool Build();
}

//Material-level settings (tint, surface) in the first card; shader numbers live in feature cards
sealed class SurfaceSection : MaterialSection
{
	//Not "surface": design.scss's .surface is the window's floor, and the material's own fields came out on a black band across the first card.
	public SurfaceSection( MaterialProperties properties ) : base( properties, "material-own" ) { }

	protected override bool Build()
	{
		//Row by row rather than handed the object: a sheet given the object files each property under its [Group], and a heading over a single
		//row - Surface over the physics surface - is a box round a field with nothing to group. These are the first card's fields, plain.
		var rows = Properties.Serialized
			.Where( x => !x.IsMethod && Properties.IsRest( x ) && !x.HasAttribute<HideAttribute>() )
			.OrderBy( x => x.Order )
			.ThenBy( x => x.SourceFile )
			.ThenBy( x => x.SourceLine )
			.ToList();

		if ( rows.Count == 0 ) return false;

		var sheet = AddChild( new Inspectors.ControlSheet() );

		foreach ( var property in rows )
			sheet.AddPropertyRow( property );

		return true;
	}
}

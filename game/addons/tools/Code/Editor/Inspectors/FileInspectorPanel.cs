using Rinten.UI;
using System;
using System.Linq;

namespace Editor.Inspectors;

/// <summary>Inspector page for widget editors found by extension ([CanEdit("asset:ext")]), saved here</summary>
[StyleSheet( "GameResourceInspectorPanel.cs.scss" )]
public sealed class FileInspectorPanel : InspectorPanel
{
	private readonly Asset _asset;
	private readonly GameResource _resource;
	private readonly bool _readOnly;

	/// <summary>The page for what was picked, or nothing where no widget claims its kind.</summary>
	public static FileInspectorPanel For( SerializedObject so )
	{
		if ( so is null ) return null;

		var assets = so.Targets.OfType<Asset>().ToArray();
		if ( assets.Length == 0 ) return null;

		var extension = assets[0].AssetType?.FileExtension;
		if ( string.IsNullOrEmpty( extension ) ) return null;

		//One kind for the lot, or there is no one widget to ask.
		if ( assets.Any( x => x.AssetType?.FileExtension != extension ) ) return null;

		if ( CanEditAttribute.CreateEditorFor( $"asset:{extension.ToLowerInvariant()}" ) is not { } widget ) return null;

		if ( widget is not IAssetInspector inspector )
		{
			//Claims the kind and takes nothing: the empty scene inspector is one.
			widget.Destroy();
			return null;
		}

		try
		{
			if ( assets.Length == 1 ) inspector.SetAsset( assets[0] );
			else if ( !inspector.SetAssets( assets ) )
			{
				widget.Destroy();
				return null;
			}
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"{widget.GetType().Name} would not take {assets[0].Path}" );
			widget.Destroy();
			return null;
		}

		return new FileInspectorPanel( so, assets[0], assets.Length == 1, widget );
	}

	private FileInspectorPanel( SerializedObject so, Asset asset, bool single, Widget widget ) : base( so )
	{
		AddClass( "file-inspector column" );

		_asset = asset;
		_readOnly = ResourceEdits.IsReadOnly( asset );

		//Only one asset's resource is written back: a widget editing several at once writes them itself, as the sound settings do.
		if ( single && asset.AssetType is { IsGameResource: true } && asset.TryLoadResource<GameResource>( out var resource ) )
			_resource = resource;

		//On a card of its own, as everything under an asset's header is; the widget is drawn as it always was, inside it.
		//In the page's scroll, so an editor taller than the dock can be scrolled through
		var card = AddChild<Panel>( "scroll" ).AddChild( new InspectorCard( asset.AssetType?.FriendlyName ?? "File", "tune" ) );
		var host = card.Body.AddChild<WidgetPanel>();
		host.AddClass( "resource-editor-host" );
		host.WidthFromLayout = true;
		host.Widget = widget;
		card.Refold();
	}

	public override void Tick()
	{
		base.Tick();

		if ( _readOnly || _resource is not { HasUnsavedChanges: true } ) return;

		//Writing clears the flag - see GameResource.SaveToDisk - so this is once per edit rather than once per frame.
		ResourceEdits.Write( _asset, _resource );
	}
}

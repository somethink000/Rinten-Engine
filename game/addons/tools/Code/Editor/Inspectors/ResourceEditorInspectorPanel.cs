using Rinten.UI;
using System;
using System.Linq;

namespace Editor.Inspectors;

/// <summary>The inspector dock's page for a resource whose editor is still a widget.</summary>
[StyleSheet( "GameResourceInspectorPanel.cs.scss" )]
public sealed class ResourceEditorInspectorPanel : InspectorPanel
{
	private readonly Asset _asset;
	private readonly GameResource _resource;
	private readonly IResourceEditor _editor;

	//Whether the file may be written at all - a cloud package cannot.
	private readonly bool _readOnly;

	//An edit is waiting to be written, at the end of a tick rather than mid-edit.
	private bool _saveWanted;

	/// <summary>The page for one asset, or nothing where its resource has no widget editor either.</summary>
	public static ResourceEditorInspectorPanel For( SerializedObject so )
	{
		if ( so is null || so.IsMultipleTargets ) return null;
		if ( so.Targets.OfType<Asset>().FirstOrDefault() is not { } asset ) return null;
		if ( !asset.TryLoadResource<GameResource>( out var resource ) ) return null;

		if ( EditorFor( resource.GetType() ) is not { } editor ) return null;

		return new ResourceEditorInspectorPanel( so, asset, resource, editor );
	}

	//The editor registered for a resource type, or for the nearest type it derives from that has one.
	private static IResourceEditor EditorFor( Type resourceType )
	{
		var generic = typeof( BaseResourceEditor<> );

		for ( var type = resourceType; type is not null && typeof( Resource ).IsAssignableFrom( type ); type = type.BaseType )
		{
			var found = EditorTypeLibrary.GetTypes( generic.MakeGenericType( type ) ).FirstOrDefault();

			if ( found is null ) continue;

			if ( found.Create<Widget>( null ) is IResourceEditor editor ) return editor;
		}

		return null;
	}

	private ResourceEditorInspectorPanel( SerializedObject so, Asset asset, GameResource resource, IResourceEditor editor )
		: base( so )
	{
		AddClass( "resource-editor-inspector column" );

		_asset = asset;
		_resource = resource;
		_editor = editor;

		_readOnly = ResourceEdits.IsReadOnly( asset );

		try
		{
			_editor.Initialize( asset, resource );
			_editor.Changed += _ => _saveWanted = true;
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"{editor.GetType().Name} would not initialize" );
			return;
		}

		//On a card of its own, as everything under an asset's header is; the widget is drawn as it always was, inside it.
		//In the page's scroll, so an editor taller than the dock can be scrolled through
		var card = AddChild<Panel>( "scroll" ).AddChild( new InspectorCard( resource.GetType().Name.ToTitleCase(), "tune" ) );
		var host = card.Body.AddChild<WidgetPanel>();

		//The column says how wide, the widget says how tall. Left to size itself both ways it took its width from an inspector widget's size
		//hint, which is a thousand pixels rather than a measurement, and the page stood in the dock squeezed into a shape nothing had asked for.
		host.AddClass( "resource-editor-host" );
		host.WidthFromLayout = true;
		host.Widget = editor as Widget;
		card.Refold();
	}

	public override void Tick()
	{
		base.Tick();

		if ( _readOnly ) return;

		//An editor that edits the resource object straight has nothing to raise Changed from, so the resource's own flag is asked
		//as well. Writing it clears the flag - see GameResource.SaveToDisk - so this is once per edit rather than once per frame.
		if ( !_saveWanted && _resource is { HasUnsavedChanges: true } ) _saveWanted = true;

		if ( !_saveWanted ) return;

		_saveWanted = false;

		Save();
	}

	//Writes the resource out and tells the editor it was written.
	private void Save()
	{
		ResourceEdits.Write( _asset, _resource );
		_editor?.SavedToDisk();
	}

	//The editor was made for this page; WidgetPanel only borrows it
	public override void OnDeleted()
	{
		base.OnDeleted();

		if ( _editor is Widget widget && widget.IsValid() ) widget.Destroy();
	}
}

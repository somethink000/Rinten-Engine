using System.Linq;

namespace Editor.Materials;

/// <summary>The material's controls in the inspector dock, for a .mat picked in the browser.</summary>
[Inspector( typeof( MaterialDefinition ) )]
public sealed class MaterialInspectorPanel : InspectorPanel, Inspectors.IInspectorHead
{
	readonly DocumentLease<MaterialDocument> _lease;
	readonly MaterialSheetPanel _sheet;

	public MaterialInspectorPanel( SerializedObject so ) : base( so )
	{
		AddClass( "material-inspector column" );

		var definition = so.Targets.OfType<MaterialDefinition>().FirstOrDefault();
		var asset = definition is null ? null : AssetSystem.FindByPath( definition.ResourcePath );

		_lease = asset is null ? null : ResourceDocuments.Acquire<MaterialDocument>( asset );

		_sheet = AddChild( new MaterialSheetPanel( _lease?.Document ?? MaterialDocument.Empty ) );
	}

	public void FillHead( Inspectors.AssetHeaderPanel head ) => _sheet.FillHead( head );

	public override void OnDeleted()
	{
		base.OnDeleted();

		//The document closes, and writes what is unsaved, once nobody else holds it - see ResourceDocuments.
		_lease?.Dispose();
	}
}

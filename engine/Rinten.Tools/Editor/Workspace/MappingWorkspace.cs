using System;

namespace Editor;

//The mapping workspace: the same scenes as General, laid out for building them.
internal sealed class MappingWorkspace : SceneWorkspace
{
	public MappingWorkspace( Widget parent ) : base( parent, "Mapping" )
	{
		//Tool settings, tree, library and inspector as tabs of one left column; settings shows first
		var settings = OpenPanel( "Tool Settings", "construction", "MappingToolPanel", DockArea.Left );

		var hierarchy = OpenPanel( "Hierarchy", "list", "SceneTreeWidget", DockArea.Left );

		//Asset browser as a tab, made by name (addon type) with the tree alone
		Widget assets;
		try
		{
			assets = EditorTypeLibrary.Create<Widget>( "TreeAssetBrowser", new object[] { this, true } );
		}
		catch ( Exception e )
		{
			Log.Warning( e, "Mapping: TreeAssetBrowser would not open" );
			assets = null;
		}

		OpenPanel( "Asset Browser", "folder_open", assets, DockArea.Left );

		//Full name: a bare "Inspector" can also resolve to the sprite editor's inspector
		OpenPanel( "Inspector", "manage_search", "Editor.Inspector", DockArea.Left );

		if ( hierarchy is not null )
		{
			//Wider than the rest of the window's own columns: a tool's settings are property sheets and picked lists, the same things
			//the inspector puts a whole side of the window to, and the room this used to have was sized for a tree of names alone.
			Docks.SetSplitterProportions( hierarchy, 0.22f, 0.78f );
		}

		settings?.SetAsCurrentTab();
	}
}

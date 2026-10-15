using Editor;
using Rinten;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Editor.TreeBrowser;

/// <summary>Tree asset browser dock with multiple side-by-side or tabbed panels.</summary>
[Dock( "Editor", "Asset Browser", "folder_open", DockArea.Bottom )]
public partial class TreeAssetBrowser : TreeBrowserWidget
{
	static readonly List<TreeAssetBrowser> _open = new();

	public static event Action<Asset> AssetHighlighted;

	internal static void RaiseAssetHighlighted( Asset asset ) => AssetHighlighted?.Invoke( asset );

	public TreeAssetBrowser( Widget parent ) : this( parent, false ) { }

	/// <summary>A browser with the tree and a grid, or the tree alone inside an editor's own window.</summary>
	public TreeAssetBrowser( Widget parent, bool treeOnly ) : base( parent, TreeBrowserOptions.Dock )
	{
		if ( treeOnly )
			Root.AddTreePanel();
		else
			Root.CreateDefaultPanels();

		_open.Add( this );
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();
		_open.Remove( this );
	}

	public static void RefreshOpen()
	{
		foreach ( var browser in _open.ToArray() )
		{
			if ( browser.IsValid() )
				browser.RefreshAll();
		}
	}

	//The browsers were made before the project was open; now they show it. Opened on the project's
	//own folder, picked, so the grid beside the tree starts with its contents rather than empty.
	[Event( "project.opened" )]
	static void OnProjectOpened( EditorMainWindow window )
	{
		RefreshOpen();

		if ( Project.Current?.GetAssetsPath() is not { Length: > 0 } assets ) return;

		foreach ( var browser in _open.ToArray() )
		{
			if ( browser.IsValid() )
				browser.Root.NavigateToFolder( assets );
		}
	}

	public static void OpenTo( Asset asset )
	{
		if ( asset is null ) return;

		RaiseDock();
		Reveal( asset );
	}

	/// <summary>Opens the dock at an asset list entry, which may be a file with no asset behind it.</summary>
	public static void OpenTo( AssetEntry entry )
	{
		if ( entry is null ) return;

		if ( entry.Asset is { } asset )
		{
			OpenTo( asset );
			return;
		}

		RaiseDock();
		RevealPath( entry.AbsolutePath );
	}

	public static void Reveal( Asset asset )
	{
		if ( asset is null ) return;

		RevealPath( asset.GetSourceFile( true ) ?? asset.AbsolutePath );
	}

	static void RaiseDock()
	{
		EditorWindow.DockManager.SetDockState( "Asset Browser", true );
		EditorWindow.DockManager.RaiseDock( "Asset Browser" );
	}

	static void RevealPath( string path )
	{
		if ( string.IsNullOrEmpty( path ) ) return;

		foreach ( var browser in _open.ToArray() )
		{
			if ( !browser.IsValid() ) continue;

			foreach ( var panel in browser.Panels )
				panel.Reveal( path );
		}
	}
}

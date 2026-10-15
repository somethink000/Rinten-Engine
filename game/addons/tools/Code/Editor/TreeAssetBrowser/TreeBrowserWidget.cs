using Editor;
using System;
using System.Collections.Generic;

namespace Editor.TreeBrowser;

//The dock lives one class down, in TreeAssetBrowser, because its [Dock] attribute reaches subclasses: anything deriving from it is offered in the View menu and built with a one-widget constructor it does not have.
/// <summary>An asset browser surface: the toolbar, the tree and the grid, with no dock of its own.</summary>
public class TreeBrowserWidget : PanelWidget
{
	//The panel this widget is made of.
	protected TreeAssetBrowserPanel Root { get; }

	/// <summary>Mode this browser runs in.</summary>
	public TreeBrowserOptions Options { get; }

	public Action<Asset> OnAssetSelected;
	public Action<Asset> OnAssetHighlighted;
	public Action<Asset> OnAssetActivated;
	public Action<IReadOnlyList<Asset>> OnSelectionChanged;
	public Action<string> OnFileSelected;
	public Action<string> OnFolderSelected;

	/// <summary>The tree and grid panels currently open.</summary>
	public IEnumerable<IBrowserPanel> Panels => Root.Panels;

	protected TreeBrowserWidget( Widget parent, TreeBrowserOptions options ) : base( parent )
	{
		Options = options ?? TreeBrowserOptions.Dock;

		HorizontalSizeMode = SizeMode.Flexible;
		VerticalSizeMode = SizeMode.Flexible;

		Root = new TreeAssetBrowserPanel
		{
			Host = this,
			Options = Options,
			OnAssetSelected = asset => OnAssetSelected?.Invoke( asset ),
			OnAssetHighlighted = asset => OnAssetHighlighted?.Invoke( asset ),
			OnAssetActivated = asset => OnAssetActivated?.Invoke( asset ),
			OnSelectionChanged = assets => OnSelectionChanged?.Invoke( assets ),
			OnFileSelected = path => OnFileSelected?.Invoke( path ),
			OnFolderSelected = path => OnFolderSelected?.Invoke( path )
		};

		SetPanel( Root );

		WindowTitle = "Asset Browser";
		MinimumSize = new Vector2( 200, 160 );
	}

	public void NavigateToFile( string path ) => Root.NavigateToFile( path );

	/// <summary>Points the tree and every grid at a folder.</summary>
	public void NavigateToFolder( string path ) => Root.NavigateToFolder( path );

	/// <summary>Opens the folder an asset lives in and picks the asset out of it.</summary>
	public void FocusOnAsset( Asset asset ) => Root.FocusOnAsset( asset );

	/// <summary>Assets the user has picked out in this browser.</summary>
	public IReadOnlyList<Asset> GetSelectedAssets() => Root.GetSelectedAssets();

	/// <summary>Types into the search box on the user's behalf.</summary>
	public void SetSearch( string text ) => Root.SetSearch( text );

	public void RefreshAll() => Root.RefreshAll();

	[EditorEvent.Frame]
	void OnFrame()
	{
		Root.EnsurePanelsMounted();
		Root.SyncLayoutToggle();
		Root.FollowPlace();
	}
}

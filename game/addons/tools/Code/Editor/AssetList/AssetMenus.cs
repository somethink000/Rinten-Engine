using System.IO;

namespace Editor;

/// <summary>The selected assets an asset context menu acts on.</summary>
public struct AssetContextMenu
{
	/// <summary>List of selected assets when the context menu was opened. These are the assets context menu should be affecting.</summary>
	public List<AssetEntry> SelectedList;

	/// <summary>Position of the cursor on screen when the context menu was opened.</summary>
	public Vector2 ScreenPosition;

	/// <summary>The menu to add context menu options to.</summary>
	public Menu Menu;
}

/// <summary>The folder a folder context menu acts on.</summary>
public struct FolderContextMenu
{
	/// <summary>The folder we should be acting upon via context menu options.</summary>
	public DirectoryInfo Target;

	public Vector2 ScreenPosition;

	public Menu Menu;

	/// <summary>Clicked on empty space in a folder, not on a specific folder.</summary>
	public bool ThisFolder;

	/// <summary>The panel that opened the context menu.</summary>
	public object Context;
}

/// <summary>A styled button for use with <see cref="Menu.AddWidget"/></summary>
internal class MenuCheckableOption : Button
{
	public MenuCheckableOption() : base( null )
	{
		IsToggle = true;
		MinimumWidth = 196;
	}

	public MenuCheckableOption( AssetTagSystem.TagDefinition tag ) : this()
	{
		Text = tag.Title;
		IconPixmap = tag.IconPixmap;
	}

	public MenuCheckableOption( string tag ) : this()
	{
		Text = tag.ToTitleCase();
		IconPixmap = AssetTagSystem.GetTagIcon( tag );
	}

	Pixmap IconPixmap = null;
	public new void SetIcon( Pixmap icon )
	{
		IconPixmap = icon;
	}

	//Mimic Option stlying, because I can't get the button text/icon to reposition.
	protected override void OnPaint()
	{
		Paint.ClearPen();

		//Under the pointer a step of grey up, like every other option - it was filled with the accent.
		if ( Paint.HasMouseOver )
		{
			Paint.SetBrush( Theme.SurfaceLightBackground.Lighten( 0.2f ) );
			Paint.DrawRect( LocalRect );
		}
		else
		{
			Paint.SetBrush( Color.Parse( "#444748" ) ?? default );
			Paint.DrawRect( LocalRect );
		}


		if ( IsChecked )
		{
			//On: the picked plate under the whole option, not a bar of the accent.
			var rect = LocalRect;
			Paint.SetBrush( Theme.SelectedBackground );
			Paint.DrawRect( rect );
		}

		bool highlight = (Paint.HasMouseOver || IsChecked);

		var r = LocalRect.Shrink( 12, 0, 0, 0 );
		if ( IconPixmap != null )
		{
			Paint.Draw( r.Align( 16, TextFlag.LeftCenter ), IconPixmap, highlight ? 1.0f : 0.2f );
		}

		Paint.SetPen( highlight ? Color.White : Color.White.WithAlphaMultiplied( 0.4f ) );
		Paint.SetDefaultFont( 8 );
		Paint.DrawText( r.Shrink( 28, 0, 0, 0 ), Text, TextFlag.LeftCenter );
	}
}

internal class MenuTextOption : LineEdit
{
	protected override void OnKeyPress( KeyEvent e )
	{
		if ( e.Key == KeyCode.Return )
		{
			OnReturnPressed();
			e.Accepted = true; //Do not close context menu..
		}
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		e.Accepted = true; //Do not close context menu..
	}
}

internal class MenuWidgetOption : Widget
{
	public MenuWidgetOption( Widget parent = null ) : base( parent )
	{
		MinimumHeight = 32;
		Layout = Layout.Row();
		Layout.Margin = 4;
		Layout.Spacing = 4;
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		e.Accepted = true; //Do not close context menu..
	}

	protected override void OnPaint()
	{
		var bgClr = Color.FromRgb( 0x444748 );

		Paint.ClearPen();
		Paint.SetBrush( bgClr );
		Paint.DrawRect( LocalRect );
	}
}

//The asset and folder menu options every browser gets through asset.contextmenu and folder.contextmenu
internal static class AssetMenus
{
	#region Asset context menu

	[Event( "asset.nativecontextmenu" )]
	private static void OnNativeAssetContextMenu( Menu menu, Asset asset )
	{
		if ( asset is null )
			return;

		var ac = new AssetContextMenu
		{
			SelectedList = new List<AssetEntry>() { new AssetEntry( asset ) },
			ScreenPosition = Application.CursorPosition,
			Menu = menu
		};

		EditorEvent.Run( "asset.contextmenu", ac );
	}

	[Event( "asset.contextmenu", Priority = 1 )]
	private static void OnAssetContextMenu_OpenSection( AssetContextMenu e )
	{
		var entry = e.SelectedList.First();
		var count = e.SelectedList.Count;
		var asset = entry.Asset;

		if ( count > 0 )
		{
			if ( e.SelectedList.All( x => x.Asset is { CanOpenInEditor: true } ) )
			{
				e.Menu.AddOption( count == 1 ? "Open in Editor" : $"Open {count} in Editor(s)", "edit",
					() => e.SelectedList.ForEach( x => x.Asset.OpenInEditor() ) );
			}
			else if ( !asset?.IsProcedural ?? true )
			{
				if ( e.SelectedList.All( x => EditorUtility.IsCodeFile( x.FileInfo.FullName ) ) )
				{
					string editorName = CodeEditor.Title;
					e.Menu.AddOption( count == 1 ? $"Open in {editorName}" : $"Open {count} in {editorName}", "edit",
						() => e.SelectedList.ForEach( x => CodeEditor.OpenFile( x.FileInfo.FullName ) ) );
				}
				//A resource with no editor of its own is edited in the inspector, which a click already has
				//it in; there is nothing to open. Any other file goes to whatever the desktop opens it with.
				else if ( !e.SelectedList.Any( x => x.Asset?.AssetType is { IsGameResource: true } ) )
				{
					e.Menu.AddOption( count == 1 ? "Open" : $"Open {count} files", "open_in_new",
					() => e.SelectedList.ForEach( x => EditorUtility.OpenFolder( x.FileInfo.FullName ) ) );
				}
			}
		}

		if ( count == 1 )
		{
			e.Menu.AddOption( $"Go to Location", "shortcut", () => AssetBrowser.OpenTo( entry ) );
		}

		var absolutePath = asset?.AbsolutePath ?? entry.AbsolutePath.Replace( '\\', '/' ).ToLower();
		var relativePath = asset?.RelativePath ?? System.IO.Path.GetRelativePath( Project.Current.GetAssetsPath(), absolutePath ).Replace( '\\', '/' ).ToLower();

		if ( asset is null || !asset.IsProcedural )
		{
			e.Menu.AddOption( $"Show in Explorer", "drive_file_move", () => EditorUtility.OpenFileFolder( absolutePath ) );
		}

		if ( asset?.Package != null )
		{
			e.Menu.AddOption( "Show in Browser", "public", () => EditorUtility.OpenFolder( asset.Package.Url ) );
		}

		e.Menu.AddSeparator();

		if ( count == 1 )
		{
			e.Menu.AddOption( $"Copy Relative Path", "content_paste_go", action: () => EditorUtility.Clipboard.Copy( relativePath ) ).Enabled = !string.IsNullOrEmpty( relativePath );

			if ( asset is null || !asset.IsProcedural )
			{
				e.Menu.AddOption( $"Copy Absolute Path", "content_paste", action: () => EditorUtility.Clipboard.Copy( absolutePath ) );
			}

			e.Menu.AddSeparator();
		}
	}

	[Event( "asset.contextmenu", Priority = 50 )]
	private static void OnMeshFileAssetContext( AssetContextMenu e )
	{
		var meshes = e.SelectedList
			.Where( x => x.Asset is not null && x.AssetType == AssetType.Mesh )
			.Select( x => x.Asset )
			.ToList();

		if ( meshes.Count == 0 )
			return;

		var label = meshes.Count == 1 ? "Create Model.." : $"Create {meshes.Count} Models..";
		e.Menu.AddOption( label, "open_in_new", () => _ = new CreateModelFromMeshDialog( meshes ) );
	}

	static void RebuildTagMenu( Menu tag_menu, List<AssetEntry> entries )
	{
		tag_menu.Clear();

		//Registered tags
		foreach ( var tag in AssetTagSystem.All.OrderBy( x => x.Title ) )
		{
			if ( tag.AutoTag ) continue;

			var tagToggle = new MenuCheckableOption( tag );
			tagToggle.IsChecked = entries.All( x => x.Asset.Tags.Contains( tag.Tag ) );
			tagToggle.Toggled = () =>
			{
				foreach ( var entry in entries )
				{
					entry.Asset.Tags.Set( tag.Tag, tagToggle.IsChecked );
				}
			};
			tag_menu.AddWidget( tagToggle );
		}

		tag_menu.AddSeparator();

		//Ability to add any tag
		var tagAddW = new MenuWidgetOption();
		tagAddW.ContentMargins = 4;

		var tagAdd = new MenuTextOption();
		tagAdd.PlaceholderText = "Add New Tag..";
		tagAdd.MinimumHeight = 32 - 8;
		tagAdd.ReturnPressed += () =>
		{
			var tagList = tagAdd.Text.Split( new[] { ',', ' ' } );

			foreach ( var tag in tagList )
			{
				entries.ForEach( x => x.Asset.Tags.Add( tag ) );
				RebuildTagMenu( tag_menu, entries );
			}
		};

		tagAddW.Layout.Add( tagAdd );

		tag_menu.AddWidget( tagAddW );

		tagAdd.Focus();
	}

	[Event( "asset.contextmenu", Priority = 150 )]
	private static void OnAssetContextMenu_Tags( AssetContextMenu e )
	{
		var assets = e.SelectedList.Where( x => x.Asset is not null ).ToList();
		if ( assets.Count <= 0 ) return;
		if ( assets.Any( x => x.Asset?.IsProcedural ?? false ) ) return;

		e.Menu.AddSeparator();

		if ( assets.Any( x => x.Asset.Package != null ) ) return; //No tags for cloud assets.

		var tag_menu = e.Menu.AddMenu( "Tags", "bookmark" );

		RebuildTagMenu( tag_menu, assets );
	}

	[Event( "asset.contextmenu", Priority = 100 )]
	private static void OnAssetContextMenu_CopySection( AssetContextMenu e )
	{
		if ( e.SelectedList.Count <= 0 ) return;

		var entry = e.SelectedList.First();
		var count = e.SelectedList.Count;
		var asset = entry.Asset;

		e.Menu.AddSeparator();

		if ( asset is not null )
		{
			e.Menu.AddSeparator();

			if ( asset.CanRecompile || asset.AssetType == AssetType.Shader )
			{
				e.Menu.AddOption( count > 1 ? $"Recompile {count} Assets" : "Recompile", "restart_alt",
					() => e.SelectedList.ForEach( x => x.Asset.Compile( true ) ) );
			}

			e.Menu.AddOption( count > 1 ? $"Regenerate {count} Thumbnails" : "Regenerate Thumbnail", "collections",
				() => e.SelectedList.ForEach( x => x.Asset.RebuildThumbnail( true ) ) );

			e.Menu.AddOption( count > 1 ? $"Dump {count} Thumbnails" : "Dump Thumbnail", "wallpaper",
				() => e.SelectedList.ForEach( x => x.Asset.DumpThumbnail() ) );
		}
	}

	#endregion

	#region Folder context menu

	static void BuildAllIconsR( List<Asset> assets )
	{
		Dialog.AskConfirm( () =>
		{
			Log.Warning( $"Rebuilding icons for {assets.Count} assets..." );
			assets.ForEach( x => x.RebuildThumbnail( true ) );
		}, $"You are about to rebuild thumbnail(s) for {assets.Count} asset(s). This can take a long time. Do you wish to continue?" );
	}

	static void RecompileAllAssetsR( List<Asset> assets )
	{
		Dialog.AskConfirm( () =>
		{
			Log.Warning( $"Recompiling {assets.Count} assets..." );
			assets.ForEach( x => x.Compile( true ) );
		}, $"You are about to recompile {assets.Count} asset(s). This can take a REALLY long time. Do you wish to continue?" );
	}

	[Event( "folder.contextmenu", Priority = 10 )]
	private static void OnFolderContextMenu_New( FolderContextMenu e )
	{
		var location = new DiskLocation( e.Target );
		if ( location.Type is AssetBrowser.LocationType.Assets or AssetBrowser.LocationType.Code or AssetBrowser.LocationType.Localization )
		{
			var menu = e.Menu.AddMenu( "New", "note_add" );
			CreateAsset.AddOptions( menu, location.Path );
			e.Menu.AddSeparator();
		}
	}

	[Event( "folder.contextmenu", Priority = 75 )]
	private static void OnFolderContextMenu_RecursiveStuff( FolderContextMenu e )
	{
		if ( e.ThisFolder ) return;

		e.Menu.AddSeparator();

		var assets = AssetSystem.All.Where( x => x.AbsolutePath.StartsWith( e.Target.FullName.Replace( '\\', '/' ), StringComparison.OrdinalIgnoreCase ) ).ToList();
		var assetCount = assets.Count;
		if ( assetCount > 0 )
		{
			e.Menu.AddOption( $"Recursively Rebuild {assetCount} Icons", "collections", () => BuildAllIconsR( assets ) );
			e.Menu.AddOption( $"Recursively Recompile {assetCount} Assets", "restart_alt", () => RecompileAllAssetsR( assets ) );
		}
	}

	[Event( "folder.contextmenu", Priority = 100 )]
	private static void OnFolderContextMenu_BottomSection( FolderContextMenu e )
	{
		//Only for folders that exist on disk
		if ( e.Target is null || !e.Target.Exists )
			return;

		e.Menu.AddSeparator();
		e.Menu.AddOption( "Show in Explorer", "folder_open", () => EditorUtility.OpenFolder( e.Target.FullName ) );
		e.Menu.AddOption( "Folder Metadata", "tune", () =>
		{
			var dialog = new FolderMetadataDialog( e.Target );
			dialog.Show();
		} );
	}

	#endregion
}

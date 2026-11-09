using System.IO;
using Rinten.Tiles;

namespace Editor.Tiles;

//"Create Tile Set" on pictures in the asset browser: the grid is guessed from the picture, see TileSheetGuess
internal static class TileSetMenu
{
	[Event( "asset.contextmenu", Priority = 50 )]
	public static void OnImageAssetContext( AssetContextMenu e )
	{
		if ( !e.SelectedList.All( x => x.AssetType == AssetType.ImageFile ) )
			return;

		var images = e.SelectedList.Select( x => x.Asset ).Where( x => x is not null ).ToList();
		if ( images.Count == 0 )
			return;

		var label = images.Count == 1 ? "Create Tile Set" : $"Create Tile Sets ({images.Count})";
		e.Menu.AddOption( label, "grid_view", action: () => CreateTileSets( images ) );
	}

	private static void CreateTileSets( List<Asset> images )
	{
		Asset last = null;

		foreach ( var image in images )
			last = CreateTileSet( image ) ?? last;

		if ( last is null )
			return;

		TreeBrowser.TreeAssetBrowser.RefreshOpen();
		TreeBrowser.TreeAssetBrowser.Reveal( last );
		EditorUtility.InspectorObject = last;
	}

	//Written beside the picture; an existing set of that name is kept and the new one numbered
	private static Asset CreateTileSet( Asset image )
	{
		Bitmap bitmap;

		try
		{
			bitmap = Bitmap.CreateFromBytes( File.ReadAllBytes( image.AbsolutePath ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Create Tile Set: could not read {image.RelativePath}: {ex.Message}" );
			return null;
		}

		if ( bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0 )
		{
			Log.Warning( $"Create Tile Set: could not read {image.RelativePath}" );
			return null;
		}

		TileSheetGuess.Grid grid;
		using ( bitmap )
			grid = TileSheetGuess.Guess( bitmap, image.Name );

		var folder = Path.GetDirectoryName( image.AbsolutePath );
		var path = Path.Combine( folder, $"{image.Name}.tileset" );
		for ( var i = 2; File.Exists( path ); i++ )
			path = Path.Combine( folder, $"{image.Name}_{i}.tileset" );

		var asset = AssetSystem.CreateResource( "tileset", path );
		if ( asset is null || !asset.TryLoadResource<TileSet>( out var set ) )
			return null;

		set.Sheet = Texture.Load( image.RelativePath );
		set.TileSize = grid.Size;
		set.Margin = grid.Margin;
		set.Spacing = grid.Spacing;
		asset.SaveToDisk( set );

		Log.Info( $"Created {asset.RelativePath}: {grid.Size.x}x{grid.Size.y} tiles, margin {grid.Margin.x},{grid.Margin.y}, spacing {grid.Spacing.x},{grid.Spacing.y} ({grid.How})" );

		return asset;
	}
}

using Editor.TreeBrowser;
using Rinten;
using Rinten.UI;

namespace Editor.TerrainEditor;

public class TerrainMaterialList : ListView
{
	Terrain Terrain;
	Drag dragData;
	int dragOverIndex = -1;
	public TerrainMaterialList( Widget parent, Terrain terrain ) : base( parent )
	{
		ItemContextMenu = ShowItemContext;
		ItemSelected = OnItemClicked;
		ItemActivated = OnItemDoubleClicked;
		ItemSpacing = 8;
		AcceptDrops = true;
		SetSizeMode( SizeMode.Expand, SizeMode.Flexible );

		ItemSize = new Vector2( 68, 68 + 20 );
		ItemAlign = Rinten.UI.Align.FlexStart;
		Margin = 8;

		Terrain = terrain;

		BuildItems();
	}

	protected override bool OnDragItem( VirtualWidget item )
	{
		if ( item.Object is not Material material ) return false;

		dragData = new Drag( this );
		dragData.Data.Object = material;
		dragData.Execute();
		return true;
	}

	protected void OnItemClicked( object value )
	{
		if ( value is not Material material )
			return;

		PaintTextureTool.SplatChannel = Terrain.Storage.Materials.IndexOf( material );
	}

	protected void OnItemDoubleClicked( object obj )
	{
		if ( obj is not Material entry ) return;
		var asset = AssetSystem.FindByPath( entry.ResourcePath );
		asset?.OpenInEditor();
	}

	protected override DropAction OnItemDrag( ItemDragEvent e )
	{
		dragOverIndex = -1;

		if ( e.IsDrop && e.Data.Object is Material draggedMaterial && e.Item.Object is Material targetMaterial )
		{
			var materials = Terrain.Storage.Materials;
			var oldIndex = materials.IndexOf( draggedMaterial );
			var newIndex = materials.IndexOf( targetMaterial );

			if ( oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex )
			{
				(materials[oldIndex], materials[newIndex]) = (materials[newIndex], materials[oldIndex]);
				Terrain.UpdateMaterials();
				BuildItems();
			}
			Update();
			return DropAction.Move;
		}

		if ( !e.IsDrop && e.Item.Object is Material hoverMaterial )
		{
			dragOverIndex = Terrain.Storage.Materials.IndexOf( hoverMaterial );
			Update();
			return DropAction.Move;
		}

		if ( e.Data.Assets != null )
		{
			foreach ( var dragAsset in e.Data.Assets )
			{
				if ( dragAsset.AssetPath?.EndsWith( ".mat" ) ?? false )
					return DropAction.Link;
			}
		}

		return base.OnItemDrag( e );
	}

	public override void OnDragHover( DragEvent ev )
	{
		base.OnDragHover( ev );

		if ( ev.Data.Object is not Material )
		{
			dragOverIndex = -1;
			foreach ( var dragAsset in ev.Data.Assets )
			{
				if ( dragAsset.AssetPath?.EndsWith( ".mat" ) ?? false )
					continue;

				ev.Action = DropAction.Link;
				break;
			}
		}
	}

	public override void OnDragDrop( DragEvent ev )
	{
		base.OnDragDrop( ev );

		dragOverIndex = -1;

		if ( ev.Data.Object is not Material )
		{
			AddMaterials( ev.Data.Assets );
		}

		Update();
	}

	public override void OnDragLeave()
	{
		base.OnDragLeave();
		dragOverIndex = -1;
		Update();
	}

	private async void AddMaterials( IEnumerable<DragAssetData> draggedAssets )
	{
		foreach ( var dragAsset in draggedAssets )
		{
			var asset = await dragAsset.GetAssetAsync();
			if ( asset.TryLoadResource<Material>( out var material ) )
			{
				if ( Terrain.Storage.Materials.Contains( material ) ) continue;
				if ( Terrain.Storage.Materials.Count >= Terrain.MostLayers ) break;
				Terrain.Storage.Materials.Add( material );
				Terrain.UpdateMaterials();
			}
		}

		BuildItems();
	}

	//There is no terrain-material resource type any more; the shader is what makes an ordinary material a terrain one. See TerrainStorage.Materials.
	/// <summary>What a layer of ground is drawn by - a material like any other, wearing the shader that declares what a layer knows about itself.</summary>
	public const string LayerShader = "shaders/terrain_layer.shader";

	/// <summary>Ask for somewhere to put a new layer, make it, and give it back - or null where the dialog was closed or the file could not be made.</summary>
	public static Material CreateLayer()
	{
		var filepath = EditorUtility.SaveFileDialog( "Create Terrain Layer", "mat",
			$"{Project.Current.GetAssetsPath()}/" );

		if ( filepath is null ) return null;

		var asset = AssetSystem.CreateResource( "mat", filepath );

		if ( asset is null || !asset.TryLoadResource<MaterialDefinition>( out var definition ) )
			return null;

		//Which is the whole of what makes it a layer rather than a wall.
		definition.Shader = LayerShader;

		asset.SaveToDisk( definition );
		TreeAssetBrowser.RefreshOpen();

		var material = asset.LoadResource<Material>();

		if ( material is not null ) asset.OpenInEditor();

		return material;
	}

	private void ShowItemContext( object obj )
	{
		if ( obj is not Material entry ) return;

		var m = new ContextMenu( this );
		m.AddOption( "Open In Editor", "edit", () =>
		{
			var asset = AssetSystem.FindByPath( entry.ResourcePath );
			asset?.OpenInEditor();
		} );

		m.AddOption( "Remove", "delete", () =>
		{
			Terrain.Storage.Materials.Remove( entry );
			Terrain.UpdateMaterials();
			BuildItems();
		} );

		m.OpenAtCursor();
	}

	public void BuildItems()
	{
		SetItems( Terrain.Storage.Materials.Cast<object>() );
	}

	protected override void PaintItem( VirtualWidget item )
	{
		var rect = item.Rect.Shrink( 0, 0, 0, 20 );

		if ( item.Object is not Material material )
			return;

		var asset = AssetSystem.FindByPath( material.ResourcePath );

		if ( asset is null )
		{
			Paint.SetDefaultFont();
			Paint.SetPen( Color.Red );
			Paint.DrawText( item.Rect.Shrink( 2 ), "<ERROR>", TextFlag.Center );
			return;
		}

		if ( item.Selected || Paint.HasMouseOver )
		{
			Paint.SetBrush( Theme.Primary.WithAlpha( item.Selected ? 0.5f : 0.2f ) );
			Paint.ClearPen();
			Paint.DrawRect( item.Rect, 4 );
		}

		if ( item.Object is Material mat )
		{
			var itemIndex = Terrain.Storage.Materials.IndexOf( mat );
			if ( dragOverIndex == itemIndex )
			{
				Paint.SetPen( Theme.TextHighlight, 3f );
				Paint.DrawRect( item.Rect.Shrink( 1 ), 4 );
			}
		}

		var pixmap = asset.GetAssetThumb();
		Paint.Draw( rect.Shrink( 2 ), pixmap );

		Paint.SetDefaultFont();
		Paint.SetPen( Theme.Text );
		Paint.DrawText( item.Rect.Shrink( 2 ), material.ResourceName, TextFlag.CenterBottom );
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect, 4 );

		base.OnPaint();
	}
}

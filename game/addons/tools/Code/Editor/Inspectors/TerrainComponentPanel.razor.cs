using System.IO;
using System.Reflection;
using Rinten.Internal;
using Editor.AssetPickers;
using Editor.TreeBrowser;
using Editor.TerrainEditor;

namespace Editor.Inspectors;

/// <summary>Terrain inspector logic (button handlers); markup and style in TerrainComponentPanel.razor(.scss)</summary>
[CustomEditor( typeof( Terrain ) )]
[StyleSheet( "TerrainComponentPanel.razor.scss" )]
public partial class TerrainComponentPanel : ComponentPanel
{
	enum Page { Layers, Ground, Mesh }

	readonly Terrain terrain;

	//What each page's sheet shows, made once: a sheet given a new list is a sheet built again.
	readonly List<SerializedProperty> groundProperties = new();
	readonly List<SerializedProperty> meshProperties = new();

	//The file the ground is kept in - all the page shows while there is none.
	readonly List<SerializedProperty> storageProperties = new();

	//The blend settings of the storage last shown, made again when the storage is another.
	SerializedObject blendSettings;
	TerrainStorage blendSettingsOf;

	SerializedObject BlendSettings
	{
		get
		{
			if ( !ReferenceEquals( blendSettingsOf, terrain.Storage ) )
			{
				blendSettingsOf = terrain.Storage;
				blendSettings = blendSettingsOf?.MaterialSettings?.GetSerialized();
			}

			return blendSettings;
		}
	}

	Page page
	{
		get => (Page)ProjectCookie.Get( "Terrain.InspectorPage", 0 );
		set => ProjectCookie.Set( "Terrain.InspectorPage", (int)value );
	}

	public TerrainComponentPanel( SerializedObject obj )
	{
		TargetObject = obj;

		terrain = obj.Targets.FirstOrDefault() as Terrain;
		if ( !terrain.IsValid() ) return;

		foreach ( var property in obj.ToList().Where( Shown ) )
		{
			if ( OnGroundPage.Contains( property.Name ) ) groundProperties.Add( property );
			else meshProperties.Add( property );

			if ( property.Name == nameof( Terrain.Storage ) ) storageProperties.Add( property );
		}
	}

	//What is about the ground rather than about the mesh it is baked into.
	static readonly HashSet<string> OnGroundPage = new()
	{
		nameof( Terrain.Storage ),
		nameof( Terrain.TerrainSize ),
		nameof( Terrain.TerrainHeight ),
		nameof( Terrain.EnableCollision ),
		nameof( Collider.Friction ),
		nameof( Collider.Elasticity ),
		nameof( Collider.RollingResistance ),
	};

	//The .terrain file; a terrain without one has no ground, and none is made up implicitly

	//Kept inside the scene rather than in a file of its own - a terrain from before they had them.
	bool InScene => terrain.Storage is { } storage
		&& ( storage.EmbeddedResource is not null || string.IsNullOrEmpty( storage.ResourcePath ) );

	//Asks where, makes a flat terrain there, and sets it.
	void NewTerrainFile() => _ = MakeFile( "New Terrain File", null );

	//Writes the ground kept in the scene out to a file, and sets that instead.
	void SaveToFile() => _ = MakeFile( "Save Terrain To File", terrain.Storage );

	async Task MakeFile( string title, TerrainStorage contents )
	{
		var name = terrain.GameObject?.Name is { Length: > 0 } objectName ? objectName : "terrain";
		var path = EditorUtility.SaveFileDialog( title, "terrain", $"{Project.Current.GetAssetsPath()}/{name}.terrain" );
		if ( path is null ) return;

		var asset = AssetSystem.CreateResource( "terrain", path );
		if ( asset is null ) return;

		await asset.CompileIfNeededAsync();

		if ( !asset.TryLoadResource<TerrainStorage>( out var storage ) ) return;

		if ( contents is not null )
		{
			storage.CopyFrom( contents );

			//It lives in a file now, so it is not kept in the scene any more.
			storage.EmbeddedResource = null;
			asset.SaveToDisk( storage );
		}

		if ( !terrain.IsValid() || storageProperties.FirstOrDefault() is not { } property ) return;

		property.Parent?.NoteStartEdit( property );
		property.SetValue( storage );
		property.Parent?.NoteFinishEdit( property );

		TreeAssetBrowser.RefreshOpen();
		StateHasChanged();
	}

	static bool Shown( SerializedProperty o )
	{
		if ( o.PropertyType is null ) return false;
		if ( o.PropertyType.IsAssignableTo( typeof( Delegate ) ) ) return false;
		if ( !o.HasAttribute<PropertyAttribute>() ) return false;
		if ( o.Name == nameof( Collider.Static ) ) return false;
		if ( o.Name == nameof( Collider.Surface ) ) return false;
		if ( o.Name == nameof( Collider.IsTrigger ) ) return false;
		return true;
	}

	//Which layer the paint tool lays down.
	static int PaintedLayer => PaintTextureTool.SplatChannel;

	void PickLayer( int index ) => PaintTextureTool.SplatChannel = index;

	void LayerMenu( int index )
	{
		var layers = terrain.Storage.Materials;
		if ( index < 0 || index >= layers.Count ) return;

		var material = layers[index];
		var menu = new Menu();

		menu.AddOption( "Open in Editor", "edit", () =>
		{
			if ( material is not null ) AssetSystem.FindByPath( material.ResourcePath )?.OpenInEditor();
		} );

		//The control map names layers by their place in this list, so only the last can go without the paint under every later one changing hands.
		if ( index == layers.Count - 1 )
		{
			menu.AddOption( "Remove Layer", "delete", () =>
			{
				layers.RemoveAt( index );
				terrain.UpdateMaterials();
			} );
		}

		menu.OpenAtCursor( this );
	}

	static string LayerName( Material material, int index )
		=> material is null ? $"Layer {index}" : material.ResourceName ?? $"Layer {index}";

	//What the bake is doing, for the line under the mesh page's sheet.
	string BakeStatus
	{
		get
		{
			if ( terrain.ChunkCount == 0 ) return "Not baked";
			if ( terrain.ChunksWaiting > 0 ) return $"Baking {terrain.ChunksWaiting} of {terrain.ChunkCount} chunks";
			return $"{terrain.ChunkCount} chunks baked";
		}
	}

	//The components in the scene laying something over this terrain.
	IEnumerable<TerrainInsert> Inserts
		=> terrain.Scene?.GetAll<TerrainInsert>().Where( x => x.IsValid() && x.Terrain == terrain ) ?? [];

	//How much ground there is, for the line under the mesh page's sheet.
	string MeshSize => terrain.Mesh is { } mesh ? $"{mesh.TriangleCount:n0} triangles, {mesh.VertexCount:n0} vertices" : "No mesh";

	static string InsertIcon( TerrainInsert insert ) => insert switch
	{
		TerrainMeshInsert { Mode: TerrainMeshInsert.InsertMode.Carve } => "download",
		TerrainMeshInsert => "landscape",
		_ => "extension",
	};

	static string InsertKind( TerrainInsert insert )
		=> DisplayInfo.ForType( insert.GetType() ).Name ?? insert.GetType().Name;

	void Select( GameObject target )
	{
		if ( !target.IsValid() || SceneEditorSession.Active is not { } session ) return;

		session.Selection.Clear();
		session.Selection.Add( target );
	}

	void AddClutter()
	{
		if ( SceneEditorSession.Active is not { } session ) return;
		if ( terrain.Components.Get<TerrainClutter>( true ) is { } already ) { Select( already.GameObject ); return; }

		using ( session.UndoScope( "Add Terrain Clutter" ).WithComponentCreations().Push() )
		{
			terrain.Components.Create<TerrainClutter>();
		}
	}

	protected override int BuildHash()
	{
		var hash = new HashCode();

		hash.Add( (int)page );
		hash.Add( terrain?.Storage );
		hash.Add( terrain.IsValid() && InScene );
		hash.Add( PaintedLayer );
		hash.Add( terrain?.Storage?.Materials.Count ?? 0 );
		hash.Add( terrain?.ChunkCount ?? 0 );
		hash.Add( terrain?.ChunksWaiting ?? 0 );

		if ( terrain.IsValid() )
		{
			foreach ( var insert in Inserts ) hash.Add( insert );
			hash.Add( terrain.Storage?.InsetMaterials.Count ?? 0 );
			hash.Add( terrain.Mesh?.TriangleCount ?? 0 );
			foreach ( var layer in terrain.Storage?.Materials ?? [] ) hash.Add( layer );
		}

		return hash.ToHashCode();
	}

	void BrowseLayers()
	{
		var picker = AssetPicker.Create( null, AssetType.FromExtension( "mat" ) );
		picker.OnAssetPicked = assets =>
		{
			var material = assets.First().LoadResource<Material>();
			terrain.Storage.Materials.Add( material );
			terrain.UpdateMaterials();
		};
		picker.Show();
	}

	void NewLayer()
	{
		//A terrain layer is an ordinary material now - the .tmat resource and the editor that opened
		//it were removed, and the list makes one the same way the widget settings do.
		if ( TerrainMaterialList.CreateLayer() is not { } material ) return;

		terrain.Storage.Materials.Add( material );
		terrain.UpdateMaterials();
	}

	void ImportHeightmap()
	{
		var fd = new FileDialog( null );
		fd.Title = "Import Heightmap from image file...";
		fd.SetFindFile();
		fd.SetModeOpen();
		fd.SetNameFilter( "Image File (*.raw *.r8 *.r16)" );

		if ( !fd.Execute() ) return;
		new ImportHeightmapPopup( null, terrain, fd.SelectedFile );
	}

	void ImportSplatmap()
	{
		var fd = new FileDialog( null ) { Title = "Import Splatmap(s) from image file(s)..." };
		fd.SetFindExistingFiles();
		fd.SetModeOpen();
		fd.SetNameFilter( "Image File (*.png *.tga *.jpg *.psd)" );

		if ( !fd.Execute() ) return;

		var files = fd.SelectedFiles;
		if ( files.Count == 0 ) return;

		//The maps are as many texels a side as the first of them, laid over the whole terrain; the rest have to match it
		int resolution;
		using ( var first = EditorUtility.LoadBitmap( files[0] ) ) resolution = first.Width;

		var numPixels = resolution * resolution;
		var control = new uint[numPixels];

		var allWeights = new List<(int materialIndex, float weight)>[numPixels];
		for ( int i = 0; i < numPixels; i++ )
			allWeights[i] = [];

		int materialIndexOffset = 0;

		foreach ( var file in files )
		{
			using var bitmap = EditorUtility.LoadBitmap( file );
			if ( bitmap.Width != resolution || bitmap.Height != resolution )
			{
				Log.Warning( $"Skipping {Path.GetFileName( file )}: not square, or not the size of the first splat map" );
				continue;
			}

			var data = bitmap.EncodeTo( ImageFormat.RGBA8888 );
			for ( int i = 0; i < numPixels; i++ )
			{
				int byteOffset = i * 4;
				float r = data[byteOffset + 0] / 255f;
				float g = data[byteOffset + 1] / 255f;
				float b = data[byteOffset + 2] / 255f;
				float a = data[byteOffset + 3] / 255f;

				allWeights[i].Add( (materialIndexOffset + 0, r) );
				allWeights[i].Add( (materialIndexOffset + 1, g) );
				allWeights[i].Add( (materialIndexOffset + 2, b) );
				allWeights[i].Add( (materialIndexOffset + 3, a) );
			}

			materialIndexOffset += 4;
		}

		if ( materialIndexOffset == 0 ) return;

		for ( int i = 0; i < numPixels; i++ )
		{
			var weights = allWeights[i];
			weights.Sort( ( a, b ) => b.weight.CompareTo( a.weight ) );

			int firstIdx = weights[0].materialIndex;
			float firstWeight = weights[0].weight;
			int secondIdx = weights.Count > 1 ? weights[1].materialIndex : firstIdx;
			float secondWeight = weights.Count > 1 ? weights[1].weight : 0;

			byte blendFactor = 0;
			float totalWeight = firstWeight + secondWeight;
			if ( totalWeight > 0.0001f )
				blendFactor = (byte)Math.Clamp( (int)(secondWeight / totalWeight * 255f), 0, 255 );

			byte baseMat = (byte)Math.Clamp( firstIdx, 0, Math.Min( 31, terrain.Storage.Materials.Count - 1 ) );
			byte overlayMat = (byte)Math.Clamp( secondIdx, 0, Math.Min( 31, terrain.Storage.Materials.Count - 1 ) );

			var compactMaterial = new CompactTerrainMaterial( baseMat, overlayMat, blendFactor, false );
			control[i] = compactMaterial.Packed;
		}

		//The mesh is what is painted, vertex by vertex; the map just filled in is read across it by where each vertex stands.
		if ( terrain.RepaintFromControlMap( control, resolution ) is { } edit )
		{
			SceneEditorSession.Active?.UndoSystem.Insert( "Import Splatmap",
				() => { if ( terrain.IsValid() ) terrain.Mesh?.Undo( edit ); },
				() => { if ( terrain.IsValid() ) terrain.Mesh?.Redo( edit ); } );
		}
	}
}

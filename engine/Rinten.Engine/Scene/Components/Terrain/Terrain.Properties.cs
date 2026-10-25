using static Rinten.ModelRenderer;

namespace Rinten;

public partial class Terrain
{
	private TerrainStorage _storage;

	[Property]
	public TerrainStorage Storage
	{
		get => _storage;
		set
		{
			if ( _storage == value ) return;

			_storage?.MaterialSettings?.OnChanged -= OnTerrainChanged;
			_storage = value;
			_storage?.MaterialSettings?.OnChanged += OnTerrainChanged;

			Create();
		}
	}

	//Nothing is baked again when it changes: the chunks wear it and it is told g_flParams, which it declares as terrain.shader
	//does; its vertex stage calls PlaceGround (terrain_layers.slang) to take part in the relief ring
	/// <summary>Override the terrain rendering with your own material shader. This needs to be explicitly set up to work with the Terrain Shader API.</summary>
	[Property]
	public Material MaterialOverride
	{
		get => field;
		set
		{
			if ( field == value ) return;
			field = value;
			MaterialChanged();
		}
	}

	/// <summary>World width and length of the terrain; setting it stretches the existing ground</summary>
	[Property, Group( "Size" )]
	public float TerrainSize
	{
		get => Storage is null ? 0.0f : Storage.TerrainSize;
		set
		{
			if ( Storage is null || !( value > 0.0f ) ) return;

			//The mesh as it is before the number changes: one still to be made from a height map is made at the size it was, and then stretched like any other.
			_ = Storage.Mesh;

			var was = Storage.TerrainSize;
			Storage.TerrainSize = value;

			if ( was > 0.0f ) Stretch( value / was, 1.0f );
		}
	}

	//The ground stretched to a new size and made again from it: the chunks laid
	//out over where it stands now, the collider, and the numbers the shader reads.
	private void Stretch( float across, float up )
	{
		if ( Storage?.Mesh is not { } mesh ) return;
		if ( !mesh.Stretch( across, up ) ) return;

		Create();
	}

	//The box the ground fills, in the terrain's own space - the collider's comes from a body this one rotates.
	BBox Component.IHasBounds.LocalBounds => Storage is null
		? BBox.FromPositionAndSize( Vector3.Zero )
		: Storage.Mesh?.Bounds ?? new BBox( Vector3.Zero, new Vector3( Storage.TerrainSize, Storage.TerrainHeight, Storage.TerrainSize ) );

	//Setting it stretches the ground up or down to it - see TerrainSize.
	/// <summary>World size of the maximum height of the terrain.</summary>
	[Property, Group( "Size" )]
	public float TerrainHeight
	{
		get => Storage is null ? 0.0f : Storage.TerrainHeight;
		set
		{
			if ( Storage is null || !( value > 0.0f ) ) return;

			//The mesh as it is before the number changes: one still to be made from a height map is made at the size it was, and then stretched like any other.
			_ = Storage.Mesh;

			var was = Storage.TerrainHeight;
			Storage.TerrainHeight = value;

			if ( was > 0.0f ) Stretch( 1.0f, value / was );
		}
	}

	/// <summary>Side length in metres of one baked chunk; larger draws cheaper, smaller rebakes faster</summary>
	[Property, Category( "Mesh" ), Range( 16, 128 ), Step( 16 )]
	public int ChunkSize
	{
		get => field;
		set
		{
			value = Math.Clamp( value, 16, 128 );
			if ( field == value ) return;

			field = value;

			if ( chunks.Length > 0 )
			{
				CreateChunks();
				Rebuild();
			}
		}
	} = (int)( WorldGrid.CellSize * 2 );

	private ModelRenderer.ShadowRenderType _renderType = ModelRenderer.ShadowRenderType.On;

	//The ground is meshes that stand still, so what it casts is baked into the sun's pages with the rest of the still world, once.
	/// <summary>Whether the ground casts a shadow: a hill onto the valley beside it, and onto whatever stands there.</summary>
	[Title( "Cast Shadows" ), Property, Category( "Lighting" )]
	public ModelRenderer.ShadowRenderType RenderType
	{
		get => _renderType;
		set
		{
			_renderType = value;

			EachObject( ( one, _ ) => Flag( one ) );
		}
	}
}

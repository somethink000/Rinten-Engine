namespace Rinten;

//Impostor sheets packed block-for-block into shared pages, so all impostors share one material
internal static class CoreImpostor
{
	/// <summary>The surface an impostor is drawn with.</summary>
	public const string Shader = "shaders/engine/impostor.shader";

	//Texels a side of a page.
	private const int PageSize = 2048;

	//The place a sheet is given is whole cells of this many texels - its own alignment.
	private const int Cell = CoreAssets.ModelBuilder.SheetAlign;

	private const int Cells = PageSize / Cell;

	//RINTEN_IMPOSTOR_LOG: a line a model's impostor as it is placed.
	private static readonly bool RintenImpostorLog = Environment.GetEnvironmentVariable( "RINTEN_IMPOSTOR_LOG" ) == "1";

	//Where a model's sheet is on its page: the page's material, and the rectangle as a share of the page.
	internal readonly record struct Placed( string Material, float U, float V, float Wide, float Tall );

	//One square of the shared pictures, the cells of it given out, and its material.
	private sealed class Page
	{
		public readonly bool[] Taken = new bool[Cells * Cells];
		public string Name;
		public ulong ColourHandle, ShapeHandle;

		/// <summary>Held here because nothing else holds them: a material found by name is kept only weakly by the index that finds it.</summary>
		public Texture Colour, Shape;
		public Material Material;
	}

	private static readonly List<Page> pages = new();

	//Where each model's sheet went, by the name its quads wear - a model loaded again is not laid again.
	private static readonly Dictionary<string, Placed> placed = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>The model's sheet onto a page, if it has an impostor and the sheet is one this engine bakes; where it went in <paramref name="where"/>. False where it has none, or it would not go - the model is then drawn without one.</summary>
	public static bool Place( CoreAssets.ModelFile model, out Placed where )
	{
		where = default;
		if ( model is null || !model.HasImpostor ) return false;

		var info = model.Impostor;
		if ( info.texels != CoreAssets.ModelBuilder.ImpostorTexels ) return false;

		var name = model.MeshMaterial( (int)info.mesh );
		if ( string.IsNullOrWhiteSpace( name ) ) return false;

		name = name.Replace( '\\', '/' ).TrimStart( '/' );

		lock ( pages )
		{
			if ( placed.TryGetValue( name, out where ) ) return true;

			var colour = CoreTextureFile.FromBytes( model.ImpostorColour.ToArray() );
			var shape = CoreTextureFile.FromBytes( model.ImpostorShape.ToArray() );

			if ( !Fits( colour ) || !Fits( shape ) || colour.Width != shape.Width || colour.Height != shape.Height ) return false;

			var wide = colour.Width / Cell;
			var tall = colour.Height / Cell;

			if ( !Find( wide, tall, out var page, out var cellX, out var cellY ) ) return false;

			if ( !Write( page.ColourHandle, colour, cellX * Cell, cellY * Cell ) ||
				!Write( page.ShapeHandle, shape, cellX * Cell, cellY * Cell ) )
				return false;

			for ( var y = 0; y < tall; y++ )
				for ( var x = 0; x < wide; x++ )
					page.Taken[( ( cellY + y ) * Cells ) + cellX + x] = true;

			where = new Placed( page.Name, cellX * Cell / (float)PageSize, cellY * Cell / (float)PageSize,
				colour.Width / (float)PageSize, colour.Height / (float)PageSize );
			placed[name] = where;

			//What the cut takes it at and how far off its quads the model is - see ModelFormat.Impostor.spread - for the switch to be weighed by.
			if ( RintenImpostorLog )
				Log.Info( $"impostor {name}: radius {info.radius:0.###}, texel {info.radius * 2.0f / info.texels:0.####}, spread {info.spread:0.####}, sheet {colour.Width}x{colour.Height} on {page.Name}" );

			return true;
		}
	}

	//Whether a sheet is one a page takes: blocks with alpha, every level a page has, whole cells.
	private static bool Fits( CoreTextureFile sheet )
		=> sheet is not null
			&& sheet.Format == CoreTextureFile.TextureFormat.Bc3
			&& sheet.MipCount >= CoreAssets.ModelBuilder.ImpostorLevels
			&& sheet.Width % Cell == 0 && sheet.Height % Cell == 0
			&& sheet.Width <= PageSize && sheet.Height <= PageSize;

	//The first free place wide by tall cells on a page, a new page where none has one.
	private static bool Find( int wide, int tall, out Page page, out int cellX, out int cellY )
	{
		foreach ( var one in pages )
		{
			if ( Free( one, wide, tall, out cellX, out cellY ) )
			{
				page = one;
				return true;
			}
		}

		page = MakePage();
		cellX = cellY = 0;

		return page is not null;
	}

	//The first place on a page, row by row, where a rectangle of cells is all free.
	private static bool Free( Page page, int wide, int tall, out int cellX, out int cellY )
	{
		for ( cellY = 0; cellY + tall <= Cells; cellY++ )
		{
			for ( cellX = 0; cellX + wide <= Cells; cellX++ )
			{
				var free = true;

				for ( var y = 0; y < tall && free; y++ )
					for ( var x = 0; x < wide && free; x++ )
						free = !page.Taken[( ( cellY + y ) * Cells ) + cellX + x];

				if ( free ) return true;
			}
		}

		cellX = cellY = 0;
		return false;
	}

	//What the pages hold on the device: two block-compressed pictures each, every level.
	internal static long PageBytes
	{
		get
		{
			var one = 0L;

			for ( var level = 0; level < CoreAssets.ModelBuilder.ImpostorLevels; level++ )
			{
				var side = Math.Max( PageSize >> level, 4 );
				one += (long)side * side;
			}

			return pages.Count * 2 * one;
		}
	}

	//A new page: its two pictures, every level and nothing in them, and the material every impostor on it wears.
	private static Page MakePage()
	{
		var name = $"impostors/page{pages.Count}";
		var levels = CoreAssets.ModelBuilder.ImpostorLevels;

		var colourHandle = CoreRenderDevice.CreateTextureBlank( PageSize, PageSize, CoreTextureFile.TextureFormat.Bc3, true, levels );
		var shapeHandle = CoreRenderDevice.CreateTextureBlank( PageSize, PageSize, CoreTextureFile.TextureFormat.Bc3, false, levels );

		if ( colourHandle == 0 || shapeHandle == 0 )
		{
			CoreRenderDevice.DestroyTexture( colourHandle );
			CoreRenderDevice.DestroyTexture( shapeHandle );
			return null;
		}

		var colour = CoreTexture.OnDevice( colourHandle, PageSize, PageSize, name + ":colour" );
		colour.DeviceSrgb = true;

		var shape = CoreTexture.OnDevice( shapeHandle, PageSize, PageSize, name + ":shape" );
		shape.DeviceSrgb = false;

		var page = new Page
		{
			Name = name,
			ColourHandle = colourHandle,
			ShapeHandle = shapeHandle,
			Colour = new Texture( colour ),
			Shape = new Texture( shape ),
		};

		page.Material = Material.Create( name, Shader, anonymous: false );

		//The colour and cover in the colour slot: the engine's depth raster cuts every light's view of the
		//quads by that slot's alpha, the way it cuts any leaf - see mesh_raster.slang and CoreSceneTables.Index.
		page.Material.Set( "Color", page.Colour );
		page.Material.Set( "g_tImpostorShape", page.Shape );

		//Cut out, as the shader is built, and each quad one-sided: of the six the eye sees the three
		//turned to it. The draw's state has to say what the shader does - see CoreResources.LiveFlags.
		page.Material.Set( "F_ALPHA", 1 );
		page.Material.Set( "F_BACKFACES", 0 );
		page.Material.Set( "AlphaCutoff", 0.5f );

		pages.Add( page );
		return page;
	}

	//Every level a page has of a sheet, into the page's picture at (x, y) of its finest level.
	private static bool Write( ulong page, CoreTextureFile sheet, int x, int y )
	{
		var count = sheet.MipCount;
		Span<int> info = count * 3 <= 96 ? stackalloc int[count * 3] : new int[count * 3];
		sheet.Levels( info, out var pixels );

		var at = 0;

		for ( var k = 0; k < CoreAssets.ModelBuilder.ImpostorLevels; k++ )
		{
			var wide = info[k * 3];
			var tall = info[( k * 3 ) + 1];
			var bytes = info[( k * 3 ) + 2];

			//A row of blocks at a time: sixteen bytes a block of four by four.
			if ( !CoreRenderDevice.WriteTextureLevel( page, k, x >> k, y >> k, wide, tall, pixels.Slice( at, bytes ), ( wide + 3 ) / 4 * 16 ) )
				return false;

			at += bytes;
		}

		return true;
	}
}

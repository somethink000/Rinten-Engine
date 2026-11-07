using System.Runtime.InteropServices;

namespace Rinten.CoreAssets;

//Impostor: six baked box-face pictures (colour, cover, normal) drawn as quads at a distance
internal sealed unsafe partial class ModelBuilder
{
	//What a mesh's material looks like, as far as the bake needs it.
	internal sealed class Look
	{
		/// <summary>The colour map, a texel a colour, rows from the top - or null for a surface of the tint alone.</summary>
		public Color32[] Pixels;
		public int Width, Height;

		/// <summary>The material's own tint, which the picture is multiplied by.</summary>
		public Color Tint = Color.White;

		/// <summary>Where the alpha is cut, where the material cuts at all.</summary>
		public float Cutoff = 0.5f;
		public bool Masked;

		/// <summary>How rough the surface is - its number, which a map of it averages to from that far away.</summary>
		public float Roughness = 0.5f;

		/// <summary>Whether its shader multiplies the colour by the mesh's corner colour - see CoreResources.TintsPerVertex.</summary>
		public bool VertexTint;
	}

	//Impostor texels across the model's sphere, and the on-screen pixel size where it takes over
	internal const int ImpostorTexels = 128;

	//The levels each picture keeps, the finest first: down to a sixteenth, where a face of the widest model is eight texels. A
	//face's place and size in its sheet are whole sixteens of texels, so every level of every face is whole texels - see Sheet.
	internal const int ImpostorLevels = 5;

	//Texels a face's place and size in its sheet are whole numbers of: a texel at the coarsest level.
	private const int FaceAlign = 1 << ( ImpostorLevels - 1 );

	//Texels a sheet's size is a whole number of: a four-by-four block at the coarsest level. A sheet laid into a
	//shared picture at a place that is the same whole number lies on whole blocks at every level - see CoreImpostor.
	internal const int SheetAlign = 4 << ( ImpostorLevels - 1 );

	//The direction face side of axis axis is seen from: +x, +y, +z for the first side, the opposite for the second.
	internal static Vector3 FaceDirection( int axis, int side )
	{
		var sign = side == 0 ? 1.0f : -1.0f;
		return axis == 0 ? new Vector3( sign, 0, 0 ) : axis == 1 ? new Vector3( 0, sign, 0 ) : new Vector3( 0, 0, sign );
	}

	//Samples a pixel across, each way, while a view is drawn - the cover is the share of them the model took.
	private const int ImpostorSamples = 4;

	//Share of self-shading baked into impostor colour, since one plane cannot shadow itself
	private const float ImpostorShade = 0.5f;

	//The first of the meshes that are the impostor's planes - one a material the model wears, in a row - how many, and the error they stand in with - see LinkImpostor.
	private int impostorMesh = -1;
	private int impostorMeshes;
	private float impostorError;

	//What the file carries of it: its grid and sphere, and its pictures as texture files.
	private ModelFormat.Impostor impostorInfo;
	private byte[] impostorColour, impostorShape, impostorPicture;

	//The sheet's triangles at every level, kept until the clusters are cut and they can be named by cluster - see ImpostorPicture.
	private int[][] impostorPicked;
	private Vector2[][] impostorPickedWhere;
	private int impostorSheetWide, impostorSheetTall;

	//Draws the model into the impostor's views and adds the planes it is drawn on, one a material, each wearing it. Returns whether it did: not
	//for a model with bones or blend shapes, whose shape is not the one it stands in its file, and not for one that came out empty.
	internal bool MakeImpostor( Look[] looks )
	{
		if ( bones.Count > 0 || morphs.Count > 0 || vertices.Count == 0 || impostorMesh >= 0 ) return false;

		//What each mesh wears, with what its art said about its surface on top of what its material did: a model of art surfaces has no picture
		//and a plain white material, and its colour is the surface's - see ModelFormat.Surface. Baked without it, every such model was white.
		looks = Worn( looks );

		var low = new Vector3( float.MaxValue, float.MaxValue, float.MaxValue );
		var high = new Vector3( float.MinValue, float.MinValue, float.MinValue );

		foreach ( var mesh in meshes )
		{
			for ( var v = 0u; v < mesh.VertexCount; v++ )
			{
				var at = vertices[(int)( mesh.FirstVertex + v )].Position;
				low = Vector3.Min( low, at );
				high = Vector3.Max( high, at );
			}
		}

		//The sphere every view is framed on: round the middle of the box, as far out
		//as the furthest corner - which a square view of it holds from any direction.
		var centre = ( low + high ) * 0.5f;
		var radius = 0.0f;

		foreach ( var mesh in meshes )
		{
			for ( var v = 0u; v < mesh.VertexCount; v++ )
				radius = MathF.Max( radius, ( vertices[(int)( mesh.FirstVertex + v )].Position - centre ).Length );
		}

		if ( !( radius > 0.0f ) ) return false;

		//A texel, the same along every face: the sphere's width in ImpostorTexels.
		var texel = radius * 2.0f / ImpostorTexels;

		//How much leaf is in each part of the model's space, for how much sky each point of it sees - see Occupancy.
		var grid = new Occupancy( looks, this, centre - new Vector3( radius, radius, radius ), centre + new Vector3( radius, radius, radius ) );

		//Each side: the box seen square from it, as many texels as that side is long, drawn into pictures of its own, trimmed to what the model covers of it.
		var faces = new List<ImpostorFace>();

		for ( var a = 0; a < 3; a++ )
		{
			for ( var side = 0; side < 2; side++ )
			{
				var toward = FaceDirection( a, side );
				FrameBasis( toward, out var right, out var up );

				var box = Frame.Box( low, high, right, up, toward );
				var wide = Align( (int)MathF.Ceiling( box.Wide / texel ), FaceAlign );
				var tall = Align( (int)MathF.Ceiling( box.Tall / texel ), FaceAlign );

				//The box's side, grown to whole texels about its middle.
				var frame = box with
				{
					Left = box.Left - ( ( ( wide * texel ) - box.Wide ) * 0.5f ),
					Wide = wide * texel,
					Top = box.Top + ( ( ( tall * texel ) - box.Tall ) * 0.5f ),
					Tall = tall * texel,
				};

				var raster = Rasterize( looks, frame, right, up, toward, wide, tall );
				if ( raster is null ) continue;

				var face = new ImpostorFace( wide, tall ) { Frame = frame, Toward = toward, Right = right, Up = up };
				if ( DrawView( raster, grid, face ) ) faces.Add( face );
			}
		}

		var drawn = faces.ToArray();
		if ( drawn.Length == 0 ) return false;

		//The faces into one sheet, as small as they go - and the sheet into blocks, a chain of the levels each face kept.
		var sheet = Sheet( drawn, out var sheetWide, out var sheetTall );
		var colourChain = new List<(byte[], int, int)>();
		var shapeChain = new List<(byte[], int, int)>();

		for ( var k = 0; k < ImpostorLevels; k++ )
		{
			colourChain.Add( (sheet.Colour[k], sheetWide >> k, sheetTall >> k) );
			shapeChain.Add( (sheet.Shape[k], sheetWide >> k, sheetTall >> k) );
		}

		//Blocks with alpha whatever the picture: a model's sheet goes into a picture shared with every other model's, of one format.
		impostorColour = CoreTextureFile.BuildLevels( colourChain, srgb: true, compress: true, keepAlpha: true );
		impostorShape = CoreTextureFile.BuildLevels( shapeChain, srgb: false, compress: true, keepAlpha: true );

		if ( impostorColour is null || impostorShape is null ) return false;

		impostorPicked = sheet.Picked;
		impostorPickedWhere = sheet.PickedWhere;
		impostorSheetWide = sheetWide;
		impostorSheetTall = sheetTall;

		var spread = 0.0f;
		foreach ( var face in drawn ) spread = MathF.Max( spread, face.Spread );

		//The meshes it is a picture of, by the material and art surface they wear: each pair's planes are shaded as its meshes are, and keep their texels alone
		var parts = new Dictionary<(string Material, ModelFormat.Surface Surface), uint>();
		var spelled = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		var count = meshes.Count;

		for ( var m = 0; m < count; m++ )
		{
			if ( ( meshes[m].Flags & ModelFormat.MeshFlags.StandsIn ) != 0 || meshes[m].LevelOf >= 0 || meshes[m].IndexCount < 3 ) continue;

			var name = meshes[m].Material ?? "";
			if ( !spelled.TryGetValue( name, out var first ) ) spelled[name] = first = name;

			var key = ( first, meshes[m].Surface );
			parts[key] = parts.GetValueOrDefault( key ) | PartBit( m );
		}

		impostorMesh = meshes.Count;

		foreach ( var ((wearing, surface), mask) in parts )
		{
			var first = vertices.Count;
			var firstIndex = indices.Count;

			//One one-sided quad per face, placed at the view's median depth; UV1 holds the model's sphere, the colour which parts it shows
			foreach ( var face in drawn )
			{
				var at = (uint)( vertices.Count - first );
				var (x0, y0, _, _) = face.Trim;

				//The outline's corners in order, anticlockwise seen from the face's side - the
				//face a counterclockwise triangle shows - and a fan of triangles over them.
				foreach ( var (x, y) in face.Outline )
				{
					var across = face.Frame.Left + ( x / (float)face.Wide * face.Frame.Wide );
					var down = face.Frame.Top - ( y / (float)face.Tall * face.Frame.Tall );

					vertices.Add( new Vertex
					{
						Position = ( face.Right * across ) + ( face.Up * down ) + ( face.Toward * face.Plane ),
						Color = new Color32( (byte)mask, (byte)( mask >> 8 ), (byte)( mask >> 16 ), (byte)( mask >> 24 ) ),
						Normal = face.Toward,
						Tangent = new Vector4( face.Right, 1 ),
						TexCoord0 = new Vector4( ( face.SheetX + x - x0 ) / (float)sheetWide, ( face.SheetY + y - y0 ) / (float)sheetTall, 0, 0 ),
						TexCoord1 = new Vector4( centre, radius ),
					} );
				}

				for ( var corner = 2; corner < face.Outline.Count; corner++ )
				{
					indices.Add( at );
					indices.Add( at + (uint)corner - 1 );
					indices.Add( at + (uint)corner );
				}
			}

			meshes.Add( new MeshEntry
			{
				Name = "impostor",
				Material = wearing,
				Surface = surface,
				FirstIndex = (uint)firstIndex,
				IndexCount = (uint)( indices.Count - firstIndex ),
				FirstVertex = (uint)first,
				VertexCount = (uint)( vertices.Count - first ),
				Flags = ModelFormat.MeshFlags.StandsIn,
			} );
		}

		if ( weighted ) PadSkin();

		impostorMeshes = meshes.Count - impostorMesh;

		//A texel: at a pixel of error the cut takes the impostor where the model is ImpostorTexels pixels across on the screen.
		impostorError = texel;

		impostorInfo = new ModelFormat.Impostor
		{
			texels = ImpostorTexels,
			radius = radius,
			mesh = (uint)impostorMesh,
			spread = spread,
		};

		impostorInfo.centre[0] = centre.x;
		impostorInfo.centre[1] = centre.y;
		impostorInfo.centre[2] = centre.z;

		impostorInfo.extent[0] = ( high.x - low.x ) * 0.5f;
		impostorInfo.extent[1] = ( high.y - low.y ) * 0.5f;
		impostorInfo.extent[2] = ( high.z - low.z ) * 0.5f;

		return true;
	}

	//A mesh's bit in a plane's mask of the parts it shows: one a mesh, the last standing for every mesh past it - see impostor_surface.slang.
	internal static uint PartBit( int mesh ) => 1u << Math.Min( mesh, 31 );

	//value up to a whole number of steps, and never nought.
	private static int Align( int value, int step ) => Math.Max( step, ( value + step - 1 ) / step * step );

	//One face of the box as it is baked: its view's frame and basis, its own pictures at every level, the
	//rectangle of them the model covers, where that rectangle went in the sheet, and the depth its quad stands at.
	private sealed class ImpostorFace
	{
		public readonly int Wide, Tall;
		public readonly byte[][] Colour = new byte[ImpostorLevels][];
		public readonly byte[][] Shape = new byte[ImpostorLevels][];

		/// <summary>Each texel's triangle and where in it, at every level - see Raster.Triangle; nought where the model is not.</summary>
		public readonly int[][] Picked = new int[ImpostorLevels][];
		public readonly Vector2[][] PickedWhere = new Vector2[ImpostorLevels][];

		public Frame Frame;
		public Vector3 Toward, Right, Up;

		/// <summary>The texels the model covers, grown by two for the filter and out to whole FaceAligns - left, top, right, bottom.</summary>
		public (int X0, int Y0, int X1, int Y1) Trim;

		/// <summary>The outline of the piece in the face's texels, x across and y down: the trimmed rectangle with its corners cut along the diagonals as close as the covered texels allow - eight sides at most, which takes a cone's empty corners off and a box's not at all. See Outline.</summary>
		public List<(float X, float Y)> Outline;

		/// <summary>Where the trimmed rectangle's top left went in the sheet.</summary>
		public int SheetX, SheetY;

		/// <summary>The depth the quad stands at along <see cref="Toward"/>, and how far off it the model is - see PlaneDepth.</summary>
		public float Plane, Spread;

		public ImpostorFace( int wide, int tall )
		{
			Wide = wide;
			Tall = tall;

			for ( var k = 0; k < ImpostorLevels; k++ )
			{
				Colour[k] = new byte[( wide >> k ) * ( tall >> k ) * 4];
				Shape[k] = new byte[( wide >> k ) * ( tall >> k ) * 4];
				Picked[k] = new int[( wide >> k ) * ( tall >> k )];
				PickedWhere[k] = new Vector2[( wide >> k ) * ( tall >> k )];
			}
		}
	}

	//Every level of a sheet's two pictures, and its triangles.
	private readonly record struct SheetLevels( byte[][] Colour, byte[][] Shape, int[][] Picked, Vector2[][] PickedWhere );

	//The faces' trimmed rectangles packed into one sheet, as small as they go: in rows, tallest first, for each width a whole number of SheetAligns
	//from the widest face to all of them side by side - the width that leaves the least sheet. Each face's levels copied into its place at every level.
	private static SheetLevels Sheet( ImpostorFace[] faces, out int sheetWide, out int sheetTall )
	{
		var order = faces.OrderByDescending( f => f.Trim.Y1 - f.Trim.Y0 ).ToArray();
		var widest = order.Max( f => f.Trim.X1 - f.Trim.X0 );
		var all = order.Sum( f => f.Trim.X1 - f.Trim.X0 );

		var bestArea = long.MaxValue;
		sheetWide = sheetTall = 0;

		for ( var wide = Align( widest, SheetAlign ); wide <= Align( all, SheetAlign ); wide += SheetAlign )
		{
			int x = 0, y = 0, row = 0;

			foreach ( var f in order )
			{
				var w = f.Trim.X1 - f.Trim.X0;
				var h = f.Trim.Y1 - f.Trim.Y0;
				if ( x + w > wide ) { y += row; x = 0; row = 0; }

				x += w;
				row = Math.Max( row, h );
			}

			var tall = Align( y + row, SheetAlign );
			var area = (long)wide * tall;

			//The smallest, and of two the same the squarer.
			if ( area < bestArea || ( area == bestArea && Math.Abs( wide - tall ) < Math.Abs( sheetWide - sheetTall ) ) )
			{
				bestArea = area;
				sheetWide = wide;
				sheetTall = tall;
			}
		}

		//Laid out again at the width chosen.
		{
			int x = 0, y = 0, row = 0;

			foreach ( var f in order )
			{
				var w = f.Trim.X1 - f.Trim.X0;
				var h = f.Trim.Y1 - f.Trim.Y0;
				if ( x + w > sheetWide ) { y += row; x = 0; row = 0; }

				f.SheetX = x;
				f.SheetY = y;
				x += w;
				row = Math.Max( row, h );
			}
		}

		var colour = new byte[ImpostorLevels][];
		var shape = new byte[ImpostorLevels][];
		var picked = new int[ImpostorLevels][];
		var pickedWhere = new Vector2[ImpostorLevels][];

		for ( var k = 0; k < ImpostorLevels; k++ )
		{
			var wide = sheetWide >> k;
			var tall = sheetTall >> k;
			colour[k] = new byte[wide * tall * 4];
			shape[k] = new byte[wide * tall * 4];
			picked[k] = new int[wide * tall];
			pickedWhere[k] = new Vector2[wide * tall];

			foreach ( var f in faces )
			{
				var faceWide = f.Wide >> k;
				var (x0, y0, x1, y1) = (f.Trim.X0 >> k, f.Trim.Y0 >> k, f.Trim.X1 >> k, f.Trim.Y1 >> k);

				for ( var y = y0; y < y1; y++ )
				{
					var from = ( ( y * faceWide ) + x0 ) * 4;
					var to = ( ( ( ( f.SheetY >> k ) + y - y0 ) * wide ) + ( f.SheetX >> k ) ) * 4;

					System.Array.Copy( f.Colour[k], from, colour[k], to, ( x1 - x0 ) * 4 );
					System.Array.Copy( f.Shape[k], from, shape[k], to, ( x1 - x0 ) * 4 );
					System.Array.Copy( f.Picked[k], from / 4, picked[k], to / 4, x1 - x0 );
					System.Array.Copy( f.PickedWhere[k], from / 4, pickedWhere[k], to / 4, x1 - x0 );
				}
			}
		}

		return new SheetLevels( colour, shape, picked, pickedWhere );
	}

	//Each mesh's look with the surface its art says it wears folded in - its colour into the tint, its roughness over the material's.
	private Look[] Worn( Look[] looks )
	{
		var worn = new Look[meshes.Count];

		for ( var m = 0; m < meshes.Count; m++ )
		{
			var look = m < looks.Length ? looks[m] : null;
			var surface = meshes[m].Surface;

			if ( !surface.Worn )
			{
				worn[m] = look;
				continue;
			}

			var colour = surface.Colour;
			var tint = look?.Tint ?? Color.White;

			worn[m] = new Look
			{
				Pixels = look?.Pixels,
				Width = look?.Width ?? 0,
				Height = look?.Height ?? 0,
				Cutoff = look?.Cutoff ?? 0.5f,
				Masked = look?.Masked ?? false,
				Tint = new Color( tint.r * colour.r, tint.g * colour.g, tint.b * colour.b, 1.0f ),
				Roughness = surface.Says( ModelFormat.SurfaceSays.Roughness ) ? surface.roughness : look?.Roughness ?? 0.5f,
			};
		}

		return worn;
	}

	//A view's right and up, from the direction it looks from: right level with the
	//ground, up the rest of the way. Straight above, where level is any way, right is +x.
	internal static void FrameBasis( Vector3 toward, out Vector3 right, out Vector3 up )
	{
		right = new Vector3( 0, 1, 0 ).Cross( toward );
		right = right.Length < 1e-4f ? new Vector3( 1, 0, 0 ) : right.Normal;
		up = toward.Cross( right );
	}

	//What one view is framed on: across its right from Left, down its up from Top, and its depth along the eye from Back, nought there and one Deep nearer.
	private readonly record struct Frame( float Left, float Wide, float Top, float Tall, float Back, float Deep )
	{
		/// <summary>The model's own box, seen square along one of its axes.</summary>
		public static Frame Box( Vector3 low, Vector3 high, Vector3 right, Vector3 up, Vector3 toward )
		{
			static (float, float) Span( Vector3 low, Vector3 high, Vector3 axis )
			{
				var a = low.Dot( axis );
				var b = high.Dot( axis );
				return (MathF.Min( a, b ), MathF.Max( a, b ));
			}

			var (x0, x1) = Span( low, high, right );
			var (y0, y1) = Span( low, high, up );
			var (z0, z1) = Span( low, high, toward );

			return new( x0, MathF.Max( x1 - x0, 1e-4f ), y1, MathF.Max( y1 - y0, 1e-4f ), z0, MathF.Max( z1 - z0, 1e-4f ) );
		}
	}

	//The samples one view is drawn into.
	private sealed class Raster
	{
		public readonly int Wide, Tall;
		public readonly float[] Depth;
		public readonly Color[] Tint;
		public readonly Vector3[] Facing;

		/// <summary>Which triangle of the model each sample is, one more than its number in the index list - nought for none - and where in it, as the weights of its second and third corners.</summary>
		public readonly int[] Triangle;
		public readonly Vector2[] Where;

		public Raster( int wide, int tall )
		{
			Wide = wide;
			Tall = tall;
			Depth = new float[wide * tall];
			Tint = new Color[wide * tall];
			Facing = new Vector3[wide * tall];
			Triangle = new int[wide * tall];
			Where = new Vector2[wide * tall];
			System.Array.Fill( Depth, float.MinValue );
		}
	}

	//A face's plane depth (median of covered samples) and spread (90th percentile distance)
	private static (float Plane, float Spread) PlaneDepth( Raster raster, Frame frame )
	{
		const int bins = 1024;
		Span<int> histogram = stackalloc int[bins];
		histogram.Clear();

		var total = 0;

		foreach ( var depth in raster.Depth )
		{
			if ( depth == float.MinValue ) continue;

			var bin = Math.Clamp( (int)( ( depth - frame.Back ) / frame.Deep * bins ), 0, bins - 1 );
			histogram[bin]++;
			total++;
		}

		float Depth( int bin ) => frame.Back + ( ( bin + 0.5f ) / bins * frame.Deep );

		var plane = frame.Back + ( frame.Deep * 0.5f );
		var under = 0;

		for ( var bin = 0; bin < bins; bin++ )
		{
			under += histogram[bin];
			if ( under > total / 2 ) { plane = Depth( bin ); break; }
		}

		//The spread: the bins by how far they are from the plane, nearest first, until nine in ten of the samples are in.
		Span<int> off = stackalloc int[bins];
		off.Clear();

		for ( var bin = 0; bin < bins; bin++ )
		{
			var away = Math.Clamp( (int)( MathF.Abs( Depth( bin ) - plane ) / frame.Deep * bins ), 0, bins - 1 );
			off[away] += histogram[bin];
		}

		var within = 0;

		for ( var bin = 0; bin < bins; bin++ )
		{
			within += off[bin];
			if ( within * 10 >= total * 9 ) return (plane, ( bin + 1.0f ) / bins * frame.Deep);
		}

		return (plane, frame.Deep);
	}

	//The model drawn into a side's samples, nearest wins - null where none of it is there.
	private Raster Rasterize( Look[] looks, Frame frame, Vector3 right, Vector3 up, Vector3 toward, int wide, int tall )
	{
		var raster = new Raster( wide * ImpostorSamples, tall * ImpostorSamples );
		var flat = CollectionsMarshal.AsSpan( vertices );
		var drawn = false;

		for ( var m = 0; m < meshes.Count; m++ )
		{
			var mesh = meshes[m];
			if ( ( mesh.Flags & ModelFormat.MeshFlags.StandsIn ) != 0 ) continue;

			//Another mesh's coarse level is not part of the model as it looks.
			if ( mesh.LevelOf >= 0 ) continue;

			var look = m < looks.Length ? looks[m] : null;

			for ( var i = 0u; i + 2 < mesh.IndexCount; i += 3 )
			{
				ref readonly var a = ref flat[(int)( mesh.FirstVertex + indices[(int)( mesh.FirstIndex + i )] )];
				ref readonly var b = ref flat[(int)( mesh.FirstVertex + indices[(int)( mesh.FirstIndex + i + 1 )] )];
				ref readonly var c = ref flat[(int)( mesh.FirstVertex + indices[(int)( mesh.FirstIndex + i + 2 )] )];

				drawn |= Triangle( in a, in b, in c, look, right, up, toward, frame.Left, frame.Wide, frame.Top, frame.Tall,
					raster.Wide, raster.Tall, raster.Depth, raster.Tint, raster.Facing, raster.Triangle, raster.Where, (int)( ( mesh.FirstIndex + i ) / 3 ) );
			}
		}

		return drawn ? raster : null;
	}

	//A piece's outline in a face's texels: the trimmed rectangle with its four corners cut along the diagonals as close to the covered texels
	//as they go, a texel and a half off them for the filter. Anticlockwise seen from the face - y runs down - beginning at the bottom left.
	private static List<(float X, float Y)> Outline( bool[] covered, int wide, (int X0, int Y0, int X1, int Y1) trim )
	{
		const float margin = 1.5f;

		float sumLow = float.MaxValue, sumHigh = float.MinValue, differenceLow = float.MaxValue, differenceHigh = float.MinValue;

		for ( var y = trim.Y0; y < trim.Y1; y++ )
		{
			for ( var x = trim.X0; x < trim.X1; x++ )
			{
				if ( !covered[( y * wide ) + x] ) continue;

				//The texel's corners: x + y least at its top left, most at its bottom right; x - y least at its bottom left, most at its top right.
				sumLow = MathF.Min( sumLow, x + y );
				sumHigh = MathF.Max( sumHigh, x + y + 2 );
				differenceLow = MathF.Min( differenceLow, x - y - 1 );
				differenceHigh = MathF.Max( differenceHigh, x - y + 1 );
			}
		}

		//The diagonals' margin is a texel and a half measured square to them.
		var diagonal = margin * MathF.Sqrt( 2.0f );

		var outline = new List<(float X, float Y)> { (trim.X0, trim.Y1), (trim.X1, trim.Y1), (trim.X1, trim.Y0), (trim.X0, trim.Y0) };
		outline = Clip( outline, 1, 1, sumLow - diagonal, true );
		outline = Clip( outline, 1, 1, sumHigh + diagonal, false );
		outline = Clip( outline, 1, -1, differenceLow - diagonal, true );
		outline = Clip( outline, 1, -1, differenceHigh + diagonal, false );

		return outline;
	}

	//The part of a convex outline where a·x + b·y is at least (or at most) limit, in the same order.
	private static List<(float X, float Y)> Clip( List<(float X, float Y)> outline, float a, float b, float limit, bool atLeast )
	{
		float Inside( (float X, float Y) p ) => atLeast ? ( a * p.X ) + ( b * p.Y ) - limit : limit - ( ( a * p.X ) + ( b * p.Y ) );

		var kept = new List<(float X, float Y)>();

		for ( var i = 0; i < outline.Count; i++ )
		{
			var here = outline[i];
			var next = outline[( i + 1 ) % outline.Count];
			var h = Inside( here );
			var n = Inside( next );

			if ( h >= 0.0f ) kept.Add( here );

			if ( ( h >= 0.0f ) != ( n >= 0.0f ) )
			{
				var t = h / ( h - n );
				kept.Add( (here.X + ( ( next.X - here.X ) * t ), here.Y + ( ( next.Y - here.Y ) * t )) );
			}
		}

		return kept.Count >= 3 ? kept : outline;
	}

	//Renders one face view into all mip levels: point-sampled finest level, vector-averaged coarser ones
	private bool DrawView( Raster raster, Occupancy grid, ImpostorFace face )
	{
		const int samples = ImpostorSamples;

		var frame = face.Frame;
		var right = face.Right;
		var up = face.Up;
		var toward = face.Toward;
		var wide = face.Wide;
		var tall = face.Tall;

		bool Mine( int at ) => raster.Depth[at] != float.MinValue;

		(face.Plane, face.Spread) = PlaneDepth( raster, frame );

		//A view's normal back into the model's space.
		Vector3 Model( Vector3 inView ) => ( right * inView.x ) + ( up * inView.y ) + ( toward * inView.z );

		//The sums of every finest texel's samples, for the levels above it: how many covered, and their colour and roughness, normal and depth added up.
		var sums = new Sums( wide * tall );

		//And the finest level, a point sample a texel.
		var level = new Level( wide, tall );
		var covered = new bool[wide * tall];
		int coveredX0 = wide, coveredY0 = tall, coveredX1 = 0, coveredY1 = 0;


		for ( var y = 0; y < tall; y++ )
		{
			for ( var x = 0; x < wide; x++ )
			{
				var here = ( y * wide ) + x;

				var count = 0;
				var colours = new Color( 0, 0, 0, 0 );
				var normals = new Vector3( 0, 0, 0 );
				var depths = 0.0f;

				for ( var sy = 0; sy < samples; sy++ )
				{
					for ( var sx = 0; sx < samples; sx++ )
					{
						var at = ( ( ( y * samples ) + sy ) * raster.Wide ) + ( x * samples ) + sx;
						if ( !Mine( at ) ) continue;

						count++;
						colours += raster.Tint[at];
						normals += raster.Facing[at];
						depths += raster.Depth[at];
					}
				}

				if ( count == 0 ) continue;

				covered[here] = true;
				coveredX0 = Math.Min( coveredX0, x );
				coveredY0 = Math.Min( coveredY0, y );
				coveredX1 = Math.Max( coveredX1, x + 1 );
				coveredY1 = Math.Max( coveredY1, y + 1 );

				//Darkened by how little sky that part of the surface sees past the rest of the
				//model, asked once a texel, where its surface is on average and along its normal.
				var across = frame.Left + ( ( x + 0.5f ) / wide * frame.Wide );
				var down = frame.Top - ( ( y + 0.5f ) / tall * frame.Tall );
				var outward = normals.Length > 1e-6f ? Model( normals.Normal ).Normal : toward;
				var point = ( right * across ) + ( up * down ) + ( toward * ( depths / count ) );
				var shade = 1.0f - ( ImpostorShade * ( 1.0f - grid.SkySeen( point, outward ) ) );

				//The tint's fourth number is the surface's roughness - see Triangle.
				sums.Count[here] = count;
				sums.Colour[here] = new Color( colours.r * shade, colours.g * shade, colours.b * shade, colours.a );
				sums.Normal[here] = Model( normals );

				//The point: the nearest of the four samples about the middle.
				var best = -1;

				for ( var sy = samples / 2 - 1; sy <= samples / 2; sy++ )
				{
					for ( var sx = samples / 2 - 1; sx <= samples / 2; sx++ )
					{
						var at = ( ( ( y * samples ) + sy ) * raster.Wide ) + ( x * samples ) + sx;
						if ( !Mine( at ) ) continue;
						if ( best < 0 || raster.Depth[at] > raster.Depth[best] ) best = at;
					}
				}

				if ( best < 0 ) continue;

				face.Picked[0][here] = raster.Triangle[best];
				face.PickedWhere[0][here] = raster.Where[best];

				var tint = raster.Tint[best];
				level.Set( here, new Color( tint.r * shade, tint.g * shade, tint.b * shade, 1.0f ),
					Model( raster.Facing[best] ).Normal, 1.0f, tint.a );
			}
		}

		if ( coveredX1 <= coveredX0 ) return false;

		//What the model covers, two texels more each way for the filter to read the air bled round it rather
		//than the next face in the sheet, out to whole FaceAligns so that every level has it in whole texels.
		face.Trim = (
			Math.Max( 0, ( coveredX0 - 2 ) / FaceAlign * FaceAlign ),
			Math.Max( 0, ( coveredY0 - 2 ) / FaceAlign * FaceAlign ),
			Math.Min( wide, Align( coveredX1 + 2, FaceAlign ) ),
			Math.Min( tall, Align( coveredY1 + 2, FaceAlign ) ));

		face.Outline = Outline( covered, wide, face.Trim );

		level.Write( face.Colour[0], face.Shape[0], toward );

		//The levels above, each from the sums under its texels.
		for ( var k = 1; k < ImpostorLevels; k++ )
		{
			var levelWide = wide >> k;
			var levelTall = tall >> k;
			var coarse = new Level( levelWide, levelTall );
			var span = 1 << k;

			for ( var y = 0; y < levelTall; y++ )
			{
				for ( var x = 0; x < levelWide; x++ )
				{
					var count = 0;
					var colours = new Color( 0, 0, 0, 0 );
					var normals = new Vector3( 0, 0, 0 );

					for ( var dy = 0; dy < span; dy++ )
					{
						for ( var dx = 0; dx < span; dx++ )
						{
							var at = ( ( ( y * span ) + dy ) * wide ) + ( x * span ) + dx;
							count += sums.Count[at];
							colours += sums.Colour[at];
							normals += sums.Normal[at];
						}
					}

					if ( count == 0 ) continue;

					var mean = normals * ( 1.0f / count );

					coarse.Set( ( y * levelWide ) + x, new Color( colours.r / count, colours.g / count, colours.b / count, count / (float)( span * span * samples * samples ) ),
						mean.Length > 1e-6f ? mean.Normal : toward, Math.Clamp( mean.Length, 0.0f, 1.0f ), colours.a / count );
				}
			}

			coarse.Write( face.Colour[k], face.Shape[k], toward );

			//And its triangles: the first of the four under each texel that has one, not a mean - a triangle cannot be averaged
			var finer = k - 1;
			var finerWide = wide >> finer;

			for ( var y = 0; y < levelTall; y++ )
			{
				for ( var x = 0; x < levelWide; x++ )
				{
					for ( var d = 0; d < 4; d++ )
					{
						var at = ( ( ( y * 2 ) + ( d >> 1 ) ) * finerWide ) + ( x * 2 ) + ( d & 1 );
						if ( face.Picked[finer][at] == 0 ) continue;

						face.Picked[k][( y * levelWide ) + x] = face.Picked[finer][at];
						face.PickedWhere[k][( y * levelWide ) + x] = face.PickedWhere[finer][at];
						break;
					}
				}
			}
		}

		return true;
	}

	//The samples of a view's finest texels, added up - see DrawView.
	private sealed class Sums
	{
		public readonly int[] Count;

		/// <summary>The colours shaded, and in the fourth number the roughnesses.</summary>
		public readonly Color[] Colour;
		public readonly Vector3[] Normal;

		public Sums( int texels )
		{
			Count = new int[texels];
			Colour = new Color[texels];
			Normal = new Vector3[texels];
		}
	}

	//One level of one view: what each texel holds, before the air round the model is bled and it is written.
	private sealed class Level
	{
		private readonly int wide, tall;
		private readonly bool[] covered;
		private readonly Color[] colours;
		private readonly Vector3[] shapes;

		//How much the normals under a texel agree, and its roughness.
		private readonly Vector2[] more;
		private readonly float[] cover;

		public Level( int wide, int tall )
		{
			this.wide = wide;
			this.tall = tall;
			covered = new bool[wide * tall];
			colours = new Color[wide * tall];
			shapes = new Vector3[wide * tall];
			more = new Vector2[wide * tall];
			cover = new float[wide * tall];
		}

		/// <summary>A texel the model reaches: its colour and cover, its normal and how much the normals under it agree, its roughness.</summary>
		public void Set( int at, Color colour, Vector3 normal, float agreement, float roughness )
		{
			covered[at] = true;
			cover[at] = colour.a;
			colours[at] = new Color( colour.r, colour.g, colour.b, 1.0f );
			shapes[at] = normal;
			more[at] = new Vector2( agreement, roughness );
		}

		/// <summary>The air round the model given the colour, normal and roughness of the surface beside it - so the levels the quad filters between do not rim the model in black - and the level into the two pictures.</summary>
		public void Write( byte[] colour, byte[] shape, Vector3 toward )
		{
			var reached = (bool[])covered.Clone();
			Bleed( reached, colours, shapes, more, wide, tall, toward );

			for ( var here = 0; here < wide * tall; here++ )
			{
				var at = here * 4;
				var c = colours[here];

				//Through the curve: the colour picture is read as sRGB.
				colour[at] = Byte( Curve( c.r ) );
				colour[at + 1] = Byte( Curve( c.g ) );
				colour[at + 2] = Byte( Curve( c.b ) );
				colour[at + 3] = Byte( cover[here] );

				//The normal folded to two numbers, how much the normals agree, the roughness: see impostor.shader, which unfolds them.
				var folded = Fold( shapes[here] );

				shape[at] = Byte( folded.x );
				shape[at + 1] = Byte( folded.y );
				shape[at + 2] = Byte( reached[here] ? more[here].x : 1.0f );
				shape[at + 3] = Byte( reached[here] ? more[here].y : 0.5f );
			}
		}
	}

	//How much leaf stands in each cell of the model's space: the area of its surface that its material
	//does not cut away, a cell at a time - for how much of the sky a point among the leaves sees.
	private sealed class Occupancy
	{
		private const int Cells = 48;

		//How much of the light a leaf crossing a cell face-on stops, on average over the angles it meets it at.
		private const float Stops = 0.5f;

		private readonly float[] depth;
		private readonly int nx, ny, nz;
		private readonly Vector3 low;
		private readonly float cell;

		public Occupancy( Look[] looks, ModelBuilder model, Vector3 low, Vector3 high )
		{
			var size = high - low;
			cell = MathF.Max( size.x, MathF.Max( size.y, size.z ) ) / Cells;
			this.low = low - new Vector3( cell, cell, cell );
			nx = (int)( size.x / cell ) + 3;
			ny = (int)( size.y / cell ) + 3;
			nz = (int)( size.z / cell ) + 3;
			depth = new float[nx * ny * nz];

			var area = new float[depth.Length];
			var flat = CollectionsMarshal.AsSpan( model.vertices );
			var random = new System.Random( 1 );

			for ( var m = 0; m < model.meshes.Count; m++ )
			{
				var mesh = model.meshes[m];
				if ( mesh.LevelOf >= 0 ) continue;

				var look = m < looks.Length ? looks[m] : null;

				for ( var i = 0u; i + 2 < mesh.IndexCount; i += 3 )
				{
					ref readonly var a = ref flat[(int)( mesh.FirstVertex + model.indices[(int)( mesh.FirstIndex + i )] )];
					ref readonly var b = ref flat[(int)( mesh.FirstVertex + model.indices[(int)( mesh.FirstIndex + i + 1 )] )];
					ref readonly var c = ref flat[(int)( mesh.FirstVertex + model.indices[(int)( mesh.FirstIndex + i + 2 )] )];

					var whole = ( b.Position - a.Position ).Cross( c.Position - a.Position ).Length * 0.5f;
					if ( !( whole > 0.0f ) ) continue;

					//Points over the triangle, as many as it has cells' worth of area, each carrying its share of the area where the picture is not cut.
					var points = Math.Clamp( (int)MathF.Ceiling( whole / ( cell * cell ) * 4.0f ), 1, 256 );
					var share = whole / points;

					for ( var p = 0; p < points; p++ )
					{
						var r1 = (float)random.NextDouble();
						var r2 = (float)random.NextDouble();
						if ( r1 + r2 > 1.0f ) { r1 = 1.0f - r1; r2 = 1.0f - r2; }

						var wa = 1.0f - r1 - r2;

						if ( look?.Pixels is not null && look.Masked )
						{
							var u = ( a.TexCoord0.x * wa ) + ( b.TexCoord0.x * r1 ) + ( c.TexCoord0.x * r2 );
							var v = ( a.TexCoord0.y * wa ) + ( b.TexCoord0.y * r1 ) + ( c.TexCoord0.y * r2 );
							u -= MathF.Floor( u );
							v -= MathF.Floor( v );

							var texel = look.Pixels[( Math.Min( (int)( v * look.Height ), look.Height - 1 ) * look.Width ) + Math.Min( (int)( u * look.Width ), look.Width - 1 )];
							if ( texel.a < look.Cutoff * 255.0f ) continue;
						}

						var at = Index( ( a.Position * wa ) + ( b.Position * r1 ) + ( c.Position * r2 ) );
						if ( at >= 0 ) area[at] += share;
					}
				}
			}

			//Leaf area in a cell over the cell's face, as the light's loss across it.
			for ( var i = 0; i < depth.Length; i++ ) depth[i] = Stops * area[i] / ( cell * cell );
		}

		private int Index( Vector3 at )
		{
			var x = (int)( ( at.x - low.x ) / cell );
			var y = (int)( ( at.y - low.y ) / cell );
			var z = (int)( ( at.z - low.z ) / cell );

			if ( x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz ) return -1;

			return ( ( ( z * ny ) + y ) * nx ) + x;
		}

		//The directions the sky is looked for in: the zenith and three rings of six.
		private static readonly Vector3[] Sky = MakeSky();

		private static Vector3[] MakeSky()
		{
			var made = new List<Vector3> { new( 0, 1, 0 ) };

			foreach ( var elevation in new[] { 12.0f, 40.0f, 68.0f } )
			{
				for ( var k = 0; k < 6; k++ )
				{
					var e = elevation * MathF.PI / 180.0f;
					var a = ( ( k * 60.0f ) + elevation ) * MathF.PI / 180.0f;
					made.Add( new Vector3( MathF.Cos( e ) * MathF.Cos( a ), MathF.Sin( e ), MathF.Cos( e ) * MathF.Sin( a ) ) );
				}
			}

			return made.ToArray();
		}

		/// <summary>The share of the sky a point sees past the leaves, stepping off its own leaf first.</summary>
		public float SkySeen( Vector3 point, Vector3 outward )
		{
			var start = point + ( outward * ( cell * 1.5f ) );
			var seen = 0.0f;

			foreach ( var direction in Sky )
			{
				var lost = 0.0f;
				var at = start;

				for ( var step = 0; step < 4 * Cells && lost < 6.0f; step++ )
				{
					at += direction * cell;

					var index = Index( at );
					if ( index < 0 ) break;

					lost += depth[index];
				}

				seen += MathF.Exp( -lost );
			}

			return seen / Sky.Length;
		}
	}

	private static byte Byte( float unit ) => (byte)Math.Clamp( (int)MathF.Round( unit * 255.0f ), 0, 255 );

	//A picture's byte, which is sRGB, as light.
	private static float Linear( byte value )
	{
		var c = value / 255.0f;
		return c <= 0.04045f ? c / 12.92f : MathF.Pow( ( c + 0.055f ) / 1.055f, 2.4f );
	}

	//Light back through the sRGB curve, for a picture read as colour.
	private static float Curve( float linear )
	{
		var c = Math.Clamp( linear, 0.0f, 1.0f );
		return c <= 0.0031308f ? c * 12.92f : ( 1.055f * MathF.Pow( c, 1.0f / 2.4f ) ) - 0.055f;
	}

	//A unit normal as two numbers from nought to one: the whole sphere folded as an octahedron, y up,
	//the lower half turned out over the corners. impostor.shader's ImpostorUnfold is the other way.
	private static Vector2 Fold( Vector3 n )
	{
		n /= MathF.Abs( n.x ) + MathF.Abs( n.y ) + MathF.Abs( n.z );

		var x = n.x;
		var z = n.z;

		if ( n.y < 0.0f )
		{
			x = ( 1.0f - MathF.Abs( n.z ) ) * ( n.x >= 0.0f ? 1.0f : -1.0f );
			z = ( 1.0f - MathF.Abs( n.x ) ) * ( n.z >= 0.0f ? 1.0f : -1.0f );
		}

		return new Vector2( ( x * 0.5f ) + 0.5f, ( z * 0.5f ) + 0.5f );
	}

	//One triangle into the samples, nearest wins; whether any sample took it.
	private static bool Triangle( in Vertex a, in Vertex b, in Vertex c, Look look, Vector3 right, Vector3 up, Vector3 toward,
		float left, float wide, float topAt, float tall, int across, int down, float[] depth, Color[] tint, Vector3[] facing,
		int[] triangle, Vector2[] where, int number )
	{
		//Onto the samples: x across from the left, y down from the top.
		float Sx( in Vertex v ) => ( v.Position.Dot( right ) - left ) / wide * across;
		float Sy( in Vertex v ) => ( topAt - v.Position.Dot( up ) ) / tall * down;

		float ax = Sx( a ), ay = Sy( a ), bx = Sx( b ), by = Sy( b ), cx = Sx( c ), cy = Sy( c );
		float az = a.Position.Dot( toward ), bz = b.Position.Dot( toward ), cz = c.Position.Dot( toward );

		var area = ( ( bx - ax ) * ( cy - ay ) ) - ( ( by - ay ) * ( cx - ax ) );
		if ( MathF.Abs( area ) < 1e-9f ) return false;

		var x0 = Math.Max( 0, (int)MathF.Floor( MathF.Min( ax, MathF.Min( bx, cx ) ) ) );
		var x1 = Math.Min( across - 1, (int)MathF.Ceiling( MathF.Max( ax, MathF.Max( bx, cx ) ) ) );
		var y0 = Math.Max( 0, (int)MathF.Floor( MathF.Min( ay, MathF.Min( by, cy ) ) ) );
		var y1 = Math.Min( down - 1, (int)MathF.Ceiling( MathF.Max( ay, MathF.Max( by, cy ) ) ) );

		if ( x0 > x1 || y0 > y1 ) return false;

		var inverse = 1.0f / area;
		var any = false;

		for ( var y = y0; y <= y1; y++ )
		{
			var py = y + 0.5f;

			for ( var x = x0; x <= x1; x++ )
			{
				var px = x + 0.5f;

				//Barycentric, by the areas the point makes with each edge.
				var wa = ( ( ( bx - px ) * ( cy - py ) ) - ( ( by - py ) * ( cx - px ) ) ) * inverse;
				var wb = ( ( ( cx - px ) * ( ay - py ) ) - ( ( cy - py ) * ( ax - px ) ) ) * inverse;
				var wc = 1.0f - wa - wb;

				if ( wa < 0.0f || wb < 0.0f || wc < 0.0f ) continue;

				var z = ( az * wa ) + ( bz * wb ) + ( cz * wc );
				var at = ( y * across ) + x;

				if ( z <= depth[at] ) continue;

				//What the material says here: its colour, and whether it cut it away.
				var colour = look?.Tint ?? Color.White;

				if ( look?.Pixels is not null )
				{
					var u = ( a.TexCoord0.x * wa ) + ( b.TexCoord0.x * wb ) + ( c.TexCoord0.x * wc );
					var v = ( a.TexCoord0.y * wa ) + ( b.TexCoord0.y * wb ) + ( c.TexCoord0.y * wc );

					u -= MathF.Floor( u );
					v -= MathF.Floor( v );

					var tx = Math.Min( (int)( u * look.Width ), look.Width - 1 );
					var ty = Math.Min( (int)( v * look.Height ), look.Height - 1 );
					var texel = look.Pixels[( ty * look.Width ) + tx];

					if ( look.Masked && texel.a < look.Cutoff * 255.0f ) continue;

					//In light, not in the picture's curve: the tint and an art surface's colour are linear, and so is
					//every mean the bake takes - the pictures are written back through the curve, see Level.Write.
					colour = new Color( Linear( texel.r ) * colour.r, Linear( texel.g ) * colour.g, Linear( texel.b ) * colour.b, 1.0f );
				}

				//And the corners' colour, authored in the curve like a picture's, where the shader paints with it
				if ( look?.VertexTint == true )
				{
					var r = ( Linear( a.Color.r ) * wa ) + ( Linear( b.Color.r ) * wb ) + ( Linear( c.Color.r ) * wc );
					var g = ( Linear( a.Color.g ) * wa ) + ( Linear( b.Color.g ) * wb ) + ( Linear( c.Color.g ) * wc );
					var bl = ( Linear( a.Color.b ) * wa ) + ( Linear( b.Color.b ) * wb ) + ( Linear( c.Color.b ) * wc );

					colour = new Color( colour.r * r, colour.g * g, colour.b * bl, 1.0f );
				}

				var normal = ( a.Normal * wa ) + ( b.Normal * wb ) + ( c.Normal * wc );
				normal = normal.Length > 1e-6f ? normal.Normal : toward;

				//Towards the eye whichever side it was: a leaf is seen from both.
				if ( normal.Dot( toward ) < 0.0f ) normal = -normal;

				depth[at] = z;
				triangle[at] = number + 1;
				where[at] = new Vector2( wb, wc );

				//Its fourth number the surface's roughness, which rides with the colour into the mean of a pixel's samples - see DrawView.
				tint[at] = new Color( colour.r, colour.g, colour.b, look?.Roughness ?? 0.5f );
				facing[at] = new Vector3( normal.Dot( right ), normal.Dot( up ), normal.Dot( toward ) );
				any = true;
			}
		}

		return any;
	}

	//Uncovered pixels take the mean of their covered neighbours, a ring at a time.
	private static void Bleed( bool[] covered, Color[] colours, Vector3[] shapes, Vector2[] extra, int wide, int tall, Vector3 outward )
	{
		var next = new bool[covered.Length];

		for ( var ring = 0; ring < 8; ring++ )
		{
			System.Array.Copy( covered, next, covered.Length );

			for ( var y = 0; y < tall; y++ )
			{
				for ( var x = 0; x < wide; x++ )
				{
					var here = ( y * wide ) + x;
					if ( covered[here] ) continue;

					var count = 0;
					var sum = new Color( 0, 0, 0, 0 );
					var normal = new Vector3( 0, 0, 0 );
					var more = new Vector2( 0, 0 );

					for ( var dy = -1; dy <= 1; dy++ )
					{
						for ( var dx = -1; dx <= 1; dx++ )
						{
							int nx = x + dx, ny = y + dy;
							if ( nx < 0 || ny < 0 || nx >= wide || ny >= tall ) continue;

							var there = ( ny * wide ) + nx;
							if ( !covered[there] ) continue;

							count++;
							sum += colours[there];
							normal += shapes[there];
							more += extra[there];
						}
					}

					if ( count == 0 ) continue;

					colours[here] = sum * ( 1.0f / count );
					extra[here] = more / count;
					shapes[here] = normal.Length > 1e-6f ? normal.Normal : outward;
					next[here] = true;
				}
			}

			System.Array.Copy( next, covered, covered.Length );
		}

		//And what no ring reached, a flat normal facing out.
		for ( var i = 0; i < covered.Length; i++ )
			if ( !covered[i] ) shapes[i] = outward;
	}


	//Most clusters a model's first level may have for a texel to name one - see ImpostorPicture.
	internal const int PictureClusters = ( 1 << 13 ) - 1;

	//A word a texel: level-nought cluster + 1 (13 bits), triangle (7), second and third corner weights (6 each); see impostor_surface.slang.
	//Each plane's corners hold their own cluster's number in TexCoord0.z. None where the cut does not allow it.
	private void ImpostorPicture( LevelChain.Build built, Span<Vertex> written )
	{
		impostorPicture = null;
		if ( impostorPicked is null || impostorMesh < 0 ) return;

		var clusters = CollectionsMarshal.AsSpan( built.Clusters );
		var corners = new Dictionary<(uint, uint, uint), (int Cluster, int Triangle, uint A, uint B, uint C)>();
		var planes = new int[impostorMeshes];
		System.Array.Fill( planes, -1 );

		for ( var i = 0; i < clusters.Length; i++ )
		{
			ref readonly var cluster = ref clusters[i];
			var part = (int)( cluster.partLevel & 0xFFFFu );

			if ( part >= impostorMesh && part < impostorMesh + impostorMeshes )
			{
				//A plane in more clusters than one cannot say which it is with one number
				if ( planes[part - impostorMesh] >= 0 ) return;

				planes[part - impostorMesh] = i;
				continue;
			}

			if ( ( cluster.partLevel >> 16 ) != 0 ) continue;
			if ( i >= PictureClusters ) return;

			var triangles = (int)( cluster.counts >> 16 );

			for ( var t = 0; t < triangles; t++ )
			{
				var from = (int)( ( cluster.triangleOffset + (uint)t ) * 3 );
				var a = built.Vertices[(int)( cluster.vertexOffset + built.Triangles[from] )];
				var b = built.Vertices[(int)( cluster.vertexOffset + built.Triangles[from + 1] )];
				var c = built.Vertices[(int)( cluster.vertexOffset + built.Triangles[from + 2] )];

				corners.TryAdd( Sorted( a, b, c ), (i, t, a, b, c) );
			}
		}

		for ( var p = 0; p < impostorMeshes; p++ )
			if ( planes[p] < 0 ) return;

		var chain = new List<(byte[], int, int)>( ImpostorLevels );

		for ( var k = 0; k < ImpostorLevels; k++ )
		{
			var picked = impostorPicked[k];
			var where = impostorPickedWhere[k];
			var bytes = new byte[picked.Length * 4];

			for ( var at = 0; at < picked.Length; at++ )
			{
				if ( picked[at] == 0 ) continue;

				var number = picked[at] - 1;
				if ( !Corners( number, out var ca, out var cb, out var cc ) ) continue;
				if ( !corners.TryGetValue( Sorted( ca, cb, cc ), out var found ) ) continue;

				//The weights by corner, put in the cluster's order of the same three.
				var weights = ( A: 1.0f - where[at].x - where[at].y, B: where[at].x, C: where[at].y );
				float Weight( uint vertex ) => vertex == ca ? weights.A : vertex == cb ? weights.B : weights.C;

				var second = (uint)Math.Clamp( MathF.Round( Weight( found.B ) * 63.0f ), 0.0f, 63.0f );
				var third = (uint)Math.Clamp( MathF.Round( Weight( found.C ) * 63.0f ), 0.0f, 63.0f );
				var word = ( (uint)( found.Cluster + 1 ) << 19 ) | ( (uint)found.Triangle << 12 ) | ( second << 6 ) | third;

				bytes[( at * 4 ) + 0] = (byte)word;
				bytes[( at * 4 ) + 1] = (byte)( word >> 8 );
				bytes[( at * 4 ) + 2] = (byte)( word >> 16 );
				bytes[( at * 4 ) + 3] = (byte)( word >> 24 );
			}

			chain.Add( (bytes, impostorSheetWide >> k, impostorSheetTall >> k) );
		}

		var picture = CoreTextureFile.BuildLevels( chain, srgb: false, compress: false, keepAlpha: true );
		if ( picture is null ) return;

		for ( var p = 0; p < impostorMeshes; p++ )
		{
			var mesh = meshes[impostorMesh + p];

			for ( var v = mesh.FirstVertex; v < mesh.FirstVertex + mesh.VertexCount && v < written.Length; v++ )
				written[(int)v].TexCoord0.z = planes[p];
		}

		impostorPicture = picture;
	}

	//The three corners of a triangle by its number in the index list, as the model numbers its vertices.
	private bool Corners( int number, out uint a, out uint b, out uint c )
	{
		a = b = c = 0;
		var index = (uint)number * 3;

		foreach ( var mesh in meshes )
		{
			if ( index < mesh.FirstIndex || index + 3 > mesh.FirstIndex + mesh.IndexCount ) continue;

			a = mesh.FirstVertex + indices[(int)index];
			b = mesh.FirstVertex + indices[(int)index + 1];
			c = mesh.FirstVertex + indices[(int)index + 2];
			return true;
		}

		return false;
	}

	private static (uint, uint, uint) Sorted( uint a, uint b, uint c )
	{
		if ( a > b ) (a, b) = (b, a);
		if ( b > c ) (b, c) = (c, b);
		if ( a > b ) (a, b) = (b, a);
		return (a, b, c);
	}

	//Makes the impostor the top LOD level by error, bounded by the sphere with no cone
	private void LinkImpostor( LevelChain.Build built )
	{
		if ( impostorMesh < 0 || !( impostorError > 0.0f ) ) return;

		var centre = new Vector3( impostorInfo.centre[0], impostorInfo.centre[1], impostorInfo.centre[2] );
		var radius = impostorInfo.radius;
		var level = built.Levels;
		var clusters = CollectionsMarshal.AsSpan( built.Clusters );

		for ( var i = 0; i < clusters.Length; i++ )
		{
			var cluster = clusters[i];
			var part = cluster.partLevel & 0xFFFFu;

			if ( part >= (uint)impostorMesh && part < (uint)( impostorMesh + impostorMeshes ) )
			{
				cluster.error = impostorError;
				cluster.parentError = 1e30f;
				cluster.partLevel = ( level << 16 ) | part;

				cluster.centre[0] = centre.x;
				cluster.centre[1] = centre.y;
				cluster.centre[2] = centre.z;
				cluster.radius = radius;

				//The quads are inside the model's own box, which bounds them closer than the sphere - but the one drawn of the sides is
				//turned about the upright through the centre, so across it reaches the box's corner either way. See impostor.shader.
				var across = MathF.Sqrt( ( impostorInfo.extent[0] * impostorInfo.extent[0] ) + ( impostorInfo.extent[2] * impostorInfo.extent[2] ) );

				for ( var k = 0; k < 3; k++ )
				{
					var half = k == 1 ? impostorInfo.extent[k] : across;
					cluster.boxMin[k] = impostorInfo.centre[k] - half;
					cluster.boxMax[k] = impostorInfo.centre[k] + half;
				}

				cluster.coneCutoff = 1.0f;
			}
			else if ( cluster.parentError > impostorError )
			{
				cluster.parentError = impostorError;
			}

			clusters[i] = cluster;
		}

		built.Levels = level + 1;
	}
}

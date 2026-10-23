using SkiaSharp;
using System.IO;
using System.Runtime.InteropServices;

namespace Editor.TerrainEditor;

/// <summary>Click to lay a height map over a square of the ground: brush size is half its side, brush rotation turns it.<br/> <br/> <b>Ctrl</b> - press it in instead</summary>
[Title( "Stamp" )]
[Icon( "approval" )]
[Alias( "tools.terrain.stamp" )]
[Group( "1" )]
[Order( 0 )]
public class StampTool : BaseBrushTool
{
	/// <summary>What is stamped and how.</summary>
	public class StampSettings
	{
		/// <summary>A grey picture or a square raw file of 8 or 16 bits: white is the full height, black nothing.</summary>
		[FilePath( "png,jpg,tga,raw,r16", AllowExternal = true )]
		public string HeightMap { get; set; }

		/// <summary>How high white stands, in metres.</summary>
		[Range( 0.1f, 500.0f )]
		public float Height { get; set; } = 20.0f;

		/// <summary>Raise adds the shape, Lower presses it in, Set makes the ground the shape from where it is clicked.</summary>
		public TerrainMesh.StampMode Mode { get; set; } = TerrainMesh.StampMode.Raise;
	}

	public static StampSettings Settings { get; } = new();

	//One stamp a click, however long the button is held.
	bool stamped;

	public StampTool( TerrainEditorTool terrainEditorTool ) : base( terrainEditorTool )
	{
		AllowBrushInvert = true;
	}

	public override void OnUpdate()
	{
		if ( !Gizmo.IsLeftMouseDown ) stamped = false;

		base.OnUpdate();
	}

	protected override void OnDab( Terrain terrain, TerrainMesh mesh, Vector3 centre, float radius, float amount, Vector3 viewDirection )
	{
		if ( stamped ) return;
		stamped = true;

		if ( Load( Settings.HeightMap ) is not { } map ) return;

		var mode = Settings.Mode;
		//Ctrl turns raise and lower about; a set stamp has no other way round
		if ( amount < 0.0f && mode != TerrainMesh.StampMode.Set ) mode = mode == TerrainMesh.StampMode.Raise ? TerrainMesh.StampMode.Lower : TerrainMesh.StampMode.Raise;

		var turn = _parent.BrushSettings.Rotation;
		var height = Settings.Height;

		mesh.Stamp( centre, radius, turn, map.At, height, mode );

		//The slopes it made cut to the brush's edge length, over all of it top to bottom; a set stamp laid again on what that added.
		var reach = MathF.Sqrt( ( 2.0f * radius * radius ) + ( height * height ) );
		var middle = centre + ( Vector3.Up * ( mode == TerrainMesh.StampMode.Lower ? -height : height ) * 0.5f );

		mesh.Remesh( middle, reach, MathF.Max( terrain.Storage.EdgeLength, 0.05f ) );

		if ( mode == TerrainMesh.StampMode.Set ) mesh.Stamp( centre, radius, turn, map.At, height, mode );
	}

	//A height map as it was read, kept while its file is unchanged.
	sealed class HeightMap
	{
		public int Width, Height;
		public float[] Values;
		public DateTime Written;

		//Nought to one between the four samples round a point, (0, 0) to (1, 1) across the picture.
		public float At( float u, float v )
		{
			var x = Math.Clamp( u, 0.0f, 1.0f ) * ( Width - 1 );
			var y = Math.Clamp( v, 0.0f, 1.0f ) * ( Height - 1 );
			var x0 = (int)x;
			var y0 = (int)y;
			var x1 = Math.Min( x0 + 1, Width - 1 );
			var y1 = Math.Min( y0 + 1, Height - 1 );
			var fx = x - x0;
			var fy = y - y0;

			var top = MathX.Lerp( Values[( y0 * Width ) + x0], Values[( y0 * Width ) + x1], fx );
			var bottom = MathX.Lerp( Values[( y1 * Width ) + x0], Values[( y1 * Width ) + x1], fx );

			return MathX.Lerp( top, bottom, fy );
		}
	}

	static HeightMap loaded;
	static string loadedFrom;

	static HeightMap Load( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		var full = Path.IsPathRooted( path ) ? path : FileSystem.Content?.GetFullPath( path ) ?? path;
		if ( !File.Exists( full ) ) return null;

		var written = File.GetLastWriteTimeUtc( full );
		if ( loaded is not null && loadedFrom == full && loaded.Written == written ) return loaded;

		try
		{
			var ext = Path.GetExtension( full ).ToLowerInvariant();
			loaded = ext is ".raw" or ".r16" ? ReadRaw( full ) : ReadPicture( full );
		}
		catch ( Exception e )
		{
			Log.Warning( $"{path}: not read as a height map - {e.Message}" );
			loaded = null;
		}

		if ( loaded is not null ) loaded.Written = written;
		loadedFrom = full;

		return loaded;
	}

	//Square, 16 bits little-end first where its size says so, else 8.
	static HeightMap ReadRaw( string full )
	{
		var bytes = File.ReadAllBytes( full );
		var wide16 = (int)MathF.Round( MathF.Sqrt( bytes.Length / 2.0f ) );
		var sixteen = wide16 * wide16 * 2 == bytes.Length;
		var side = sixteen ? wide16 : (int)MathF.Round( MathF.Sqrt( bytes.Length ) );

		if ( side < 2 || ( !sixteen && side * side != bytes.Length ) ) throw new InvalidDataException( "not a square of 8 or 16 bit samples" );

		var values = new float[side * side];

		for ( var i = 0; i < values.Length; i++ )
			values[i] = sixteen ? BitConverter.ToUInt16( bytes, i * 2 ) / 65535.0f : bytes[i] / 255.0f;

		return new HeightMap { Width = side, Height = side, Values = values };
	}

	//The red of each pixel, read at full precision so a 16 bit picture keeps its steps.
	static HeightMap ReadPicture( string full )
	{
		using var codec = SKCodec.Create( full ) ?? throw new InvalidDataException( "not a picture" );

		var info = new SKImageInfo( codec.Info.Width, codec.Info.Height, SKColorType.RgbaF32, SKAlphaType.Unpremul );
		using var bitmap = new SKBitmap( info );

		if ( codec.GetPixels( info, bitmap.GetPixels() ) is not ( SKCodecResult.Success or SKCodecResult.IncompleteInput ) )
			throw new InvalidDataException( "could not be decoded" );

		var pixels = MemoryMarshal.Cast<byte, float>( bitmap.GetPixelSpan() );
		var values = new float[info.Width * info.Height];

		for ( var i = 0; i < values.Length; i++ ) values[i] = Math.Clamp( pixels[i * 4], 0.0f, 1.0f );

		return new HeightMap { Width = info.Width, Height = info.Height, Values = values };
	}
}

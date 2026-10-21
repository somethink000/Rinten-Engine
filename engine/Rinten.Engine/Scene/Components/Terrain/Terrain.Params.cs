using NativeEngine;
using System;

namespace Rinten;

public partial class Terrain
{
	//Terrain shader parameters packed into a texture, since mesh draws can't bind custom buffers
	//Layout is shared with shaders/engine/terrain.shader; change both together
	internal Texture ParamsMap { get; private set; }

	//A weight each on the drawn vertex over its colour and both UVs, for the chunk's palette - see TerrainChunkBaker
	//The shader's MaxLayers is the same number
	/// <summary>How many layers one chunk of ground draws.</summary>
	public const int MaxLayers = 8;

	/// <summary>How many layers a terrain may list; a point wears four at most - see TerrainPaint.</summary>
	public const int MostLayers = 32;

	//How wide the picture is: a column a layer, with room to spare.
	private const int ParamsWide = 64;

	//And how many rows it has.
	private const int ParamsHigh = 8;

	private const int RowTerrain = 0;

	//Colour index low, normal index high.
	private const int RowLayerMaps = 1;

	//Displacement in metres low, as a half; height index high.
	private const int RowLayerPacks = 2;

	//One over the layer's uv scale, as a whole float.
	private const int RowLayerScale = 3;

	//Roughness, metalness, flags, spare - a byte each.
	private const int RowLayerBytes = 4;

	//Normal strength low, height strength high, as halves.
	private const int RowLayerStrengths = 5;

	//Roughness map index low, occlusion map index high.
	private const int RowLayerMaps2 = 6;

	//The ring of relief round the eye - see FillRelief.
	private const int RowRing = 7;

	//Current and last-sent bytes, so frames where nothing changed skip the upload
	private byte[] paramsBytes;
	private byte[] paramsSent;

	//Writes the numbers picture, making it first if there is not one.
	internal void UpdateParamsMap()
	{
		if ( Application.IsHeadless || Storage is null ) return;

		paramsBytes ??= new byte[ParamsWide * ParamsHigh * 4];
		paramsSent ??= new byte[ParamsWide * ParamsHigh * 4];

		//A freshly created texture is empty, so it must be uploaded regardless of the last-sent copy
		var fresh = ParamsMap is null;

		ParamsMap ??= Texture.Create( ParamsWide, ParamsHigh, ImageFormat.LINEAR_RGBA8888 )
			.WithName( "terrain_params" )
			.Finish();

		if ( ParamsMap is null ) return;

		Array.Clear( paramsBytes );

		Fill();

		if ( !fresh && paramsBytes.AsSpan().SequenceEqual( paramsSent ) ) return;

		ParamsMap.Update( new ReadOnlySpan<byte>( paramsBytes ) );
		paramsBytes.CopyTo( paramsSent, 0 );
	}

	//The picture's contents, out of what the terrain knows now.
	private void Fill()
	{
		var flags = 0u;
		if ( Storage.MaterialSettings.HeightBlendEnabled ) flags |= 1u;

		WriteBits( 5, RowTerrain, flags | ( Fraction( Storage.MaterialSettings.HeightBlendSharpness ) << 16 ) );
		WriteShorts( 6, RowTerrain, Storage.Materials.Count, 0 );

		for ( var i = 0; i < ParamsWide; i++ )
		{
			var layer = Storage.Materials.ElementAtOrDefault( i );

			//Layer maps and numbers read by name from the material, defaults from its shader; missing maps are 0
			WriteShorts( i, RowLayerMaps,
				LayerPicture( layer, "Color", srgb: true ),
				LayerPicture( layer, "Normal", srgb: false ) );

			WriteShorts( i, RowLayerPacks,
				BitConverter.HalfToUInt16Bits( (Half)CoreResources.Number( layer?.core, "g_flDisplacement", 0.0f ) ),
				LayerPicture( layer, "g_tHeight", srgb: false ) );

			WriteShorts( i, RowLayerMaps2,
				LayerPicture( layer, "g_tRoughness", srgb: false ),
				LayerPicture( layer, "g_tOcclusion", srgb: false ) );

			//Inverted here rather than in the shader: it is one divide a layer against one per pixel of ground.
			WriteFloat( i, RowLayerScale,
				1.0f / MathF.Max( CoreResources.Number( layer?.core, "g_flUvScale", 1.0f ), 1e-4f ) );

			//Roughness is what the layer's map is multiplied by, nought to two, so it is written halved and the shader doubles it back.
			WriteBits( i, RowLayerBytes,
				Fraction( ( layer?.GetFloat( "Roughness", 0.5f ) ?? 0.5f ) * 0.5f, 255 )
				| ( Fraction( layer?.GetFloat( "Metalness", 0.0f ) ?? 0.0f, 255 ) << 8 )
				| ( ( (uint)LayerFlags( layer ) & 0xFF ) << 16 ) );

			WriteHalves( i, RowLayerStrengths,
				CoreResources.Number( layer?.core, "g_flNormalStrength", 1.0f ),
				CoreResources.Number( layer?.core, "g_flHeightBlend", 1.0f ) );
		}

		FillRelief();
		FillMask();
	}

	//Bindless index of a layer's compiled texture, looked up per frame since slots get reused
	private int LayerPicture( Material layer, string name, bool srgb )
	{
		var texture = layer?.GetTexture( name );
		if ( texture is null ) return 0;

		var path = texture.ResourcePath;
		if ( string.IsNullOrWhiteSpace( path ) || !CoreNative.Available ) return texture.Index;

		if ( pictureGeneration != CoreResources.Generation )
		{
			pictures.Clear();
			pictureGeneration = CoreResources.Generation;
		}

		var key = (path, srgb);

		if ( pictures.TryGetValue( key, out var held ) )
		{
			if ( held.Handle != 0 && CoreRender.TextureIndex( held.Handle ) is var live && live != 0 )
				return (int)live;

			//Never had: asked again, but not every frame. One that was had and has gone is asked again now.
			if ( held.Handle == 0 && RealTime.NowDouble < held.RetryAt ) return texture.Index;
		}

		var handle = CoreResources.Texture( path, srgb );
		var at = handle != 0 ? CoreRender.TextureIndex( handle ) : 0;

		pictures[key] = at != 0 ? (handle, 0.0) : (0ul, RealTime.NowDouble + PictureRetrySeconds);

		return at != 0 ? (int)at : texture.Index;
	}

	private readonly Dictionary<(string, bool), (ulong Handle, double RetryAt)> pictures = new();
	private int pictureGeneration;

	//How long a compiled picture that could not be had is left before it is asked for again.
	private const double PictureRetrySeconds = 0.5;

	//What a layer switched on for itself.
	private static TerrainFlags LayerFlags( Material layer )
	{
		var flags = TerrainFlags.None;

		if ( CoreResources.Number( layer?.core, "g_bNoTiling", 0.0f ) > 0.5f )
			flags |= TerrainFlags.NoTile;

		return flags;
	}

	//A fraction of one as so many steps of an integer - a byte, or sixteen bits.
	private static uint Fraction( float value, int steps = 65535 )
		=> (uint)Math.Clamp( MathF.Round( Math.Clamp( value, 0.0f, 1.0f ) * steps ), 0.0f, steps );

	//One number into one texel, as its own four bytes.
	private void WriteFloat( int x, int y, float value )
		=> WriteBits( x, y, BitConverter.SingleToUInt32Bits( value ) );

	//Two whole numbers into one texel, sixteen bits each.
	private void WriteShorts( int x, int y, int low, int high )
		=> WriteBits( x, y, ( (uint)Math.Clamp( low, 0, 65535 ) ) | ( (uint)Math.Clamp( high, 0, 65535 ) << 16 ) );

	//Two halves into one texel.
	private void WriteHalves( int x, int y, float low, float high )
		=> WriteBits( x, y, BitConverter.HalfToUInt16Bits( (Half)low )
			| ( (uint)BitConverter.HalfToUInt16Bits( (Half)high ) << 16 ) );

	//Thirty-two bits into one texel, low byte first - the order the shader puts them back in.
	private void WriteBits( int x, int y, uint bits )
	{
		var at = ( ( y * ParamsWide ) + x ) * 4;

		paramsBytes[at + 0] = (byte)( bits & 0xFF );
		paramsBytes[at + 1] = (byte)( ( bits >> 8 ) & 0xFF );
		paramsBytes[at + 2] = (byte)( ( bits >> 16 ) & 0xFF );
		paramsBytes[at + 3] = (byte)( ( bits >> 24 ) & 0xFF );
	}
}

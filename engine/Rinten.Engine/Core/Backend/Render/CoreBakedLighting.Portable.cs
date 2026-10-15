using System.Runtime.InteropServices;

namespace Rinten;

//The bake as the portable frame binds it (g_bakedSheet, g_bakedVolume): sheet light, face and sky, ≤ 2048 a side,
//the volume's layers as tiles with the live bounce below; glow layers folded in at their brightness
internal static partial class CoreBakedLighting
{
	/// <summary>The portable frame's sheet for the frame being written, nought for none - its set binds it.</summary>
	internal static ulong PortableSheet { get; private set; }

	/// <summary>And its volume.</summary>
	internal static ulong PortableVolume { get; private set; }

	//The most a side of either picture is, which every phone takes
	private const int PortableSide = 2048;
	private const int PortableWidest = 4096;

	//One bake's two pictures, the shape they were laid out in and what was folded into them
	private sealed class PortablePictures
	{
		public ulong Sheet, Volume;
		public int SheetWidth, SheetHeight, Columns, Rows;
		public readonly float[] Glow = new float[BakedGlow.Groups];
		public int Landed = -1;

		public void Release()
		{
			if ( Sheet != 0 ) CoreRenderDevice.DestroyTexture( Sheet );
			if ( Volume != 0 ) CoreRenderDevice.DestroyTexture( Volume );
			Sheet = Volume = 0;
		}
	}

	private static bool WritePortable( float[] frame, Held on, BakedLightingData data, Scene scene )
	{
		Span<float> brightness = stackalloc float[BakedGlow.Groups];
		GlowBrightness( scene, brightness );

		var made = on.Portable;

		//Made again only where a glow group's brightness moved: the rest of the bake never changes
		if ( made is null || !brightness.SequenceEqual( made.Glow ) )
		{
			made?.Release();
			made = on.Portable = MakePortable( data, brightness );
		}

		on.Bounce?.Tick( scene );

		if ( on.Bounce is { Solved: { } solved } bounce && bounce.Landed != made.Landed && made.Volume != 0 )
		{
			WritePortableBounce( made, data, solved );
			made.Landed = bounce.Landed;
		}

		PortableSheet = made.Sheet;
		PortableVolume = made.Volume;

		var at = CoreSceneRenderer.BakedAt;

		if ( made.Volume != 0 )
		{
			frame[at + 8] = data.VolumeOrigin.x;
			frame[at + 9] = data.VolumeOrigin.y;
			frame[at + 10] = data.VolumeOrigin.z;
			frame[at + 11] = 1.0f / data.VolumeStep;

			frame[at + 12] = data.VolumeX;
			frame[at + 13] = data.VolumeY;
			frame[at + 14] = data.VolumeZ;
		}

		var portable = CoreSceneRenderer.PortableBakeAt;

		frame[portable + 0] = made.Sheet != 0 ? made.SheetWidth : 0.0f;
		frame[portable + 1] = made.SheetHeight;
		frame[portable + 2] = made.Volume != 0 ? made.Columns : 0.0f;
		frame[portable + 3] = made.Landed >= 0 ? 1.0f : 0.0f;

		return made.Sheet != 0 || made.Volume != 0;
	}

	//Each group's brightness now: its brightest switched-on BakedGlow, nought where none is on
	private static void GlowBrightness( Scene scene, Span<float> brightness )
	{
		brightness.Clear();

		foreach ( var glow in scene.GetAllComponents<BakedGlow>() )
		{
			if ( !glow.IsValid() || !glow.Active ) continue;
			brightness[glow.Layer] = MathF.Max( brightness[glow.Layer], MathF.Max( glow.Brightness, 0.0f ) );
		}
	}

	private static PortablePictures MakePortable( BakedLightingData data, ReadOnlySpan<float> brightness )
	{
		var made = new PortablePictures();
		brightness.CopyTo( made.Glow );

		if ( data.HasSheet ) MakePortableSheet( made, data, brightness );
		if ( data.HasVolume ) MakePortableVolume( made, data, brightness );

		return made;
	}

	//Halved until it fits, each texel the mean of those it covers on the face most of them lie on: light and glows rgb,
	//and in a the face's mark (BakeSheet.FaceOf, a whole number) with the sky seen as the fraction - see PortableSheet
	private static void MakePortableSheet( PortablePictures made, BakedLightingData data, ReadOnlySpan<float> brightness )
	{
		var step = 1;
		while ( data.SheetWidth / step > PortableSide || data.SheetHeight / step > PortableSide ) step *= 2;

		var width = Math.Max( data.SheetWidth / step, 1 );
		var height = Math.Max( data.SheetHeight / step, 1 );
		var pixels = new Half[width * height * 4];

		var sky = data.HasSky && data.SheetSky.Length == data.Sheet.Length ? data.SheetSky : null;
		Span<int> marks = stackalloc int[step * step];

		for ( var y = 0; y < height; ++y )
		for ( var x = 0; x < width; ++x )
		{
			//The face most of the covered texels lie on, which is the only one they are averaged over
			for ( var i = 0; i < marks.Length; ++i )
				marks[i] = MarkAt( data, ( ( ( y * step ) + ( i / step ) ) * data.SheetWidth ) + ( x * step ) + ( i % step ) );

			var mark = Commonest( marks );

			System.Numerics.Vector4 light = default;
			var count = 0;

			for ( var i = 0; i < marks.Length; ++i )
			{
				if ( marks[i] != mark ) continue;

				var texel = ( ( ( ( y * step ) + ( i / step ) ) * data.SheetWidth ) + ( x * step ) + ( i % step ) ) * 4;

				var r = (float)data.Sheet[texel];
				var g = (float)data.Sheet[texel + 1];
				var b = (float)data.Sheet[texel + 2];

				for ( var k = 0; k < data.Glows.Count && k < BakedGlow.Groups; ++k )
				{
					var glow = data.Glows[k];
					if ( glow.Sheet is not { } layer || layer.Length != data.Sheet.Length ) continue;

					var by = brightness[Math.Clamp( glow.Group, 1, BakedGlow.Groups ) - 1];
					r += (float)layer[texel] * by;
					g += (float)layer[texel + 1] * by;
					b += (float)layer[texel + 2] * by;
				}

				light += new System.Numerics.Vector4( r, g, b, sky is null ? 0.0f : (float)sky[texel + 3] );
				count++;
			}

			light /= Math.Max( count, 1 );
			light.W = mark + ( Math.Clamp( light.W, 0.0f, 1.0f ) * PortableSkyShare );

			Put( pixels, ( ( y * width ) + x ) * 4, light );
		}

		made.Sheet = CoreRenderDevice.CreateTextureHalf( pixels, width, height );
		made.SheetWidth = width;
		made.SheetHeight = height;
	}

	//What of a portable sheet texel's fraction the sky seen takes: under one by enough that a half, a thirty-second
	//apart at sixty-four, never rounds the mark up into the next face's
	private const float PortableSkyShare = 0.9f;

	//A sheet texel's face mark as a whole number, nought for a texel no face owns - see BakeSheet.FaceOf
	private static int MarkAt( BakedLightingData data, int texel ) => (int)MathF.Round( (float)data.Sheet[( texel * 4 ) + 3] * 64.0f );

	private static int Commonest( ReadOnlySpan<int> marks )
	{
		var best = marks[0];
		var most = 0;

		foreach ( var mark in marks )
		{
			var count = 0;
			foreach ( var other in marks ) if ( other == mark ) count++;

			if ( count > most || ( count == most && best == 0 ) )
			{
				best = mark;
				most = count;
			}
		}

		return best;
	}

	//Each layer a tile, as many to a row as fit: light and glow constants rgb, sky seen a; the bounce half below
	private static void MakePortableVolume( PortablePictures made, BakedLightingData data, ReadOnlySpan<float> brightness )
	{
		var columns = Math.Clamp( PortableWidest / data.VolumeX, 1, data.VolumeY );
		var rows = ( data.VolumeY + columns - 1 ) / columns;

		var width = columns * data.VolumeX;
		var height = rows * data.VolumeZ * 2;
		if ( width > PortableWidest || height > PortableWidest ) return;

		var pixels = new Half[width * height * 4];
		var sky = data.HasSky ? data.VolumeSky : null;

		for ( var z = 0; z < data.VolumeZ; ++z )
		for ( var y = 0; y < data.VolumeY; ++y )
		for ( var x = 0; x < data.VolumeX; ++x )
		{
			var point = ( x + ( data.VolumeX * ( y + ( data.VolumeY * z ) ) ) ) * 4;

			var light = new System.Numerics.Vector4( (float)data.VolumeRed[point], (float)data.VolumeGreen[point], (float)data.VolumeBlue[point],
				sky is null ? 0.0f : (float)sky[point] );

			for ( var i = 0; i < data.Glows.Count && i < BakedGlow.Groups; ++i )
			{
				var glow = data.Glows[i];
				if ( glow.Volume is not { } layer || layer.Length != data.VolumeRed.Length ) continue;

				var by = brightness[Math.Clamp( glow.Group, 1, BakedGlow.Groups ) - 1];
				light += new System.Numerics.Vector4( (float)layer[point] * by, (float)layer[point + 1] * by, (float)layer[point + 2] * by, 0.0f );
			}

			Put( pixels, TileTexel( made, data, x, y, z, width, columns, false ), light );
		}

		made.Columns = columns;
		made.Rows = rows;
		made.Volume = CoreRenderDevice.CreateTextureHalf( pixels, width, height );
	}

	//A solve's colours into the bounce half, in place
	private static void WritePortableBounce( PortablePictures made, BakedLightingData data, Half[] solved )
	{
		var width = made.Columns * data.VolumeX;
		var half = made.Rows * data.VolumeZ;
		var pixels = new Half[width * half * 4];

		for ( var z = 0; z < data.VolumeZ; ++z )
		for ( var y = 0; y < data.VolumeY; ++y )
		for ( var x = 0; x < data.VolumeX; ++x )
		{
			var point = ( x + ( data.VolumeX * ( y + ( data.VolumeY * z ) ) ) ) * 4;
			var texel = TileTexel( made, data, x, y, z, width, made.Columns, false );

			pixels[texel] = solved[point];
			pixels[texel + 1] = solved[point + 1];
			pixels[texel + 2] = solved[point + 2];
			pixels[texel + 3] = (Half)1.0f;
		}

		CoreRenderDevice.WriteTextureLevel( made.Volume, 0, 0, half, width, half, MemoryMarshal.AsBytes( pixels.AsSpan() ), width * 8 );
	}

	//Where a point lands among the tiles, in halves; the bounce half starts as many rows down as the light's take
	private static int TileTexel( PortablePictures made, BakedLightingData data, int x, int y, int z, int width, int columns, bool bounce )
	{
		var column = ( ( y % columns ) * data.VolumeX ) + x;
		var row = ( ( y / columns ) * data.VolumeZ ) + z + ( bounce ? made.Rows * data.VolumeZ : 0 );

		return ( ( row * width ) + column ) * 4;
	}

	private static void Put( Half[] into, int at, System.Numerics.Vector4 value )
	{
		into[at] = (Half)MathF.Min( value.X, 60000.0f );
		into[at + 1] = (Half)MathF.Min( value.Y, 60000.0f );
		into[at + 2] = (Half)MathF.Min( value.Z, 60000.0f );
		into[at + 3] = (Half)MathF.Min( value.W, 60000.0f );
	}
}

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Rinten;

/// <summary>A bake: atlas, directions, volume and each renderer's square, kept in the scene's binary half.</summary>
public sealed class BakedLightingData : BlobData
{
	public override int Version => 9;

	/// <summary>One renderer's square of the atlas, and what it was when it was baked.</summary>
	public struct Place
	{
		/// <summary>The renderer, by its component's id.</summary>
		public Guid Renderer;

		/// <summary>The model it drew; another model has another unwrap and reads the volume until rebaked.</summary>
		public string Model;

		/// <summary>Scale in xy, offset in zw - see ModelRenderer.LightmapRect.</summary>
		public Vector4 Rect;

		/// <summary>Its model's vertices: a MeshComponent edited since has another sheet, and reads the volume.</summary>
		public int Vertices;
	}

	public int SheetWidth { get; set; }
	public int SheetHeight { get; set; }

	/// <summary>Four halves a texel: the light arriving, over pi, and one where the texel is a surface's.</summary>
	public Half[] Sheet { get; set; } = [];

	/// <summary>Four halves a texel: where that light mostly came from, and its lean onto the normal.</summary>
	public Half[] Ways { get; set; } = [];

	public List<Place> Places { get; set; } = new();

	public int VolumeX { get; set; }
	public int VolumeY { get; set; }
	public int VolumeZ { get; set; }
	public Vector3 VolumeOrigin { get; set; }
	public float VolumeStep { get; set; }

	/// <summary>A colour channel each, four halves a point: the constant, then the lean along x, y and z.</summary>
	public Half[] VolumeRed { get; set; } = [];
	public Half[] VolumeGreen { get; set; } = [];
	public Half[] VolumeBlue { get; set; } = [];

	/// <summary>Every light all in the bake - its light, soft shadows and bounces; the frame draws none of them.</summary>
	public List<Guid> Baked { get; set; } = new();

	/// <summary>One BakedGlow group's light alone, scaled by its brightness in the frame.</summary>
	public struct Glow
	{
		/// <summary>The group, one to four - see BakedGlow.Group.</summary>
		public int Group;

		/// <summary>Four halves a texel of the sheet: its light over pi, and one where the texel is a surface's.</summary>
		public Half[] Sheet;

		/// <summary>Four halves a point of the volume: its light's constant, and one.</summary>
		public Half[] Volume;
	}

	/// <summary>The glow groups baked apart from the rest, each its own layer.</summary>
	public List<Glow> Glows { get; set; } = new();

	/// <summary>Four halves a texel: which way the sky is open and how much is seen; the frame lights it.</summary>
	public Half[] SheetSky { get; set; } = [];

	/// <summary>Whether the volume holds only the baked lights' bounce, the frame drawing their direct light.</summary>
	public bool VolumeIndirect { get; set; }

	/// <summary>The live bounce sheet's blocks across and down; nought where the bake has none.</summary>
	public int BounceWidth { get; set; }
	public int BounceHeight { get; set; }

	/// <summary>Each block's slot in TexelLinks, -1 for a block with none.</summary>
	public int[] TexelSlots { get; set; } = [];

	/// <summary>Each slot's surface and facing bin, which the frame smooths the bounce within.</summary>
	public int[] TexelKeys { get; set; } = [];

	/// <summary>The surfel clusters each slot's block sees, and the share of its view each fills.</summary>
	public ushort[] TexelLinks { get; set; } = [];
	public Half[] TexelWeights { get; set; } = [];

	/// <summary>Whether the live bounce can be laid at the lightmap's detail, not read from the volume.</summary>
	public bool HasTexelBounce => BounceWidth > 0 && BounceHeight > 0 && TexelSlots.Length == BounceWidth * BounceHeight
		&& TexelLinks.Length > 0 && TexelLinks.Length == TexelWeights.Length && TexelKeys.Length * Bake.BakeSurfels.TexelLinks == TexelLinks.Length;

	/// <summary>The sheet's first band over its light, half size - see BakeSheet.Band; empty before version 6.</summary>
	public byte[] SheetBand { get; set; } = [];

	/// <summary>Whether the sheet's first band is kept, for the light to lean into a normal map by colour.</summary>
	public bool HasBand => HasSheet && SheetBand.Length == Math.Max( SheetWidth / 2, 1 ) * Math.Max( SheetHeight / 2, 1 ) * 12;

	/// <summary>Four halves a point: how much sky is seen, then its lean along x, y, z.</summary>
	public Half[] VolumeSky { get; set; } = [];

	/// <summary>Twelve bytes a cell, as three rows of the volume's picture: how far each corner sees into it.</summary>
	public byte[] VolumeWalls { get; set; } = [];

	/// <summary>The sun whose bounce the frame relights, empty for none; the sky's bounce is relit with it.</summary>
	public Guid LiveSun { get; set; }

	/// <summary>Points on the still world the bounce is relit through, BakeSurfels.Floats floats each.</summary>
	public float[] Surfels { get; set; } = [];

	/// <summary>Each surfel's sun visibility by direction, a bit each - see BakeSurfels.Sun.</summary>
	public byte[] SurfelSun { get; set; } = [];

	/// <summary>Each surfel's free distance by direction, a byte each - see BakeSurfels.Depth.</summary>
	public byte[] SurfelDepth { get; set; } = [];

	/// <summary>Every light whose bounce is in the bake; the frame relights the rest's - see CoreLiveBounce.</summary>
	public List<Guid> Bounced { get; set; } = new();

	/// <summary>Each surfel's cluster - see BakeSurfels.Cluster.</summary>
	public ushort[] SurfelClusters { get; set; } = [];

	/// <summary>The clusters each surfel sees, BakeSurfels.Links each, and the share of its view they fill.</summary>
	public ushort[] SurfelLinks { get; set; } = [];
	public Half[] SurfelWeights { get; set; } = [];

	/// <summary>The clusters each volume point sees, laid out as the surfels' own.</summary>
	public ushort[] ProbeLinks { get; set; } = [];
	public Half[] ProbeWeights { get; set; } = [];

	/// <summary>Whether the frame can relight the sun's and sky's bounce - see CoreLiveBounce.</summary>
	public bool HasBounce
	{
		get
		{
			if ( !HasVolume || Surfels.Length == 0 || Surfels.Length % Bake.BakeSurfels.Floats != 0 ) return false;

			var count = Surfels.Length / Bake.BakeSurfels.Floats;

			return SurfelSun.Length == count * Bake.BakeSurfels.SunBytes
				&& SurfelDepth.Length == count * Bake.BakeSurfels.DepthBytes && SurfelClusters.Length == count
				&& SurfelLinks.Length == count * Bake.BakeSurfels.Links && SurfelWeights.Length == SurfelLinks.Length
				&& ProbeLinks.Length == VolumeX * VolumeY * VolumeZ * Bake.BakeSurfels.Links && ProbeWeights.Length == ProbeLinks.Length;
		}
	}

	/// <summary>Whether the volume knows its walls, so a point reads only the corners on its side of one.</summary>
	public bool HasWalls => HasVolume && VolumeWalls.Length == VolumeX * VolumeY * VolumeZ * 12;

	/// <summary>Whether the sky is kept as visibility; a bake before version 3 has it in its light.</summary>
	public bool HasSky => HasVolume && VolumeSky.Length == VolumeRed.Length;

	//New with every change and unique across bakes, so the card and the reflection probes know to catch up
	internal int Revision { get; private set; }

	private static int stamped;

	public BakedLightingData() => Touched();

	public bool HasSheet => SheetWidth > 0 && SheetHeight > 0 && Sheet.Length >= SheetWidth * SheetHeight * 4 && Ways.Length == Sheet.Length;

	public bool HasVolume => VolumeX > 0 && VolumeY > 0 && VolumeZ > 0 && VolumeStep > 0.0f
		&& VolumeRed.Length >= VolumeX * VolumeY * VolumeZ * 4
		&& VolumeGreen.Length == VolumeRed.Length && VolumeBlue.Length == VolumeRed.Length;

	public bool IsEmpty => !HasSheet && !HasVolume;

	internal void Touched() => Revision = System.Threading.Interlocked.Increment( ref stamped );

	public override void Serialize( ref Writer writer )
	{
		var s = writer.Stream;

		s.Write( SheetWidth );
		s.Write( SheetHeight );
		Packed( ref s, Sheet );
		Packed( ref s, Ways );

		s.Write( Places.Count );

		foreach ( var place in Places )
		{
			s.Write( place.Renderer );
			s.Write( place.Model ?? "" );
			s.Write( place.Rect );
		}

		s.Write( VolumeX );
		s.Write( VolumeY );
		s.Write( VolumeZ );
		s.Write( VolumeOrigin );
		s.Write( VolumeStep );
		Packed( ref s, VolumeRed );
		Packed( ref s, VolumeGreen );
		Packed( ref s, VolumeBlue );

		//The lights in the bake, where version 2 to 4 kept masks and channels.
		s.Write( Baked.Count );
		foreach ( var light in Baked ) s.Write( light );

		//Version 3: the glow groups and the sky's visibility.
		s.Write( Glows.Count );

		foreach ( var glow in Glows )
		{
			s.Write( glow.Group );
			Packed( ref s, glow.Sheet ?? [] );
			Packed( ref s, glow.Volume ?? [] );
		}

		Packed( ref s, SheetSky );
		Packed( ref s, VolumeSky );

		//Version 4: the volume's walls and the surfels.
		PackedBytes( ref s, VolumeWalls );

		s.Write( LiveSun );
		Packed( ref s, Surfels );
		PackedBytes( ref s, SurfelSun );
		Packed( ref s, SurfelLinks );
		Packed( ref s, SurfelWeights );
		Packed( ref s, ProbeLinks );
		Packed( ref s, ProbeWeights );
		PackedBytes( ref s, SurfelDepth );
		Packed( ref s, SurfelClusters );

		//Version 6: the sheet's first band.
		PackedBytes( ref s, SheetBand );

		//Version 7: each place's vertices.
		foreach ( var place in Places ) s.Write( place.Vertices );

		//Version 8: whether the volume left the baked lights' direct light to the frame.
		s.Write( VolumeIndirect );

		//Version 9: the live bounce's sheet links.
		s.Write( BounceWidth );
		s.Write( BounceHeight );
		Packed( ref s, TexelSlots );
		Packed( ref s, TexelKeys );
		Packed( ref s, TexelLinks );
		Packed( ref s, TexelWeights );

		writer.Stream = s;
	}

	/// <summary>An older bake: what its version held, the rest empty. Its masked lights are drawn in the frame.</summary>
	public override void Upgrade( ref Reader reader, int fromVersion )
	{
		//From version 5 on the layout starts over - see ReadFifth
		if ( fromVersion >= 5 )
		{
			ReadFifth( ref reader );
			if ( fromVersion >= 6 ) ReadBand( ref reader );
			if ( fromVersion >= 7 ) ReadVertices( ref reader );

			if ( fromVersion >= 8 )
			{
				var s = reader.Stream;
				VolumeIndirect = s.Read<bool>();
				reader.Stream = s;
			}

			return;
		}

		ReadFirst( ref reader );

		if ( fromVersion == 1 ) IsOld = true;
		if ( fromVersion >= 2 ) ReadMasks( ref reader );
		if ( fromVersion >= 3 ) ReadThird( ref reader );

		if ( fromVersion >= 4 ) ReadFourth( ref reader, true );

		Touched();
	}

	//Whether this is a first-version bake, which names no lights - see Upgrade
	internal bool IsOld { get; private set; }

	public override void Deserialize( ref Reader reader )
	{
		ReadFifth( ref reader );
		ReadBand( ref reader );
		ReadVertices( ref reader );

		var s = reader.Stream;
		VolumeIndirect = s.Read<bool>();
		BounceWidth = s.Read<int>();
		BounceHeight = s.Read<int>();
		TexelSlots = Unpacked<int>( ref s );
		TexelKeys = Unpacked<int>( ref s );
		TexelLinks = Unpacked<ushort>( ref s );
		TexelWeights = Unpacked( ref s );
		reader.Stream = s;
	}

	//What version 7 added: each place's vertices
	private void ReadVertices( ref Reader reader )
	{
		var s = reader.Stream;

		for ( var i = 0; i < Places.Count; ++i )
		{
			var place = Places[i];
			place.Vertices = s.Read<int>();
			Places[i] = place;
		}

		reader.Stream = s;
	}

	//What version 6 added: the sheet's first band
	private void ReadBand( ref Reader reader )
	{
		var s = reader.Stream;
		SheetBand = UnpackedBytes( ref s );
		reader.Stream = s;
	}

	//What version 5 held: the first, the lights in the bake, the third and the fourth
	private void ReadFifth( ref Reader reader )
	{
		ReadFirst( ref reader );

		var s = reader.Stream;
		var baked = s.Read<int>();
		Baked = new List<Guid>( baked );
		for ( var i = 0; i < baked; ++i ) Baked.Add( s.Read<Guid>() );
		reader.Stream = s;

		ReadThird( ref reader );
		ReadFourth( ref reader, false );

		Bounced = new List<Guid>( Baked );
		Touched();
	}

	//What version 4 added: the volume's walls and the surfels; it kept the bounced lights apart, after them
	private void ReadFourth( ref Reader reader, bool bounced )
	{
		var s = reader.Stream;
		VolumeWalls = UnpackedBytes( ref s );

		LiveSun = s.Read<Guid>();
		Surfels = Unpacked<float>( ref s );
		SurfelSun = UnpackedBytes( ref s );
		SurfelLinks = Unpacked<ushort>( ref s );
		SurfelWeights = Unpacked<Half>( ref s );
		ProbeLinks = Unpacked<ushort>( ref s );
		ProbeWeights = Unpacked<Half>( ref s );
		SurfelDepth = UnpackedBytes( ref s );
		SurfelClusters = Unpacked<ushort>( ref s );

		if ( bounced )
		{
			var count = s.Read<int>();
			Bounced = new List<Guid>( count );
			for ( var i = 0; i < count; ++i ) Bounced.Add( s.Read<Guid>() );
		}

		reader.Stream = s;
	}

	//What version 3 added: the glow groups and the sky's visibility
	private void ReadThird( ref Reader reader )
	{
		var s = reader.Stream;

		var glows = s.Read<int>();
		Glows = new List<Glow>( glows );

		for ( var i = 0; i < glows; ++i )
			Glows.Add( new Glow { Group = s.Read<int>(), Sheet = Unpacked( ref s ), Volume = Unpacked( ref s ) } );

		SheetSky = Unpacked( ref s );
		VolumeSky = Unpacked( ref s );

		reader.Stream = s;
	}

	//What versions 2 to 4 kept after the first: the masks, read past, and each light's channel - only those baked
	//whole are in the bake; the bounces of all of them are
	private void ReadMasks( ref Reader reader )
	{
		var s = reader.Stream;

		UnpackedBytes( ref s );
		UnpackedBytes( ref s );

		var lights = s.Read<int>();
		Baked = new List<Guid>();
		Bounced = new List<Guid>( lights );

		for ( var i = 0; i < lights; ++i )
		{
			var light = s.Read<Guid>();
			if ( s.Read<int>() < 0 ) Baked.Add( light );
			Bounced.Add( light );
		}

		reader.Stream = s;
	}

	//What version 1 held, which version 2 begins with.
	private void ReadFirst( ref Reader reader )
	{
		var s = reader.Stream;

		SheetWidth = s.Read<int>();
		SheetHeight = s.Read<int>();
		Sheet = Unpacked( ref s );
		Ways = Unpacked( ref s );

		var places = s.Read<int>();
		Places = new List<Place>( places );

		for ( var i = 0; i < places; ++i )
		{
			Places.Add( new Place
			{
				Renderer = s.Read<Guid>(),
				Model = s.Read<string>(),
				Rect = s.Read<Vector4>(),
			} );
		}

		VolumeX = s.Read<int>();
		VolumeY = s.Read<int>();
		VolumeZ = s.Read<int>();
		VolumeOrigin = s.Read<Vector3>();
		VolumeStep = s.Read<float>();
		VolumeRed = Unpacked( ref s );
		VolumeGreen = Unpacked( ref s );
		VolumeBlue = Unpacked( ref s );

		reader.Stream = s;
	}

	private static void PackedBytes( ref ByteStream s, byte[] values )
	{
		using var memory = new MemoryStream();

		using ( var deflate = new DeflateStream( memory, CompressionLevel.Optimal, leaveOpen: true ) )
			deflate.Write( values );

		var bytes = memory.ToArray();

		s.Write( values.Length );
		s.Write( bytes.Length );
		s.Write( bytes );
	}

	private static byte[] UnpackedBytes( ref ByteStream s )
	{
		var count = s.Read<int>();
		var length = s.Read<int>();

		var bytes = new byte[length];
		s.Read( bytes, 0, length );

		var values = new byte[count];

		using var deflate = new DeflateStream( new MemoryStream( bytes ), CompressionMode.Decompress );
		deflate.ReadExactly( values );

		return values;
	}

	//Through deflate, length first. Light is smooth, and most of an atlas is the same few values over and over.
	private static void Packed<T>( ref ByteStream s, T[] values ) where T : unmanaged
	{
		using var memory = new MemoryStream();

		using ( var deflate = new DeflateStream( memory, CompressionLevel.Optimal, leaveOpen: true ) )
			deflate.Write( MemoryMarshal.AsBytes( values.AsSpan() ) );

		var bytes = memory.ToArray();

		s.Write( values.Length );
		s.Write( bytes.Length );
		s.Write( bytes );
	}

	private static Half[] Unpacked( ref ByteStream s ) => Unpacked<Half>( ref s );

	private static T[] Unpacked<T>( ref ByteStream s ) where T : unmanaged
	{
		var count = s.Read<int>();
		var length = s.Read<int>();

		var bytes = new byte[length];
		s.Read( bytes, 0, length );

		var values = new T[count];

		using var deflate = new DeflateStream( new MemoryStream( bytes ), CompressionMode.Decompress );
		deflate.ReadExactly( MemoryMarshal.AsBytes( values.AsSpan() ) );

		return values;
	}
}

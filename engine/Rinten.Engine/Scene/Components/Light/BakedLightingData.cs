using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Rinten;

/// <summary>A bake: atlas, directions, volume and each renderer's square, kept in the scene's binary half.</summary>
public sealed class BakedLightingData : BlobData
{
	public override int Version => 2;

	/// <summary>One renderer's square of the atlas, and what it was when it was baked.</summary>
	public struct Place
	{
		/// <summary>The renderer, by its component's id.</summary>
		public Guid Renderer;

		/// <summary>The model it drew; another model has another unwrap and reads the volume until rebaked.</summary>
		public string Model;

		/// <summary>Scale in xy, offset in zw - see ModelRenderer.LightmapRect.</summary>
		public Vector4 Rect;
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

	/// <summary>Four bytes a texel: each masked light's share through the still world; empty where none.</summary>
	public byte[] SheetMask { get; set; } = [];

	/// <summary>The same at each point of the volume, for what moves - laid out as its colours are.</summary>
	public byte[] VolumeMask { get; set; } = [];

	/// <summary>A light the bake holds something of, and what: its mask channel, or the whole of it.</summary>
	public struct BakedLight
	{
		/// <summary>The light component.</summary>
		public Guid Light;

		/// <summary>The mask channel holding its shadow, nought to three, or -1 where it is baked whole.</summary>
		public int Channel;
	}

	/// <summary>Every light the bake took more than the bounces of.</summary>
	public List<BakedLight> Lights { get; set; } = new();

	public bool HasSheetMask => HasSheet && SheetMask.Length >= SheetWidth * SheetHeight * 4;

	public bool HasVolumeMask => HasVolume && VolumeMask.Length >= VolumeX * VolumeY * VolumeZ * 4;

	//Counts every change, so whoever put it on the card knows to put it there again.
	internal int Revision { get; private set; }

	public bool HasSheet => SheetWidth > 0 && SheetHeight > 0 && Sheet.Length >= SheetWidth * SheetHeight * 4 && Ways.Length == Sheet.Length;

	public bool HasVolume => VolumeX > 0 && VolumeY > 0 && VolumeZ > 0 && VolumeStep > 0.0f
		&& VolumeRed.Length >= VolumeX * VolumeY * VolumeZ * 4
		&& VolumeGreen.Length == VolumeRed.Length && VolumeBlue.Length == VolumeRed.Length;

	public bool IsEmpty => !HasSheet && !HasVolume;

	internal void Touched() => Revision++;

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

		//Version 2: the masks and the lights.
		PackedBytes( ref s, SheetMask );
		PackedBytes( ref s, VolumeMask );

		s.Write( Lights.Count );

		foreach ( var light in Lights )
		{
			s.Write( light.Light );
			s.Write( light.Channel );
		}

		writer.Stream = s;
	}

	/// <summary>A version 1 bake: no masks and no lights named, so every light stays in the frame.</summary>
	public override void Upgrade( ref Reader reader, int fromVersion )
	{
		if ( fromVersion >= 2 )
		{
			Deserialize( ref reader );
			return;
		}

		ReadFirst( ref reader );
		SheetMask = [];
		VolumeMask = [];
		Lights = new List<BakedLight>();
		IsOld = true;

		Touched();
	}

	//Whether this came out of a bake from before the masks - see Upgrade.
	internal bool IsOld { get; private set; }

	public override void Deserialize( ref Reader reader )
	{
		ReadFirst( ref reader );

		var s = reader.Stream;

		SheetMask = UnpackedBytes( ref s );
		VolumeMask = UnpackedBytes( ref s );

		var lights = s.Read<int>();
		Lights = new List<BakedLight>( lights );

		for ( var i = 0; i < lights; ++i )
			Lights.Add( new BakedLight { Light = s.Read<Guid>(), Channel = s.Read<int>() } );

		reader.Stream = s;
		Touched();
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

	//Halves through deflate, length first. Light is smooth, and most of an atlas is the same few values over and over.
	private static void Packed( ref ByteStream s, Half[] values )
	{
		using var memory = new MemoryStream();

		using ( var deflate = new DeflateStream( memory, CompressionLevel.Optimal, leaveOpen: true ) )
			deflate.Write( MemoryMarshal.AsBytes( values.AsSpan() ) );

		var bytes = memory.ToArray();

		s.Write( values.Length );
		s.Write( bytes.Length );
		s.Write( bytes );
	}

	private static Half[] Unpacked( ref ByteStream s )
	{
		var count = s.Read<int>();
		var length = s.Read<int>();

		var bytes = new byte[length];
		s.Read( bytes, 0, length );

		var values = new Half[count];

		using var deflate = new DeflateStream( new MemoryStream( bytes ), CompressionMode.Decompress );
		deflate.ReadExactly( MemoryMarshal.AsBytes( values.AsSpan() ) );

		return values;
	}
}

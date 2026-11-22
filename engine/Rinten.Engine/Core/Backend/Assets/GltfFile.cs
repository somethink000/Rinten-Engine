using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace Rinten;

//An opened glTF file with every accessor decoded once to plain arrays; import-time only
internal sealed class GltfFile
{
	public sealed class BufferView
	{
		public int Buffer;
		public int ByteOffset;
		public int ByteLength;
		public int ByteStride;
	}

	public sealed class Accessor
	{
		public int BufferView = -1;
		public int ByteOffset;
		public int ComponentType;
		public bool Normalized;
		public int Count;
		public string Type = "SCALAR";

		public int SparseCount;
		public int SparseIndicesView = -1;
		public int SparseIndicesOffset;
		public int SparseIndicesComponentType;
		public int SparseValuesView = -1;
		public int SparseValuesOffset;

		/// <summary>Every value, decoded once. Count × components floats.</summary>
		public float[] Floats;
	}

	public sealed class Node
	{
		public string Name;
		public int Mesh = -1;
		public int Skin = -1;
		public int Parent = -1;
		public int[] Children = Array.Empty<int>();

		public float[] Matrix;
		public float[] Translation;
		public float[] Rotation;
		public float[] Scale;
	}

	public sealed class Primitive
	{
		public readonly Dictionary<string, int> Attributes = new( StringComparer.Ordinal );
		public int Indices = -1;
		public int Material = -1;
		public int Mode = 4;
	}

	public sealed class Mesh
	{
		public string Name;
		public Primitive[] Primitives = Array.Empty<Primitive>();
	}

	public sealed class Skin
	{
		public int[] Joints = Array.Empty<int>();
		public int InverseBindMatrices = -1;
	}

	public sealed class AnimationSampler
	{
		public int Input = -1;
		public int Output = -1;
		public string Interpolation = "LINEAR";
	}

	public sealed class AnimationChannel
	{
		public int Sampler = -1;
		public int TargetNode = -1;
		public string TargetPath = "";
	}

	public sealed class Animation
	{
		public AnimationSampler[] Samplers = Array.Empty<AnimationSampler>();
		public AnimationChannel[] Channels = Array.Empty<AnimationChannel>();

		/// <summary>What the file calls it, where it calls it anything.</summary>
		public string Name = "";
	}

	public Node[] Nodes = Array.Empty<Node>();
	public Mesh[] Meshes = Array.Empty<Mesh>();
	public Skin[] Skins = Array.Empty<Skin>();
	public Animation[] Animations = Array.Empty<Animation>();
	public string[] MaterialNames = Array.Empty<string>();

	/// <summary>What each material says its surface is in numbers, beside its name - see <see cref="ModelFormat.Surface"/>.</summary>
	public ModelFormat.Surface[] MaterialSurfaces = Array.Empty<ModelFormat.Surface>();

	/// <summary>Each material's base colour picture, as the encoded file it is - a PNG or a JPEG - or null where it has none or it cannot be read. For whatever builds a model straight from the file rather than through a material definition; the importers name materials and leave the pictures to them.</summary>
	public byte[][] MaterialColourPictures = Array.Empty<byte[]>();

	/// <summary>Whether each material draws both faces.</summary>
	public bool[] MaterialTwoSided = Array.Empty<bool>();

	/// <summary>A picture a material samples: the file a URI names, or a view of the file's own bytes.</summary>
	public sealed class Picture
	{
		public string File;
		public int View = -1;
		public byte[] Inline;
	}

	/// <summary>The pictures and alpha a material names, for writing a material definition from it.</summary>
	public sealed class MaterialMaps
	{
		public Picture Colour, Normal, Emission, RoughMetal, Occlusion;

		//What the rough/metal map is multiplied by; glTF's defaults
		public float Roughness = 1.0f, Metalness = 1.0f;

		public string AlphaMode = "OPAQUE";
		public float AlphaCutoff = 0.5f;
	}

	public MaterialMaps[] MaterialPictures = Array.Empty<MaterialMaps>();

	private Accessor[] accessors = Array.Empty<Accessor>();
	private BufferView[] views = Array.Empty<BufferView>();
	private byte[][] buffers = Array.Empty<byte[]>();

	public Accessor AccessorAt( int index )
		=> index >= 0 && index < accessors.Length ? accessors[index] : null;

	public static int ComponentsOf( string type ) => type switch
	{
		"SCALAR" => 1,
		"VEC2" => 2,
		"VEC3" => 3,
		"VEC4" => 4,
		"MAT2" => 4,
		"MAT3" => 9,
		"MAT4" => 16,
		_ => 0,
	};

	/// <summary>Opens a file, or null when it is not a glTF anybody can read. Both containers: JSON with buffers beside or inside it, and GLB.</summary>
	public static GltfFile Load( string path )
	{
		byte[] bytes;

		try
		{
			ImportReads.File( path );
			bytes = SealedFile.ReadAllBytes( path );
		}
		catch ( IOException ) { return null; }
		catch ( UnauthorizedAccessException ) { return null; }

		return Read( bytes, Path.GetDirectoryName( path ) );
	}

	/// <summary>A file already in memory - a GLB, or JSON whose buffers are inside it, since there is no folder to look for them in. What a headset's runtime hands over is the first.</summary>
	public static GltfFile Load( byte[] bytes ) => bytes is null ? null : Read( bytes, null );

	private static GltfFile Read( byte[] bytes, string directory )
	{
		try
		{
			byte[] binChunk = null;
			ReadOnlyMemory<byte> json;

			if ( bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian( bytes ) == 0x46546C67 ) //"glTF"
			{
				//GLB: a 12-byte header, then chunks - JSON first by the spec, the binary buffer usually after it.
				var at = 12;
				ReadOnlyMemory<byte> found = default;

				while ( at + 8 <= bytes.Length )
				{
					var length = (int)BinaryPrimitives.ReadUInt32LittleEndian( bytes.AsSpan( at ) );
					var kind = BinaryPrimitives.ReadUInt32LittleEndian( bytes.AsSpan( at + 4 ) );

					if ( at + 8 + length > bytes.Length ) break;

					if ( kind == 0x4E4F534A ) found = bytes.AsMemory( at + 8, length ); //"JSON"
					if ( kind == 0x004E4942 ) binChunk = bytes.AsSpan( at + 8, length ).ToArray(); //"BIN\0"

					at += 8 + length;

					//Chunks are padded to four bytes; length says the unpadded size.
					at += ( 4 - ( length % 4 ) ) % 4;
				}

				if ( found.IsEmpty ) return null;
				json = found;
			}
			else
			{
				json = bytes;
			}

			using var document = JsonDocument.Parse( json );

			var file = new GltfFile();

			if ( !file.Parse( document.RootElement, directory, binChunk ) ) return null;

			return file;
		}
		catch ( JsonException ) { return null; }
		catch ( FormatException ) { return null; } //a data: URI that is not base64
	}

	private bool Parse( JsonElement root, string directory, byte[] binChunk )
	{
		//Buffers first - everything else points into them.
		if ( root.TryGetProperty( "buffers", out var bufferList ) )
		{
			buffers = new byte[bufferList.GetArrayLength()][];

			var at = 0;

			foreach ( var entry in bufferList.EnumerateArray() )
			{
				if ( entry.TryGetProperty( "uri", out var uri ) )
				{
					buffers[at] = ReadBufferUri( uri.GetString(), directory );
				}
				else
				{
					//No URI is the GLB binary chunk, and only the first buffer may be it.
					buffers[at] = at == 0 ? binChunk : null;
				}

				buffers[at] ??= Array.Empty<byte>();
				++at;
			}
		}

		if ( root.TryGetProperty( "bufferViews", out var viewList ) )
		{
			views = new BufferView[viewList.GetArrayLength()];

			var at = 0;

			foreach ( var entry in viewList.EnumerateArray() )
			{
				views[at++] = new BufferView
				{
					Buffer = IntOf( entry, "buffer" ),
					ByteOffset = IntOf( entry, "byteOffset" ),
					ByteLength = IntOf( entry, "byteLength" ),
					ByteStride = IntOf( entry, "byteStride" ),
				};
			}
		}

		if ( root.TryGetProperty( "accessors", out var accessorList ) )
		{
			accessors = new Accessor[accessorList.GetArrayLength()];

			var at = 0;

			foreach ( var entry in accessorList.EnumerateArray() )
			{
				var accessor = new Accessor
				{
					BufferView = IntOf( entry, "bufferView", -1 ),
					ByteOffset = IntOf( entry, "byteOffset" ),
					ComponentType = IntOf( entry, "componentType" ),
					Normalized = entry.TryGetProperty( "normalized", out var n ) && n.GetBoolean(),
					Count = IntOf( entry, "count" ),
					Type = StringOf( entry, "type" ) ?? "SCALAR",
				};

				if ( entry.TryGetProperty( "sparse", out var sparse ) )
				{
					accessor.SparseCount = IntOf( sparse, "count" );

					if ( sparse.TryGetProperty( "indices", out var indices ) )
					{
						accessor.SparseIndicesView = IntOf( indices, "bufferView", -1 );
						accessor.SparseIndicesOffset = IntOf( indices, "byteOffset" );
						accessor.SparseIndicesComponentType = IntOf( indices, "componentType" );
					}

					if ( sparse.TryGetProperty( "values", out var values ) )
					{
						accessor.SparseValuesView = IntOf( values, "bufferView", -1 );
						accessor.SparseValuesOffset = IntOf( values, "byteOffset" );
					}
				}

				accessors[at++] = accessor;
			}

			foreach ( var accessor in accessors ) Decode( accessor );
		}

		if ( root.TryGetProperty( "nodes", out var nodeList ) )
		{
			Nodes = new Node[nodeList.GetArrayLength()];

			var at = 0;

			foreach ( var entry in nodeList.EnumerateArray() )
			{
				var node = new Node
				{
					Name = StringOf( entry, "name" ),
					Mesh = IntOf( entry, "mesh", -1 ),
					Skin = IntOf( entry, "skin", -1 ),
					Matrix = FloatsOf( entry, "matrix", 16 ),
					Translation = FloatsOf( entry, "translation", 3 ),
					Rotation = FloatsOf( entry, "rotation", 4 ),
					Scale = FloatsOf( entry, "scale", 3 ),
				};

				if ( entry.TryGetProperty( "children", out var children ) )
				{
					node.Children = new int[children.GetArrayLength()];

					var child = 0;
					foreach ( var value in children.EnumerateArray() ) node.Children[child++] = value.GetInt32();
				}

				Nodes[at++] = node;
			}

			//Parents, worked out once: the file only stores children.
			for ( var parent = 0; parent < Nodes.Length; ++parent )
			{
				foreach ( var child in Nodes[parent].Children )
				{
					if ( child >= 0 && child < Nodes.Length ) Nodes[child].Parent = parent;
				}
			}
		}

		if ( root.TryGetProperty( "meshes", out var meshList ) )
		{
			Meshes = new Mesh[meshList.GetArrayLength()];

			var at = 0;

			foreach ( var entry in meshList.EnumerateArray() )
			{
				var mesh = new Mesh { Name = StringOf( entry, "name" ) };

				if ( entry.TryGetProperty( "primitives", out var primitives ) )
				{
					mesh.Primitives = new Primitive[primitives.GetArrayLength()];

					var p = 0;

					foreach ( var primitiveEntry in primitives.EnumerateArray() )
					{
						var primitive = new Primitive
						{
							Indices = IntOf( primitiveEntry, "indices", -1 ),
							Material = IntOf( primitiveEntry, "material", -1 ),
							Mode = IntOf( primitiveEntry, "mode", 4 ),
						};

						if ( primitiveEntry.TryGetProperty( "attributes", out var attributes ) )
						{
							foreach ( var attribute in attributes.EnumerateObject() )
								primitive.Attributes[attribute.Name] = attribute.Value.GetInt32();
						}

						mesh.Primitives[p++] = primitive;
					}
				}

				Meshes[at++] = mesh;
			}
		}

		if ( root.TryGetProperty( "skins", out var skinList ) )
		{
			Skins = new Skin[skinList.GetArrayLength()];

			var at = 0;

			foreach ( var entry in skinList.EnumerateArray() )
			{
				var skin = new Skin { InverseBindMatrices = IntOf( entry, "inverseBindMatrices", -1 ) };

				if ( entry.TryGetProperty( "joints", out var joints ) )
				{
					skin.Joints = new int[joints.GetArrayLength()];

					var j = 0;
					foreach ( var value in joints.EnumerateArray() ) skin.Joints[j++] = value.GetInt32();
				}

				Skins[at++] = skin;
			}
		}

		if ( root.TryGetProperty( "materials", out var materialList ) )
		{
			MaterialNames = new string[materialList.GetArrayLength()];
			MaterialSurfaces = new ModelFormat.Surface[MaterialNames.Length];
			MaterialColourPictures = new byte[MaterialNames.Length][];
			MaterialTwoSided = new bool[MaterialNames.Length];
			MaterialPictures = new MaterialMaps[MaterialNames.Length];

			var at = 0;

			foreach ( var entry in materialList.EnumerateArray() )
			{
				MaterialSurfaces[at] = SurfaceOf( entry );
				MaterialColourPictures[at] = ColourPictureOf( root, entry, directory );
				MaterialTwoSided[at] = entry.TryGetProperty( "doubleSided", out var sides ) && sides.ValueKind == JsonValueKind.True;
				MaterialPictures[at] = MapsOf( root, entry, directory );

				//Unnamed materials get one, so a model can remap each by surface name
				MaterialNames[at] = StringOf( entry, "name" ) is { Length: > 0 } named ? named : $"material_{at}";
				at++;
			}
		}

		if ( root.TryGetProperty( "animations", out var animationList ) )
		{
			Animations = new Animation[animationList.GetArrayLength()];

			var at = 0;

			foreach ( var entry in animationList.EnumerateArray() )
			{
				var animation = new Animation { Name = StringOf( entry, "name" ) ?? "" };

				if ( entry.TryGetProperty( "samplers", out var samplers ) )
				{
					animation.Samplers = new AnimationSampler[samplers.GetArrayLength()];

					var s = 0;

					foreach ( var samplerEntry in samplers.EnumerateArray() )
					{
						animation.Samplers[s++] = new AnimationSampler
						{
							Input = IntOf( samplerEntry, "input", -1 ),
							Output = IntOf( samplerEntry, "output", -1 ),
							Interpolation = StringOf( samplerEntry, "interpolation" ) ?? "LINEAR",
						};
					}
				}

				if ( entry.TryGetProperty( "channels", out var channels ) )
				{
					animation.Channels = new AnimationChannel[channels.GetArrayLength()];

					var c = 0;

					foreach ( var channelEntry in channels.EnumerateArray() )
					{
						var channel = new AnimationChannel { Sampler = IntOf( channelEntry, "sampler", -1 ) };

						if ( channelEntry.TryGetProperty( "target", out var target ) )
						{
							channel.TargetNode = IntOf( target, "node", -1 );
							channel.TargetPath = StringOf( target, "path" ) ?? "";
						}

						animation.Channels[c++] = channel;
					}
				}

				Animations[at++] = animation;
			}
		}

		return true;
	}

	//Material factors, skipped where a texture multiplies them; colour always set, glTF defaults filled
	private static ModelFormat.Surface SurfaceOf( JsonElement material )
	{
		var surface = default( ModelFormat.Surface );

		//Alpha is only an amount of see-through where the material blends; in the other two modes it is a mask's threshold or nothing at all.
		var blends = StringOf( material, "alphaMode" ) == "BLEND";

		if ( material.TryGetProperty( "pbrMetallicRoughness", out var pbr ) && pbr.ValueKind == JsonValueKind.Object )
		{
			var colour = FloatsOf( pbr, "baseColorFactor", 4 ) ?? new[] { 1.0f, 1.0f, 1.0f, 1.0f };

			surface.SetColour( colour[0], colour[1], colour[2], blends ? colour[3] : 1.0f );

			if ( !pbr.TryGetProperty( "metallicRoughnessTexture", out _ ) )
			{
				surface.SetRoughness( FloatOf( pbr, "roughnessFactor", 1.0f ) );
				surface.SetMetalness( FloatOf( pbr, "metallicFactor", 1.0f ) );
			}
		}

		if ( !material.TryGetProperty( "emissiveTexture", out _ ) && FloatsOf( material, "emissiveFactor", 3 ) is { } emissive )
		{
			var strength = 1.0f;

			if ( material.TryGetProperty( "extensions", out var extensions ) && extensions.ValueKind == JsonValueKind.Object
				&& extensions.TryGetProperty( "KHR_materials_emissive_strength", out var emissiveStrength ) )
			{
				strength = FloatOf( emissiveStrength, "emissiveStrength", 1.0f );
			}

			surface.SetEmission( emissive[0], emissive[1], emissive[2], strength );
		}

		return surface;
	}

	private static float FloatOf( JsonElement element, string name, float missing )
		=> element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number
			? value.GetSingle()
			: missing;

	private static int IntOf( JsonElement element, string name, int missing = 0 )
		=> element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.Number
			? value.GetInt32()
			: missing;

	private static string StringOf( JsonElement element, string name )
		=> element.TryGetProperty( name, out var value ) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static float[] FloatsOf( JsonElement element, string name, int count )
	{
		if ( !element.TryGetProperty( name, out var list ) || list.ValueKind != JsonValueKind.Array ) return null;
		if ( list.GetArrayLength() != count ) return null;

		var values = new float[count];

		var at = 0;
		foreach ( var value in list.EnumerateArray() ) values[at++] = value.GetSingle();

		return values;
	}

	//The picture a material's base colour samples: texture, then image, then the bytes the image names - a buffer view in a GLB, a URI otherwise.
	private byte[] ColourPictureOf( JsonElement root, JsonElement material, string directory )
	{
		if ( !material.TryGetProperty( "pbrMetallicRoughness", out var pbr ) || pbr.ValueKind != JsonValueKind.Object ) return null;
		if ( !pbr.TryGetProperty( "baseColorTexture", out var slot ) || slot.ValueKind != JsonValueKind.Object ) return null;

		var texture = Element( root, "textures", IntOf( slot, "index", -1 ) );
		if ( texture is not { } textureEntry ) return null;

		var image = Element( root, "images", IntOf( textureEntry, "source", -1 ) );
		if ( image is not { } imageEntry ) return null;

		if ( imageEntry.TryGetProperty( "bufferView", out var view ) && view.ValueKind == JsonValueKind.Number )
		{
			var bytes = ViewBytes( view.GetInt32() );
			return bytes.IsEmpty ? null : bytes.ToArray();
		}

		return ReadBufferUri( StringOf( imageEntry, "uri" ), directory );
	}

	private static MaterialMaps MapsOf( JsonElement root, JsonElement material, string directory )
	{
		var maps = new MaterialMaps
		{
			Normal = PictureOf( root, material, "normalTexture", directory ),
			Emission = PictureOf( root, material, "emissiveTexture", directory ),
			Occlusion = PictureOf( root, material, "occlusionTexture", directory ),
			AlphaMode = StringOf( material, "alphaMode" ) ?? "OPAQUE",
			AlphaCutoff = FloatOf( material, "alphaCutoff", 0.5f ),
		};

		if ( material.TryGetProperty( "pbrMetallicRoughness", out var pbr ) && pbr.ValueKind == JsonValueKind.Object )
		{
			maps.Colour = PictureOf( root, pbr, "baseColorTexture", directory );
			maps.RoughMetal = PictureOf( root, pbr, "metallicRoughnessTexture", directory );
			maps.Roughness = FloatOf( pbr, "roughnessFactor", 1.0f );
			maps.Metalness = FloatOf( pbr, "metallicFactor", 1.0f );
		}

		return maps;
	}

	//Where a texture slot's image is, unread: a file beside, a buffer view, or a data URI's bytes
	private static Picture PictureOf( JsonElement root, JsonElement owner, string slotName, string directory )
	{
		if ( !owner.TryGetProperty( slotName, out var slot ) || slot.ValueKind != JsonValueKind.Object ) return null;

		var texture = Element( root, "textures", IntOf( slot, "index", -1 ) );
		if ( texture is not { } textureEntry ) return null;

		var image = Element( root, "images", IntOf( textureEntry, "source", -1 ) );
		if ( image is not { } imageEntry ) return null;

		if ( imageEntry.TryGetProperty( "bufferView", out var view ) && view.ValueKind == JsonValueKind.Number )
			return new Picture { View = view.GetInt32() };

		var uri = StringOf( imageEntry, "uri" );
		if ( string.IsNullOrEmpty( uri ) ) return null;

		if ( uri.StartsWith( "data:", StringComparison.Ordinal ) )
			return ReadBufferUri( uri, directory ) is { } inline ? new Picture { Inline = inline } : null;

		return directory is null ? null : new Picture { File = Path.GetFullPath( Path.Combine( directory, Uri.UnescapeDataString( uri ) ) ) };
	}

	/// <summary>The encoded bytes of a picture held inside the file, or null for one that is a file of its own.</summary>
	public byte[] InlineBytes( Picture picture )
	{
		if ( picture is null ) return null;
		if ( picture.Inline is not null ) return picture.Inline;
		if ( picture.View < 0 ) return null;

		var bytes = ViewBytes( picture.View );
		return bytes.IsEmpty ? null : bytes.ToArray();
	}

	private static JsonElement? Element( JsonElement root, string list, int index )
	{
		if ( index < 0 ) return null;
		if ( !root.TryGetProperty( list, out var entries ) || entries.ValueKind != JsonValueKind.Array ) return null;
		if ( index >= entries.GetArrayLength() ) return null;

		return entries[index];
	}

	private static byte[] ReadBufferUri( string uri, string directory )
	{
		if ( string.IsNullOrEmpty( uri ) ) return null;

		if ( uri.StartsWith( "data:", StringComparison.Ordinal ) )
		{
			var comma = uri.IndexOf( ',' );
			if ( comma < 0 ) return null;

			return Convert.FromBase64String( uri[( comma + 1 )..] );
		}

		//No folder is no file to read beside: a file from memory holds its own.
		if ( directory is null ) return null;

		var path = Path.Combine( directory ?? ".", Uri.UnescapeDataString( uri ) );

		try
		{
			ImportReads.File( path );
			return SealedFile.ReadAllBytes( path );
		}
		catch ( IOException ) { return null; }
		catch ( UnauthorizedAccessException ) { return null; }
	}

	//-- decoding ---------------------------------------------------------------

	private static int ComponentSize( int componentType ) => componentType switch
	{
		5120 or 5121 => 1, //byte, unsigned byte
		5122 or 5123 => 2, //short, unsigned short
		5125 => 4,         //unsigned int
		5126 => 4,         //float
		_ => 0,
	};

	private static float ComponentToFloat( ReadOnlySpan<byte> at, int componentType, bool normalized )
	{
		switch ( componentType )
		{
			case 5126: return BinaryPrimitives.ReadSingleLittleEndian( at );

			case 5120:
			{
				var value = (sbyte)at[0];
				return normalized ? MathF.Max( value / 127.0f, -1.0f ) : value;
			}

			case 5121:
			{
				var value = at[0];
				return normalized ? value / 255.0f : value;
			}

			case 5122:
			{
				var value = BinaryPrimitives.ReadInt16LittleEndian( at );
				return normalized ? MathF.Max( value / 32767.0f, -1.0f ) : value;
			}

			case 5123:
			{
				var value = BinaryPrimitives.ReadUInt16LittleEndian( at );
				return normalized ? value / 65535.0f : value;
			}

			case 5125: return BinaryPrimitives.ReadUInt32LittleEndian( at );

			default: return 0.0f;
		}
	}

	//The bytes one view covers, or empty when anything is out of range.
	private ReadOnlySpan<byte> ViewBytes( int view )
	{
		if ( view < 0 || view >= views.Length ) return default;

		var v = views[view];
		if ( v.Buffer < 0 || v.Buffer >= buffers.Length ) return default;

		var buffer = buffers[v.Buffer];
		if ( v.ByteOffset < 0 || v.ByteOffset + v.ByteLength > buffer.Length ) return default;

		return buffer.AsSpan( v.ByteOffset, v.ByteLength );
	}

	//Everything an accessor holds, as floats, decoded once: base values - zeros where there
	//is no buffer view, which sparse-only accessors rely on - then the sparse overlay on top.
	private void Decode( Accessor accessor )
	{
		var components = ComponentsOf( accessor.Type );
		var componentSize = ComponentSize( accessor.ComponentType );

		accessor.Floats = new float[(long)accessor.Count * components];

		if ( components == 0 || componentSize == 0 || accessor.Count == 0 ) return;

		if ( accessor.BufferView >= 0 )
		{
			var bytes = ViewBytes( accessor.BufferView );

			var stride = views[accessor.BufferView].ByteStride;
			if ( stride == 0 ) stride = components * componentSize;

			for ( var element = 0; element < accessor.Count; ++element )
			{
				var at = accessor.ByteOffset + ( element * stride );
				if ( at < 0 || at + ( components * componentSize ) > bytes.Length ) break;

				for ( var c = 0; c < components; ++c )
				{
					accessor.Floats[( element * components ) + c] = ComponentToFloat(
						bytes.Slice( at + ( c * componentSize ) ),
						accessor.ComponentType, accessor.Normalized );
				}
			}
		}

		if ( accessor.SparseCount <= 0 ) return;

		var indexBytes = ViewBytes( accessor.SparseIndicesView );
		var valueBytes = ViewBytes( accessor.SparseValuesView );

		var indexSize = ComponentSize( accessor.SparseIndicesComponentType );
		if ( indexSize == 0 ) return;

		for ( var sparse = 0; sparse < accessor.SparseCount; ++sparse )
		{
			var indexAt = accessor.SparseIndicesOffset + ( sparse * indexSize );
			if ( indexAt + indexSize > indexBytes.Length ) break;

			var element = (int)ComponentToFloat( indexBytes.Slice( indexAt ),
				accessor.SparseIndicesComponentType, false );

			if ( element < 0 || element >= accessor.Count ) continue;

			var valueAt = accessor.SparseValuesOffset + ( sparse * components * componentSize );
			if ( valueAt + ( components * componentSize ) > valueBytes.Length ) break;

			for ( var c = 0; c < components; ++c )
			{
				accessor.Floats[( element * components ) + c] = ComponentToFloat(
					valueBytes.Slice( valueAt + ( c * componentSize ) ),
					accessor.ComponentType, accessor.Normalized );
			}
		}
	}

	/// <summary>One element of an accessor into the front of <paramref name="into"/>.</summary>
	public void ReadFloat( int accessor, int element, Span<float> into )
	{
		var a = AccessorAt( accessor );
		if ( a?.Floats is null ) return;

		var components = Math.Min( ComponentsOf( a.Type ), into.Length );
		var at = (long)element * ComponentsOf( a.Type );

		for ( var c = 0; c < components; ++c )
		{
			into[c] = at + c < a.Floats.Length ? a.Floats[at + c] : 0.0f;
		}
	}

	public uint ReadIndex( int accessor, int element )
	{
		var a = AccessorAt( accessor );
		if ( a?.Floats is null ) return 0;

		var at = (long)element * ComponentsOf( a.Type );

		return at < a.Floats.Length ? (uint)a.Floats[at] : 0;
	}

	//-- node transforms ----------------------------------------------------------

	/// <summary>A node's own transform, column-major, matrix or TRS - the same answer cgltf gave.</summary>
	public void LocalMatrix( int node, Span<float> m )
	{
		if ( node < 0 || node >= Nodes.Length )
		{
			GltfMath.Identity( m );
			return;
		}

		var n = Nodes[node];

		if ( n.Matrix is not null )
		{
			n.Matrix.CopyTo( m );
			return;
		}

		var trs = new GltfMath.Trs();

		if ( n.Translation is not null ) n.Translation.CopyTo( trs.Translation, 0 );
		if ( n.Rotation is not null ) n.Rotation.CopyTo( trs.Rotation, 0 );
		if ( n.Scale is not null ) n.Scale.CopyTo( trs.Scale, 0 );

		GltfMath.Compose( trs, m );
	}

	/// <summary>Where a node stands in the file's own space, to the root.</summary>
	public void WorldMatrix( int node, Span<float> m )
	{
		LocalMatrix( node, m );

		Span<float> parent = stackalloc float[16];
		Span<float> combined = stackalloc float[16];

		for ( var up = node >= 0 && node < Nodes.Length ? Nodes[node].Parent : -1;
			up >= 0;
			up = Nodes[up].Parent )
		{
			LocalMatrix( up, parent );
			GltfMath.Multiply( parent, m, combined );
			combined.CopyTo( m );
		}
	}
}

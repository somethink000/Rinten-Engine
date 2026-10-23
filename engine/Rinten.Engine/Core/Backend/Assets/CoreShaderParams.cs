using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Rinten;

//Parses a shader's PARAMETERS block (legacy shader syntax) into material numbers, one float4 slot each
internal static class CoreShaderParams
{
	internal const string BlockName = "PARAMETERS";

	//What kind of number it is, and how wide.
	internal enum Kind
	{
		Float,
		Float2,
		Float3,
		Float4,
		Colour,
		Int,
		Bool,

		//An index into the bindless texture table carried in the material numbers, not a new binding
		/// <summary>A picture of the shader's own, on top of the four every material has.</summary>
		Texture,

		/// <summary>A picture a compute pass stores into - <c>RWTexture2D g_tOut &lt; Format( "rgba16f" ); &gt;</c>. Not a number and not the material's: bound as the pass's set two in the order declared, and set by a command list to one of the frame's pictures. See DeclareOutputs and ComputeShader.</summary>
		Storage,
	}

	//One declared material number; Fold(id) bakes a spec constant to whether it is non-zero
	internal sealed record Parameter( string Name, string Title, string Group, Kind Kind,
		Vector4 Default, float Least, float Most, string Attribute = null, string Feature = null,
		string Format = null, int Fold = -1 )
	{
		/// <summary>A picture a compute pass stores into - see Kind.Storage.</summary>
		public bool IsStorage => Kind == Kind.Storage;

		/// <summary>Whether this belongs to the draw rather than to the material.</summary>
		public bool IsAttribute => !string.IsNullOrWhiteSpace( Attribute );

		/// <summary>How many of the four floats in its slot it uses.</summary>
		public int Width => Kind switch
		{
			Kind.Float2 => 2,
			Kind.Float3 or Kind.Colour => 3,
			Kind.Float4 => 4,
			_ => 1,
		};

		/// <summary>Whether it is a picture rather than a number.</summary>
		public bool IsTexture => Kind == Kind.Texture;

		//A picture carries where it is in the table and what to read where there is none - see Kind.Texture. Both have to travel with the material, and neither is worth a binding of its own.
		/// <summary>How many four-float slots it takes: two for a picture, one for anything else.</summary>
		public int Slots => IsTexture ? 2 : 1;

		/// <summary>Whether it is worth a slider rather than a box to type in.</summary>
		public bool HasRange => Most > Least;
	}

	internal static readonly Parameter[] None = Array.Empty<Parameter>();

	//The numbers a shader file declares, in the order it declared them.
	internal static Parameter[] Of( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) ) return None;

		try
		{
			var found = new List<Parameter>();
			var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

			Gather( path, CompiledFile.TextOf( path ), found, seen );

			//Everything but what a pass stores into, which is not a number and not the material's - see
			//OutputsOf. Every reader of these was written for numbers and pictures a material holds.
			found.RemoveAll( x => x.IsStorage );

			return found.Count == 0 ? None : found.ToArray();
		}
		catch ( IOException )
		{
			return None;
		}
	}

	private static void Gather( string path, string source, List<Parameter> into, HashSet<string> seen )
	{
		if ( !CoreShaderFeatures.Block( source, BlockName, out var body ) ) return;

		var directory = Path.GetDirectoryName( path ) ?? "";

		//A declaration may run over more than one line, so the body is read as statements rather
		//than as lines - an include being the exception, which is a line and has no semicolon.
		foreach ( var line in body.Split( '\n' ) )
		{
			var written = line.Trim();

			if ( !written.StartsWith( "#include", StringComparison.Ordinal ) ) continue;

			var named = CoreShaderFeatures.Inside( written, '"', '"' );

			if ( string.IsNullOrWhiteSpace( named ) ) continue;

			var full = Path.GetFullPath( Path.Combine( directory, named ) );

			//Either the file or the packed one beside it: a build carries the second, and an include skipped
			//here is a block of numbers missing from the text a key is made of - which is a key nothing baked.
			if ( !seen.Add( full ) || !CompiledFile.Exists( full ) ) continue;

			try
			{
				var inner = CompiledFile.TextOf( full );

				Gather( full, CoreShaderFeatures.Block( inner, BlockName, out _ )
					? inner
					: BlockName + "\n{\n" + inner + "\n}\n", into, seen );
			}
			catch ( IOException )
			{
			}
		}

		//Every declaration is a head, then everything between one pair of angle brackets. Not split on semicolons: the
		//annotations end with those, so splitting on them takes a declaration apart and loses all but the first of what it said.
		var text = WithoutComments( body );
		var at = 0;

		while ( true )
		{
			var open = text.IndexOf( '<', at );

			if ( open < 0 ) break;

			var close = text.IndexOf( '>', open );

			if ( close < 0 ) break;

			//Back to the end of whatever came before it.
			var from = text.LastIndexOf( ';', open );

			var head = text[( from + 1 )..open];
			var notes = text[( open + 1 )..close];

			at = close + 1;

			if ( Read( head, notes ) is not { } made ) continue;

			if ( !into.Exists( x => string.Equals( x.Name, made.Name, StringComparison.Ordinal ) ) )
				into.Add( made );
		}
	}

	//The text with its comments taken out, because a comment may say anything at all and this is looking for punctuation.
	private static string WithoutComments( string text )
	{
		var made = new StringBuilder( text.Length );

		for ( var i = 0; i < text.Length; i++ )
		{
			if ( text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/' )
			{
				while ( i < text.Length && text[i] != '\n' ) i++;

				made.Append( '\n' );
				continue;
			}

			if ( text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*' )
			{
				i += 2;

				while ( i + 1 < text.Length && !( text[i] == '*' && text[i + 1] == '/' ) ) i++;

				i++;
				continue;
			}

			made.Append( text[i] );
		}

		return made.ToString();
	}

	//Parses one declaration; annotations follow the head as separate semicolon pieces
	private static Parameter Read( string head, string notes )
	{
		var words = head.Trim().Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );

		if ( words.Length < 2 ) return null;

		var kind = KindOf( words[^2] );

		if ( kind is null ) return null;

		var name = words[^1];

		if ( name.Length == 0 || ( !char.IsLetter( name[0] ) && name[0] != '_' ) ) return null;

		var least = 0.0f;
		var most = 0.0f;

		if ( Call( notes, "Range" ) is { } range )
		{
			var ends = Numbers( range );

			if ( ends.Length >= 2 ) { least = ends[0]; most = ends[1]; }
		}

		var value = new Vector4( 0, 0, 0, 0 );

		foreach ( var named in new[] { "Default", "Default2", "Default3", "Default4" } )
		{
			if ( Call( notes, named ) is not { } written ) continue;

			var numbers = Numbers( written );

			if ( numbers.Length == 1 ) value = new Vector4( numbers[0], numbers[0], numbers[0], numbers[0] );
			else if ( numbers.Length >= 4 ) value = new Vector4( numbers[0], numbers[1], numbers[2], numbers[3] );
			else if ( numbers.Length == 3 ) value = new Vector4( numbers[0], numbers[1], numbers[2], 1.0f );
			else if ( numbers.Length == 2 ) value = new Vector4( numbers[0], numbers[1], 0, 0 );

			break;
		}

		var group = Call( notes, "UiGroup" )?.Trim().Trim( '"' ) ?? "";

		//A shader writes a group as "Group,10/20" - the numbers order it, and nothing here orders anything, so they are dropped.
		var comma = group.IndexOf( ',' );

		if ( comma >= 0 ) group = group[..comma];

		var shown = kind.Value;

		if ( Call( notes, "UiType" )?.Trim().Trim( '"' ) is "Color" or "Colour" )
			shown = Kind.Colour;

		//And what a draw calls it, where a draw may set it at all.
		var attribute = Call( notes, "Attribute" )?.Trim().Trim( '"' );

		if ( string.IsNullOrWhiteSpace( attribute ) ) attribute = null;

		//And which feature it is part of, where it is part of one at all.
		var feature = Call( notes, "Feature" )?.Trim().Trim( '"' );

		if ( string.IsNullOrWhiteSpace( feature ) ) feature = null;

		//And for a picture a pass stores into, the format the shader declares it in - which the picture it is given has to be. See DeclareOutputs.
		var stored = kind == Kind.Storage ? Call( notes, "Format" )?.Trim().Trim( '"' ) : null;

		if ( string.IsNullOrWhiteSpace( stored ) ) stored = null;

		//And which specialization constant it folds into, where it folds into one at all - see Parameter.Fold.
		var fold = -1;

		if ( Call( notes, "Fold" ) is { } folds && Numbers( folds ) is { Length: >= 1 } ids )
			fold = (int)ids[0];

		return new Parameter( name, Pretty( name ), group, shown, value, least, most, attribute, feature, stored, fold );
	}

	//What is inside Name( … ) , or null where it is not there.
	private static string Call( string text, string name )
	{
		var at = 0;

		while ( true )
		{
			at = text.IndexOf( name, at, StringComparison.Ordinal );

			if ( at < 0 ) return null;

			var before = at == 0 || !char.IsLetterOrDigit( text[at - 1] );
			var open = at + name.Length;

			while ( open < text.Length && char.IsWhiteSpace( text[open] ) ) open++;

			if ( before && open < text.Length && text[open] == '(' )
			{
				var close = text.IndexOf( ')', open );

				return close > open ? text[( open + 1 )..close] : null;
			}

			at += name.Length;
		}
	}

	private static float[] Numbers( string text )
	{
		var parts = text.Split( ',', StringSplitOptions.RemoveEmptyEntries );
		var made = new List<float>( parts.Length );

		foreach ( var part in parts )
		{
			if ( float.TryParse( part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value ) )
				made.Add( value );
		}

		return made.ToArray();
	}

	private static Kind? KindOf( string word ) => word switch
	{
		"float" => Kind.Float,
		"float2" => Kind.Float2,
		"float3" => Kind.Float3,
		"float4" => Kind.Float4,
		"int" => Kind.Int,
		"bool" => Kind.Bool,
		"Texture2D" or "texture2D" => Kind.Texture,
		"RWTexture2D" => Kind.Storage,
		_ => null,
	};

	//g_flRoughness without the prefix, which is what everything outside the shader calls it: Roughness . The name an effect sets on its attributes,
	//and the name a material file writes. Pretty is this with the capitals spaced out for a person to read; this is the one to look things up by.
	internal static string Bare( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return "";

		return bareNames.GetOrAdd( name, static n => Strip( n ) );
	}

	//Cache of Bare's answers; it ran per post-process number every frame and allocated each time
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> bareNames = new( StringComparer.Ordinal );

	//Their type prefixes, after the g_ .
	private static readonly string[] typePrefixes = { "fl", "v", "b", "n", "i", "t" };

	private static string Strip( string name )
	{
		var text = name;

		if ( text.StartsWith( "g_", StringComparison.Ordinal ) ) text = text[2..];

		foreach ( var prefix in typePrefixes )
		{
			if ( text.Length > prefix.Length && text.StartsWith( prefix, StringComparison.Ordinal ) &&
				char.IsUpper( text[prefix.Length] ) )
			{
				return text[prefix.Length..];
			}
		}

		return text;
	}

	//g_flRoughness as somebody would say it out loud.
	internal static string Pretty( string name )
	{
		var text = name;

		//Their prefixes: g_ then a type - fl for a float, v for a vector, b for a bool, n for a number, t for a texture.
		if ( text.StartsWith( "g_", StringComparison.Ordinal ) ) text = text[2..];

		foreach ( var prefix in new[] { "fl", "v", "b", "n", "i", "t" } )
		{
			if ( text.Length > prefix.Length && text.StartsWith( prefix, StringComparison.Ordinal ) &&
				char.IsUpper( text[prefix.Length] ) )
			{
				text = text[prefix.Length..];
				break;
			}
		}

		//And their capitals: RoughnessScale becomes Roughness Scale.
		var words = new StringBuilder();

		for ( var i = 0; i < text.Length; i++ )
		{
			if ( i > 0 && char.IsUpper( text[i] ) && !char.IsUpper( text[i - 1] ) ) words.Append( ' ' );

			words.Append( text[i] == '_' ? ' ' : text[i] );
		}

		return words.ToString().Trim();
	}

	//Push-block slots for compute passes, which have no material block to read numbers from
	internal const int PushSlots = 8;

	//Push-block slots for per-draw numbers; asking for more is a compile error, not a silent zero
	internal const int DrawSlots = 2;

	//The same block for a pass that runs rather than draws, out of the push constants - see PushSlots. Same
	//layout as the material's, so one routine packs for both and the shader cannot tell which it was handed.
	internal static string DeclarePush( Parameter[] parameters )
	{
		if ( parameters is null || parameters.Length == 0 ) return "";

		var text = new StringBuilder();

		text.Append( "struct EngineParams\n{\n" );
		text.Append( "\tfloat4 surface;\n" );

		var slot = 1;

		foreach ( var parameter in parameters )
		{
			if ( IsSurface( parameter.Name ) || IsCull( parameter.Name ) ) continue;

			if ( slot + parameter.Slots > PushSlots ) break;

			slot += parameter.Slots;

			if ( parameter.IsTexture )
			{
				text.Append( "\tfloat4 " ).Append( parameter.Name ).Append( "_where;\n" );
				text.Append( "\tfloat4 " ).Append( parameter.Name ).Append( "_instead;\n" );
				continue;
			}

			text.Append( "\tfloat4 " ).Append( parameter.Name ).Append( "_slot;\n" );
		}

		text.Append( "};\n" );
		text.Append( "[[vk::push_constant]] ConstantBuffer<EngineParams> g_params;\n" );

		Defines( text, parameters, "g_params", PushSlots );

		return text.ToString();
	}

	internal static string Declare( Parameter[] parameters )
	{
		if ( parameters is null || parameters.Length == 0 ) return "";

		var text = new StringBuilder();

		text.Append( "#define ENGINE_MATERIAL_BLOCK 1\n" );
		text.Append( "struct MaterialParams\n{\n" );

		//The four the engine has always put there, whether or not this shader declared them: the renderer writes them and something may read them.
		text.Append( "\tfloat4 surface;\n" );

		foreach ( var parameter in parameters )
		{
			if ( IsSurface( parameter.Name ) || IsCull( parameter.Name ) ) continue;

			//A number the draw sets is not the material's, and taking a slot in its block would be a second place to write the same thing.
			if ( parameter.IsAttribute ) continue;

			if ( parameter.IsTexture )
			{
				//Where the picture is, and what to read where there is none.
				text.Append( "\tfloat4 " ).Append( parameter.Name ).Append( "_where;\n" );
				text.Append( "\tfloat4 " ).Append( parameter.Name ).Append( "_instead;\n" );
				continue;
			}

			text.Append( "\tfloat4 " ).Append( parameter.Name ).Append( "_slot;\n" );
		}

		text.Append( "};\n" );
		text.Append( "[[vk::binding(4,0)]] ConstantBuffer<MaterialParams> g_material;\n" );

		//Past what a material holds the packer has nowhere to put them, and the shader would read whatever lies there
		var slots = MaterialSlots( parameters );
		if ( slots > CoreRenderDevice.MaterialParamSlots )
		{
			text.Append( "#error \"PARAMETERS take " ).Append( slots ).Append( " material slots of " )
				.Append( CoreRenderDevice.MaterialParamSlots ).Append( ": " ).Append( Overflowing( parameters ) )
				.Append( " do not fit - a texture takes two, every other parameter one\"\n" );
		}

		Defines( text, parameters, "g_material", CoreRenderDevice.MaterialParamSlots );

		return text.ToString();
	}

	//How many floats a full-screen pass may carry in its push block - the tail of the block postprocess.slang declares, after the frame's
	//own sixty-four bytes. Sixteen, which with the frame's four is the whole of the hundred and twenty-eight bytes every layout guarantees.
	internal const int PostFloats = 16;

	//Where each of a full-screen pass's numbers sits in its push block, or nothing for one that stays in the material.
	internal readonly record struct PostSlot( Parameter Parameter, int Offset );

	//Packs a full-screen pass's numbers tightly into its push block; overflow stays in the material
	internal static PostSlot[] PostLayout( Parameter[] parameters )
	{
		if ( parameters is null || parameters.Length == 0 ) return Array.Empty<PostSlot>();

		var slots = new List<PostSlot>();
		var at = 0;

		foreach ( var parameter in parameters )
		{
			if ( IsSurface( parameter.Name ) || IsCull( parameter.Name ) ) continue;
			if ( parameter.IsTexture || !parameter.IsAttribute ) continue;

			var width = parameter.Width;
			var align = width >= 3 ? 4 : width;

			var start = ( at + align - 1 ) / align * align;

			if ( start + width > PostFloats ) break;

			slots.Add( new PostSlot( parameter, start ) );
			at = start + width;
		}

		foreach ( var parameter in parameters )
		{
			if ( IsSurface( parameter.Name ) || IsCull( parameter.Name ) ) continue;
			if ( parameter.IsTexture || parameter.IsAttribute ) continue;

			var width = parameter.Width;
			var align = width >= 3 ? 4 : width;

			var start = ( at + align - 1 ) / align * align;

			if ( start + width > PostFloats ) break;

			slots.Add( new PostSlot( parameter, start ) );
			at = start + width;
		}

		return slots.ToArray();
	}

	//Storage textures a compute pass writes, in declaration order
	internal static Parameter[] OutputsOf( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) ) return None;

		try
		{
			var found = new List<Parameter>();

			Gather( path, CompiledFile.TextOf( path ), found, new HashSet<string>( StringComparer.OrdinalIgnoreCase ) );
			found.RemoveAll( x => !x.IsStorage );

			return found.Count == 0 ? None : found.ToArray();
		}
		catch ( IOException )
		{
			return None;
		}
	}

	//What a stored picture is declared in where the shader does not say.
	internal const string DefaultStorageFormat = "rgba16f";

	//The declarations of a compute pass's outputs: set two, in order, each in the format it said - which is the format of the picture it will be handed,
	//checked when it is. Written as RWTexture2D g_tOut in the block, without a template: the block's reader splits a declaration at its first angle bracket.
	internal static string DeclareOutputs( Parameter[] outputs )
	{
		if ( outputs is null || outputs.Length == 0 ) return "";

		var text = new StringBuilder();

		for ( var i = 0; i < outputs.Length && i < 8; i++ )
		{
			//"compact" is the device's smaller format for light past white, which the engine defines in front of the shader - see CoreShaderFile.Compile.
			var format = outputs[i].Format ?? DefaultStorageFormat;
			var named = format == "compact" ? "ENGINE_COMPACT_FORMAT" : "\"" + format + "\"";

			text.Append( "[[vk::binding(" ).Append( i ).Append( ",2)]] [format(" ).Append( named )
				.Append( ")] RWTexture2D<float4> " ).Append( outputs[i].Name ).Append( ";\n" );
		}

		return text.ToString();
	}

	internal static string DeclarePost( Parameter[] parameters )
	{
		var text = new StringBuilder( Declare( parameters ) );

		var layout = PostLayout( parameters );

		//A draw's own number has no draw block to be read out of here - the world's push is not this pass's push - so one
		//that did not fit the slots is its declared default, spelled out, rather than a name the shader cannot resolve.
		if ( parameters is not null )
		{
			foreach ( var parameter in parameters )
			{
				if ( !parameter.IsAttribute || parameter.IsTexture ) continue;
				if ( Array.Exists( layout, slot => ReferenceEquals( slot.Parameter, parameter ) ) ) continue;

				var d = parameter.Default;

				text.Append( "#undef " ).Append( parameter.Name ).Append( '\n' );
				text.Append( "#define " ).Append( parameter.Name ).Append( ' ' ).Append( parameter.Width switch
				{
					4 => Invariant( $"(float4({d.x},{d.y},{d.z},{d.w}))" ),
					3 => Invariant( $"(float3({d.x},{d.y},{d.z}))" ),
					2 => Invariant( $"(float2({d.x},{d.y}))" ),
					_ => Invariant( $"({d.x})" ),
				} ).Append( '\n' );
			}
		}

		foreach ( var (parameter, offset) in layout )
		{
			var slot = offset / 4;
			var lane = offset % 4;

			var swizzle = parameter.Width switch
			{
				4 => "",
				3 => ".xyz",
				2 => lane == 0 ? ".xy" : ".zw",
				_ => "." + "xyzw"[lane],
			};

			text.Append( "#undef " ).Append( parameter.Name ).Append( '\n' );
			text.Append( "#define " ).Append( parameter.Name )
				.Append( " (g_vPostSlots[" ).Append( slot ).Append( ']' ).Append( swizzle ).Append( ")\n" );
		}

		return text.ToString();
	}

	//A number the way a shader spells it, whatever the machine's locale says.
	private static string Invariant( FormattableString text )
		=> FormattableString.Invariant( text ).Replace( ",", ", " ).Replace( ",  ", ", " );

	//One define per name, pointing at whichever block holds it.
	private static void Defines( StringBuilder text, Parameter[] parameters, string block, int slots )
	{
		var slot = 1;
		var drawn = 0;

		foreach ( var parameter in parameters )
		{
			//Nothing to define for the three the cull pass reads: the shader declares them so that a material may set them, not so that it can read them back.
			if ( IsCull( parameter.Name ) ) continue;

			//A number the draw sets reads out of the draw's own slots, wherever the material's
			//block is. Counted separately, because the two blocks fill up independently.
			if ( parameter.IsAttribute )
			{
				//Packed by lane rather than a slot each: eight numbers a draw is eight floats, not two - see DrawLane.
				var lane = DrawLane( ref drawn, parameter );
				if ( lane < 0 ) continue;

				text.Append( "#define " ).Append( parameter.Name ).Append( ' ' )
					.Append( '(' ).Append( "g_draw" ).Append( lane / 4 ).Append( LaneChannel( parameter, lane % 4 ) )
					.Append( ")\n" );

				continue;
			}

			if ( !IsSurface( parameter.Name ) )
			{
				//Past what the block holds, which only a push block ever is. Left undefined
				//on purpose: a shader reading a number it was never given should not compile.
				if ( slot + parameter.Slots > slots ) continue;

				slot += parameter.Slots;
			}

			text.Append( "#define " ).Append( parameter.Name ).Append( ' ' );

			if ( parameter.IsTexture )
			{
				//EngineTexture is written in frame.slang, beside the table it reads - a struct here would be using g_textures before the line that declares it.
				text.Append( "( EngineTexture( " ).Append( block ).Append( '.' ).Append( parameter.Name )
					.Append( "_where, " ).Append( block ).Append( '.' ).Append( parameter.Name )
					.Append( "_instead ) )\n" );

				continue;
			}

			var where = IsSurface( parameter.Name )
				? block + ".surface." + SurfaceChannel( parameter.Name )
				: block + "." + parameter.Name + "_slot" + parameter.Kind switch
				{
					Kind.Float2 => ".xy",
					Kind.Float3 or Kind.Colour => ".xyz",
					Kind.Float4 => "",
					_ => ".x",
				};

			text.Append( '(' ).Append( where ).Append( ")\n" );
		}
	}

	//Allocates a draw attribute's lane among the eight per-draw lanes; -1 if it does not fit
	internal static int DrawLane( ref int lanes, Parameter parameter )
	{
		var width = parameter.Width;
		var align = width >= 3 ? 4 : width;
		var start = ( lanes + align - 1 ) / align * align;

		if ( start + width > DrawSlots * 4 ) return -1;

		lanes = start + width;
		return start;
	}

	private static string LaneChannel( Parameter parameter, int lane ) => parameter.Kind switch
	{
		Kind.Float2 => lane == 0 ? ".xy" : ".zw",
		Kind.Float3 or Kind.Colour => ".xyz",
		Kind.Float4 => "",
		_ => lane switch { 0 => ".x", 1 => ".y", 2 => ".z", _ => ".w" },
	};

	private static string Channel( Parameter parameter ) => parameter.Kind switch
	{
		Kind.Float2 => ".xy",
		Kind.Float3 or Kind.Colour => ".xyz",
		Kind.Float4 => "",
		_ => ".x",
	};

	//Whether this is one of the four built-in surface numbers that share the first slot
	private static bool IsSurface( string name ) => SurfaceChannel( name ) is not null;

	private static string SurfaceChannel( string name ) => name switch
	{
		"g_flRoughness" => "x",
		"g_flMetalness" => "y",
		"g_flEmissionStrength" => "z",
		"g_flAlphaCutoff" => "w",
		_ => null,
	};

	//Cull-pass parameters (grow, never, fade), declared like numbers but taking no material slot
	internal const string GrowName = "CullGrow";
	internal const string NeverName = "CullNever";
	internal const string FadeName = "CullFade";

	internal static bool IsCull( string name ) => Which( name ) >= 0;

	//Which of the three it is, or minus one where it is none of them.
	private static int Which( string name )
	{
		var bare = Bare( name );

		if ( string.Equals( bare, GrowName, StringComparison.OrdinalIgnoreCase ) ) return 0;
		if ( string.Equals( bare, NeverName, StringComparison.OrdinalIgnoreCase ) ) return 1;
		if ( string.Equals( bare, FadeName, StringComparison.OrdinalIgnoreCase ) ) return 2;

		return -1;
	}

	//What the shader and the material between them say about culling one of these: grow, never, fade. Nought all round where
	//neither said anything, which is every material in the tree today and is exactly the behaviour there was before this existed.
	internal static Vector3 Cull( Parameter[] parameters, Func<string, Vector4?> asked )
	{
		if ( parameters is null || parameters.Length == 0 ) return Vector3.Zero;

		var made = Vector3.Zero;

		foreach ( var parameter in parameters )
		{
			var which = Which( parameter.Name );

			if ( which < 0 ) continue;

			var value = ( asked?.Invoke( parameter.Name ) ?? parameter.Default ).x;

			if ( which == 0 ) made.x = value;
			else if ( which == 1 ) made.y = value;
			else made.z = value;
		}

		return made;
	}

	//Packs per-draw numbers in declaration order; unset ones get the shader's default
	internal static void Draw( Span<float> numbers, Parameter[] parameters,
		Func<string, Vector4?> asked )
	{
		numbers.Clear();

		if ( parameters is null ) return;

		var lanes = 0;

		foreach ( var parameter in parameters )
		{
			if ( !parameter.IsAttribute ) continue;

			var at = DrawLane( ref lanes, parameter );
			if ( at < 0 ) break;

			var width = parameter.Width;
			if ( at + width > numbers.Length ) break;

			//Asked by the name the draw set it under, and then by the declared name, which is what a material would call it.
			var value = asked?.Invoke( parameter.Attribute ) ?? asked?.Invoke( parameter.Name ) ?? parameter.Default;

			numbers[at] = value.x;
			if ( width > 1 ) numbers[at + 1] = value.y;
			if ( width > 2 ) numbers[at + 2] = value.z;
			if ( width > 3 ) numbers[at + 3] = value.w;
		}
	}

	//Same numbers read from attributes via precomputed tokens instead of by name
	internal static void Draw( Span<float> numbers, Parameter[] parameters, RenderAttributes attributes )
		=> Draw( numbers, PlanOf( parameters ), attributes );

	//The cached draw plan for a parameter array, for callers that keep it
	internal static DrawPlan PlanOf( Parameter[] parameters ) => DrawPlanOf( parameters );

	internal static void Draw( Span<float> numbers, DrawPlan plan, RenderAttributes attributes )
	{

		var room = Math.Min( numbers.Length, plan.Defaults.Length );

		numbers.Clear();
		plan.Defaults.AsSpan( 0, room ).CopyTo( numbers );

		if ( attributes is null || attributes.Count == 0 || plan.Lanes.Count == 0 ) return;

		//Which spelling won each lane so far, so that a lane the attributes hold under two names keeps the one
		//the walk by name would have found first. Nothing has been set when every lane is at the worst rank.
		Span<byte> ranks = stackalloc byte[room];
		ranks.Fill( byte.MaxValue );

		attributes.IntoDraw( numbers, ranks, plan.Lanes );
	}

	//Where one of a pass's draw-set numbers lands, and how well the name it was found under fits.
	internal readonly struct DrawPlace
	{
		public readonly byte At;
		public readonly byte Width;
		public readonly byte Rank;

		public DrawPlace( int at, int width, int rank )
		{
			At = (byte)at;
			Width = (byte)width;
			Rank = (byte)Math.Min( rank, byte.MaxValue - 1 );
		}
	}

	//A pass's draw numbers: what they are when nothing sets them, and where each name puts one.
	internal sealed class DrawPlan
	{
		public float[] Defaults = Array.Empty<float>();
		public Dictionary<StringToken, DrawPlace[]> Lanes = new();
	}

	//Per-array draw plan, cached, so lookups go one probe per attribute the draw actually set
	private static DrawPlan DrawPlanOf( Parameter[] parameters )
		=> drawPlans.GetValue( parameters ?? Array.Empty<Parameter>(), static of =>
		{
			var made = new DrawPlan { Defaults = new float[DrawSlots * 4] };
			var building = new Dictionary<StringToken, List<DrawPlace>>();
			var lanes = 0;

			foreach ( var parameter in of )
			{
				if ( !parameter.IsAttribute ) continue;

				var at = DrawLane( ref lanes, parameter );
				if ( at < 0 ) break;

				var width = parameter.Width;
				if ( at + width > made.Defaults.Length ) break;

				var fallback = parameter.Default;

				made.Defaults[at] = fallback.x;
				if ( width > 1 ) made.Defaults[at + 1] = fallback.y;
				if ( width > 2 ) made.Defaults[at + 2] = fallback.z;
				if ( width > 3 ) made.Defaults[at + 3] = fallback.w;

				//In the order the walk by name tried them: the name the draw sets it by, then the declared name, each in its spellings.
				var rank = 0;

				foreach ( var name in new[] { parameter.Attribute, parameter.Name } )
				{
					if ( string.IsNullOrEmpty( name ) ) continue;

					foreach ( var spelling in RenderAttributes.Spellings( name ) )
					{
						if ( !building.TryGetValue( spelling, out var held ) )
							building[spelling] = held = new List<DrawPlace>( 1 );

						var already = false;

						foreach ( var one in held )
						{
							if ( one.At == at ) { already = true; break; }
						}

						if ( !already ) held.Add( new DrawPlace( at, width, rank ) );

						rank++;
					}
				}
			}

			foreach ( var (token, held) in building ) made.Lanes[token] = held.ToArray();

			return made;
		} );

	//Kept against the array itself, so it goes when the pass that declared it does.
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Parameter[], DrawPlan> drawPlans = new();

	//Whether any of these belongs to the draw rather than the material.
	internal static bool AnyAttribute( Parameter[] parameters )
	{
		if ( parameters is null ) return false;

		foreach ( var parameter in parameters )
		{
			if ( parameter.IsAttribute ) return true;
		}

		return false;
	}

	//Whether a parameter has a place of its own in the material's block, as Into packs it
	private static bool InMaterialBlock( Parameter parameter )
		=> !IsSurface( parameter.Name ) && !IsCull( parameter.Name ) && !parameter.IsAttribute;

	/// <summary>The float4 slots a material of this shader fills: the surface's first, then a slot or two per parameter.</summary>
	internal static int MaterialSlots( Parameter[] parameters )
	{
		var slot = 1;

		foreach ( var parameter in parameters ?? Array.Empty<Parameter>() )
		{
			if ( InMaterialBlock( parameter ) ) slot += parameter.Slots;
		}

		return slot;
	}

	//The names that fall past the last slot, in declared order
	private static string Overflowing( Parameter[] parameters )
	{
		var slot = 1;
		var names = new List<string>();

		foreach ( var parameter in parameters )
		{
			if ( !InMaterialBlock( parameter ) ) continue;

			slot += parameter.Slots;
			if ( slot > CoreRenderDevice.MaterialParamSlots ) names.Add( parameter.Name );
		}

		return string.Join( ", ", names );
	}

	internal static void Into( Span<float> numbers, Parameter[] parameters,
		Func<string, Vector4?> asked, Func<string, uint> pictures = null )
	{
		if ( parameters is null ) return;

		var slot = 1;

		foreach ( var parameter in parameters )
		{
			if ( IsSurface( parameter.Name ) || IsCull( parameter.Name ) ) continue;

			//The draw's own numbers go in the draw's own block - see Draw.
			if ( parameter.IsAttribute ) continue;

			var at = slot * 4;

			slot += parameter.Slots;

			if ( at + ( parameter.Slots * 4 ) > numbers.Length ) break;

			if ( parameter.IsTexture )
			{
				//Where the picture is in the device's table, or nought where the material named none and the shader's own default stands in for it - see Kind.Texture.
				numbers[at] = pictures?.Invoke( parameter.Name ) ?? 0u;

				var instead = asked?.Invoke( parameter.Name ) ?? parameter.Default;

				numbers[at + 4] = instead.x;
				numbers[at + 5] = instead.y;
				numbers[at + 6] = instead.z;
				numbers[at + 7] = instead.w;

				continue;
			}

			var value = asked?.Invoke( parameter.Name ) ?? parameter.Default;

			numbers[at] = value.x;
			numbers[at + 1] = value.y;
			numbers[at + 2] = value.z;
			numbers[at + 3] = value.w;
		}
	}
}

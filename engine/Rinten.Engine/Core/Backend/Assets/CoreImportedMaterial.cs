using System.Globalization;
using System.IO;
using System.Text;

namespace Rinten;

//Reads a .vmat directly and maps it onto MaterialDefinition fields
internal static class CoreImportedMaterial
{
	private static readonly Logger log = new( "Core" );

	//What a material file says, as far as anything here cares.
	internal sealed class Fields
	{
		public string Shader = "";
		public string ColorMap = "";
		public string Normal = "";
		public string Emission = "";

		/// <summary>What the colour is cut by - see MaterialDefinition.OpacityMask. Ours only.</summary>
		public string OpacityMask = "";

		public Color Tint = Color.White;

		public float Roughness = 0.5f;
		public float Metalness = 0.0f;
		public float EmissionStrength = 0.0f;
		public float AlphaCutoff = 0.5f;

		public bool Masked;
		public bool Translucent;
		public bool TwoSided;

		/// <summary>Its pictures read texel by texel rather than smoothed. The features the material file set by hand - see MaterialDefinition.Features.</summary>
		public System.Collections.Generic.Dictionary<string, int> Features;

		/// <summary>What it feels like underfoot, by resource path. Ours only.</summary>
		public string PhysicsSurface = "";

		/// <summary>Extra numbers the shader declared, by name; null if none are set</summary>
		public Dictionary<string, Vector4> Numbers;

		/// <summary>And the pictures it declared, by the same rule.</summary>
		public Dictionary<string, string> Pictures;
	}

	/// <summary>Whether this path names one of their materials.</summary>
	public static bool IsTheirs( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return false;

		return path.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase )
			|| path.EndsWith( ".vmat_c", StringComparison.OrdinalIgnoreCase );
	}

	private static readonly Dictionary<string, Fields> cache = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Reads the material at this resource path, or null if there is no file of theirs there. Kept, because a scene asks for the same material once per object that wears it.</summary>
	public static Fields Read( string materialPath )
	{
		if ( !IsTheirs( materialPath ) ) return null;

		lock ( cache )
		{
			if ( cache.TryGetValue( materialPath, out var cached ) ) return cached;
		}

		var fields = ReadFile( materialPath );

		lock ( cache )
		{
			cache[materialPath] = fields;
		}

		return fields;
	}

	/// <summary>Forgets everything read, so an edited material is read again.</summary>
	public static void Forget()
	{
		lock ( cache ) cache.Clear();
	}

	private static Fields ReadFile( string materialPath )
	{
		//Their compiled material is the text one with a c on the end. The text is the readable one, so that is what is looked for first.
		var wanted = materialPath.Replace( '\\', '/' );

		if ( wanted.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) )
			wanted = wanted[..^2];

		if ( CoreModel.ResolveOnDisk( wanted ) is { } full )
		{
			try
			{
				return Parse( SealedFile.ReadAllText( full ) );
			}
			catch ( IOException e )
			{
				log.Warning( $"could not read {wanted}: {e.Message}" );
				return null;
			}
		}

		//No text beside it, which is every material a package brings: what a project downloads is compiled and nothing else.
		return CoreModel.ResolveOnDisk( wanted + "_c" ) is { } compiled ? ReadCompiled( compiled ) : null;
	}

	//Reads a compiled material; old and compiled names share one table so the mapping is defined once
	private static Fields ReadCompiled( string path )
		=> CompiledValues( path ) is { } values ? FromValues( values ) : null;

	/// <summary>A compiled material's parameters as the text one would write them, by name, the shader under "shader"; null where it will not read.</summary>
	internal static Dictionary<string, string> CompiledValues( string path )
	{
		var resource = CoreImportedResource.Open( path );

		if ( resource?.Find( "DATA" ) is not { } block ) return null;

		var data = CoreImportedKeyValues3.Read( resource.Read( block ) );
		if ( data is null ) return null;

		var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			["shader"] = data.String( "m_shaderName", "" ),
		};

		foreach ( var parameter in data.Array( "m_textureParams" ) )
		{
			Add( values, parameter, parameter.String( "m_pValue", "" ) );
		}

		foreach ( var parameter in data.Array( "m_intParams" ) )
		{
			Add( values, parameter, parameter.Int( "m_nValue" ).ToString( CultureInfo.InvariantCulture ) );
		}

		foreach ( var parameter in data.Array( "m_floatParams" ) )
		{
			Add( values, parameter, parameter.Float( "m_flValue" ).ToString( CultureInfo.InvariantCulture ) );
		}

		foreach ( var parameter in data.Array( "m_vectorParams" ) )
		{
			var vector = parameter["m_value"];
			if ( vector is null ) continue;

			var written = string.Join( ' ',
				vector[0]?.AsFloat() ?? 0.0f,
				vector[1]?.AsFloat() ?? 0.0f,
				vector[2]?.AsFloat() ?? 0.0f,
				vector[3]?.AsFloat() ?? 1.0f );

			Add( values, parameter, $"[{written}]" );
		}

		return values;
	}

	private static void Add( Dictionary<string, string> values, CoreImportedKeyValues3 parameter, string value )
	{
		var name = parameter.String( "m_name", "" );

		if ( string.IsNullOrWhiteSpace( name ) ) return;

		values[name] = value;
	}

	//Parses a .vmat's top-level KeyValues token by token, without building a tree
	internal static Fields Parse( string text )
	{
		var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var (key, value) in Pairs( text ) )
		{
			values[key] = value;
		}

		return FromValues( values );
	}

	//What a table of their parameters means here. The names come in two sets - what the text calls things
	//and what the compiler renamed them to - and both are asked for, because a material arrives as either.
	private static Fields FromValues( Dictionary<string, string> values )
	{
		var fields = new Fields();

		string Text( params string[] names )
		{
			foreach ( var name in names )
			{
				if ( values.TryGetValue( name, out var value ) && !string.IsNullOrWhiteSpace( value ) )
					return value;
			}

			return "";
		}

		float Number( string name, float fallback )
		{
			if ( !values.TryGetValue( name, out var value ) ) return fallback;

			return float.TryParse( value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number )
				? number
				: fallback;
		}

		bool Flag( string name )
		{
			var value = Text( name ).Trim();

			return value is "1" or "true" or "True";
		}

		fields.Shader = ShaderOf( Text( "shader", "m_shaderName" ) );

		fields.ColorMap = Image( Text( "TextureColor", "TextureTranslucency", "TextureAlbedo",
			"g_tColor", "g_tColorTranslucency", "g_tAlbedo" ) );

		fields.Normal = Image( Text( "TextureNormal", "g_tNormal" ) );

		fields.Emission = Image( Text( "TextureSelfIllumMask", "TextureEmissive",
			"g_tSelfIllumMask", "g_tEmissive" ) );

		//Rough/metal/AO maps go to the shader read in red; a packed map uses the material numbers instead
		var rough = Image( Text( "TextureRoughness", "g_tRoughness" ) );
		var metal = Image( Text( "TextureMetalness", "g_tMetalness" ) );
		var occlusion = Image( Text( "TextureAmbientOcclusion", "g_tAmbientOcclusion" ) );

		if ( !string.IsNullOrEmpty( rough ) && rough == metal && rough == occlusion )
			rough = metal = occlusion = "";

		void Picture( string name, string image )
		{
			if ( !string.IsNullOrEmpty( image ) ) ( fields.Pictures ??= new() )[name] = image;
		}

		Picture( "g_tRoughness", rough );
		Picture( "g_tMetalness", metal );
		Picture( "g_tAO", occlusion );

		fields.Tint = ColorOf( Text( "g_vColorTint" ), Color.White );

		//Beside a picture the number is what the picture is multiplied by, so one that said nothing reads the picture at face value rather than
		//halved - and a metalness map is read only where metalness is on at all: complex.shader's F_METALNESS is the number being above nought.
		fields.Roughness = Number( "g_flRoughnessScaleFactor",
			Number( "g_flRoughness", string.IsNullOrEmpty( rough ) ? 0.5f : 1.0f ) );
		fields.Metalness = Number( "g_flMetalness",
			Number( "g_flMetalnessScaleFactor", string.IsNullOrEmpty( metal ) ? 0.0f : 1.0f ) );
		fields.EmissionStrength = Number( "g_flSelfIllumScale", Number( "g_flSelfIllumStrength", 0.0f ) );
		fields.AlphaCutoff = Number( "g_flAlphaTestReference", 0.5f );

		fields.Masked = Flag( "F_ALPHA_TEST" );

		//Glass says it is see-through by being glass rather than by setting the flag, and a window drawn solid is worse than one drawn as a pane.
		var glass = Text( "shader", "m_shaderName" ).Contains( "glass", StringComparison.OrdinalIgnoreCase );

		fields.Translucent = glass || Flag( "F_TRANSLUCENT" ) ||
			Flag( "F_BLEND_MODE" ) && Number( "F_BLEND_MODE", 0 ) > 0;
		fields.TwoSided = Flag( "F_RENDER_BACKFACES" ) || Flag( "F_NO_CULLING" );

		return fields;
	}

	//Every name and value in the file, in the order they are written. Braces and comments are stepped over; what is inside
	//a nested block reads as more pairs, which is harmless because their names do not collide with the ones asked for above.
	private static IEnumerable<(string Key, string Value)> Pairs( string text )
	{
		var i = 0;

		//Where the next token starts, with whitespace and comments behind it.
		void Skip()
		{
			while ( i < text.Length )
			{
				if ( char.IsWhiteSpace( text[i] ) ) { i++; continue; }

				if ( text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/' )
				{
					while ( i < text.Length && text[i] != '\n' ) i++;
					continue;
				}

				return;
			}
		}

		string Token()
		{
			Skip();

			if ( i >= text.Length ) return null;

			if ( text[i] == '"' )
			{
				var end = text.IndexOf( '"', i + 1 );
				if ( end < 0 ) { i = text.Length; return null; }

				var quoted = text[( i + 1 )..end];
				i = end + 1;

				return quoted;
			}

			if ( text[i] is '{' or '}' )
			{
				i++;
				return null;
			}

			var start = i;
			while ( i < text.Length && !char.IsWhiteSpace( text[i] ) && text[i] is not ( '{' or '}' ) ) i++;

			return text[start..i];
		}

		while ( i < text.Length )
		{
			var key = Token();
			if ( key is null ) continue;

			//A name with a brace after it opens a block rather than holding a value. Reading the
			//block's first name as this one's value is how every pair in the file ends up one out.
			Skip();

			if ( i < text.Length && text[i] == '{' )
			{
				i++;
				continue;
			}

			var value = Token();
			if ( value is null ) continue;

			yield return (key, value);
		}
	}

	//What their shader names mean here. Ours has one shader for a surface, so almost everything
	//lands on it; a shader we have no answer for lands there too rather than not drawing.
	private static string ShaderOf( string shader )
	{
		if ( string.IsNullOrWhiteSpace( shader ) ) return "";

		var name = Path.GetFileNameWithoutExtension( shader.Replace( '\\', '/' ) );

		return name.ToLowerInvariant() switch
		{
			//Their sky is a shader of its own, and so is ours.
			"sky" or "skybox" => "shaders/engine/sky.shader",
			_ => "",
		};
	}

	//The image a texture parameter names. Their compiled textures point at the source image beside them, which is what our texture compiler reads.
	private static string Image( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return "";

		path = path.Trim().Replace( '\\', '/' );

		//A compiled material writes the colour a missing texture stands in as under the texture's own name, so a number in brackets is not a path.
		if ( path.StartsWith( '[' ) ) return "";

		if ( path.EndsWith( ".vtex_c", StringComparison.OrdinalIgnoreCase ) )
			path = path[..^7];
		else if ( path.EndsWith( ".vtex", StringComparison.OrdinalIgnoreCase ) )
			path = path[..^5];
		else
			return path;

		//A texture of theirs with the image extension taken off: the file on disk is one of these.
		foreach ( var extension in new[] { ".tga", ".png", ".jpg", ".psd", ".exr" } )
		{
			if ( CoreModel.ResolveOnDisk( path + extension ) is not null )
				return path + extension;
		}

		//No source beside it - which is every texture their compiler generated. The compiled one is the only copy, and it can be read; see CoreImportedTexture.
		return CoreModel.ResolveOnDisk( path + ".vtex_c" ) is not null
			? path + ".vtex_c"
			: "";
	}

	//A colour written as [r g b a] , with one to four numbers.
	private static Color ColorOf( string value, Color fallback )
	{
		if ( string.IsNullOrWhiteSpace( value ) ) return fallback;

		var parts = value.Trim( '[', ']', ' ' )
			.Split( new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries );

		if ( parts.Length == 0 ) return fallback;

		float At( int index, float otherwise )
			=> index < parts.Length && float.TryParse( parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var number )
				? number
				: otherwise;

		return new Color( At( 0, 1 ), At( 1, 1 ), At( 2, 1 ), 1.0f );
	}
}

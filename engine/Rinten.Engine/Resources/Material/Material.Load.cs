using NativeEngine;
using Rinten.Mounting;
using System.Text;

namespace Rinten;

public partial class Material
{
	/// <summary>Load a material from disk. Has internal cache.</summary>
	public static Material Load( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		if ( filename.StartsWith( '/' ) || filename.StartsWith( '\\' ) )
			filename = filename[1..];

		if ( !string.IsNullOrWhiteSpace( filename ) && Directory.TryLoad( filename, ResourceType.Material, out object model ) && model is Material m )
			return m;

		return FromFile( filename ) ?? Ported( filename );
	}

	//Maps a ported .vmat name in code to our .mat beside it; null if neither applies
	private static Material Ported( string filename )
	{
		if ( !CoreImportedMaterial.IsTheirs( filename ) ) return null;

		var theirs = filename.EndsWith( "_c", StringComparison.OrdinalIgnoreCase )
			? filename[..^2]
			: filename;

		var ours = System.IO.Path.ChangeExtension( theirs, ".mat" ).Replace( '\\', '/' );

		if ( Directory.TryLoad( ours, ResourceType.Material, out object loaded ) && loaded is Material m )
			return m;

		return FromFile( ours );
	}

	//Reads our .mat (MaterialDefinition JSON) fields directly; null if missing or not one
	private static CoreImportedMaterial.Fields Definition( string path )
	{
		if ( !path.EndsWith( ".mat", StringComparison.OrdinalIgnoreCase ) ) return null;

		var full = CoreModel.ResolveTextOnDisk( path );
		if ( full is null ) return null;

		try
		{
			return DefinitionFromJson( CompiledFile.TextOf( full ) );
		}
		catch ( Exception )
		{
			return null;
		}
	}

	//What a material file says, for a test that has the text rather than a file - the reader itself is the thing worth proving.
	internal static CoreImportedMaterial.Fields FieldsFromJson( string json )
		=> DefinitionFromJson( json );

	private static CoreImportedMaterial.Fields DefinitionFromJson( string json )
	{
		using var document = System.Text.Json.JsonDocument.Parse( json );
		var root = document.RootElement;

		if ( root.ValueKind != System.Text.Json.JsonValueKind.Object ) return null;

		string Text( string name )
			=> root.TryGetProperty( name, out var value ) && value.ValueKind == System.Text.Json.JsonValueKind.String
				? value.GetString()
				: "";

		float Number( string name, float fallback )
			=> root.TryGetProperty( name, out var value ) && value.ValueKind == System.Text.Json.JsonValueKind.Number
				? value.GetSingle()
				: fallback;

		return new CoreImportedMaterial.Fields
		{
			Shader = Text( nameof( MaterialDefinition.Shader ) ),
			PhysicsSurface = Text( nameof( MaterialDefinition.PhysicsSurface ) ),
			Numbers = Declared( root ),
			Pictures = Named( root ),
			ColorMap = Text( nameof( MaterialDefinition.ColorMap ) ),
			Normal = Text( nameof( MaterialDefinition.Normal ) ),
			Emission = Text( nameof( MaterialDefinition.Emission ) ),
			OpacityMask = Text( nameof( MaterialDefinition.OpacityMask ) ),

			Tint = Colour( Text( nameof( MaterialDefinition.Tint ) ) ),

			Roughness = Number( nameof( MaterialDefinition.Roughness ), 0.5f ),
			Metalness = Number( nameof( MaterialDefinition.Metalness ), 0.0f ),
			EmissionStrength = Number( nameof( MaterialDefinition.EmissionStrength ), 0.0f ),
			AlphaCutoff = Number( nameof( MaterialDefinition.AlphaCutoff ), 0.5f ),

			Features = FeaturesIn( root ),
		};
	}

	//Shader numbers set by the material, e.g. "Numbers": { "g_flSway": 2.5 }; unset names are omitted
	private static Dictionary<string, Vector4> Declared( System.Text.Json.JsonElement root )
	{
		if ( !root.TryGetProperty( nameof( MaterialDefinition.Numbers ), out var listed ) ||
			listed.ValueKind != System.Text.Json.JsonValueKind.Object )
		{
			return null;
		}

		Dictionary<string, Vector4> declared = null;

		foreach ( var field in listed.EnumerateObject() )
		{
			Vector4 value;

			if ( field.Value.ValueKind == System.Text.Json.JsonValueKind.Number )
			{
				var one = field.Value.GetSingle();
				value = new Vector4( one, one, one, one );
			}
			else if ( field.Value.ValueKind == System.Text.Json.JsonValueKind.Array )
			{
				var parts = new float[4];
				var at = 0;

				foreach ( var part in field.Value.EnumerateArray() )
				{
					if ( at >= 4 ) break;
					if ( part.ValueKind != System.Text.Json.JsonValueKind.Number ) continue;

					parts[at++] = part.GetSingle();
				}

				value = new Vector4( parts[0], parts[1], parts[2], parts[3] );
			}
			else if ( field.Value.ValueKind == System.Text.Json.JsonValueKind.String && Vector4.TryParse( field.Value.GetString(), out var parsed ) )
			{
				//"x,y,z,w" - how the engine's own serializer writes a Vector4, and so how every material the editor saved has its numbers.
				value = parsed;
			}
			else
			{
				continue;
			}

			declared ??= new Dictionary<string, Vector4>( StringComparer.Ordinal );
			declared[field.Name] = value;
		}

		return declared;
	}

	//The features a material file set by hand - and a file older than them, its Blend and TwoSided as the features they became.
	static Dictionary<string, int> FeaturesIn( System.Text.Json.JsonElement root )
	{
		var told = new Dictionary<string, int>( StringComparer.Ordinal );

		if ( root.TryGetProperty( nameof( MaterialDefinition.Features ), out var listed ) &&
			listed.ValueKind == System.Text.Json.JsonValueKind.Object )
		{
			foreach ( var field in listed.EnumerateObject() )
			{
				if ( field.Value.ValueKind == System.Text.Json.JsonValueKind.Number && field.Value.TryGetInt32( out var value ) )
					told[field.Name] = value;
			}
		}

		MaterialDefinition.LegacyFeatures( root, told );

		return told.Count > 0 ? told : null;
	}

	//The pictures it set on its shader, by name, as written.
	private static Dictionary<string, string> Named( System.Text.Json.JsonElement root )
	{
		if ( !root.TryGetProperty( nameof( MaterialDefinition.Textures ), out var listed ) ||
			listed.ValueKind != System.Text.Json.JsonValueKind.Object )
		{
			return null;
		}

		Dictionary<string, string> named = null;

		foreach ( var field in listed.EnumerateObject() )
		{
			if ( field.Value.ValueKind != System.Text.Json.JsonValueKind.String ) continue;

			var path = field.Value.GetString();
			if ( string.IsNullOrWhiteSpace( path ) ) continue;

			named ??= new Dictionary<string, string>( StringComparer.Ordinal );
			named[field.Name] = path;
		}

		return named;
	}

	//"r,g,b,a" as a colour, which is how one is written in our files. White where it says nothing, because
	//a tint of nothing at all means the field was never set rather than a surface that draws black.
	private static Color Colour( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return Color.White;

		var parts = text.Split( ',' );
		if ( parts.Length is < 3 or > 4 ) return Color.White;

		var channels = new float[4] { 0f, 0f, 0f, 1f };

		for ( var i = 0; i < parts.Length; i++ )
		{
			if ( !float.TryParse( parts[i].Trim(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out channels[i] ) )
				return Color.White;
		}

		return new Color( channels[0], channels[1], channels[2], channels[3] );
	}

	//Reads a material from disk, ours or a ported legacy one; null if there is no file
	internal static Material FromFile( string filename )
	{
		if ( string.IsNullOrWhiteSpace( filename ) ) return null;

		var path = filename.Replace( '\\', '/' );

		if ( Loaded( path ) is { } cached )
			return cached;

		//Ours first: it is what a project written here contains, and their reader would not know what to make of it anyway.
		var fields = Definition( path ) ?? CoreImportedMaterial.Read( path );
		if ( fields is null ) return null;

		var core = new CoreMaterial( path, string.IsNullOrEmpty( fields.Shader )
			? CoreSceneRenderer.StandardShader
			: fields.Shader );

		Fill( core, fields );
		return new Material( core );
	}

	//Reloads a file into the existing instance, since holders keep the object; bumps its revision
	internal static void ReloadFromDisk( string filename )
	{
		if ( string.IsNullOrWhiteSpace( filename ) ) return;

		var path = filename.Replace( '\\', '/' ).TrimStart( '/' );

		if ( Loaded( path ) is not { core: not null } loaded ) return;

		var fields = Definition( path ) ?? CoreImportedMaterial.Read( path );
		if ( fields is null ) return;

		Fill( loaded.core, fields );

		//Everything that built a draw from it builds that again - see CoreResources.MaterialRefilled.
		CoreResources.MaterialRefilled();
	}

	//Materials loaded from files, by path, weakly held; separate from Game.Resources to avoid clashes
	static readonly System.Collections.Generic.Dictionary<string, System.WeakReference<Material>> loaded
		= new( System.StringComparer.OrdinalIgnoreCase );

	//Normalised path key, cached per spelling to avoid per-frame string garbage
	static string LoadedKey( string path )
	{
		path ??= "";

		lock ( keys )
		{
			if ( keys.TryGetValue( path, out var key ) ) return key;

			key = Resource.FixPath( path );

			//Names made up as a game runs would grow it for ever; past a project's worth of paths it starts again.
			if ( keys.Count >= 8192 ) keys.Clear();

			keys[path] = key;
			return key;
		}
	}

	private static readonly Dictionary<string, string> keys = new( StringComparer.Ordinal );

	static void Remember( Material material )
	{
		if ( string.IsNullOrWhiteSpace( material?.Name ) ) return;

		lock ( loaded ) loaded[LoadedKey( material.Name )] = new System.WeakReference<Material>( material );
	}

	//The material loaded from this file and still alive, if there is one.
	internal static Material Loaded( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		lock ( loaded )
		{
			if ( loaded.TryGetValue( LoadedKey( path ), out var held ) && held.TryGetTarget( out var material ) &&
				material.core is not null )
			{
				return material;
			}
		}

		return Game.Resources?.Get<Material>( path );
	}

	/// <summary>A material from an in-memory definition, not the file on disk.</summary>
	public static Material FromDefinition( MaterialDefinition definition, string name = null )
	{
		if ( definition is null ) return null;

		var core = new CoreMaterial(
			string.IsNullOrWhiteSpace( name ) ? ( definition.ResourcePath ?? "preview" ) : name,
			"" );

		Fill( core, FieldsOf( definition ) );
		return new Material( core, anonymous: true );
	}

	/// <summary>Replaces this material's maps and numbers with the definition's, without touching disk.</summary>
	public void ApplyDefinition( MaterialDefinition definition )
	{
		if ( definition is null || core is null ) return;

		Fill( core, FieldsOf( definition ) );
	}

	static CoreImportedMaterial.Fields FieldsOf( MaterialDefinition definition )
	{
		return new CoreImportedMaterial.Fields
		{
			Shader = definition.Shader ?? "",
			PhysicsSurface = definition.PhysicsSurface ?? "",
			Numbers = definition.Numbers is { Count: > 0 } ? new( definition.Numbers ) : null,
			Pictures = definition.Textures is { Count: > 0 } ? new( definition.Textures ) : null,
			ColorMap = definition.ColorMap ?? "",
			Normal = definition.Normal ?? "",
			Emission = definition.Emission ?? "",
			OpacityMask = definition.OpacityMask ?? "",
			Tint = definition.Tint,
			Roughness = definition.Roughness,
			Metalness = definition.Metalness,
			EmissionStrength = definition.EmissionStrength,
			AlphaCutoff = definition.AlphaCutoff,
			Features = definition.Features is { Count: > 0 } ? new( definition.Features ) : null,
		};
	}

	//A picture by path as the renderer holds it for materials read from file - see CoreResources.FileMap - wrapped
	//for a material's slots. Borrowed: the renderer's cache owns it. One wrapper a picture and colour space.
	static Texture FileTexture( string image, bool data )
	{
		var key = $"{image}|{( data ? "data" : "colour" )}";

		lock ( fileTextures )
		{
			if ( fileTextures.TryGetValue( key, out var kept ) ) return kept;
		}

		var handle = CoreResources.FileMap( image, srgb: !data, out var width, out var height );
		if ( handle == 0 ) return null;

		var made = CoreTexture.Borrowing( handle, width, height );
		made.Name = image;
		made.RawPixels = data;
		made.DeviceSrgb = !data;

		var texture = new Texture( made, image );

		lock ( fileTextures ) fileTextures[key] = texture;

		return texture;
	}

	private static readonly Dictionary<string, Texture> fileTextures = new();

	static void Fill( CoreMaterial core, CoreImportedMaterial.Fields fields )
	{
		core.Shader = string.IsNullOrEmpty( fields.Shader )
			? CoreSceneRenderer.StandardShader
			: fields.Shader;

		core.Textures.Clear();

		//Colour and glow are sRGB; normals and shader-declared maps are linear data
		void Map( string name, string image, bool data )
		{
			if ( string.IsNullOrWhiteSpace( image ) ) return;

			//Use the streamed, compressed file texture rather than loading the image into memory again
			var texture = CoreDevTextures.IsPicture( image ) ? FileTexture( image, data ) : null;
			texture ??= Texture.Load( image, warnOnMissing: false );

			//A colour picked in the editor has no file behind it. The colour slot folds it into the tint below
			//and needs no sample; every other slot - the glow above all - needs the one texel, or it is lost.
			if ( texture is null && name != "Color" ) texture = CoreDevTextures.AsTexture( image );

			if ( texture is null ) return;

			texture.UsedOutsidePanels = true;
			if ( data && texture.core is not null ) texture.core.RawPixels = true;

			core.Textures[name] = texture;
		}

		Map( "Color", fields.ColorMap, data: false );

		//Cut by a mask of its own: the colour with the mask folded into its alpha, the blocks the renderer draws a material on disk from -
		//see MaterialDefinition.OpacityMask. Kept by that name, so a refill finds the one it made rather than putting another on the device.
		if ( CoreDevTextures.IsPicture( fields.OpacityMask ) && CoreDevTextures.IsPicture( fields.ColorMap ) )
		{
			var name = fields.ColorMap + "#cut=" + fields.OpacityMask;
			var cut = Game.Resources.Get<Texture>( name );

			if ( cut is null && CoreSceneRenderer.CutColour( fields.ColorMap, fields.OpacityMask, out var width, out var height ) is var handle && handle != 0 )
			{
				var made = CoreTexture.OnDevice( handle, width, height, name );
				made.DeviceSrgb = true;

				cut = new Texture( made );
				cut.RegisterWeakResourceId( name );
			}

			if ( cut is not null )
			{
				cut.UsedOutsidePanels = true;
				core.Textures["Color"] = cut;
			}
		}
		Map( "Normal", fields.Normal, data: true );
		Map( "Emission", fields.Emission, data: false );

		//And the ones its shader asked for on top of those, under the names the shader gave them - g_tHeight and the like. They
		//cost no binding: the shader reads them out of the device's indexed table, so a material may carry as many as it likes.
		if ( fields.Pictures is not null )
		{
			foreach ( var (name, image) in fields.Pictures ) Map( name, image, data: true );
		}

		//A flat colour in the colour slot is folded into the tint, since it has no texture to sample
		var tint = fields.Tint;

		if ( CoreDevTextures.AsRead( fields.ColorMap ) is { } flat ) tint *= flat;

		core.Values["Tint"] = new Vector4( tint.r, tint.g, tint.b, tint.a );
		core.Values["Roughness"] = new Vector4( fields.Roughness, 0, 0, 0 );
		core.Values["Metalness"] = new Vector4( fields.Metalness, 0, 0, 0 );
		core.Values["EmissionStrength"] = new Vector4( fields.EmissionStrength, 0, 0, 0 );
		core.Values["AlphaCutoff"] = new Vector4( fields.AlphaCutoff, 0, 0, 0 );
		//Features as numbers (see CoreResources.ComboOf); last fill's cleared first so removed ones go
		var stale = new List<string>();

		foreach ( var name in core.Values.Keys )
		{
			if ( name.StartsWith( "F_", StringComparison.OrdinalIgnoreCase ) ) stale.Add( name );
		}

		foreach ( var name in stale ) core.Values.Remove( name );

		if ( fields.Features is not null )
		{
			foreach ( var (name, value) in fields.Features ) core.Values[name] = new Vector4( value, 0, 0, 0 );
		}

		void Switch( string name, bool on )
		{
			if ( on && !core.Values.ContainsKey( name ) ) core.Values[name] = new Vector4( 1, 0, 0, 0 );
		}

		Switch( "F_ALPHA", fields.Masked );
		Switch( "F_TRANSLUCENT", fields.Translucent );
		Switch( "F_BACKFACES", fields.TwoSided );

		//Declared shader numbers; previous ones cleared first so a refill updates them, named fields win
		foreach ( var name in core.Declared ) core.Values.Remove( name );
		core.Declared.Clear();

		if ( fields.Numbers is not null )
		{
			foreach ( var (name, value) in fields.Numbers )
			{
				if ( core.Values.ContainsKey( name ) ) continue;

				core.Values[name] = value;
				core.Declared.Add( name );
			}
		}

		//What it feels like underfoot. Read here rather than only by the physics, because a material is where appearance and surface are set
		//together - see MaterialDefinition.PhysicsSurface. Below zero says the material named none, which is what every material made at runtime is.
		core.SurfaceIndex = string.IsNullOrWhiteSpace( fields.PhysicsSurface )
			? -1
			: ResourceLibrary.Get<Surface>( fields.PhysicsSurface )?.Index ?? -1;

		core.Revision++;
	}

	/// <summary>Load a material from disk. Has internal cache.</summary>
	public static async Task<Material> LoadAsync( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		if ( !string.IsNullOrWhiteSpace( filename ) && await Directory.TryLoadAsync( filename, ResourceType.Material ) is Material m )
			return m;

		using var manifest = AsyncResourceLoader.Load( filename );
		if ( manifest is not null )
		{
			await manifest.WaitForLoad();
		}

		//The same two as in Load: our own file off disk, and then the name a ported project would
		//have called it. Reading either is opening a file, so there is nothing here worth waiting on.
		return FromFile( filename ) ?? Ported( filename );
	}

}

using Rinten.Engine;
using System;
using System.Diagnostics;
using System.Reflection;

namespace Editor;

public class AssetType
{
	internal static Dictionary<int, AssetType> AssetTypeCache = new Dictionary<int, AssetType>();

	TypeDescription _typeDescription;
	AssetTypeAttribute _assetTypeAttribute;

	/// <summary>All currently registered asset types, including the base types such as models, etc.</summary>
	public static IReadOnlyCollection<AssetType> All => AssetTypeCache.Values;

	/// <summary>Model (.mdl) asset type.</summary>
	public static AssetType Model { get; protected set; }

	/// <summary>Texture definition (.tex) asset type.</summary>
	public static AssetType Texture { get; protected set; }

	/// <summary>Material (.mat) asset type.</summary>
	public static AssetType Material { get; protected set; }

	/// <summary>Source audio (.wav, .ogg, .mp3, .flac) asset type.</summary>
	public static AssetType SoundFile { get; protected set; }

	/// <summary>A sound event</summary>
	public static AssetType SoundEvent { get; protected set; }

	/// <summary>A soundscape</summary>
	public static AssetType Soundscape { get; protected set; }

	/// <summary>Source picture (.png, .jpg, .tga) asset type.</summary>
	public static AssetType ImageFile { get; protected set; }

	/// <summary>Shader (.shader) asset type.</summary>
	public static AssetType Shader { get; protected set; }

	/// <summary>Exported mesh (.fbx, .gltf, .obj, .smd) asset type.</summary>
	public static AssetType Mesh { get; protected set; }

	/// <summary>Name of the asset type for UI purposes.</summary>
	public string FriendlyName { get; internal set; } = "";

	/// <summary>Primary file extension for this asset type.</summary>
	public string FileExtension { get; internal set; } = "";

	/// <summary>All file extensions for this asset type.</summary>
	public IReadOnlyList<string> FileExtensions => AllFileExtensions;

	/// <summary>This asset type is hidden by default from asset browser, etc.</summary>
	public bool HiddenByDefault { get; internal set; }

	/// <summary>A simple asset is used by something else. It never exists in the game on its own.</summary>
	public bool IsSimpleAsset { get; internal set; }

	/// <summary>This asset type can have dependencies</summary>
	public bool HasDependencies { get; internal set; }

	/// <summary>Use asset type icon, over any preview image.</summary>
	public bool PrefersIconThumbnail { get; internal set; }

	/// <summary>16x16 icon for this asset type.</summary>
	public Pixmap Icon16 { get; internal set; }

	/// <summary>64x64 icon for this asset type.</summary>
	public Pixmap Icon64 { get; internal set; }

	/// <summary>128x128 icon for this asset type.</summary>
	public Pixmap Icon128 { get; internal set; }

	/// <summary>256x256 icon for this asset type.</summary>
	public Pixmap Icon256 { get; internal set; }

	/// <summary>Whether this asset type is a custom game resource or not.</summary>
	public bool IsGameResource { get; internal set; }

	/// <summary>Type that will be returned by <see cref="Asset.LoadResource()"/>.</summary>
	public Type ResourceType { get; internal set; }

	/// <summary>Category of this asset type, for grouping in UI.</summary>
	public string Category { get; internal set; } = "Other";

	/// <summary>Color that represents this asset, for use in the asset browser.</summary>
	public Color Color { get; internal set; } = Color.Magenta;

	/// <summary>Flags for this asset type</summary>
	public AssetTypeFlags Flags => _assetTypeAttribute?.Flags ?? default;

	public override string ToString() => FriendlyName;

	internal List<string> AllFileExtensions { get; set; } = new();

	private void GenerateGlyphs( AssetTypeAttribute gr )
	{
		//A resource that can only be loaded, not built empty, cannot draw its icon; it gets the default instead
		try
		{
			if ( Activator.CreateInstance( gr.TargetType ) is Resource temp )
			{
				Icon256 = Pixmap.FromBitmap( temp.GetAssetTypeIcon( 256, 256 ) );
				Icon128 = Pixmap.FromBitmap( temp.GetAssetTypeIcon( 128, 128 ) );
				Icon64 = Pixmap.FromBitmap( temp.GetAssetTypeIcon( 64, 64 ) );
				Icon16 = Pixmap.FromBitmap( temp.GetAssetTypeIcon( 16, 16 ) );
				return;
			}
		}
		catch ( Exception e )
		{
			Log.Info( $"{gr.TargetType} could not draw its own icon: {e.Message}" );
		}

		var missing_svg = "<svg viewBox=\"0 0 16 16\" xmlns=\"http://www.w3.org/2000/svg\" fill=\"#000000\"><g id=\"SVGRepo_bgCarrier\" stroke-width=\"0\"></g><g id=\"SVGRepo_tracerCarrier\" stroke-linecap=\"round\" stroke-linejoin=\"round\"></g><g id=\"SVGRepo_iconCarrier\"> <g fill=\"#66b8ff\"> <path d=\"m 4 0 c -2 0 -2 2 -2 2 v 12 c 0 2 2 2 2 2 h 8 s 2 0 2 -2 v -11 l -3 -3 z m 3.753906 3 c 0.019532 0 0.039063 0.011719 0.058594 0.011719 c 2.160156 -0.089844 4.0625 1.5625 4.183594 3.746093 l 0.003906 0.027344 v 0.027344 c 0 0.046875 -0.003906 0.085938 -0.007812 0.132812 c 0.003906 0.011719 0.007812 0.054688 0.007812 0.054688 l -0.003906 1 h -0.121094 c -0.074219 0.328125 -0.1875 0.636719 -0.355469 0.917969 c -0.015625 0.03125 -0.039062 0.054687 -0.054687 0.082031 h 0.53125 v 2 h 0.003906 v 2 c 0 1 -1 1 -1 1 l -1.003906 -0.003906 v -0.984375 c -0.007813 1.089843 -0.910156 1.988281 -1.996094 1.988281 c -1.09375 0 -2 -0.90625 -2 -2 c 0 -0.382812 0.132812 -0.710938 0.335938 -1 h -0.335938 v -1 h -2.003906 v -2 h 2.003906 v -1 h -0.003906 v 0.007812 h -2 l 0.003906 -1.007812 s 0.003906 -0.054688 0.007812 -0.09375 c -0.03125 -2.109375 1.671876 -3.789062 3.746094 -3.902344 z m 4.242188 8 h -1.996094 v 0.003906 h 1.996094 z m -3.332032 -5.867188 c 0.71875 0.261719 1.273438 0.929688 1.324219 1.730469 c 0 -0.003906 0.003907 -0.003906 0.003907 -0.011719 c -0.054688 -0.796874 -0.601563 -1.457031 -1.328126 -1.722656 z m 1 6.867188 c 0.199219 0.285156 0.332032 0.613281 0.332032 0.988281 v -0.988281 z m -5.667968 0.003906 h 2 v 2 l -0.996094 -0.003906 c -1 0 -1 -1 -1 -1 z m 0 0\" fill-opacity=\"0.34902\"></path> <path d=\"m 8.152344 4.007812 c -0.4375 -0.023437 -0.882813 0.046876 -1.300782 0.222657 c -1.117187 0.460937 -1.851562 1.558593 -1.851562 2.769531 h 2 c 0 -0.40625 0.242188 -0.769531 0.617188 -0.921875 c 0.375 -0.15625 0.800781 -0.074219 1.089843 0.214844 c 0.289063 0.289062 0.371094 0.714843 0.214844 1.089843 s -0.515625 0.617188 -0.921875 0.617188 c -0.550781 0 -1 0.449219 -1 1 v 2 h 2 v -1.179688 c 0.785156 -0.28125 1.441406 -0.875 1.769531 -1.671874 c 0.464844 -1.117188 0.207031 -2.414063 -0.648437 -3.269532 c -0.535156 -0.535156 -1.242188 -0.835937 -1.96875 -0.871094 z m -0.152344 7.992188 c -0.550781 0 -1 0.449219 -1 1 s 0.449219 1 1 1 s 1 -0.449219 1 -1 s -0.449219 -1 -1 -1 z m 0 0\"></path> </g> </g></svg>";
		using var bitmap256 = Bitmap.CreateFromSvgString( missing_svg, 256, 256 );
		Icon256 = Pixmap.FromBitmap( bitmap256 );

		using var bitmap128 = bitmap256.Resize( 128, 128 );
		Icon128 = Pixmap.FromBitmap( bitmap128 );

		using var bitmap64 = bitmap256.Resize( 64, 64 );
		Icon64 = Pixmap.FromBitmap( bitmap64 );

		using var bitmap16 = bitmap256.Resize( 64, 64 );
		Icon16 = Pixmap.FromBitmap( bitmap16 );
	}

	internal void Init()
	{
		//Bound by extension, not name; a .mdl stays a ModelDefinition resource type
		if ( FileExtension == "mdl" ) Model = this;
		if ( FileExtension == "mat" ) Material = this;
		if ( FileExtension == "tex" ) Texture = this;
		if ( FileExtension == "sound" ) SoundEvent = this;
		if ( FileExtension == "sndscape" ) Soundscape = this;

		//Keep an icon already drawn; this runs last and would otherwise overwrite it with the placeholder
		if ( Icon256 is not null && Icon16 is not null )
			return;

		DrawGlyphs( "description", "#8a8f98" );
	}

	/// <summary>Another resource type this asset can load as, or null - only our own formats have one.</summary>
	public System.Type ProvidedResourceType { get; private set; }

	private void Init( TypeDescription type, AssetTypeAttribute attribute )
	{
		IsGameResource = true;

		_typeDescription = type;
		_assetTypeAttribute = attribute;

		ResourceType = type.TargetType;
		ProvidedResourceType = type.GetAttribute<ProvidesResourceAttribute>()?.ResourceType;

		Category = attribute.Category;
		FriendlyName = attribute.Name;
		FileExtension = attribute.Extension;

		GenerateGlyphs( attribute );

		//For game resources, use the background color specified in the attribute
		Color = attribute.IconColor;
	}

	//Return true if this extension matches
	internal bool HasExtension( string extension )
	{
		if ( extension.StartsWith( '.' ) )
			extension = extension.Trim( '.' );

		if ( extension.EndsWith( "_c" ) )
			extension = extension.Substring( 0, extension.Length - 2 );

		if ( string.Equals( extension, FileExtension, StringComparison.InvariantCultureIgnoreCase ) )
			return true;

		foreach ( var extra in AllFileExtensions )
		{
			if ( string.Equals( extra, extension, StringComparison.InvariantCultureIgnoreCase ) )
				return true;
		}

		return false;
	}

	internal bool CouldBeIdentifiedAs( string name, bool fuzzy = false )
	{
		if ( string.Equals( FriendlyName, name, System.StringComparison.OrdinalIgnoreCase ) ) return true;
		if ( string.Equals( FileExtension, name, System.StringComparison.OrdinalIgnoreCase ) ) return true;

		if ( fuzzy )
		{
			if ( FriendlyName.Contains( name, System.StringComparison.OrdinalIgnoreCase ) ) return true;
			if ( FileExtension.Contains( name, System.StringComparison.OrdinalIgnoreCase ) ) return true;
		}

		return false;
	}

	/// <summary>Find an asset type by name or extension match.</summary>
	public static AssetType Find( string name, bool allowPartials = false )
	{
		//find exact first
		var v = AssetTypeCache.Values.FirstOrDefault( x => x.CouldBeIdentifiedAs( name ) );
		if ( v != null ) return v;
		if ( !allowPartials ) return null;

		var topFriendlyName = AssetTypeCache.Values.Where( x => x.FriendlyName.Contains( name, System.StringComparison.OrdinalIgnoreCase ) )
								.OrderBy( x => x.FriendlyName.Length + (x.HiddenByDefault ? 30 : 0) )
								.FirstOrDefault();

		if ( topFriendlyName != null )
			return topFriendlyName;

		var topExtension = AssetTypeCache.Values
								.Where( x => x.FileExtension.Contains( name, System.StringComparison.OrdinalIgnoreCase ) )
								.OrderBy( x => x.FileExtension.Length + (x.HiddenByDefault ? 30 : 0) )
								.FirstOrDefault();
		return topExtension;
	}

	//Called to insert the asset types for types defined in Engine/Game
	internal static bool UpdateCustomTypes()
	{
		//Before the project walk, which asks whether a file has a type; pictures have none until this runs
		AddNativeTypes();

		bool bChanged = false;
		foreach ( var t in EditorTypeLibrary.GetTypesWithAttribute<AssetTypeAttribute>() )
		{
			bChanged = UpdateType( t.Type, t.Attribute ) || bChanged;
		}

		return bChanged;
	}

	private static bool UpdateType( TypeDescription type, AssetTypeAttribute attribute )
	{
		var old = AssetTypeCache.Values.FirstOrDefault( x => x.FileExtension == attribute.Extension );

		bool created = false;

		if ( old is null )
		{
			created = true;

			//create it in the engine
			old = AddManagedType( attribute );

			if ( old is null )
				return false;
		}

		old.Init( type, attribute );

		return created;
	}

	//Next id for game-declared types, counting down so it can't collide with other ids
	private static int nextManagedId = -1;

	//A game resource's type, registered by us and by nobody else.
	private static AssetType AddManagedType( AssetTypeAttribute attribute )
	{
		var type = new AssetType
		{
			FriendlyName = attribute.Name,
			FileExtension = attribute.Extension,
			Category = attribute.Category,
			Color = attribute.IconColor,
			IsSimpleAsset = true,
			IsGameResource = true,
		};

		type.AllFileExtensions.Add( attribute.Extension );

		AssetTypeCache[nextManagedId--] = type;

		return type;
	}

	//The pictures a project keeps beside its materials.
	private static readonly string[] ImageExtensions = { "png", "jpg", "jpeg", "tga", "bmp", "gif", "webp", "exr", "hdr" };

	//The audio a sound is played from.
	private static readonly string[] AudioExtensions = { "wav", "mp3", "ogg", "flac" };

	//The shaders a material names.
	private static readonly string[] ShaderExtensions = { "shader", "slang" };

	//The exports a model definition is built from.
	private static readonly string[] MeshExtensions = { "fbx", "gltf", "glb", "obj", "dae", "stl", "smd" };

	//Types with no resource class (images, audio, shaders, meshes), so those files are still assets
	internal static void AddNativeTypes()
	{
		if ( ImageFile is not null ) return;

		ImageFile = AddNativeType( "Image", ImageExtensions, "Rendering", "#d6a35c", "image", typeof( Texture ) );
		SoundFile = AddNativeType( "Sound File", AudioExtensions, "Audio", "#7fd68f", "volume_up", typeof( SoundFile ) );
		Shader = AddNativeType( "Shader", ShaderExtensions, "Rendering", "#5cd6c4", "gradient", null );
		Mesh = AddNativeType( "Mesh", MeshExtensions, "Rendering", "#c4d65c", "architecture", null );
	}

	//One file kind that no class declares.
	private static AssetType AddNativeType( string name, string[] extensions, string category, string color, string glyph, System.Type resourceType )
	{
		var type = new AssetType
		{
			FriendlyName = name,
			FileExtension = extensions[0],
			Category = category,
			Color = color,
			IsSimpleAsset = true,
			IsGameResource = false,
			ResourceType = resourceType,
		};

		type.AllFileExtensions.AddRange( extensions );
		type.DrawGlyphs( glyph, color );

		AssetTypeCache[nextManagedId--] = type;

		return type;
	}

	//Draws a type tile; bitmaps aren't disposed since Pixmap.FromBitmap keeps the object, not a copy
	[System.Diagnostics.CodeAnalysis.SuppressMessage( "Reliability", "CA2000",
		Justification = "Pixmap.FromBitmap takes the bitmap rather than copying it" )]
	private void DrawGlyphs( string glyph, Color background )
	{
		//Not "using": see the remark above. The pixmap is the owner from here on.
		var large = Resource.CreateSimpleAssetTypeIcon( glyph, 256, 256, background );
		var medium = Resource.CreateSimpleAssetTypeIcon( glyph, 128, 128, background );
		var small = Resource.CreateSimpleAssetTypeIcon( glyph, 64, 64, background );
		var tiny = Resource.CreateSimpleAssetTypeIcon( glyph, 16, 16, background );

		Icon256 = Pixmap.FromBitmap( large );
		Icon128 = Pixmap.FromBitmap( medium );
		Icon64 = Pixmap.FromBitmap( small );
		Icon16 = Pixmap.FromBitmap( tiny );
	}

	internal static void ImportCustomTypeFiles()
	{
		var sw = Stopwatch.StartNew();

		Logging.Flush();

		AddNativeTypes();

		var found = new List<string>();

		foreach ( var file in FileSystem.Content.FindFile( "/", "*", true ) )
		{
			var ext = System.IO.Path.GetExtension( file );
			var t = FromExtension( ext );
			if ( t is null ) continue;

			found.Add( FileSystem.Content.GetFullPath( file ) );
		}

		AssetSystem.RegisterFiles( found );

		if ( sw.Elapsed.TotalSeconds > 1 )
		{
			Log.Warning( $"ImportCustomTypeFiles took {sw.Elapsed.TotalSeconds:0.00} seconds" );
		}
	}

	/// <summary>The AssetType for a resource type (Texture, Material, ...), or null.</summary>
	public static AssetType FromType( System.Type t )
	{
		//A picture, not the definition beside it: that is what Asset.LoadResource reads for a Texture.
		if ( t == typeof( Texture ) ) return AssetType.ImageFile;
		if ( t == typeof( Material ) ) return AssetType.Material;
		if ( t == typeof( Model ) ) return AssetType.Model;
		if ( t == typeof( SoundFile ) ) return AssetType.SoundFile;
		//A graph lives in its model, so picking a graph picks the model - see AnimGraph.Load
		if ( t == typeof( AnimGraph ) ) return AssetType.Model;

		foreach ( var a in t.GetCustomAttributes<AssetTypeAttribute>() )
		{
			var at = Find( a.Extension, false );
			if ( at != null ) return at;
		}

		return null;
	}

	//Written by whatever threads scan content
	static System.Collections.Concurrent.ConcurrentDictionary<string, AssetType> _extensionCache = new();

	public static AssetType FromExtension( string extension )
	{
		if ( _extensionCache.TryGetValue( extension, out var t ) )
			return t;

		t = AssetTypeCache.Where( x => x.Value.HasExtension( extension ) ).Select( x => x.Value ).FirstOrDefault();

		//Don't cache null, the asset type might appear later
		if ( t is not null )
		{
			_extensionCache[extension] = t;
		}

		return t;
	}

	//Tries its hardest to resolve an asset type from a file path
	internal static AssetType ResolveFromPath( string path )
	{
		var extension = System.IO.Path.GetExtension( path ).Trim( '.' );
		if ( extension.EndsWith( "_c" ) ) extension = extension.Substring( 0, extension.Length - 2 );

		return FromExtension( extension );
	}

	/// <summary>Returns true if there is an editor available for this asset type.</summary>
	public bool HasEditor
	{
		get
		{
			return EditorTypeLibrary.GetTypesWithAttribute<EditorForAssetTypeAttribute>()
					.Where( x => HasExtension( x.Attribute.Extension ) )
					.Any();
		}
	}
}

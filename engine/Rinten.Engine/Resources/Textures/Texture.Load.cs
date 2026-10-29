using NativeEngine;
using Rinten.Engine;
using Rinten.Mounting;

namespace Rinten;

public partial class Texture
{
	/// <summary>Try to load a texture from given filesystem, by filename.</summary>
	[System.Obsolete( "Use Texture.Load or Texture.LoadFromFileSystem" )]
	public static Texture Load( BaseFileSystem filesystem, string filepath, bool warnOnMissing = true )
	{
		return LoadInternal( filesystem, filepath, warnOnMissing );
	}

	/// <summary>Try to load a texture from given filesystem, by filename.</summary>
	public static Texture LoadFromFileSystem( string filepath, BaseFileSystem filesystem, bool warnOnMissing = true )
	{
		return LoadInternal( filesystem, filepath, warnOnMissing );
	}

	//All the helpers should flow through this to actually load
	static Texture LoadInternal( BaseFileSystem filesystem, string filepath, bool warnOnMissing = true )
	{
		//if ( Host.IsUnitTest ) return null;
		if ( string.IsNullOrWhiteSpace( filepath ) ) return null;

		//A compiled name from somewhere else - a mounted game, a ported project - read as the texture resource this
		//engine has. The compiled name is a .vtex_c; ours is a .tex.
		filepath = filepath.Replace( ".vtex_c", ".tex" );

		if ( Rinten.Mounting.Directory.TryLoad( filepath, ResourceType.Texture, out object model ) && model is Texture m )
			return m;

		//Normalise separators and leading slash but keep case; Linux paths are case-sensitive
		if ( !TextureLoader.ImageUrl.IsAppropriate( filepath ) && !TextureLoader.ImageDataUri.IsAppropriate( filepath ) )
			filepath = filepath.NormalizeFilename( false, false );

		if ( filepath.StartsWith( '/' ) )
			filepath = filepath[1..];

		if ( Find( filepath ) is Texture existing )
			return existing;

		filesystem ??= GlobalContext.Current.FileMount;

		var key = (filesystem, filepath);
		if ( missedAt.TryGetValue( key, out var when ) && RealTime.Now - when < MissLasts )
			return null;

		var tex = TryToLoad( filesystem, filepath, warnOnMissing );
		if ( tex == null )
		{
			if ( TextureLoader.Image.IsAppropriate( filepath ) )
				missedAt[key] = RealTime.Now;

			return null;
		}

		missedAt.TryRemove( key, out _ );
		return tex;
	}

	//When each picture file that was not there was last looked for: a gizmo handle per object asks for the same one
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<(BaseFileSystem, string), float> missedAt = new();

	//Seconds a missing picture stays missing; new imports show up after it
	private const float MissLasts = 1.0f;


	/// <summary>Try to load a texture.</summary>
	public static Texture Load( string path_or_url, bool warnOnMissing = true ) => LoadInternal( GlobalContext.Current.FileMount, path_or_url, warnOnMissing );


	internal static void Hotload( BaseFileSystem filesystem, string filepath )
	{
		//What was generated from the picture - a sprite's frames - is not under the picture's own name, so the lookup below never finds it.
		Rinten.Resources.ImageFileGenerator.FileChanged( filepath );

		var existing = Game.Resources.Get<Texture>( filepath );
		if ( existing is not null )
		{
			existing.TryReload( filesystem, existing.ResourcePath );
		}
		else if ( filepath.StartsWith( "/" ) && TextureLoader.Image.IsAppropriate( filepath ) )
		{
			//Image might have been loaded without '/' so try again without it
			Hotload( filesystem, filepath[1..] );
		}
		else if ( TextureLoader.SvgLoader.IsAppropriate( filepath ) )
		{
			//SVGs can have query parameters appended to them, find the ones that match and reload with the same parameters
			var svgPath = filepath.TrimStart( '/' );
			foreach ( var svgTarget in Game.Resources.FindWeakByPathPrefix<Texture>( svgPath ).ToArray() )
			{
				svgTarget.TryReload( filesystem, svgTarget.ResourcePath );
			}
		}
	}

	internal static Texture TryToLoad( BaseFileSystem filesystem, string filepath, bool warnOnMissing = true )
	{
		if ( Application.IsUnitTest )
			return null;

		//if no filesystem provided
		filesystem ??= GlobalContext.Current.FileMount;

		//Svg Loader
		if ( TextureLoader.SvgLoader.IsAppropriate( filepath ) )
		{
			return TextureLoader.SvgLoader.Load( filesystem, filepath, warnOnMissing );
		}

		//Video Texture loader
		if ( TextureLoader.VideoTextureLoader.IsAppropriate( filepath ) )
		{
			return TextureLoader.VideoTextureLoader.Load( filesystem, filepath, warnOnMissing );
		}

		//Image Url loader
		if ( TextureLoader.ImageUrl.IsAppropriate( filepath ) )
		{
			return TextureLoader.ImageUrl.Load( filepath, warnOnMissing );
		}

		//Data URI loader
		if ( TextureLoader.ImageDataUri.IsAppropriate( filepath ) )
		{
			return TextureLoader.ImageDataUri.Load( filepath, warnOnMissing );
		}

		//Image loader
		if ( TextureLoader.Image.IsAppropriate( filepath ) )
		{
			return TextureLoader.Image.Load( filesystem, filepath, warnOnMissing );
		}

		//Thumb loader
		if ( TextureLoader.ThumbLoader.IsAppropriate( filepath ) )
		{
			return TextureLoader.ThumbLoader.Load( filepath );
		}

		//Precache.Add( filename );

		//A resource in the legacy compiled format, which this engine does not read. Nothing to load it with here - the caller gets nothing, the same as for a file that is not there.
		return null;
	}

	/// <summary>Load a texture asynchronously. Will return when the texture is loaded and valid. This is useful when loading textures from the web.</summary>
	[Obsolete( "Use LoadAsync or LoadFromFileSystemAsync" )]
	public static Task<Texture> LoadAsync( BaseFileSystem filesystem, string filepath, bool warnOnMissing = true )
	{
		return LoadFromFileSystemAsync( filepath, filesystem, warnOnMissing );
	}

	/// <summary>Load a texture asynchronously. Will return when the texture is loaded and valid. This is useful when loading textures from the web, or without any big loading hitches.</summary>
	public static Task<Texture> LoadAsync( string filepath, bool warnOnMissing = true )
	{
		return LoadFromFileSystemAsync( filepath, GlobalContext.Current.FileMount, warnOnMissing );
	}

	/// <summary>Load a texture asynchronously. Will return when the texture is loaded and valid. This is useful when loading textures from the web, or without any big loading hitches.</summary>
	public static async Task<Texture> LoadFromFileSystemAsync( string filepath, BaseFileSystem filesystem, bool warnOnMissing = true )
	{
		//Capture the context's cancellation token now, before awaiting. GlobalContext.Current is AsyncLocal and flows correctly
		//here (before the first await), but may not be correct after awaiting if the scope was disposed by the caller.
		var ct = GlobalContext.Current.CancellationTokenSource?.Token ?? default;

		var t = LoadInternal( filesystem, filepath, warnOnMissing );
		if ( t == null ) return null;

		while ( !t.IsLoaded )
		{
			await Task.Delay( 10, ct );
		}

		return t;
	}

	/// <summary>Try to get an already loaded texture.</summary>
	public static Texture Find( string filepath )
	{
		if ( string.IsNullOrWhiteSpace( filepath ) ) return null;

		if ( !TextureLoader.ImageUrl.IsAppropriate( filepath ) && !TextureLoader.ImageDataUri.IsAppropriate( filepath ) )
			filepath = filepath.NormalizeFilename( false );

		return Game.Resources.Get<Texture>( filepath );
	}
}

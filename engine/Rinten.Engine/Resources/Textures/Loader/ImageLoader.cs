using NativeEngine;
using SkiaSharp;

namespace Rinten.TextureLoader;

internal static class Image
{
	internal static readonly HashSet<string> Extensions = new( System.StringComparer.OrdinalIgnoreCase )
	{
		".png",
		".jpg",
		".gif",
		".webp",
		".tga",
		".psd",
		".tif",
		".ies",
	};

	internal static bool IsAppropriate( string url )
	{
		var split = url.Split( '?' )[0];
		var extension = System.IO.Path.GetExtension( split );

		return Extensions.Contains( extension );
	}

	public static Texture Load( System.IO.Stream stream, string debugName, bool mips = true )
	{
		System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

		//The bytes copied in rather than the stream handed over: a stream is read by Skia calling back into managed
		//code, and a page's runtime has no way back from native code it was not told about when the module was linked.
		using var memory = new System.IO.MemoryStream();
		stream.CopyTo( memory );
		using var data = SKData.CreateCopy( memory.ToArray() );
		SKCodec codec = null;
		try
		{
			codec = SKCodec.Create( data );

			if ( codec == null )
			{
				Log.Warning( $"Error loading image: {debugName}" );
				return default;
			}

			var frameCount = codec.FrameCount;
			if ( frameCount > 1 )
			{
				var animation = new Texture.Animation( codec );
				codec = null; //ownership transferred to Animation

				var texture = CreateTexture( animation.Bitmap, debugName, mips );
				texture.IsAnimated = true;
				animation.Texture = new System.WeakReference<Texture>( texture );
				Texture.Animations.Add( animation );

				return texture;
			}
			else
			{
				using var image = SKImage.FromEncodedData( data );
				using var bitmap = SKBitmap.FromImage( image );

				return CreateTexture( bitmap, debugName, mips );
			}
		}
		finally
		{
			codec?.Dispose();
		}
	}

	//The formats Skia does not read, which this engine reads itself.
	internal static Texture LoadOurselves( BaseFileSystem filesystem, string filename )
	{
		if ( !filesystem.FileExists( filename ) )
			return null;

		var bytes = filesystem.ReadAllBytes( filename ).ToArray();

		using var bitmap = Bitmap.CreateFromTgaBytes( bytes );
		if ( bitmap is null ) return null;

		return bitmap.ToTexture();
	}

	private static Texture CreateTexture( SKBitmap bitmap, string name, bool mips )
	{
		var format = bitmap.ColorType switch
		{
			SKColorType.Rgba8888 => ImageFormat.RGBA8888,
			SKColorType.Bgra8888 => ImageFormat.BGRA8888,
			SKColorType.Gray8 => ImageFormat.I8,
			_ => throw new System.Exception( $"bitmap.ColorType is {bitmap.ColorType} - unsupported" ),
		};

		int numMips = mips ? (int)Math.Log2( Math.Min( bitmap.Width, bitmap.Height ) ) + 1 : 1;

		var texture = Texture.Create( bitmap.Width, bitmap.Height, format )
			.WithName( name )
			.WithData( bitmap.GetPixels(), bitmap.ByteCount )
			.WithMips( numMips )
			.Finish();

		return texture;
	}

	public static Texture Load( BaseFileSystem filesystem, string filename, bool warnOnMissing = true )
	{
		filename = filename.Normalize();

		if ( Game.Resources.Get<Texture>( filename ) is { } cached )
			return cached;

		//A build that carries this picture as what it compiles to rather than as itself - see CorePictures.
		if ( !filesystem.FileExists( filename ) && FromShippedBlocks( filename ) is { } shipped )
			return shipped;

		try
		{
			var tex = LoadFile( filesystem, filename, true );

			//An IES profile is a light's shape, not a picture to share by name
			if ( System.IO.Path.GetExtension( filename ) != ".ies" ) tex?.RegisterWeakResourceId( filename );

			return tex;
		}
		catch ( System.IO.FileNotFoundException e )
		{
			//Warn once per missing name, otherwise per-frame lookups flood the log
			if ( warnOnMissing && Unseen( filename ) )
			{
				Log.Warning( $"Image.Load: {filename} not found ({e.Message})" );
			}
		}
		catch ( System.Exception e )
		{
			if ( Unseen( filename ) )
				Log.Warning( e, $"Image.Load: {filename}: {e.Message}" );
		}

		return null;
	}

	//A picture file read into a texture of its own: not looked up, not registered under its name
	internal static Texture LoadFile( BaseFileSystem filesystem, string filename, bool mips )
	{
		var extension = System.IO.Path.GetExtension( filename );

		if ( extension == ".tga" ) return LoadOurselves( filesystem, filename );

		if ( extension == ".ies" )
		{
			var bytes = filesystem.ReadAllBytes( filename ).ToArray();
			using var iesBitmap = Bitmap.CreateFromIesBytes( bytes );

			return iesBitmap?.ToTexture();
		}

		using var stream = filesystem.OpenRead( filename );
		return Load( stream, filename, mips );
	}

	//A picture a build carries as its compiled blocks, loaded from those. The colour reading first: that is how a picture loaded from its file
	//goes to the device - see CoreTexture.IsSrgb - so it is the one that looks the same. The data reading where it is the only one the build has.
	private static Texture FromShippedBlocks( string filename )
	{
		var srgb = true;
		var path = CorePictures.ShippedBlocks( filename, srgb: true );

		if ( path is null )
		{
			srgb = false;
			path = CorePictures.ShippedBlocks( filename, srgb: false );
		}

		if ( path is null ) return null;

		ulong handle;
		int width, height;

		//A web build's picture: Basis blocks the page turns into what it reads.
		if ( CoreUniversalTexture.IsOne( path ) )
		{
			if ( !CoreRenderDevice.TextureSize( path, out width, out height ) ) return null;

			handle = CoreRenderDevice.CreateTexture( path );
		}
		else
		{
			var file = CoreTextureFile.Load( path );
			if ( file is null ) return null;

			width = file.Width;
			height = file.Height;
			handle = CoreRenderDevice.CreateTexture( file );
		}

		if ( handle == 0 ) return null;

		var core = CoreTexture.OnDevice( handle, width, height, filename );
		core.DeviceSrgb = srgb;

		var texture = new Texture( core );
		texture.RegisterWeakResourceId( filename );

		return texture;
	}

	//True the first time a name is seen; only throttles warnings, not disk reads
	private static bool Unseen( string filename )
	{
		lock ( complained )
			return complained.Add( filename ?? "" );
	}

	private static readonly System.Collections.Generic.HashSet<string> complained =
		new( System.StringComparer.OrdinalIgnoreCase );
}

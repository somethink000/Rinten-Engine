
namespace Rinten;

public partial class Texture
{
	/// <summary>1x1 solid magenta colored texture.</summary>
	public static Texture Invalid { get => field ??= Create( 1, 1 ).WithData( new byte[4] { 255, 0, 255, 255 } ).Finish(); internal set; }

	/// <summary>1x1 solid white opaque texture.</summary>
	public static Texture White { get => field ??= Create( 1, 1 ).WithData( new byte[4] { 255, 255, 255, 255 } ).Finish(); internal set; }

	/// <summary>1x1 solid black opaque texture.</summary>
	public static Texture Black { get => field ??= Create( 1, 1 ).WithData( new byte[4] { 0, 0, 0, 255 } ).Finish(); internal set; }

	/// <summary>1x1 fully transparent texture.</summary>
	public static Texture Transparent { get => field ??= Create( 1, 1 ).WithData( new byte[4] { 128, 128, 128, 0 } ).Finish(); internal set; }

	internal static unsafe Texture Create( string name, bool anonymous, TextureBuilder builder, IntPtr data, int dataSize )
	{
		var config = builder._config.GetWithFixes();

		//Without their render device there is nowhere to put a texture but memory, which is where one built out of pixels came from anyway.
		var made = CoreTexture.Create( config, string.IsNullOrEmpty( name ) ? "unnamed" : name );

		if ( data != IntPtr.Zero && dataSize > 0 )
			made.Write( new ReadOnlySpan<byte>( (void*)data, dataSize ) );
		else
			made.Clear( builder._initialColor ?? Color.Transparent );

		var result = new Texture( made );

		//Named, it can be found by that name like a texture loaded from disk
		if ( !string.IsNullOrWhiteSpace( name ) )
			result.RegisterWeakResourceId( name );

		return result;
	}
}

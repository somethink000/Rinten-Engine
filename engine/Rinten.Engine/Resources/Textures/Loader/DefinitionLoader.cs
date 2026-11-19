using System.Text.Json.Nodes;

namespace Rinten.TextureLoader;

//A .tex (see TextureDefinition): a picture file and how it is read. Each is a texture of its own,
//so a linear one does not turn the picture's shared colour copy linear too
internal static class Definition
{
	//Source picture -> the .tex files made from it, reloaded when the picture changes
	private static readonly Dictionary<string, HashSet<string>> bySource = new( StringComparer.OrdinalIgnoreCase );

	internal static bool IsAppropriate( string path )
		=> System.IO.Path.GetExtension( path.Split( '?' )[0] ).Equals( ".tex", StringComparison.OrdinalIgnoreCase );

	internal static Texture Load( BaseFileSystem filesystem, string path, bool warnOnMissing )
	{
		if ( !filesystem.FileExists( path ) )
		{
			if ( warnOnMissing ) Log.Warning( $"Texture: {path} not found" );
			return null;
		}

		string source;
		bool srgb, mips;

		try
		{
			var json = JsonNode.Parse( filesystem.ReadAllText( path ) ) as JsonObject;

			source = json?["Source"]?.GetValue<string>();
			srgb = json?["Srgb"]?.GetValue<bool>() ?? true;
			mips = json?["GenerateMips"]?.GetValue<bool>() ?? true;
		}
		catch ( Exception e ) when ( e is System.Text.Json.JsonException or InvalidOperationException or FormatException )
		{
			Log.Warning( $"Texture: {path} is not a readable texture definition: {e.Message}" );
			return null;
		}

		source = source?.Trim().Replace( '\\', '/' ).TrimStart( '/' );

		if ( string.IsNullOrEmpty( source ) )
		{
			Log.Warning( $"Texture: {path} names no source picture" );
			return null;
		}

		if ( IsAppropriate( source ) )
		{
			Log.Warning( $"Texture: {path} names another .tex ({source}) as its picture" );
			return null;
		}

		lock ( bySource )
		{
			if ( !bySource.TryGetValue( source, out var made ) ) bySource[source] = made = new( StringComparer.OrdinalIgnoreCase );
			made.Add( path );
		}

		//A build may carry the picture only as its compiled blocks, already in the reading it was shipped for;
		//a format the image reader does not take (.exr, .dds) goes the ordinary way
		if ( !filesystem.FileExists( source ) || !Image.IsAppropriate( source ) ) return Texture.LoadFromFileSystem( source, filesystem, warnOnMissing );

		var texture = Image.LoadFile( filesystem, source, mips );

		if ( texture is null )
		{
			Log.Warning( $"Texture: {path}: could not read its picture {source}" );
			return null;
		}

		if ( !srgb && texture.core is not null ) texture.core.RawPixels = true;

		texture.RegisterWeakResourceId( path );
		return texture;
	}

	//The .tex textures loaded from a picture that changed, read again
	internal static void SourceChanged( BaseFileSystem filesystem, string picture )
	{
		string[] made;

		lock ( bySource )
		{
			if ( !bySource.TryGetValue( picture.TrimStart( '/' ), out var set ) ) return;
			made = set.ToArray();
		}

		foreach ( var path in made )
			Game.Resources.Get<Texture>( path )?.TryReload( filesystem, path );
	}
}

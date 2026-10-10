using System.IO;

namespace Rinten;

/// <summary>Marks a string as the name of a shader a material may wear, so that the editor offers the ones there are rather than a box to type a path into.</summary>
[System.AttributeUsage( System.AttributeTargets.Property | System.AttributeTargets.Field )]
public sealed class ShaderNameAttribute : System.Attribute
{
}

public sealed partial class MaterialDefinition
{
	/// <summary>One shader a material may name, as somebody picking it sees it.</summary>
	public readonly record struct ShaderChoice( string Path, string Title, string Summary, bool Post );

	/// <summary>Shaders a material may use: under shaders/ or postprocess/, bar the engine's internal ones</summary>
	public static IReadOnlyList<ShaderChoice> Shaders()
	{
		var found = new Dictionary<string, ShaderChoice>( StringComparer.OrdinalIgnoreCase );

		//The project's mounts first, then the engine's own content, so that a project shadowing one of the engine's
		//shaders is the one offered - the same order everything else resolves a shader in. See CoreModel.ResolveOnDisk.
		foreach ( var system in new[] { EngineFileSystem.Mounted, EngineFileSystem.Assets, EngineFileSystem.CoreContent } )
		{
			if ( system is null ) continue;

			IEnumerable<string> files;

			try
			{
				files = system.FindFile( "/", "*.shader", true );
			}
			catch ( IOException )
			{
				continue;
			}

			foreach ( var file in files )
			{
				var path = ( file ?? "" ).Replace( '\\', '/' );

				if ( path.Length == 0 || found.ContainsKey( path ) ) continue;
				if ( !CoreShaderFile.IsOffered( path ) ) continue;

				string full;

				try
				{
					full = system.GetFullPath( path );
				}
				catch ( IOException )
				{
					continue;
				}

				if ( !Wearable( full ) ) continue;

				if ( Choice( path, full ) is { } choice ) found[path] = choice;
			}
		}

		//And the engine's own where nothing has them mounted - a tool, a test, an editor opened before a project was chosen. The same fallback
		//the renderer keeps for the same reason, and it is the reason the engine can draw at all without being handed a project first.
		foreach ( var (path, full) in ShippedShaders() )
		{
			if ( found.ContainsKey( path ) ) continue;

			if ( Choice( path, full ) is { } choice ) found[path] = choice;
		}

		var all = new List<ShaderChoice>( found.Values );

		//Surfaces first and the post-process passes under them, each alphabetically. Somebody opening this list is nearly always
		//painting something, and the handful of shaders that paint the whole screen instead should not be in the way of that.
		all.Sort( static ( a, b ) =>
		{
			if ( a.Post != b.Post ) return a.Post ? 1 : -1;

			return string.Compare( a.Title, b.Title, StringComparison.OrdinalIgnoreCase );
		} );

		return all;
	}

	//Without reading it, because this is also what the closed field says, and that is painted rather than opened - walking a project's folders to draw a row is not a thing to do sixty times a second.
	/// <summary>What to call a shader in the list, from its path alone.</summary>
	public static string ShaderTitle( string path )
	{
		//A material always names its surface now - see MaterialDefinition.Shader - so blank is not a choice somebody made and not a thing to give
		//a name of its own. It used to read "Standard surface", which put an entry in the list that was not a shader and could not be looked up.
		if ( string.IsNullOrWhiteSpace( path ) ) path = MaterialDefinition.StandardShader;

		return Pretty( Path.GetFileNameWithoutExtension( path ) );
	}

	//The engine's own shaders where they are shipped, as the path a material would name and the file it is.
	private static IEnumerable<(string Path, string Full)> ShippedShaders()
	{
		foreach ( var root in new[] { EngineFileSystem.Root?.GetFullPath( "/" ), Directory.GetCurrentDirectory() } )
		{
			if ( string.IsNullOrEmpty( root ) ) continue;

			var folder = Path.Combine( root, "addons", "base", "Assets", "shaders" );

			if ( !Directory.Exists( folder ) ) continue;

			string[] files;

			try
			{
				files = Directory.GetFiles( folder, "*.shader", SearchOption.AllDirectories );
			}
			catch ( IOException )
			{
				yield break;
			}

			foreach ( var file in files )
			{
				var under = Path.GetRelativePath( folder, file ).Replace( '\\', '/' );

				if ( !Wearable( file ) ) continue;

				yield return ( "shaders/" + under, file );
			}

			yield break;
		}
	}

	//Not one only the engine makes materials on, nor a compute pass or the legacy format nothing here builds
	private static bool Wearable( string full )
		=> !CoreResources.IsInternal( full ) && !CoreResources.IsCompute( full ) && !CoreShaderFile.IsForeign( full );

	//One file, read, and what it should say in the list.
	private static ShaderChoice? Choice( string path, string full )
	{
		if ( string.IsNullOrEmpty( full ) || !File.Exists( full ) ) return null;

		string source;

		try
		{
			source = CompiledFile.TextOf( full );
		}
		catch ( IOException )
		{
			return null;
		}

		return new ShaderChoice( path, ShaderTitle( path ), Summary( source ),
			CoreShaderFile.IsPost( path ) );
	}

	//First sentence of the shader's opening comment, shown as its description
	private static string Summary( string source )
	{
		var text = new System.Text.StringBuilder();

		foreach ( var line in ( source ?? "" ).Split( '\n' ) )
		{
			var trimmed = line.Trim();

			//The comment ends where the code starts, and the first paragraph ends at the first bare slash-slash.
			if ( !trimmed.StartsWith( "//", StringComparison.Ordinal ) ) break;

			var said = trimmed[2..].Trim();

			if ( said.Length == 0 ) break;

			if ( text.Length > 0 ) text.Append( ' ' );

			text.Append( said );

			//A full stop that ends a sentence rather than a number or an abbreviation: one with a space behind it, or one at the end.
			var whole = text.ToString();
			var stop = whole.IndexOf( ". ", StringComparison.Ordinal );

			if ( stop > 0 ) return whole[..( stop + 1 )];
			if ( whole.EndsWith( ".", StringComparison.Ordinal ) ) return whole;
		}

		var all = text.ToString().Trim();

		return all.Length > 160 ? all[..157] + "..." : all;
	}
}

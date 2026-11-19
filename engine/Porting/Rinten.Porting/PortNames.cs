using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rinten.Porting;

/// <summary>Rewrites the source engine's namespace out of a ported project and this engine's namespace in.</summary>
public static class PortNames
{
	//What the source engine calls its root namespace.
	private const string From = "Sandbox";

	//What this one calls it.
	private const string To = "Rinten";

	//A qualified name: the root, a dot, and something after it. The leading group keeps MySandbox. and Other.Sandbox. out of it.
	private static readonly Regex Qualified = new(
		$@"(^|[^\w.]){From}\.(?=\w)", RegexOptions.Compiled | RegexOptions.Multiline );

	//Source files whose text names types.
	private static readonly string[] CodeExtensions = { ".cs", ".razor", ".csproj" };

	//Documents that name a component type per object.
	private static readonly string[] SceneExtensions = { ".scene", ".prefab" };

	//Single-object assets that also hold measurements and need unit conversion
	private static readonly string[] ResourceExtensions = { ".terrain", ".clutter" };

	/// <summary>Whether this extension is source code, which is ported by hand.</summary>
	public static bool IsCode( string extension ) => CodeExtensions.Contains( extension );

	/// <summary>Whether this asset is one measured object rather than a scene of them.</summary>
	public static bool IsResource( string extension ) => ResourceExtensions.Contains( extension );
	public static bool IsScene( string extension ) => SceneExtensions.Contains( extension );

	/// <summary>Rewrite the type names in one scene or prefab in place. Returns how many were changed.</summary>
	public static int RewriteTypesInFile( string path, string relative, PortReport report )
	{
		JsonNode root;

		try
		{
			root = JsonNode.Parse( File.ReadAllText( path ) );
		}
		catch ( Exception e )
		{
			report.Skipped.Add( $"{relative}: could not read to rewrite names - {e.Message}" );
			return 0;
		}

		if ( root is null ) return 0;

		var changed = RewriteTypes( root );
		if ( changed == 0 ) return 0;

		try
		{
			File.WriteAllText( path, root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );
		}
		catch ( Exception e )
		{
			report.Skipped.Add( $"{relative}: could not write rewritten names - {e.Message}" );
			return 0;
		}

		return changed;
	}

	/// <summary>Walk a parsed document and rename every qualified name in it. Modified in place; returns how many strings changed.</summary>
	public static int RewriteTypes( JsonNode node )
	{
		switch ( node )
		{
			case JsonObject o:
			{
				var changed = 0;

				foreach ( var (key, value) in o.ToList() )
				{
					if ( value is JsonValue v && v.TryGetValue<string>( out var text ) )
					{
						var rewritten = Qualified.Replace( text, $"$1{To}." );
						if ( rewritten == text ) continue;

						o[key] = JsonValue.Create( rewritten );
						++changed;
						continue;
					}

					changed += RewriteTypes( value );
				}

				return changed;
			}

			case JsonArray a:
			{
				var changed = 0;

				for ( var i = 0; i < a.Count; i++ )
				{
					if ( a[i] is JsonValue v && v.TryGetValue<string>( out var text ) )
					{
						var rewritten = Qualified.Replace( text, $"$1{To}." );
						if ( rewritten == text ) continue;

						a[i] = JsonValue.Create( rewritten );
						++changed;
						continue;
					}

					changed += RewriteTypes( a[i] );
				}

				return changed;
			}

			default:
				return 0;
		}
	}
}

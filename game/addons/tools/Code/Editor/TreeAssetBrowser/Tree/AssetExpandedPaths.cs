using System.IO;

namespace Editor.TreeBrowser;

//Expanded folders shared by every asset tree, kept in memory only
static class AssetExpandedPaths
{
	static readonly HashSet<string> Paths = new( StringComparer.OrdinalIgnoreCase );
	static int sharedVersion;

	public static int SharedVersion => sharedVersion;

	public static IReadOnlyCollection<string> Snapshot() => Paths;

	public static bool SetExpanded( string key, bool expanded )
	{
		if ( string.IsNullOrEmpty( key ) ) return false;

		bool changed;

		if ( expanded )
		{
			changed = Paths.Add( key );
		}
		else
		{
			changed = Paths.Remove( key );

			var prefix1 = key + Path.DirectorySeparatorChar;
			var prefix2 = key + "/";
			changed |= Paths.RemoveWhere( p =>
				p.StartsWith( prefix1, StringComparison.OrdinalIgnoreCase ) ||
				p.StartsWith( prefix2, StringComparison.OrdinalIgnoreCase ) ) > 0;
		}

		if ( changed )
			sharedVersion++;

		return changed;
	}

	public static bool Contains( string key ) => Paths.Contains( key );

	public static void AddDefault( string key )
	{
		if ( Paths.Add( key ) )
			sharedVersion++;
	}
}

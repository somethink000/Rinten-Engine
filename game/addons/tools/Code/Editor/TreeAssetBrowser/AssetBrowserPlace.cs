using System;
using System.IO;

namespace Editor.TreeBrowser;

//Where the docked and embedded asset browsers are, in memory only: each follows it every frame, so moving in one moves
//them all. Pickers keep their own place, since they open on the value being picked
static class AssetBrowserPlace
{
	public static AssetTreeSection? Section { get; private set; }
	public static string Folder { get; private set; }
	public static AssetBrowser.Location Location { get; private set; }

	//Goes up with every move; a browser that has seen this one is where it should be
	public static int Version { get; private set; }

	//Set while a browser follows, so what it does to get there is not taken for a move of its own
	public static bool Following { get; set; }

	public static void MoveTo( AssetTreeSection section )
	{
		if ( Following || Section == section ) return;

		Section = section;
		Version++;
	}

	public static void MoveTo( string folder )
	{
		if ( Following || string.IsNullOrEmpty( folder ) ) return;

		folder = Normal( folder );
		if ( Location is null && string.Equals( Folder, folder, StringComparison.OrdinalIgnoreCase ) ) return;

		Folder = folder;
		Location = null;
		Version++;
	}

	public static void MoveTo( AssetBrowser.Location location )
	{
		if ( Following || location is null ) return;

		if ( location is DiskLocation disk )
		{
			MoveTo( disk.Path );
			return;
		}

		if ( Equals( Location, location ) ) return;

		Location = location;
		Folder = null;
		Version++;
	}

	//One spelling per folder: full, with no trailing separator
	public static string Normal( string folder )
	{
		try
		{
			return Path.TrimEndingDirectorySeparator( Path.GetFullPath( folder ) );
		}
		catch
		{
			return folder;
		}
	}

	public static bool Same( string a, string b )
		=> !string.IsNullOrEmpty( a ) && !string.IsNullOrEmpty( b ) && string.Equals( Normal( a ), Normal( b ), StringComparison.OrdinalIgnoreCase );
}

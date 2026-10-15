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

	/// <summary>Where the top of one kind of view is: the key of the item there, and how far into its row the view starts.</summary>
	public sealed class Scroll
	{
		public string Key { get; private set; }
		public float Into { get; private set; }
		public int Version { get; private set; }

		public void Set( string key, float into )
		{
			Key = key;
			Into = into;
			Version++;
		}
	}

	//An item rather than a distance, so a short browser scrolls to it and a tall one that shows everything does not scroll
	public static readonly Scroll TreeScroll = new();
	public static readonly Scroll GridScroll = new();

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

//One view's part in a shared scroll: scrolled by the user, it says where its top is; otherwise it goes where the others are
sealed class ScrollFollower
{
	//Frames a move is tried for before it is given up, as when this view has no such item
	const int Tries = 30;

	readonly AssetBrowserPlace.Scroll shared;
	float last = float.NaN;
	int seen;
	int tries;

	public ScrollFollower( AssetBrowserPlace.Scroll shared ) => this.shared = shared;

	//Hovered is whether the pointer is over the view, which is how a scroll of the user's is told from a layout's
	public void Tick( bool hovered, float offset, Func<(string Key, float Into)?> top, Func<string, float, bool> scrollTo, Func<float> offsetNow )
	{
		if ( hovered && !float.IsNaN( last ) && MathF.Abs( offset - last ) > 0.5f )
		{
			last = offset;

			if ( top() is { } at )
			{
				shared.Set( at.Key, at.Into );
				seen = shared.Version;
			}

			return;
		}

		if ( seen == shared.Version || shared.Key is null )
		{
			last = offset;
			return;
		}

		if ( scrollTo( shared.Key, shared.Into ) || ++tries > Tries )
		{
			seen = shared.Version;
			tries = 0;
		}

		last = offsetNow();
	}
}

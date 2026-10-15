using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Editor.TreeBrowser;

/// <summary>Which part of the browser's content the tree is showing.</summary>
public enum AssetTreeSection
{
	/// <summary>What has been opened lately, newest first.</summary>
	Recent,

	/// <summary>This project's own assets.</summary>
	Assets,

	/// <summary>Its code.</summary>
	Code,

	/// <summary>Its editor folder, where it has one.</summary>
	Editor,

	/// <summary>The projects this one is built on.</summary>
	Libraries,

	/// <summary>The package an addon belongs to.</summary>
	Parent,

	/// <summary>The engine's own content.</summary>
	Engine,

	/// <summary>Games mounted from elsewhere on the machine.</summary>
	Mounts
}

/// <summary>Title and icon per section, and whether it has content, so empty sections get no tab</summary>
public static class AssetTreeSections
{
	public readonly record struct Entry( AssetTreeSection Section, string Icon, string Title );

	//Written as an array rather than a collection expression - see the note in AssetTreeModel about what the editor's own compiler makes of a bare [].
	private static readonly Entry[] All = new Entry[]
	{
		new( AssetTreeSection.Recent, "history", "Recent" ),
		new( AssetTreeSection.Assets, "folder_special", "Assets" ),
		new( AssetTreeSection.Code, "code", "Code" ),
		new( AssetTreeSection.Editor, "edit", "Editor" ),
		new( AssetTreeSection.Libraries, "class", "Libraries" ),
		new( AssetTreeSection.Parent, "cloud", "Parent" ),
		new( AssetTreeSection.Engine, "dns", "Engine" ),
		new( AssetTreeSection.Mounts, "museum", "Mounted Games" ),
	};

	/// <summary>The sections this project actually has, in the order they are shown.</summary>
	public static IReadOnlyList<Entry> For( TreeBrowserOptions options )
		=> All.Where( x => Has( x.Section, options ) ).ToList();

	/// <summary>The one to fall back to when the wanted section is not there.</summary>
	public const AssetTreeSection Default = AssetTreeSection.Assets;

	/// <summary>Whether a section has anything behind it in this project.</summary>
	public static bool Has( AssetTreeSection section, TreeBrowserOptions options ) => section switch
	{
		AssetTreeSection.Assets => Exists( Project.Current?.GetAssetsPath() ),
		AssetTreeSection.Code => Exists( Project.Current?.GetCodePath() ),
		AssetTreeSection.Editor => Exists( EditorPath() ),
		AssetTreeSection.Libraries => Libraries().Any(),
		AssetTreeSection.Parent => ParentPackage() is not null,
		AssetTreeSection.Engine => Exists( CorePath() ),
		AssetTreeSection.Mounts => options is { EnableMounts: true } && MountedGames.Available().Any(),

		//Recent stands for a list rather than a folder, so it is always there: empty is an answer it is allowed to give.
		_ => true
	};

	/// <summary>The wanted section, or the default where this project has not got it.</summary>
	public static AssetTreeSection Resolve( AssetTreeSection? wanted, TreeBrowserOptions options )
	{
		if ( wanted is { } section && Has( section, options ) )
			return section;

		//Recent is the one section that is there whatever the project holds, so it is what is left when even the assets folder is missing.
		return Has( Default, options ) ? Default : AssetTreeSection.Recent;
	}

	internal static string EditorPath()
	{
		var root = Project.Current?.GetRootPath();

		return string.IsNullOrEmpty( root ) ? null : Path.Combine( root, "Editor" );
	}

	internal static string CorePath() => global::Editor.FileSystem.Root?.GetFullPath( "/core/" );

	internal static IEnumerable<Project> Libraries()
		=> EditorUtility.Projects.GetAll()
			.Where( p => p != Project.Current && p.Config.Ident != "menu" )
			.Where( p => Exists( p.GetRootPath() ) )
			.OrderBy( p => p.Config.Title );

	internal static Package ParentPackage()
	{
		if ( Project.Current?.Config?.Type != "addon" ) return null;

		var ident = Project.Current?.Config?.GetMetaOrDefault( "ParentPackage", "" );
		if ( string.IsNullOrWhiteSpace( ident ) ) return null;

		return Package.TryGetCached( ident, out var package ) ? package : null;
	}

	private static bool Exists( string path )
		=> !string.IsNullOrEmpty( path ) && Directory.Exists( path );
}

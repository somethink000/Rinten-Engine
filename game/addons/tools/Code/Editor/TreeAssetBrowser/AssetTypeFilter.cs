using System;
using System.Collections.Generic;
using System.Linq;

namespace Editor.TreeBrowser;

/// <summary>The asset types the user has narrowed the browser down to. Empty means all of them.</summary>
public sealed class AssetTypeFilter : IAssetFilter
{
	/// <summary>One type the project actually has files of.</summary>
	public readonly record struct Entry( AssetType Type, int Count );

	/// <summary>The types of one category, and how many of each the project holds.</summary>
	public readonly record struct Group( string Name, IReadOnlyList<Entry> Entries );

	readonly HashSet<string> extensions = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>Raised when the chosen set changes, so views can reload.</summary>
	public event Action Changed;

	//Listing every registered type buries a game's own resources among dozens of engine formats that nothing in the project uses.
	/// <summary>The types worth offering, by category: only ones the project has files of.</summary>
	public static IReadOnlyList<Group> PresentGroups()
	{
		var counts = new Dictionary<AssetType, int>();

		foreach ( var asset in AssetSystem.All )
		{
			if ( asset?.AssetType is not { } type ) continue;
			if ( string.IsNullOrEmpty( type.FileExtension ) ) continue;

			counts[type] = counts.GetValueOrDefault( type ) + 1;
		}

		return counts
			.Select( x => new Entry( x.Key, x.Value ) )
			.GroupBy( x => CategoryOf( x.Type ) )
			.OrderBy( x => x.Key, StringComparer.OrdinalIgnoreCase )
			.Select( x => new Group( x.Key, x.OrderBy( e => e.Type.FriendlyName, StringComparer.OrdinalIgnoreCase ).ToList() ) )
			.ToList();
	}

	static string CategoryOf( AssetType type ) => string.IsNullOrEmpty( type.Category ) ? "Other" : type.Category;

	public bool IsActive => extensions.Count > 0;

	public int Count => extensions.Count;

	public bool Contains( AssetType type ) => type is not null && extensions.Contains( type.FileExtension );

	public bool Accepts( Asset asset )
	{
		if ( !IsActive ) return true;

		return asset?.AssetType is { } type && extensions.Contains( type.FileExtension );
	}

	public void Toggle( AssetType type )
	{
		if ( type is null || string.IsNullOrEmpty( type.FileExtension ) )
			return;

		if ( !extensions.Remove( type.FileExtension ) )
			extensions.Add( type.FileExtension );

		Changed?.Invoke();
	}

	/// <summary>How many of a category's types are on.</summary>
	public int CountIn( Group group ) => group.Entries.Count( x => Contains( x.Type ) );

	public void Clear()
	{
		if ( extensions.Count == 0 ) return;

		extensions.Clear();
		Changed?.Invoke();
	}

	/// <summary>The chosen types by name, for the line beside the filter button.</summary>
	public IEnumerable<string> Names => extensions
		.Select( x => AssetType.FromExtension( x )?.FriendlyName ?? x )
		.OrderBy( x => x, StringComparer.OrdinalIgnoreCase );

	/// <summary>What the toolbar button says it is doing right now.</summary>
	public string Describe() => IsActive ? $"Showing {string.Join( ", ", Names )}" : "Filter by Type";
}

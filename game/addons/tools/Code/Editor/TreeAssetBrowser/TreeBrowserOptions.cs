using System.Collections.Generic;
using System.Linq;

namespace Editor.TreeBrowser;

/// <summary>How one browser instance behaves: the editor dock, or a modal picker.</summary>
public sealed class TreeBrowserOptions : IAssetFilter
{
	/// <summary>Defaults the editor dock runs with.</summary>
	public static TreeBrowserOptions Dock { get; } = new();

	/// <summary>Asset types the grid may show, or null for every type.</summary>
	public IReadOnlyList<AssetType> FilterAssetTypes { get; init; }

	/// <summary>File extensions the browser may show, asset or not, or null for every file. What narrows a browser to files that are no asset type - the UI editor's razor panels under Code - which <see cref="FilterAssetTypes"/> cannot.</summary>
	public IReadOnlyList<string> FileExtensions { get; init; }

	/// <summary>Is this file one of <see cref="FileExtensions"/>, where there are any?</summary>
	public bool AcceptsFile( string path )
	{
		if ( FileExtensions is not { Count: > 0 } ) return true;

		var extension = System.IO.Path.GetExtension( path ?? "" ).TrimStart( '.' );

		return FileExtensions.Contains( extension, System.StringComparer.OrdinalIgnoreCase );
	}

	/// <summary>Browser is inside a picker dialog rather than the dock.</summary>
	public bool IsPickerMode { get; init; }

	/// <summary>More than one asset may be selected at a time; a pooled picker retunes this per field.</summary>
	public bool EnableMultiselect { get; set; }

	/// <summary>Fixed tree+grid layout: no add, close, move or tab controls.</summary>
	public bool LockLayout { get; init; }

	/// <summary>Mounted games get their own tree section.</summary>
	public bool EnableMounts { get; init; } = true;

	/// <summary>What a double click on an asset does.</summary>
	public IAssetActivationPolicy Activation { get; init; } = OpenInEditorActivation.Instance;

	public bool HasTypeFilter => FilterAssetTypes is { Count: > 0 };

	/// <summary>Is this asset one the browser is allowed to offer?</summary>
	public bool Accepts( Asset asset )
	{
		if ( !HasTypeFilter ) return true;

		return asset is not null && FilterAssetTypes.Contains( asset.AssetType );
	}
}

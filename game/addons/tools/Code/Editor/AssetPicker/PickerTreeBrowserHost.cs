using Editor.TreeBrowser;
using System;
using System.Collections.Generic;

namespace Editor.AssetPickers;

/// <summary>The editor's asset browser, configured to live inside a picker dialog.</summary>
public sealed class PickerTreeBrowserHost : TreeBrowserWidget
{
	public PickerTreeBrowserHost( Widget parent, List<AssetType> assetTypes, AssetPicker.PickerOptions options, Action<Asset> onPicked )
		: base( parent, BuildOptions( assetTypes, options, onPicked ) )
	{
		MinimumSize = new Vector2( 100, 100 );
		Root.CreateDefaultPanels();
	}

	static TreeBrowserOptions BuildOptions( List<AssetType> assetTypes, AssetPicker.PickerOptions options, Action<Asset> onPicked ) => new()
	{
		IsPickerMode = true,
		LockLayout = true,
		FilterAssetTypes = assetTypes,
		EnableMultiselect = options.EnableMultiselect,
		EnableMounts = options.EnableMounts,
		FileExtensions = options.FileExtensions,
		Activation = new CallbackActivation( onPicked )
	};
}

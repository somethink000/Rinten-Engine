namespace Editor.TreeBrowser;

//Extension-to-icon mapping for asset tree file rows.
static class AssetFileIcons
{
	static readonly Dictionary<string, string> Icons = new( StringComparer.OrdinalIgnoreCase )
	{
		{ ".fbx", "view_in_ar" },
		{ ".obj", "view_in_ar" },
		{ ".gltf", "view_in_ar" },
		{ ".glb", "view_in_ar" },
		{ ".dae", "view_in_ar" },
		{ ".stl", "view_in_ar" },
		{ ".smd", "view_in_ar" },
		{ ".png", "image" },
		{ ".jpg", "image" },
		{ ".jpeg", "image" },
		{ ".tga", "image" },
		{ ".tex", "image" },
		{ ".psd", "image" },
		{ ".exr", "image" },
		{ ".hdr", "image" },
		{ ".wav", "audiotrack" },
		{ ".mp3", "audiotrack" },
		{ ".ogg", "audiotrack" },
		{ ".flac", "audiotrack" },
		{ ".snd", "audiotrack" },
		{ ".cs", "code" },
		{ ".razor", "code" },
		{ ".scss", "style" },
		{ ".css", "style" },
		{ ".shader", "gradient" },
		{ ".shdrgrph", "gradient" },
		{ ".prefab", "inventory_2" },
		{ ".scene", "landscape" },
		{ ".json", "data_object" },
		{ ".xml", "data_object" },
		{ ".txt", "description" },
		{ ".md", "description" },
		{ ".item", "category" },
		{ ".clothing", "checkroom" },
		{ ".weapon", "sports_martial_arts" },
		{ ".particle", "blur_on" },
		{ ".vanmgrph", "animation" },
		{ ".vpost", "auto_fix_high" }
	};

	public static string GetIcon( string extension )
	{
		if ( string.IsNullOrEmpty( extension ) )
			return "description";

		return Icons.TryGetValue( extension, out var icon ) ? icon : "description";
	}
}

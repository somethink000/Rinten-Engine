using Rinten.CoreAnimation;
using System.IO;

namespace Rinten;

/// <summary>Imported mesh, take and surface names, for the model editor tree.</summary>
public static class ModelImportCatalog
{
	//Selecting a node asks again, and a big export takes seconds to import; size and write time pick up a re-export
	private static readonly Dictionary<(string List, string Path, long Length, long Written, float Scale), List<string>> imported = new();

	//Enough for a session's sources; past it the cache starts again
	private const int MostLists = 192;

	private static List<string> Cached( string list, string path, float scale, Func<string, float, List<string>> import )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return new List<string>();

		var info = new FileInfo( path );
		var key = (list, path, info.Exists ? info.Length : -1L, info.Exists ? info.LastWriteTimeUtc.Ticks : 0L, scale);

		lock ( imported )
		{
			if ( !imported.TryGetValue( key, out var names ) )
			{
				if ( imported.Count >= MostLists ) imported.Clear();

				imported[key] = names = import( path, scale );
			}

			return new List<string>( names );
		}
	}

	public static List<string> MeshNames( string path, float scale = 1.0f )
		=> Cached( "meshes", path, scale, CoreMeshImport.Catalog );

	public static List<string> MaterialNames( string path, float scale = 1.0f )
		=> Cached( "materials", path, scale, CoreMeshImport.Materials );

	/// <summary>The takes an export holds, whatever kind of export it is.</summary>
	public static List<string> TakeNames( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return new List<string>();

		return Path.GetExtension( path ).ToLowerInvariant() switch
		{
			".fbx" => CoreFbxTakes.List( path ),
			".gltf" or ".glb" => CoreGltfImport.ListTakes( path ),
			_ => new List<string>(),
		};
	}

	public static List<string> BoneNames( string path, float scale = 1.0f )
		=> Cached( "bones", path, scale, CoreMeshImport.Bones );

	/// <summary>Bone names from a model's compiled takes, when the mesh has no skeleton.</summary>
	public static List<string> ClipBoneNames( Model model )
	{
		var names = new List<string>();
		if ( model is not { IsValid: true } ) return names;

		var seen = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );

		for ( var i = 0; i < model.AnimationCount; i++ )
		{
			var path = model.GetAnimationClipPath( model.GetAnimationName( i ) );
			var clip = CoreClips.Clip( path );
			if ( clip is null ) continue;

			for ( var bone = 0; bone < clip.Bones.Length; bone++ )
			{
				var name = clip.Bones[bone].Name;
				if ( string.IsNullOrWhiteSpace( name ) || !seen.Add( name ) ) continue;
				names.Add( name );
			}

			if ( names.Count > 0 ) break;
		}

		return names;
	}

	/// <summary>Drops Blender's ".001" duplicate suffix so "metal.005" is "metal".</summary>
	public static string Bare( string surface )
	{
		if ( string.IsNullOrEmpty( surface ) ) return "";

		var dot = surface.LastIndexOf( '.' );
		if ( dot <= 0 || dot != surface.Length - 4 ) return surface;

		for ( var i = dot + 1; i < surface.Length; i++ )
		{
			if ( !char.IsAsciiDigit( surface[i] ) ) return surface;
		}

		return surface[..dot];
	}
}

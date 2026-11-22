using Rinten.CoreAnimation;
using System.IO;

namespace Rinten;

/// <summary>Imported mesh, take and surface names, for the model editor tree.</summary>
public static class ModelImportCatalog
{
	//One source file, imported once for the lists the model editor asks for. Selecting a mesh node rebuilds its
	//inspector, and that asks for the sub-mesh names - which used to read and import the whole export again. On a
	//level-sized FBX that is seconds, on every click. Keyed by the file's size and write time as well as its path,
	//so a re-export is picked up without anybody having to empty this.
	private sealed class Imported
	{
		private readonly string path;
		private readonly float scale;
		private readonly object gate = new();

		private List<string> meshes;
		private List<string> materials;
		private List<string> bones;

		public Imported( string path, float scale )
		{
			this.path = path;
			this.scale = scale;
		}

		public List<string> Meshes
		{
			get
			{
				lock ( gate )
				{
					using var scope = Diagnostics.Performance.Scope( "model source import" );
					return meshes ??= CoreMeshImport.Catalog( path, scale );
				}
			}
		}

		public List<string> Materials
		{
			get
			{
				lock ( gate )
				{
					using var scope = Diagnostics.Performance.Scope( "model source import" );
					return materials ??= CoreMeshImport.Materials( path, scale );
				}
			}
		}

		public List<string> Bones
		{
			get
			{
				lock ( gate )
				{
					using var scope = Diagnostics.Performance.Scope( "model source import" );
					return bones ??= CoreMeshImport.Bones( path, scale );
				}
			}
		}
	}

	//A session opens a handful of source files; the cap is only there so a long-lived editor with many projects
	//does not keep every export it ever listed.
	private const int MostFiles = 64;

	private static readonly Dictionary<(string Path, long Length, long Written, float Scale), Imported> imported = new();
	private static readonly object importedLock = new();

	private static Imported FileOf( string path, float scale )
	{
		var info = new FileInfo( path );
		var key = (path, info.Exists ? info.Length : -1L, info.Exists ? info.LastWriteTimeUtc.Ticks : 0L, scale);

		lock ( importedLock )
		{
			if ( imported.TryGetValue( key, out var found ) ) return found;

			if ( imported.Count >= MostFiles ) imported.Clear();

			return imported[key] = new Imported( path, scale );
		}
	}

	public static List<string> MeshNames( string path, float scale = 1.0f )
		=> string.IsNullOrWhiteSpace( path ) ? new List<string>() : new( FileOf( path, scale ).Meshes );

	public static List<string> MaterialNames( string path, float scale = 1.0f )
		=> string.IsNullOrWhiteSpace( path ) ? new List<string>() : new( FileOf( path, scale ).Materials );

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
		=> string.IsNullOrWhiteSpace( path ) ? new List<string>() : new( FileOf( path, scale ).Bones );

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

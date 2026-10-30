using Rinten.Engine;
using System;
using System.Text.Json.Nodes;
using System.Threading;

namespace Editor;

public static partial class AssetSystem
{
	internal static CloudAssetDirectory CloudDirectory { get; set; }

	public static async Task<Asset> InstallAsync( string packageIdent, bool skipIfInstalled = true, Action<float> loading = null, CancellationToken token = default )
	{
		loading?.Invoke( 0.0f );
		var package = await Package.FetchAsync( packageIdent, false, false );
		return await InstallAsync( package, skipIfInstalled, loading, token );
	}

	/// <summary>Install a cloud package. Will return the primary asset on completion (if it has one)</summary>
	public static async Task<Asset> InstallAsync( Package package, bool skipIfInstalled = true, Action<float> loading = null, CancellationToken token = default )
	{
		ThreadSafe.AssertIsMainThread();

		if ( package == null )
			return null;

		if ( package.Revision == null )
			return null;

		if ( !CanCloudInstall( package ) )
			return null;

		if ( !skipIfInstalled || !IsCloudInstalled( package ) )
		{
			//download the manifest
			await package.Revision.DownloadManifestAsync( token );

			using var suppressWatchers = FileWatch.Suppress();
			await DownloadCloudFiles( package, loading, token );

			foreach ( var file in package.Revision.Manifest.Files )
			{
				var fullPath = FileSystem.Cloud.GetFullPath( file.Path );
				var asset = RegisterFile( fullPath );

				if ( asset is null )
					continue;

				asset.Package = package;

				//update ref version on any assets that depend on this
				ReplaceReferences( asset.GetDependants( false ), package, package );
			}

			CloudDirectory.AddPackage( package );
			EditorEvent.Run( "package.changed.installed", package );
		}

		var primaryAssetName = package.PrimaryAsset;
		return FindByPath( primaryAssetName );
	}

	internal static void UninstallPackage( Package package )
	{
		if ( !IsCloudInstalled( package ) )
		{
			return;
		}

		var contents = GetPackageFiles( package );
		CloudDirectory.RemovePackage( package );

		foreach ( var file in contents )
		{
			var fullPath = FileSystem.Cloud.GetFullPath( file );
			var asset = FindByPath( fullPath );

			if ( asset is null || asset.Package != package )
				continue;

			var requiredBy = CloudDirectory.FindPackages( fullPath, file ).FirstOrDefault();
			if ( requiredBy == null )
			{
				//no other package needs this, we can delete it
				asset.Delete();
			}
			else
			{
				//update any assets that depend on this to point to the package that remains
				ReplaceReferences( asset.GetDependants( false ), package, requiredBy );
				asset.Package = requiredBy;
			}
		}
	}

	//Replaces any references in assets to fromPackage with a reference to toPackage (does a replace by ident to avoid having to open the map etc)
	private static void ReplaceReferences( IEnumerable<Asset> assets, Package fromPackage, Package toPackage )
	{
		if ( assets.Count() < 1 ) return;

		string newIdent = toPackage?.GetIdent( false, true );
		string projectPath = Project.Current.GetAssetsPath().Replace( '\\', '/' );

		foreach ( var asset in assets )
		{
			//more bs
			if ( !asset.AbsolutePath.StartsWith( projectPath, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( !asset.AssetType.IsGameResource )
			{
				var config = asset?.Publishing?.ProjectConfig;
				if ( config is null || config.EditorReferences is null )
					continue;

				int count = config.EditorReferences.RemoveAll( fromPackage.IsNamed );

				if ( count > 0 )
				{
					if ( newIdent != null && !config.EditorReferences.Contains( newIdent ) )
						config.EditorReferences.Add( newIdent );

					config.EditorReferences = config.EditorReferences.OrderBy( x => x ).ToList();
					asset.MetaData.Set( "publish", asset.Publishing );
				}
			}
			else if ( typeof( GameResource ).IsAssignableFrom( asset.AssetType.ResourceType ) && asset.LoadResource() is GameResource gameResource )
			{
				string filename = asset.GetSourceFile( true );
				if ( string.IsNullOrWhiteSpace( filename ) )
					continue;

				var json = System.IO.File.ReadAllText( filename );

				if ( JsonNode.Parse( json ) is not JsonObject jso )
					continue;

				if ( jso["__references"] is not JsonArray references )
					continue;

				var newReferences = references.Select( x => x.ToString() ).ToList();
				int count = newReferences.RemoveAll( fromPackage.IsNamed );

				if ( count > 0 )
				{
					if ( newIdent != null && !newReferences.Contains( newIdent ) )
						newReferences.Add( newIdent );

					jso["__references"] = JsonValue.Create( newReferences.OrderBy( x => x ) );

					gameResource.SaveToDisk( filename, jso.ToJsonString( Json.options ) );
				}
			}
		}
	}

	/// <summary>Gets the locally installed package revision by ident</summary>
	public static Package.IRevision GetInstalledRevision( string packageIdent )
	{
		return CloudDirectory.FindPackage( packageIdent )?.Revision;
	}

	/// <summary>Is this package installed in our cloud directory?</summary>
	public static bool IsCloudInstalled( string packageIdent )
	{
		return CloudDirectory.FindPackage( packageIdent ) != null;
	}

	/// <summary>Is a version this package installed in our cloud directory?</summary>
	public static bool IsCloudInstalled( Package package, bool exactVersion = false )
	{
		if ( package is null ) return false;
		if ( package.Org is null )
		{
			Log.Trace( $"{package.FullIdent} no org" );
			return false;
		}
		if ( !package.IsRemote )
		{
			Log.Trace( $"{package.FullIdent} not remote" );
			return false;
		}

		var local = CloudDirectory.FindPackage( package.FullIdent );
		if ( local is null )
		{
			Log.Trace( $"{package.FullIdent} wasn't found in CloudDirectory" );
			return false;
		}

		if ( exactVersion && package.Revision is not null && package.Revision.VersionId != local.Revision.VersionId )
		{
			Log.Trace( $"{package.Revision.VersionId} != {local.Revision.VersionId} version is different" );
			return false; //version is different
		}

		return true;
	}

	/// <summary>Get all packages in the download cache</summary>
	public static IReadOnlyCollection<Package> GetInstalledPackages()
	{
		return CloudDirectory.GetPackages();
	}

	/// <summary>Get all packages, referenced by assets in the current project, in the download cache</summary>
	public static IReadOnlyCollection<Package> GetReferencedPackages()
	{
		return CloudAsset.GetAssetReferences( true )
			.Select( CloudDirectory.FindPackage )
			.Where( p => p != null ).ToList();
	}

	public static IReadOnlyCollection<string> GetPackageFiles( Package package )
	{
		return CloudDirectory.GetPackageFiles( package ).ToList();
	}

	/// <summary>Is this package type something we can install?</summary>
	public static bool CanCloudInstall( Package package )
	{
		if ( package.TypeName == "map" ) return false;
		if ( package.TypeName == "collection" ) return false;

		return true;
	}

	//Initialize the files from the
	private static async Task DownloadCloudFiles( Package package, Action<float> progress, CancellationToken token )
	{
		long totalSize = package.Revision.Manifest.Files.Sum( x => x.Size );
		long downloaded = 0;

		IToolsDll.Current?.RunEvent( "package.download.start", package, token );

		await package.Revision.Manifest.Files.ForEachTaskAsync( async ( e ) =>
		{
			await DownloadFile( package, e, token );
			downloaded += e.Size;

			float frac = (float)((double)downloaded / (double)totalSize);
			progress?.Invoke( frac );
			IToolsDll.Current?.RunEvent( "package.download.update", package, frac );
		}, Package.MaxParallelDownloads );

		IToolsDll.Current?.RunEvent( "package.download.complete", package );
	}

	static async Task DownloadFile( Package package, ManifestSchema.File entry, CancellationToken token )
	{
		ThreadSafe.AssertIsMainThread();

		// Ignore thumbnails
		string path = entry.Path.StartsWith( "/" ) ? entry.Path : $"/{entry.Path}";
		if ( path == $"/{package.FullIdent}/thumb.png" )
			return;

		// Ignore assemblies
		if ( entry.Path.EndsWith( ".dll" ) )
			return;

		//This file exists in core, no need to download it (at this moment)
		if ( EngineFileSystem.CoreContent.FileExists( entry.Path ) )
			return;

		if ( !CloudDirectory.AddFile( entry.Path, entry.Crc, entry.Size, package ) )
			return; //unwanted - newer revision already registered

		//Same size isn't enough: a republished compiled file with a bumped resource version is the old one's size
		var crc = Convert.ToUInt64( entry.Crc, 16 );
		if ( await CloudDirectory.IsFileCurrentAsync( entry.Path, crc, entry.Size, token ) )
			return;

		//Hashing yields to other installs; a newer package may have claimed this path meanwhile
		if ( !CloudDirectory.AddFile( entry.Path, entry.Crc, entry.Size, package ) )
			return;

		CloudDirectory.InvalidateFileVerification( entry.Path );

		var targetFile = FileSystem.Cloud.GetFullPath( entry.Path );
		var targetPath = System.IO.Path.GetDirectoryName( entry.Path );
		FileSystem.Cloud.CreateDirectory( targetPath );

		if ( System.IO.File.Exists( targetFile ) )
		{
			System.IO.File.Delete( targetFile );
		}

		if ( entry.Size == 0 )
		{
			await System.IO.File.WriteAllTextAsync( targetFile, "" );
		}
		else
		{
			var url = $"{entry.Url}";
			await Rinten.Utility.Web.DownloadFile( url, targetFile, token );
		}

		if ( !await CloudDirectory.IsFileCurrentAsync( entry.Path, crc, entry.Size, token ) )
			Log.Warning( $"[{package.FullIdent}] downloaded cloud file doesn't match its manifest: {entry.Path}" );
	}
}

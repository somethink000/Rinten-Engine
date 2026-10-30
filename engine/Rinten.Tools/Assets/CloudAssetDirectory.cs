using LiteDB;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Editor;

//There are a bunch of loose files in the .rinten/cloud folder. This is a directory of
//where those files are from, so we can backwards lookup which package(s) they're from.
internal class CloudAssetDirectory : IDisposable
{
	LiteDatabase db;

	//one row per on-disk file
	ILiteCollection<File> files;

	//one row per (path, package, revision) claim on a file
	ILiteCollection<PackageFile> links;

	//stores a flat record of each package for returning in Asset.Package
	ILiteCollection<CachedPackage> packages;

	//to avoid hitting the database over and over
	Dictionary<string, Package> packageCache = new( StringComparer.OrdinalIgnoreCase );
	Dictionary<string, File> filesByPath = new( StringComparer.OrdinalIgnoreCase );
	Dictionary<string, List<PackageFile>> linksByPath = new( StringComparer.OrdinalIgnoreCase );
	Dictionary<(string Package, long Revision), List<PackageFile>> linksByPackage = new();

	/// <summary>A physical file that exists on disk in the cloud cache. There's exactly one of these per path - it records what's actually there, independent of who depends on it.</summary>
	public class File
	{
		[BsonId]
		public string Path { get; set; }
		public string Crc { get; set; }
		public long Size { get; set; }
		public DateTimeOffset CreationDate { get; set; }
		public DateTimeOffset InstallDate { get; set; }

		//What the disk held when last hashed, apart from the manifest's claim; ticks since LiteDB dates drop sub-ms
		public string VerifiedCrc { get; set; }
		public long VerifiedSize { get; set; }
		public long VerifiedLastWriteUtcTicks { get; set; }
	}

	/// <summary>Link between a physical file and a package that depends on it. There can be many of these per path, and many per package.</summary>
	public class PackageFile
	{
		[BsonId]
		public int Id { get; set; }
		public string Path { get; set; }
		public string Package { get; set; }
		public long Revision { get; set; }
		public bool IsPrimaryAsset { get; set; }
		public DateTimeOffset RevisionCreated { get; set; }
	}

	//Flat snapshot of a Package; the full graph has a circular ref that overflows LiteDB nesting
	private class CachedPackage
	{
		[BsonId]
		public string FullIdent { get; set; }

		public string OrgIdent { get; set; }
		public string OrgTitle { get; set; }
		public string OrgThumb { get; set; }
		public string Ident { get; set; }
		public string Title { get; set; }
		public string Summary { get; set; }
		public string Thumb { get; set; }
		public string TypeName { get; set; }
		public string[] Tags { get; set; }
		public DateTimeOffset Created { get; set; }

		//the current revision - VersionId joins to the links table, Meta backs Package.PrimaryAsset
		public long VersionId { get; set; }
		public long FileCount { get; set; }
		public long TotalSize { get; set; }
		public DateTimeOffset RevisionCreated { get; set; }
		public int EngineVersion { get; set; }
		public string ManifestUrl { get; set; }
		public string Changes { get; set; }
		public string Meta { get; set; }
	}

	//Flatten a package into the record we persist. Reads the concrete PackageRevision so we keep
	//the raw metadata JSON and manifest URL, which the public revision interface doesn't expose.
	static CachedPackage ToCached( Package package )
	{
		var cached = new CachedPackage
		{
			FullIdent = package.FullIdent,
			OrgIdent = package.Org?.Ident,
			OrgTitle = package.Org?.Title,
			OrgThumb = package.Org?.Thumb,
			Ident = package.Ident,
			Title = package.Title,
			Summary = package.Summary,
			Thumb = package.Thumb,
			TypeName = package.TypeName,
			Tags = package.Tags,
			Created = package.Created,
		};

		if ( package.Revision is PackageRevision revision )
		{
			cached.VersionId = revision.AssetVersionId;
			cached.FileCount = revision.FileCount;
			cached.TotalSize = revision.TotalSize;
			cached.RevisionCreated = revision.Created;
			cached.EngineVersion = revision.EngineVersion;
			cached.ManifestUrl = revision.ManifestUrl;
			cached.Changes = revision.Changes;
			cached.Meta = revision.Meta;
		}

		return cached;
	}

	//Rebuild a usable package from its stored record. This is what consumers get from FindPackage and GetPackages once the directory has been
	//re-opened, so it carries everything they read: identity, display fields, and a revision whose metadata still resolves Package.PrimaryAsset.
	static Package FromCached( CachedPackage cached )
	{
		return new RemotePackage
		{
			Org = new Package.Organization
			{
				Ident = cached.OrgIdent,
				Title = cached.OrgTitle,
				Thumb = cached.OrgThumb,
			},
			Ident = cached.Ident,
			Title = cached.Title,
			Summary = cached.Summary,
			Thumb = cached.Thumb,
			TypeName = cached.TypeName,
			Tags = cached.Tags,
			Created = cached.Created,
			Version = new PackageRevision
			{
				AssetVersionId = cached.VersionId,
				FileCount = cached.FileCount,
				TotalSize = cached.TotalSize,
				Created = cached.RevisionCreated,
				EngineVersion = cached.EngineVersion,
				ManifestUrl = cached.ManifestUrl,
				Changes = cached.Changes,
				Meta = cached.Meta,
			},
		};
	}

	public CloudAssetDirectory( string filename )
	{
		ConnectionString cs = new();
		cs.Connection = ConnectionType.Shared;
		cs.Filename = filename;
		cs.Upgrade = true;

		db = new LiteDatabase( cs );

		//"files" used to store one row per (path, package, revision) with content data duplicated across every package that referenced the same path.
		//The new shape splits physical files from package links, so the old collections can't be read back into it - drop them and rebuild from scratch.
		if ( db.CollectionExists( "files" ) )
			db.DropCollection( "files" );

		//the package store used to hold the entire Package object graph, which couldn't serialize packages whose latest news post referenced
		//themselves. we store a flat CachedPackage now, so drop the old collection - its documents can't be read back into the new shape.
		if ( db.CollectionExists( "packages" ) )
			db.DropCollection( "packages" );

		//1:1 Package:Files -> multiple PackageFile links between those
		if ( db.CollectionExists( "packages_v2" ) )
			db.DropCollection( "packages_v2" );

		files = db.GetCollection<File>( "files_v2" );
		files.Count(); //to stimulate an open

		links = db.GetCollection<PackageFile>( "links_v1" );
		links.EnsureIndex( x => x.Path );
		links.EnsureIndex( x => new { x.Package, x.Revision } );
		links.Count(); //to stimulate an open

		packages = db.GetCollection<CachedPackage>( "packages_v3" );
		packages.Count(); //to stimulate an open

		ValidateFiles();
		RebuildCaches();

		foreach ( var cached in packages.FindAll().ToArray() )
		{
			var package = FromCached( cached );
			var ident = package.FullIdent;

			if ( !ValidatePackage( package ) )
			{
				Log.Info( $"'{ident}' failed to validate, removing" );
				RemovePackage( package );
				continue;
			}

			packageCache[ident] = package;
		}

		PruneOrphanedFiles();
	}

	public void Dispose()
	{
		db?.Dispose();
		db = null;

		files = null;
		links = null;
		packages = null;
	}

	void RebuildCaches()
	{
		filesByPath.Clear();
		linksByPath.Clear();
		linksByPackage.Clear();

		foreach ( var file in files.FindAll() )
		{
			filesByPath[file.Path] = file;
		}

		foreach ( var link in links.FindAll() )
		{
			AddLinkToCache( link );
		}
	}

	void AddLinkToCache( PackageFile link )
	{
		if ( !linksByPath.TryGetValue( link.Path, out var byPath ) )
		{
			byPath = new List<PackageFile>();
			linksByPath[link.Path] = byPath;
		}
		byPath.Add( link );

		var key = (link.Package, link.Revision);
		if ( !linksByPackage.TryGetValue( key, out var byPackage ) )
		{
			byPackage = new List<PackageFile>();
			linksByPackage[key] = byPackage;
		}
		byPackage.Add( link );
	}

	void RemoveLinksFromCache( string package, long revision )
	{
		var key = (package, revision);
		if ( !linksByPackage.TryGetValue( key, out var byPackage ) )
			return;

		foreach ( var link in byPackage )
		{
			if ( linksByPath.TryGetValue( link.Path, out var byPath ) )
			{
				byPath.RemoveAll( l => l.Package == package && l.Revision == revision );
				if ( byPath.Count == 0 )
					linksByPath.Remove( link.Path );
			}
		}
		linksByPackage.Remove( key );
	}

	//Remove any Files that no longer have a link pointing at it. If a file isn't claimed by any package, we don't need to track it anymore.
	void PruneOrphanedFiles()
	{
		var orphaned = filesByPath.Keys.Where( p => !linksByPath.ContainsKey( p ) ).ToList();
		if ( orphaned.Count == 0 )
			return;

		foreach ( var path in orphaned )
		{
			files.Delete( path );
			filesByPath.Remove( path );
		}
	}

	IEnumerable<File> GetFilesForPackage( string fullIdent, long revision )
	{
		if ( !linksByPackage.TryGetValue( (fullIdent, revision), out var linkList ) )
			return Enumerable.Empty<File>();

		return linkList
			.Select( l => filesByPath.TryGetValue( l.Path, out var f ) ? f : null )
			.Where( f => f != null );
	}

	/// <summary>Remember this package, our cloud assets are using it.</summary>
	public void AddPackage( Package package )
	{
		var ident = package.FullIdent;
		var revision = package.Revision.VersionId;

		db.BeginTrans();
		try
		{
			packages.Upsert( ToCached( package ) );
			links.DeleteMany( x => x.Package == ident && x.Revision != revision );
			db.Commit();
		}
		catch
		{
			db.Rollback();
			throw;
		}

		//tidy stale entries from old revisions of this package
		foreach ( var key in linksByPackage.Keys.Where( k => k.Package == ident && k.Revision != revision ).ToList() )
		{
			RemoveLinksFromCache( key.Package, key.Revision );
		}
		PruneOrphanedFiles();

		packageCache[ident] = package;
	}

	/// <summary>Remove this package and it's files from our database</summary>
	public void RemovePackage( Package package )
	{
		var ident = package.FullIdent;
		var revision = package.Revision.VersionId;

		db.BeginTrans();
		try
		{
			packages.Delete( ident );
			links.DeleteMany( x => x.Package == ident && x.Revision == revision );
			db.Commit();
		}
		catch
		{
			db.Rollback();
			throw;
		}

		RemoveLinksFromCache( ident, revision );
		PruneOrphanedFiles();

		packageCache.Remove( ident );
	}

	/// <summary>Start tracking this path, associate it with this package.</summary>
	public bool AddFile( string path, string crc, long size, Package package )
	{
		path = path.NormalizeFilename( true );

		if ( TryGetFile( path, out var existing ) )
		{
			if ( existing.Crc == crc && existing.Size == size )
			{
				//identical content, nothing to conflict over
				AddPackageLink( path, package );
				return true;
			}

			if ( existing.CreationDate > package.Revision.Created )
			{
				//this file is already installed from a newer package, don't overwrite it.
				Log.Warning( $"[{package.FullIdent}] WARNING cloud asset conflict! keeping the newer-published file: {path}" );

				AddPackageLink( path, package );
				return false;
			}
		}

		var file = new File
		{
			Path = path,
			Crc = crc,
			Size = size,
			CreationDate = package.Revision.Created,
			InstallDate = DateTime.UtcNow,
			VerifiedCrc = existing?.VerifiedCrc,
			VerifiedSize = existing?.VerifiedSize ?? 0,
			VerifiedLastWriteUtcTicks = existing?.VerifiedLastWriteUtcTicks ?? 0
		};

		db.BeginTrans();
		try
		{
			files.Upsert( file );
			db.Commit();

			filesByPath[path] = file;
		}
		catch
		{
			db.Rollback();
			throw;
		}

		AddPackageLink( path, package );
		return true;
	}

	/// <summary>Whether the file on disk matches a manifest entry, hashing off-thread only when it changed since last verified.</summary>
	internal async Task<bool> IsFileCurrentAsync( string path, ulong crc, long size, CancellationToken token = default )
	{
		token.ThrowIfCancellationRequested();
		if ( !TryGetFile( path, out var file ) )
			return false;

		var info = new System.IO.FileInfo( FileSystem.Cloud.GetFullPath( path ) );
		if ( !info.Exists || info.Length != size )
			return false;

		var lastWrite = info.LastWriteTimeUtc.Ticks;
		if ( file.VerifiedCrc is not null && file.VerifiedSize == size && file.VerifiedLastWriteUtcTicks == lastWrite )
			return Convert.ToUInt64( file.VerifiedCrc, 16 ) == crc;

		var actualCrc = await FileSystem.Cloud.GetCrcAsync( path );
		token.ThrowIfCancellationRequested();

		//Changed while hashing, or another install replaced the record: don't certify it with this CRC
		info.Refresh();
		if ( !info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != lastWrite )
			return false;
		if ( !TryGetFile( path, out var current ) || !ReferenceEquals( file, current ) )
			return false;

		file.VerifiedCrc = actualCrc.ToString( "x" );
		file.VerifiedSize = size;
		file.VerifiedLastWriteUtcTicks = lastWrite;
		files.Update( file );
		return actualCrc == crc;
	}

	/// <summary>Forgets a file's verification before it is replaced, so a failed download is not trusted.</summary>
	internal void InvalidateFileVerification( string path )
	{
		if ( !TryGetFile( path, out var file ) || file.VerifiedCrc is null )
			return;

		file.VerifiedCrc = null;
		files.Update( file );
	}

	/// <summary>Create an association between an existing file and a package.</summary>
	public void AddPackageLink( string path, Package package )
	{
		path = path.NormalizeFilename( true );

		if ( linksByPath.TryGetValue( path, out var existingLinks ) )
		{
			//avoid touching the db if there'e already a link between this file and package
			if ( existingLinks.Any( x => x.Package == package.FullIdent && x.Revision == package.Revision.VersionId ) )
				return;
		}

		var link = new PackageFile
		{
			Path = path,
			Package = package.FullIdent,
			Revision = package.Revision.VersionId,
			RevisionCreated = package.Revision.Created,
			IsPrimaryAsset = string.Equals( package.PrimaryAsset?.NormalizeFilename( true ), path, StringComparison.OrdinalIgnoreCase )
		};

		db.BeginTrans();
		try
		{
			links.Insert( link );

			db.Commit();

			if ( linksByPath.TryGetValue( path, out var byPath ) )
				byPath.RemoveAll( l => l.Package == package.FullIdent && l.Revision == package.Revision.VersionId );

			if ( linksByPackage.TryGetValue( (package.FullIdent, package.Revision.VersionId), out var byPackage ) )
				byPackage.RemoveAll( l => string.Equals( l.Path, path, StringComparison.OrdinalIgnoreCase ) );

			AddLinkToCache( link );
		}
		catch
		{
			db.Rollback();
			throw;
		}
	}

	public IEnumerable<string> GetPackageFiles( Package package )
	{
		if ( !linksByPackage.TryGetValue( (package.FullIdent, package.Revision.VersionId), out var linkList ) )
			return Enumerable.Empty<string>();

		return linkList.Select( l => l.Path ).ToList();
	}

	//Given an ident, find the saved package. This doesn't access the internet, it looks it up in the database of packages that we've previously downloaded.
	internal Package FindPackage( string ident )
	{
		if ( string.IsNullOrEmpty( ident ) )
			return default;

		if ( !Package.TryParseIdent( ident, out var p ) )
			return default;

		if ( packageCache.TryGetValue( Package.FormatIdent( p.org, p.package, local: p.local ), out var package ) )
			return package;

		return default;
	}

	//Validate files on disk match their db entries. If a file is missing or has the wrong size, remove it from the db.
	void ValidateFiles()
	{

		var badFiles = new ConcurrentBag<string>();
		files.FindAll().ToArray().AsParallel().ForAll( file =>
		{
			if ( !FileSystem.Cloud.FileExists( file.Path ) || FileSystem.Cloud.FileSize( file.Path ) != file.Size )
				badFiles.Add( file.Path );
		} );

		if ( badFiles.IsEmpty )
			return;

		var toRemove = new HashSet<string>( badFiles, StringComparer.OrdinalIgnoreCase );
		files.DeleteMany( x => toRemove.Contains( x.Path ) );
		Log.Info( $"..removed {toRemove.Count} entries" );
	}

	//Checks links against filesByPath only; files were already validated on disk this start
	internal bool ValidatePackage( Package package )
	{
		if ( !linksByPackage.TryGetValue( (package.FullIdent, package.Revision.VersionId), out var linkList ) )
			return true;

		return linkList.All( link => filesByPath.ContainsKey( link.Path ) );
	}

	//Package owning this path, preferring one where it's the primary asset; see FindPackages for all
	internal Package FindPackage( string absolutePath, string relativePath )
	{
		absolutePath = absolutePath.NormalizeFilename( true );
		relativePath = relativePath.NormalizeFilename( true );

		if ( !absolutePath.Contains( Project.CloudPath ) )
			return null;

		if ( !linksByPath.TryGetValue( relativePath, out var linkList ) || linkList.Count == 0 )
		{
			Log.Warning( $"Was unable to determine package for {relativePath}.. Absolute path: {absolutePath}" );

			try
			{
				System.IO.File.Delete( absolutePath );
			}
			catch { } //ignore errors, we'll try again next time

			return null;
		}

		//prefer primary asset links here, that's the one we really want in that case
		foreach ( var link in linkList.OrderByDescending( x => x.IsPrimaryAsset ) )
		{
			var package = FindPackage( link.Package );
			if ( package != null )
				return package;
		}

		return null;
	}

	//Same as FindPackage but returns every package that currently claims this path.
	internal IEnumerable<Package> FindPackages( string absolutePath, string relativePath )
	{
		absolutePath = absolutePath.NormalizeFilename( true );
		relativePath = relativePath.NormalizeFilename( true );

		if ( !absolutePath.Contains( Project.CloudPath ) )
			return Enumerable.Empty<Package>();

		if ( !linksByPath.TryGetValue( relativePath, out var linkList ) )
			return Enumerable.Empty<Package>();

		return linkList
			.Select( l => FindPackage( l.Package ) )
			.Where( p => p != null )
			.ToList();
	}

	internal bool TryGetFile( string path, out File file )
	{
		return filesByPath.TryGetValue( path.NormalizeFilename( true ), out file );
	}

	internal List<Package> GetPackages()
	{
		return packageCache.Values.ToList();
	}
}

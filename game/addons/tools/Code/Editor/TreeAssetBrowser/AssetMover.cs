using Editor;
using Rinten;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Editor.TreeBrowser;

//Batch moves/copies assets, avoids clobbering, and repoints references in one project pass
internal static class AssetMover
{
    //What a batch did, worth logging: a move that renamed six files on collision is news to the user
    internal sealed class Result
    {
        public int Moved;
        public int Copied;
        public int Skipped;
        public int Renamed;
        public List<string> Errors = new();

        /// <summary>Old to new path of everything moved, to repoint; null if nothing to repoint.</summary>
        public Dictionary<string, string> PendingReferences;

        public bool DidAnything => Moved > 0 || Copied > 0;
    }

    /// <summary>Moves or copies sources into targetFolder, then repoints references to the old locations.</summary>
    public static Result Run( IEnumerable<string> sources, string targetFolder, bool copy )
    {
        var result = new Result();

        if ( string.IsNullOrEmpty( targetFolder ) || !Directory.Exists( targetFolder ) )
        {
            result.Errors.Add( $"Target folder doesn't exist: {targetFolder}" );
            return result;
        }

        var items = Plan( sources, targetFolder, copy, result );
        if ( items.Count == 0 )
            return result;

        //Map references before anything moves: a renamed folder's old contents cannot be enumerated
        var references = copy ? null : BuildReferenceMap( items );

        foreach ( var item in items )
        {
            Apply( item, result );
        }

        result.PendingReferences = references;
        return result;
    }

    /// <summary>Runs the batch, refreshes now, and repoints references in the background to avoid a freeze</summary>
    public static void RunAndReport( IEnumerable<string> sources, string targetFolder, bool copy, Action onComplete = null )
    {
        var result = Run( sources, targetFolder, copy );
        Report( result, onComplete );
    }

    //Log what a batch did, refresh, and kick off the reference pass if there's one to do.
    private static void Report( Result result, Action onComplete )
    {
        foreach ( var error in result.Errors )
        {
            Log.Error( error );
        }

        if ( result.Renamed > 0 )
        {
            Log.Info( $"Renamed {result.Renamed} item(s) to avoid overwriting something in the destination." );
        }

        onComplete?.Invoke();

        //Every browser, not only the panel the drop landed in: a file that moved out of
        //a folder is gone from every tree and grid showing it, not just from that one.
        TreeAssetBrowser.RefreshOpen();

        RepointInBackground( result.PendingReferences );
    }

    //One reference pass at a time; overlapping passes work from stale views and can undo each other
    private static readonly object RepointLock = new();

    private static void RepointInBackground( Dictionary<string, string> references )
    {
        if ( references is not { Count: > 0 } )
            return;

        System.Threading.Tasks.Task.Run( () =>
        {
            List<string> changed;

            lock ( RepointLock )
            {
                changed = RepointReferences( references );
            }

            if ( changed.Count == 0 )
                return;

            //The asset system isn't thread safe, so re-register back on the main thread
            MainThread.Queue( () =>
            {
                foreach ( var file in changed )
                {
                    try { AssetSystem.RegisterFile( file ); }
                    catch ( Exception ex ) { Log.Warning( $"Failed to re-register '{file}': {ex.Message}" ); }
                }

                Log.Info( $"Updated references in {changed.Count} file(s)." );
            } );
        } );
    }

    /// <summary>Renames in place and repoints references; a rename is a move with the same reference problem.</summary>
    public static Result Rename( string path, string newName )
    {
        var result = new Result();

        if ( string.IsNullOrWhiteSpace( newName ) )
        {
            result.Errors.Add( "Name can't be empty." );
            return result;
        }

        if ( newName.IndexOfAny( Path.GetInvalidFileNameChars() ) >= 0 )
        {
            result.Errors.Add( $"'{newName}' isn't a valid name." );
            return result;
        }

        string full;
        try { full = Path.GetFullPath( path ); }
        catch { result.Errors.Add( $"Bad path: {path}" ); return result; }

        var isDirectory = Directory.Exists( full );
        if ( !isDirectory && !File.Exists( full ) )
        {
            result.Errors.Add( $"'{Path.GetFileName( path )}' no longer exists." );
            return result;
        }

        var currentName = Path.GetFileName( full );

        //Nothing typed, or nothing changed
        if ( string.Equals( currentName, newName, StringComparison.Ordinal ) )
            return result;

        var destination = Path.Combine( Path.GetDirectoryName( full ), newName );

        //Changing only the capitalisation isn't a collision with itself
        var caseOnly = string.Equals( currentName, newName, StringComparison.OrdinalIgnoreCase );

        if ( !caseOnly && (File.Exists( destination ) || Directory.Exists( destination )) )
        {
            result.Errors.Add( $"'{newName}' already exists in this folder." );
            return result;
        }

        var item = new PlannedItem
        {
            Source = full,
            Destination = destination,
            IsDirectory = isDirectory,
            Copy = false
        };

        var references = BuildReferenceMap( new List<PlannedItem> { item } );

        Apply( item, result );

        if ( result.Errors.Count == 0 )
        {
            result.PendingReferences = references;
        }

        return result;
    }

    /// <summary>Convenience wrapper for the rename call sites: rename, refresh, repoint in the background.</summary>
    public static void RenameAndReport( string path, string newName, Action onComplete = null )
    {
        Report( Rename( path, newName ), onComplete );
    }

    /// <summary>Starts a drag of every path, one "file:///" line each, as Explorer sends them.</summary>
    public static bool StartDrag( Widget source, IEnumerable<string> paths, object payload = null )
        => StartDragCore( source, paths, payload );

    public static bool StartDrag( Panel source, IEnumerable<string> paths, object payload = null )
        => StartDragCore( source, paths, payload );

    static bool StartDragCore( object source, IEnumerable<string> paths, object payload )
    {
        var list = (paths ?? Enumerable.Empty<string>())
            .Where( x => !string.IsNullOrWhiteSpace( x ) )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToList();

        if ( list.Count == 0 )
            return false;

        var data = BuildDragData( list, payload );

        switch ( source )
        {
            case Panel panel:
                EditorDrag.Begin( panel, data, null );
                return true;
            case Widget widget:
            {
                var drag = new Drag( widget ) { Data = data };
                drag.Execute();
                return true;
            }
            default:
                return false;
        }
    }

    static DragData BuildDragData( List<string> list, object payload )
    {
        var data = new DragData();

        if ( payload != null )
            data.Object = payload;

        data.Text = string.Join( "\n", list.Select( FileUri ) );
        data.Url = new Uri( FileUri( list[0] ) );
        return data;
    }

    //A path as a file URI: three slashes counting the path's own; "file:///" + path is one too many on
    //Linux, where file:////home/x is a network share - see DragData.LocalPathOf
    internal static string FileUri( string path )
    {
        var forward = path.Replace( '\\', '/' );

        return "file://" + ( forward.StartsWith( '/' ) ? forward : "/" + forward );
    }

    /// <summary>The paths to drag: the whole selection if the grabbed row is in it, else that row.</summary>
    public static List<string> GetDragPaths( IEnumerable<string> selectedPaths, string grabbedPath )
    {
        var selected = (selectedPaths ?? Enumerable.Empty<string>())
            .Where( x => !string.IsNullOrWhiteSpace( x ) )
            .ToList();

        if ( !selected.Any( p => PathsEqual( p, grabbedPath ) ) )
            return [grabbedPath];

        return selected;
    }

    //A single thing to move, with its destination already resolved.
    private sealed class PlannedItem
    {
        public string Source;
        public string Destination;
        public bool IsDirectory;
        public bool Copy;
    }

    //Filters a raw drop: duplicates, items already in target, folders into themselves, nested items
    private static List<PlannedItem> Plan( IEnumerable<string> sources, string targetFolder, bool copy, Result result )
    {
        var planned = new List<PlannedItem>();
        var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        var targetFull = Path.GetFullPath( targetFolder );

        //Normalise and drop the obvious rubbish first
        var candidates = new List<string>();

        foreach ( var raw in sources ?? Enumerable.Empty<string>() )
        {
            if ( string.IsNullOrWhiteSpace( raw ) )
                continue;

            string full;
            try { full = Path.GetFullPath( NormalisePath( raw ) ); }
            catch { continue; }

            if ( !seen.Add( full ) )
                continue;

            if ( !File.Exists( full ) && !Directory.Exists( full ) )
            {
                result.Skipped++;
                continue;
            }

            candidates.Add( full );
        }

        //If a folder and something inside it are both selected, the child travels with the folder
        //- moving it separately would either fail or quietly pull it out of the folder being moved.
        var folders = candidates.Where( Directory.Exists ).ToList();
        candidates.RemoveAll( c => folders.Any( f => !PathsEqual( f, c ) && IsInside( c, f ) ) );

        foreach ( var source in candidates )
        {
            var isDirectory = Directory.Exists( source );

            //Anything from outside the project is copied in, never moved - we don't own it
            var copyThis = copy || AssetContextMenuHelper.IsExternalSource( source );

            //Moving something to where it already is does nothing. Copying it there is a duplicate,
            //which is what Explorer does on ctrl-drag and what paste-into-same-folder means.
            if ( !copyThis && PathsEqual( Path.GetDirectoryName( source ), targetFull ) )
            {
                result.Skipped++;
                continue;
            }

            //Dropping a folder onto itself, or into its own subtree, would eat the folder
            if ( isDirectory && !copyThis && (PathsEqual( source, targetFull ) || IsInside( targetFull, source )) )
            {
                result.Errors.Add( $"Can't move '{Path.GetFileName( source )}' into itself." );
                continue;
            }

            //Copying a folder into itself would recurse forever
            if ( isDirectory && copyThis && (PathsEqual( source, targetFull ) || IsInside( targetFull, source )) )
            {
                result.Errors.Add( $"Can't copy '{Path.GetFileName( source )}' into itself." );
                continue;
            }

            var destination = Path.Combine( targetFull, Path.GetFileName( source ) );

            //Never silently overwrite what's already there
            if ( File.Exists( destination ) || Directory.Exists( destination ) )
            {
                destination = MakeUniquePath( destination );
                result.Renamed++;
            }

            planned.Add( new PlannedItem
            {
                Source = source,
                Destination = destination,
                IsDirectory = isDirectory,
                Copy = copyThis
            } );
        }

        return planned;
    }

    //Actually move or copy one item.
    private static void Apply( PlannedItem item, Result result )
    {
        try
        {
            if ( item.IsDirectory )
            {
                if ( item.Copy )
                {
                    CopyDirectory( item.Source, item.Destination );
                    result.Copied++;
                }
                else
                {
                    MoveDirectory( item.Source, item.Destination );
                    result.Moved++;
                }

                AssetContextMenuHelper.RegisterNewPath( item.Destination );
                return;
            }

            //Done on files rather than through the asset system, which would leave a hidden "_c" behind at the old path
            if ( item.Copy )
            {
                CopyFileWithCompiled( item.Source, item.Destination );
                result.Copied++;
            }
            else
            {
                MoveFileWithCompiled( item.Source, item.Destination );
                result.Moved++;
            }

            AssetContextMenuHelper.RegisterNewPath( item.Destination );
        }
        catch ( Exception ex )
        {
            var verb = item.Copy ? "copy" : "move";
            result.Errors.Add( $"Failed to {verb} '{Path.GetFileName( item.Source )}': {ex.Message}" );
        }
    }

    //File.Move, taking the compiled "_c" sibling along so no stale asset is orphaned at the old path
    private static void MoveFileWithCompiled( string source, string destination )
    {
        File.Move( source, destination );

        var compiled = source + "_c";
        if ( File.Exists( compiled ) )
        {
            var compiledDestination = destination + "_c";

            if ( File.Exists( compiledDestination ) )
                File.Delete( compiledDestination );

            File.Move( compiled, compiledDestination );
        }
    }

    //File.Copy, taking the compiled "_c" sibling along, the same way a move does.
    private static void CopyFileWithCompiled( string source, string destination )
    {
        File.Copy( source, destination );

        var compiled = source + "_c";
        if ( File.Exists( compiled ) )
            File.Copy( compiled, destination + "_c", true );
    }

    //Directory.Move cannot cross volumes, so fall back to copy and delete
    private static void MoveDirectory( string source, string destination )
    {
        try
        {
            EditorUtility.RenameDirectory( source, destination );
        }
        catch ( IOException )
        {
            CopyDirectory( source, destination );
            Directory.Delete( source, true );
        }
    }

    private static void CopyDirectory( string sourceDir, string destDir )
    {
        Directory.CreateDirectory( destDir );

        foreach ( var file in Directory.GetFiles( sourceDir ) )
        {
            File.Copy( file, Path.Combine( destDir, Path.GetFileName( file ) ), true );
        }

        foreach ( var dir in Directory.GetDirectories( sourceDir ) )
        {
            CopyDirectory( dir, Path.Combine( destDir, Path.GetFileName( dir ) ) );
        }
    }

    //References

    //Old to new asset-relative path for this batch; folders map as a prefix, so one entry covers their contents
    private static Dictionary<string, string> BuildReferenceMap( List<PlannedItem> items )
    {
        var map = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

        var assetsRoot = Project.Current?.GetAssetsPath();
        if ( string.IsNullOrEmpty( assetsRoot ) )
            return map;

        foreach ( var item in items )
        {
            if ( item.Copy )
                continue;

            var oldRef = ToAssetRelative( assetsRoot, item.Source );
            var newRef = ToAssetRelative( assetsRoot, item.Destination );

            //Outside the assets mount - nothing references it by asset path
            if ( oldRef == null || newRef == null || oldRef == newRef )
                continue;

            //A folder's own name only ever appears as part of a longer path, so match it with
            //the separator attached. That also stops "models/car" matching "models/carpet".
            if ( item.IsDirectory )
            {
                map[oldRef + "/"] = newRef + "/";
            }
            else
            {
                map[oldRef] = newRef;
            }
        }

        return map;
    }

    //Rewrites every old path to its new one across the project in one pass; returns changed files
    private static List<string> RepointReferences( Dictionary<string, string> map )
    {
        var empty = new List<string>();

        var root = Project.Current?.GetAssetsPath();
        if ( string.IsNullOrEmpty( root ) || !Directory.Exists( root ) )
            return empty;

        var regex = BuildCombinedRegex( map.Keys );
        if ( regex == null )
            return empty;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories )
                .Where( IsScannableTextFile )
                .ToList();
        }
        catch ( Exception ex )
        {
            Log.Warning( $"Couldn't scan for references: {ex.Message}" );
            return empty;
        }

        var changed = new ConcurrentBag<string>();

        //Reading and matching a few thousand files is the slow part, and each file is independent
        System.Threading.Tasks.Parallel.ForEach( files, file =>
        {
            try
            {
                var info = new FileInfo( file );
                if ( info.Length == 0 || info.Length > 16_000_000 )
                    return;

                var text = File.ReadAllText( file );
                var updated = regex.Replace( text, m => map.TryGetValue( m.Value, out var to ) ? to : m.Value );

                if ( updated == text )
                    return;

                File.WriteAllText( file, updated );
                changed.Add( file );
            }
            catch
            {
                //Unreadable, locked, or not really text - leave it alone
            }
        } );

        return changed.ToList();
    }

    //One regex over all old paths, longest first so a nested folder beats its parent; the lookbehind keeps
    //"models/foo.mdl" out of "othermodels/foo.mdl", and a trailing "_c" still matches
    private static Regex BuildCombinedRegex( IEnumerable<string> references )
    {
        var alternatives = references
            .Where( x => !string.IsNullOrEmpty( x ) )
            .OrderByDescending( x => x.Length )
            .Select( Regex.Escape )
            .ToList();

        if ( alternatives.Count == 0 )
            return null;

        return new Regex( @"(?<![\w./\\-])(?:" + string.Join( "|", alternatives ) + ")", RegexOptions.IgnoreCase );
    }

    private static readonly HashSet<string> NonTextExtensions = new( StringComparer.OrdinalIgnoreCase )
    {
        ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".gif", ".dds", ".hdr", ".exr",
        ".fbx", ".obj", ".gltf", ".glb", ".dmx", ".blend",
        ".wav", ".mp3", ".ogg", ".flac",
        ".tex", ".vtex", ".vsnd", ".dll", ".pdb", ".exe", ".zip", ".bin"
    };

    private static bool IsScannableTextFile( string file )
    {
        var name = Path.GetFileName( file );

        if ( name.StartsWith( "." ) ) return false;
        if ( name.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) ) return false;
        if ( name.EndsWith( ".meta", StringComparison.OrdinalIgnoreCase ) ) return false;
        if ( name.Contains( ".generated", StringComparison.OrdinalIgnoreCase ) ) return false;

        return !NonTextExtensions.Contains( Path.GetExtension( file ) );
    }

    //Paths

    //The path relative to Assets with forward slashes, or null if it is not under the assets mount
    private static string ToAssetRelative( string assetsRoot, string fullPath )
    {
        try
        {
            var relative = Path.GetRelativePath( assetsRoot, fullPath ).Replace( '\\', '/' );
            return relative.StartsWith( ".." ) ? null : relative;
        }
        catch
        {
            return null;
        }
    }

    //Drops arrive as a mix of plain paths and "file:///" URIs, and the URI form is
    //percent-encoded. Flatten both to a plain path before we go anywhere near the disk.
    private static string NormalisePath( string raw )
    {
        var path = raw.Trim().Trim( '"' );

        const string prefix = "file:///";
        if ( path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
        {
            path = path[prefix.Length..];
            path = Uri.UnescapeDataString( path );
        }

        return path;
    }

    private static bool PathsEqual( string a, string b )
    {
        if ( a == null || b == null )
            return false;

        return string.Equals( a.TrimEnd( Path.DirectorySeparatorChar, '/' ),
                              b.TrimEnd( Path.DirectorySeparatorChar, '/' ),
                              StringComparison.OrdinalIgnoreCase );
    }

    //True if path sits inside folder at any depth.
    private static bool IsInside( string path, string folder )
    {
        var prefix = folder.TrimEnd( Path.DirectorySeparatorChar, '/' ) + Path.DirectorySeparatorChar;
        return path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase );
    }

    //"foo.mdl" -> "foo_copy.mdl" -> "foo_copy2.mdl", until it's free.
    private static string MakeUniquePath( string path )
    {
        if ( !File.Exists( path ) && !Directory.Exists( path ) )
            return path;

        var directory = Path.GetDirectoryName( path );
        var name = Path.GetFileNameWithoutExtension( path );
        var ext = Path.GetExtension( path );

        for ( int counter = 1; counter < 10000; counter++ )
        {
            var suffix = counter == 1 ? "_copy" : $"_copy{counter}";
            var candidate = Path.Combine( directory, name + suffix + ext );

            if ( !File.Exists( candidate ) && !Directory.Exists( candidate ) )
                return candidate;
        }

        return Path.Combine( directory, name + "_" + Guid.NewGuid().ToString( "N" )[..8] + ext );
    }
}

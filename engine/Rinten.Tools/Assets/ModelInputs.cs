using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Rinten;

//Files each model was built from (named exports plus what its last build read) and their staleness
internal static class ModelInputs
{
	//Where the records go, under the project's cache folder.
	private const string Folder = "/modelinputs";

	//A line of a record: a file read whole.
	private const string WholeFile = "F";

	//A line of a record: a few fields of a file, and what they said.
	private const string FieldsOf = "K";

	//A line of a record: the compiler's build it was made by - see ModelDefinitionCompiler.Build.
	private const string BuiltBy = "B";

	private static readonly object gate = new();

	//The exports each definition names, by when the definition was written.
	private static readonly Dictionary<string, (DateTime Written, IReadOnlyList<string> Named)> named = new( StringComparer.Ordinal );

	//Each model's record as read, by when the record was written.
	private static readonly Dictionary<string, (DateTime Written, Record Record)> records = new( StringComparer.Ordinal );

	//What a build read.
	private sealed record Record( string[] Files, (string Path, string Key)[] Fields, int Build );

	/// <summary>Write down what a build of <paramref name="definitionPath"/> read - see ModelDefinitionCompiler.Compile.</summary>
	public static void Save( string definitionPath, ImportReads.Recording reads )
	{
		if ( RecordPath( definitionPath ) is not { } path ) return;

		var definition = Normal( definitionPath );
		var text = new StringBuilder();

		text.Append( BuiltBy ).Append( '\t' ).Append( ModelDefinitionCompiler.Build ).Append( '\n' );

		foreach ( var file in reads.Files )
		{
			if ( !Keep( file, definition ) ) continue;

			text.Append( WholeFile ).Append( '\t' ).Append( file ).Append( '\n' );
		}

		foreach ( var (file, keys) in reads.Keyed )
		{
			if ( !Keep( file, definition ) ) continue;

			foreach ( var key in keys.Split( '\n' ) )
				text.Append( FieldsOf ).Append( '\t' ).Append( file ).Append( '\t' ).Append( key.Replace( '\t', ' ' ) ).Append( '\n' );
		}

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path ) );
			File.WriteAllText( path, text.ToString() );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			Log.Warning( $"{definitionPath}: could not write down what its build read - {e.Message}" );
		}
	}

	/// <summary>Every file the model is built from, where each is on disk now - what a change to one of them takes the model with it.</summary>
	public static IEnumerable<string> Paths( string definitionPath )
	{
		var definition = Normal( definitionPath );

		foreach ( var file in Named( definition ) )
			yield return file;

		if ( RecordOf( definition ) is not { } record ) yield break;

		foreach ( var file in record.Files )
			yield return file;

		foreach ( var (file, _) in record.Fields )
			yield return file;
	}

	/// <summary>Whether nothing the model was built from has changed since <paramref name="built"/>, when its compiled file was written.</summary>
	public static bool IsCurrent( string definitionPath, DateTime built )
	{
		var definition = Normal( definitionPath );

		foreach ( var file in Named( definition ) )
		{
			if ( File.GetLastWriteTimeUtc( file ) > built ) return false;
		}

		var keeping = RecordPath( definition ) is not null;
		var record = keeping ? RecordOf( definition ) : null;

		//An engine model is built before it ships, and no project has its record: its named exports are the whole check
		if ( record is null ) return !keeping || !InProject( definition );

		//Built by an older compiler, which made something else of the same files
		if ( record.Build != ModelDefinitionCompiler.Build ) return false;

		foreach ( var file in record.Files )
		{
			//Gone is changed: the build it went into read it.
			if ( !File.Exists( file ) || File.GetLastWriteTimeUtc( file ) > built ) return false;
		}

		foreach ( var (file, key) in record.Fields )
		{
			if ( !File.Exists( file ) ) return false;
			if ( File.GetLastWriteTimeUtc( file ) <= built ) continue;

			//Written since, and only out of date if what the build used of it says something else now.
			var colon = key.IndexOf( ':' );
			var purpose = colon < 0 ? key : key[..colon];

			if ( $"{purpose}:{ModelDefinitionCompiler.MaterialBakeKey( file, purpose )}" != key ) return false;
		}

		return true;
	}

	/// <summary>Everything remembered, for a project being closed.</summary>
	public static void Clear()
	{
		lock ( gate )
		{
			named.Clear();
			records.Clear();
		}
	}

	private static IEnumerable<string> Named( string definition )
	{
		DateTime written;

		try
		{
			written = File.GetLastWriteTimeUtc( definition );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			yield break;
		}

		IReadOnlyList<string> names;

		lock ( gate )
		{
			if ( !named.TryGetValue( definition, out var had ) || had.Written != written )
			{
				had = (written, ModelDefinitionCompiler.NamedInputsOf( definition ));
				named[definition] = had;
			}

			names = had.Named;
		}

		//Looked for every time: what a name finds can arrive after the definition was read.
		foreach ( var name in names )
		{
			if ( ModelDefinitionCompiler.ResolveInput( definition, name ) is { } found )
				yield return Normal( found );
		}
	}

	private static Record RecordOf( string definition )
	{
		if ( RecordPath( definition ) is not { } path ) return null;

		DateTime written;

		try
		{
			if ( !File.Exists( path ) ) return null;
			written = File.GetLastWriteTimeUtc( path );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			return null;
		}

		lock ( gate )
		{
			if ( records.TryGetValue( definition, out var had ) && had.Written == written )
				return had.Record;
		}

		var files = new List<string>();
		var fields = new List<(string, string)>();
		var build = 0;

		try
		{
			foreach ( var line in File.ReadAllLines( path ) )
			{
				var parts = line.Split( '\t' );

				if ( parts.Length == 2 && parts[0] == WholeFile ) files.Add( parts[1] );
				else if ( parts.Length == 3 && parts[0] == FieldsOf ) fields.Add( (parts[1], parts[2]) );
				else if ( parts.Length == 2 && parts[0] == BuiltBy && int.TryParse( parts[1], out var by ) ) build = by;
			}
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			return null;
		}

		var record = new Record( files.ToArray(), fields.ToArray(), build );

		lock ( gate ) records[definition] = (written, record);

		return record;
	}

	//Where a model's record is kept, or null with no project open. Named by the definition's whole path, so two models called the same are two records.
	private static string RecordPath( string definition )
	{
		var cache = Editor.FileSystem.ProjectTemporary;
		if ( cache is null ) return null;

		var full = Normal( definition );
		var hash = Convert.ToHexString( System.Security.Cryptography.SHA1.HashData( Encoding.UTF8.GetBytes( full ) ), 0, 8 ).ToLowerInvariant();

		return cache.GetFullPath( $"{Folder}/{Path.GetFileNameWithoutExtension( full )}.{hash}.inputs" );
	}

	//Whether a file read by a build is one of its inputs: not the definition, not something compiled - its own model, a clip it writes - and still there.
	private static bool Keep( string file, string definition )
	{
		if ( string.Equals( file, definition, StringComparison.Ordinal ) ) return false;
		if ( file.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) ) return false;

		return File.Exists( file );
	}

	private static bool InProject( string definition )
	{
		if ( Project.Current?.GetRootPath() is not { } root ) return true;

		return definition.StartsWith( Normal( root ).TrimEnd( '/' ) + "/", StringComparison.OrdinalIgnoreCase );
	}

	private static string Normal( string path ) => Path.GetFullPath( path ).Replace( '\\', '/' );
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Rinten;

//Compiled shader stages keyed by their source plus includes, so shipped builds never need Slang
//Holds engine passes (shaders/engine/) and material shader variants; a stale cache is slow, not wrong
internal static class CoreShaderCache
{
	private static readonly Logger log = new( "Shaders" );

	//'ESSC', little end first - a cache, as against 'ESSS' a shader.
	private const uint Magic = 0x43535345;

	//Cache file version; an older file is read as empty, costing one bake
	private const uint Version = 4;

	//How a file's table is packed. Brotli everywhere a runtime can unpack it, which a browser's cannot - its .NET
	//has no Brotli - so a page's cache is stored as it is, and the host's own compression carries it over the wire.
	private enum Packing
	{
		Stored = 0,
		Brotli = 1,
	}

	/// <summary>Where it sits, under the game folder.</summary>
	public const string FileName = "core/shaders/builtin.shadercache";

	//What is in it, by key.
	private static Dictionary<string, byte[]> entries;

	//What this run compiled that the file did not have.
	private static readonly Dictionary<string, byte[]> compiled = new();

	//Every key this run asked for - what a bake writes, so words nothing asks for drop out of the file
	private static readonly HashSet<string> touched = new( StringComparer.Ordinal );

	//The keys in the baked file as read: the engine's own as the build baked them, not what this machine compiled
	private static HashSet<string> baked = new( StringComparer.Ordinal );

	//Every key asked for since Record, for WriteForGame. Null while nobody is recording.
	private static HashSet<string> recording;

	//What has been said about once, so it is not said sixty times a second.
	private static readonly HashSet<string> said = new( StringComparer.OrdinalIgnoreCase );

	//How many times a shader file has changed under a running engine - the engine's
	//own pipelines are made again when it moves; see CoreSceneDrawer.MakePostPipelines.
	internal static int Revision { get; private set; }

	//The language a bake compiles for while one does - see Target; said once around a bake, as a bake asks
	//the same questions a frame does and only the answer's language differs
	internal static CoreShaderCompiler.Format? Baking { get; set; }

	//The language a stage is wanted in when not said: a bake's, else what this device takes
	internal static CoreShaderCompiler.Format Target => Baking ?? CoreRender.ShaderFormat;

	//A shader file was edited, or a project that may carry its own was mounted: the includes may resolve elsewhere
	internal static void Changed()
	{
		CoreShaderFile.ForgetIncludes();
		Revision++;
	}

	//The key for one compile: the target, the entry point and its stage, the text itself and what its includes say.
	private static string Key( CoreShaderCompiler.Format format, string entry, CoreShaderCompiler.Stage stage,
		string source, string includes )
	{
		//What Slang wrote is not all there is to a browser's words: they are written over afterwards, and a change
		//to that is a change to the words that the text alone would never show - see CoreShaderCompiler.Revision.
		var revision = CoreShaderCompiler.Revision( format );
		var language = revision == 0 ? $"{(int)format}" : $"{(int)format}r{revision}";

		var bytes = Encoding.UTF8.GetBytes( $"{language} {entry} {(int)stage}\n{includes}\n{source}" );

		return Convert.ToHexString( SHA256.HashData( bytes ) );
	}

	//Whether to say what each key was made of - RINTEN_SHADER_KEYS=1, or -shaderkeys where there is no environment (a page);
	//asked late, as a page's switches are read after the first shaders
	private static bool Saying => saying ??= Environment.GetEnvironmentVariable( "RINTEN_SHADER_KEYS" ) == "1" ||
		Rinten.Utility.CommandLine.HasSwitch( "-shaderkeys" );

	private static bool? saying;

	//What a key was made of, the two halves hashed apart, so a miss names which half moved
	private static void Say( string name, string entry, CoreShaderCompiler.Stage stage, string source, string includes, string key )
	{
		if ( !Saying ) return;

		static string Short( string of )
			=> Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( of ?? "" ) ) )[..8];

		log.Info( $"key {name ?? entry} {entry} {(int)stage}: text {Short( source )} ({source?.Length ?? 0}), " +
			$"includes {Short( includes )} ({includes?.Length ?? 0}) -> {key[..8]}" );
	}

	/// <summary>One entry point of an engine pass under shaders/engine/, with the defines choosing its variant.</summary>
	public static byte[] Pass( string file, string entry, CoreShaderCompiler.Stage stage, string defines = null,
		CoreShaderCompiler.Format? language = null, bool optional = false )
	{
		var format = language ?? Target;

		//A pass the portable frame has not got is not missing from it: said here once, and whoever asks takes
		//no words to mean it is not drawn, which in that frame is right
		if ( CoreShaderCompiler.IsPortable( format ) && !EngineShaders.InPortableFrame( file ) ) return null;

		if ( PassText( file, defines ) is not { } made ) return null;

		//Asked of the cache and of nobody else. A build with no compiler - a page, a phone - would
		//otherwise say out loud that a shader is missing, about a run it is perfectly all right not to have.
		if ( optional )
			return Cached( made.Text, made.Directory, entry, stage, format, out _, out var baked ) ? baked : null;

		return Words( made.Text, made.Directory, entry, stage, format, file );
	}

	//An engine pass's text with defines before it and text after it, and its folder; null where it is not there
	internal static (string Text, string Directory)? PassText( string file, string defines = null, string after = null )
	{
		var path = CoreShaderFile.Source( file );

		if ( path is null )
		{
			if ( Once( "missing " + file ) )
				log.Error( $"{file} is not there - nothing the engine draws with it will be drawn. It ships in addons/base/Assets." );

			return null;
		}

		try
		{
			//Through the seal: a shipped build's shaders are sealed, and the sealed bytes are not the text a key is made of
			return ( ( defines ?? "" ) + CompiledFile.TextOf( path ) + ( after ?? "" ), Path.GetDirectoryName( path ) );
		}
		catch ( IOException e )
		{
			log.Warning( $"{file} would not read: {e.Message}" );
			return null;
		}
	}

	/// <summary>The compiled words for one entry point, from the cache or else the compiler.</summary>
	public static byte[] Words( string source, string directory, string entry, CoreShaderCompiler.Stage stage,
		CoreShaderCompiler.Format format = CoreShaderCompiler.Format.Spirv, string name = null, string instead = null )
	{
		if ( string.IsNullOrEmpty( source ) || string.IsNullOrEmpty( entry ) ) return null;

		//One key for a file checked out either way round.
		source = source.Replace( "\r\n", "\n" );

		var digest = CoreShaderFile.IncludeDigest( source, directory );
		var key = Key( format, entry, stage, source, digest );

		Say( name, entry, stage, source, digest, key );

		Load();

		lock ( compiled )
		{
			touched.Add( key );
			recording?.Add( key );

			if ( entries.TryGetValue( key, out var cached ) ) return cached;
			if ( compiled.TryGetValue( key, out var made ) ) return made;
		}

		var words = Compile( source, directory, entry, stage, format, name, instead );

		if ( words is null ) return null;

		//Kept, so a bake can write out what it made, so the next pipeline out of the same text in
		//this run does not compile it again, and so the next run does not either - see LocalPath.
		Keep( key, words );

		return words;
	}

	//The words for one entry where the cache has them, and their key, without compiling; the key reads the
	//includes, so this runs on the asking thread - see CompileAndKeep and CoreShaderJobs
	internal static bool Cached( string source, string directory, string entry, CoreShaderCompiler.Stage stage,
		CoreShaderCompiler.Format format, out string key, out byte[] words )
	{
		key = null;
		words = null;

		if ( string.IsNullOrEmpty( source ) || string.IsNullOrEmpty( entry ) ) return false;

		source = source.Replace( "\r\n", "\n" );

		var digest = CoreShaderFile.IncludeDigest( source, directory );

		key = Key( format, entry, stage, source, digest );

		Say( null, entry, stage, source, digest, key );

		Load();

		lock ( compiled )
		{
			touched.Add( key );
			recording?.Add( key );

			if ( entries.TryGetValue( key, out words ) ) return true;
			if ( compiled.TryGetValue( key, out words ) ) return true;
		}

		return false;
	}

	//The half of Words that may run on a thread of its own: the compile, kept under the key Cached gave.
	internal static byte[] CompileAndKeep( string key, string source, string directory, string entry,
		CoreShaderCompiler.Stage stage, CoreShaderCompiler.Format format, string name, string instead = null )
	{
		var words = Compile( source.Replace( "\r\n", "\n" ), directory, entry, stage, format, name, instead );

		if ( words is not null ) Keep( key, words );

		return words;
	}

	private static byte[] Compile( string source, string directory, string entry, CoreShaderCompiler.Stage stage,
		CoreShaderCompiler.Format format, string name, string instead = null )
	{
		if ( !CoreShaderCompiler.Available )
		{
			//Warn once per shader; a missing material variant isn't drawn, missing point effects fall back
			if ( Once( "compiler " + ( name ?? entry ) ) )
				log.Warning( $"{name ?? entry} is not in the shader cache and there is no compiler here to build it - " +
					( instead ?? "whatever draws with it will be missing. Bake the shaders into this build." ) );

			return null;
		}

		//The engine's folders and the projects', the projects' first - see Looking.
		CoreShaderFile.Looking();

		var words = CoreRenderDevice.CompileShaderSource( source, directory, entry, stage, format );

		//The compiler's own words, once per entry: a caller only sees a null and the frame silently draws differently
		if ( words is null && Once( "broken " + ( name ?? "" ) + "|" + entry ) )
			log.Warning( $"{name ?? entry}: {entry} would not compile - {CoreRenderDevice.ShaderError}" );

		return words;
	}

	private static bool Once( string what )
	{
		lock ( said ) return said.Add( what );
	}

	//Reads the file, once.
	private static void Load()
	{
		if ( entries is not null ) return;

		lock ( compiled )
		{
			if ( entries is not null ) return;

			var read = new Dictionary<string, byte[]>( StringComparer.Ordinal );

			var path = Path.Combine( Directory.GetCurrentDirectory(),
				FileName.Replace( '/', Path.DirectorySeparatorChar ) );

			try
			{
				if ( File.Exists( path ) )
				{
					Read( File.ReadAllBytes( path ), read );
					log.Info( $"{read.Count} shader stages from the cache" );
				}
			}
			catch ( Exception e ) when ( e is IOException or ArgumentException )
			{
				//A cache that will not read is compiled past, as an absent one is; said once
				read.Clear();
				log.Warning( $"the shader cache would not read ({e.Message}) - compiling instead" );
			}

			baked = new HashSet<string>( read.Keys, StringComparer.Ordinal );

			//And what this machine compiled since, beneath the baked file, which wins where both have a key
			local = new Dictionary<string, byte[]>( StringComparer.Ordinal );

			try
			{
				if ( File.Exists( LocalPath ) )
				{
					Read( File.ReadAllBytes( LocalPath ), local );

					var added = 0;

					foreach ( var (key, words) in local )
					{
						if ( read.TryAdd( key, words ) ) added++;
					}

					if ( added > 0 ) log.Info( $"{added} more compiled here before, from {LocalPath}" );
				}
			}
			catch ( Exception e ) when ( e is IOException or ArgumentException or UnauthorizedAccessException )
			{
				//Only ever a slower start: whatever it held is compiled again.
				local.Clear();
			}

			entries = read;
		}
	}

	//Per-machine cache of stages missing from the baked file, so edits don't recompile on every start
	private static string LocalPath
	{
		get
		{
			var root = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
			if ( string.IsNullOrEmpty( root ) ) root = Path.GetTempPath();

			return Path.Combine( root, "Rinten", "shaders.cache" );
		}
	}

	//What LocalPath held when it was read.
	private static Dictionary<string, byte[]> local;

	//Whether a save is already on its way.
	private static int saving;

	//Past this, only what this run asked for is kept - each edit of a shader leaves its old words behind.
	private const long LocalBudget = 64L * 1024 * 1024;

	//Words compiled this run, kept for the rest of it and for the next.
	private static void Keep( string key, byte[] words )
	{
		lock ( compiled ) compiled[key] = words;

		//Written a moment after the last compile rather than only at shutdown: an editor
		//is closed by being killed as often as not, and a burst of compiles is one write.
		if ( System.Threading.Interlocked.Exchange( ref saving, 1 ) != 0 ) return;

		System.Threading.Tasks.Task.Run( async () =>
		{
			await System.Threading.Tasks.Task.Delay( 2000 );
			System.Threading.Volatile.Write( ref saving, 0 );
			SaveLocal();
		} );
	}

	//Writes what this run compiled into LocalPath, beside what was there. Nothing when nothing was compiled.
	internal static void SaveLocal()
	{
		Dictionary<string, byte[]> all;

		lock ( compiled )
		{
			if ( compiled.Count == 0 || local is null ) return;

			all = new Dictionary<string, byte[]>( local, StringComparer.Ordinal );

			var added = false;

			foreach ( var (key, words) in compiled )
			{
				if ( !all.ContainsKey( key ) ) added = true;
				all[key] = words;
			}

			if ( !added ) return;

			long size = 0;
			foreach ( var words in all.Values ) size += words.Length;

			if ( size > LocalBudget )
			{
				foreach ( var key in new List<string>( all.Keys ) )
				{
					if ( !touched.Contains( key ) && !compiled.ContainsKey( key ) ) all.Remove( key );
				}
			}

			local = all;
		}

		//One write at a time in this process: the delayed save clears its flag before it writes,
		//so a burst of compiles behind it can start the next while this one is still on the disk.
		lock ( writing )
		{
			var path = LocalPath;

			//Write to a unique temp name and move into place, so crashes and concurrent writers can't clash
			var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.new";

			try
			{
				Directory.CreateDirectory( Path.GetDirectoryName( path ) );
				File.WriteAllBytes( temporary, Serialise( all ) );
				File.Move( temporary, path, overwrite: true );
			}
			catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
			{
				log.Info( $"the compiled shaders were not kept: {e.Message}" );

				try { File.Delete( temporary ); } catch ( Exception ) { }
			}
		}
	}

	//Held while SaveLocal writes - see there.
	private static readonly object writing = new();

	//The file into a table: magic, version, packing and packed size, then the table - see Serialise
	private static void Read( byte[] file, Dictionary<string, byte[]> into )
	{
		if ( file.Length < 16 ) throw new ArgumentException( "too short to be one" );
		if ( BitConverter.ToUInt32( file, 0 ) != Magic ) throw new ArgumentException( "not a shader cache" );
		if ( BitConverter.ToUInt32( file, 4 ) != Version ) throw new ArgumentException( "a different version" );

		var packing = (Packing)BitConverter.ToInt32( file, 8 );
		var length = BitConverter.ToInt32( file, 12 );

		if ( length < 8 || length > 1 << 30 ) throw new ArgumentException( "says it holds a silly amount" );

		var body = new byte[length];

		if ( packing == Packing.Stored )
		{
			if ( file.Length < 16 + length ) throw new ArgumentException( "ends inside the table" );
			Array.Copy( file, 16, body, 0, length );
		}
		else
		{
			using var packed = new MemoryStream( file, 16, file.Length - 16, writable: false );
			using var unpacking = new BrotliStream( packed, CompressionMode.Decompress );

			unpacking.ReadExactly( body, 0, length );
		}

		var stages = BitConverter.ToInt32( body, 0 );
		var keys = BitConverter.ToInt32( body, 4 );

		if ( stages < 0 || keys < 0 ) throw new ArgumentException( "says it holds a silly number of entries" );

		var words = new byte[stages][];
		var at = 8;

		for ( var i = 0; i < stages; i++ )
		{
			if ( at + 4 > body.Length ) throw new ArgumentException( "ends inside the stages" );

			var size = BitConverter.ToInt32( body, at );

			at += 4;

			if ( size < 0 || at + size > body.Length ) throw new ArgumentException( "a stage runs past the end" );

			words[i] = new byte[size];
			Array.Copy( body, at, words[i], 0, size );
			at += size;
		}

		for ( var i = 0; i < keys; i++ )
		{
			if ( at + 68 > body.Length ) throw new ArgumentException( "ends inside the keys" );

			var key = Encoding.ASCII.GetString( body, at, 64 );
			var which = BitConverter.ToInt32( body, at + 64 );

			at += 68;

			if ( which < 0 || which >= stages ) throw new ArgumentException( "a key names a stage that is not there" );

			into[key] = words[which];
		}
	}

	/// <summary>What is known now - what was read and what this run compiled - as the bytes of a cache file.</summary>
	public static byte[] Write( bool touchedOnly = false, bool small = false )
	{
		Load();

		Dictionary<string, byte[]> all;

		lock ( compiled )
		{
			all = new Dictionary<string, byte[]>( entries, StringComparer.Ordinal );

			foreach ( var (key, words) in compiled ) all[key] = words;

			if ( touchedOnly )
			{
				foreach ( var key in new List<string>( all.Keys ) )
				{
					if ( !touched.Contains( key ) ) all.Remove( key );
				}
			}
		}

		return Serialise( all, small );
	}

	//Serialises a table with each distinct stage stored once, then packs it; see Read
	private static byte[] Serialise( Dictionary<string, byte[]> all, bool small = false, bool stored = false )
	{
		//The distinct stages, and which one each key is.
		var stages = new List<byte[]>();
		var which = new Dictionary<byte[], int>( Same.Words );
		var keys = new List<(string Key, int Which)>( all.Count );

		foreach ( var (key, words) in all )
		{
			if ( !which.TryGetValue( words, out var at ) )
			{
				at = stages.Count;
				stages.Add( words );
				which[words] = at;
			}

			keys.Add( ( key, at ) );
		}

		var total = 8 + keys.Count * 68;

		foreach ( var words in stages ) total += 4 + words.Length;

		var body = new byte[total];

		BitConverter.GetBytes( stages.Count ).CopyTo( body, 0 );
		BitConverter.GetBytes( keys.Count ).CopyTo( body, 4 );

		var into = 8;

		foreach ( var words in stages )
		{
			BitConverter.GetBytes( words.Length ).CopyTo( body, into );
			into += 4;

			words.CopyTo( body, into );
			into += words.Length;
		}

		foreach ( var (key, at) in keys )
		{
			Encoding.ASCII.GetBytes( key, 0, 64, body, into );
			BitConverter.GetBytes( at ).CopyTo( body, into + 64 );
			into += 68;
		}

		using var packed = new MemoryStream();

		packed.Write( BitConverter.GetBytes( Magic ) );
		packed.Write( BitConverter.GetBytes( Version ) );
		packed.Write( BitConverter.GetBytes( (int)( stored ? Packing.Stored : Packing.Brotli ) ) );
		packed.Write( BitConverter.GetBytes( body.Length ) );

		if ( stored )
		{
			packed.Write( body );
			return packed.ToArray();
		}

		//Brotli quality 9 normally (fast); 11 only for the shipped file, where size matters
		var quality = small ? 11 : 9;
		var room = new byte[body.Length + 1024];

		if ( BrotliEncoder.TryCompress( body, room, out var written, quality, 22 ) )
		{
			packed.Write( room, 0, written );
		}
		else
		{
			using var packing = new BrotliStream( packed, CompressionLevel.Optimal, leaveOpen: true );

			packing.Write( body, 0, body.Length );
		}

		return packed.ToArray();
	}

	//One array of bytes is the same stage as another when it says the same thing.
	private sealed class Same : IEqualityComparer<byte[]>
	{
		internal static readonly Same Words = new();

		public bool Equals( byte[] a, byte[] b ) => a.AsSpan().SequenceEqual( b );

		public int GetHashCode( byte[] words )
		{
			//The length and both ends: SPIR-V that agrees on those and differs in the middle is
			//rare enough to be worth one comparison, and hashing several megabytes a bake is not.
			var hash = new HashCode();

			hash.Add( words.Length );

			for ( var i = 0; i < 64 && i < words.Length; i++ ) hash.Add( words[i] );
			for ( var i = Math.Max( 0, words.Length - 64 ); i < words.Length; i++ ) hash.Add( words[i] );

			return hash.ToHashCode();
		}
	}

	/// <summary>How many entries a written cache would have.</summary>
	public static int Count
	{
		get
		{
			Load();

			lock ( compiled )
			{
				var all = new HashSet<string>( entries.Keys, StringComparer.Ordinal );
				all.UnionWith( compiled.Keys );

				return all.Count;
			}
		}
	}

	//Forgets what was read, so a test can put a different file there.
	internal static void Forget()
	{
		lock ( compiled )
		{
			entries = null;
			local = null;
			compiled.Clear();
			touched.Clear();
			baked = new HashSet<string>( StringComparer.Ordinal );
			recording = null;
		}

		lock ( said ) said.Clear();
	}

	//Starts keeping every key asked for from here on - what a bake for a game writes; see WriteForGame.
	internal static void Record()
	{
		Load();

		lock ( compiled ) recording = new HashSet<string>( StringComparer.Ordinal );
	}

	//Writes a game's cache: the baked engine shaders plus what was recorded since Record; stops recording
	internal static bool WriteForGame( string path )
	{
		Load();

		var all = new Dictionary<string, byte[]>( StringComparer.Ordinal );

		lock ( compiled )
		{
			foreach ( var key in baked )
			{
				if ( entries.TryGetValue( key, out var words ) ) all[key] = words;
			}

			if ( recording is not null )
			{
				foreach ( var key in recording )
				{
					if ( compiled.TryGetValue( key, out var words ) || entries.TryGetValue( key, out words ) )
						all[key] = words;
				}
			}

			recording = null;
		}

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path ) );
			File.WriteAllBytes( path, Serialise( all, small: true ) );
		}
		catch ( IOException e )
		{
			log.Error( $"could not write the shader cache: {e.Message}" );
			return false;
		}

		log.Info( $"wrote {all.Count} shader stages into {path}" );

		return true;
	}

	//-- baking -----------------------------------------------------------------

	/// <summary>Compiles all engine passes and shipped shaders into the engine cache (-bakeshaders), no window</summary>
	public static bool BakeAll()
	{
		if ( !CoreShaderCompiler.Available )
		{
			log.Error( "cannot bake shaders: there is no compiler in this build" );
			return false;
		}

		var whole = true;

		try
		{
			//The full frame, which is everything.
			Baking = CoreShaderCompiler.Format.Spirv;
			whole &= BakeFrame( portable: false );

			//And the portable one, which is what draws straight onto a screen: a device that cannot hold the full
			//frame has no compiler either, more often than not - a phone - so what it draws with is baked or missing.
			Baking = CoreShaderCompiler.Format.SpirvPortable;
			whole &= BakeFrame( portable: true );
		}
		finally
		{
			Baking = null;
		}

		var written = WriteTo( Path.Combine( Directory.GetCurrentDirectory(),
			FileName.Replace( '/', Path.DirectorySeparatorChar ) ) );

		return written && whole && StereoCompiles();
	}

	//Build check that engine passes compile for two views (headset stereo); results are discarded
	private static bool StereoCompiles()
	{
		if ( !CoreShaderCompiler.Available ) return true;

		try
		{
			Baking = CoreShaderCompiler.Format.GlslStereo;
			return BakePasses( portable: true );
		}
		finally
		{
			Baking = null;
		}
	}

	//The engine's own passes the frame has, in the language Baking names.
	private static bool BakePasses( bool portable )
	{
		var whole = true;

		foreach ( var (file, defines, stages, everywhere) in EngineShaders.All )
		{
			if ( portable && !everywhere ) continue;

			foreach ( var (entry, stage) in stages )
			{
				if ( stage == CoreShaderCompiler.Stage.Compute && portable && Target != CoreShaderCompiler.Format.SpirvPortable ) continue;

				if ( Pass( file, entry, stage, defines ) is not null ) continue;

				log.Error( $"{file} {entry}{( string.IsNullOrEmpty( defines ) ? "" : " with " + defines.Trim().Replace( '\n', ' ' ) )} " +
					$"did not compile{( portable ? " for the portable frame" : "" )}: {CoreRenderDevice.ShaderError}" );

				whole = false;
			}
		}

		return whole;
	}

	//Writes a page's cache: portable engine passes plus project shaders, in both browser languages
	internal static bool WriteForWeb( string path, Action project = null, bool stereo = true )
	{
		if ( !CoreShaderCompiler.Available )
		{
			log.Error( "cannot bake the page's shaders: there is no compiler in this build" );
			return false;
		}

		Record();

		var whole = true;

		try
		{
			//GlslStereo: the engine's passes for two views, as a pass drawing both eyes binds; only passes - a
			//surface's two-view run is baked into its own .shader_c
			foreach ( var format in new[] { CoreShaderCompiler.Format.Wgsl, CoreShaderCompiler.Format.Glsl } )
			{
				Baking = format;
				whole &= BakePasses( portable: true );
				BakeShippedPasses();
				project?.Invoke();
			}

			//Bake materials for two views too, or StereoBaked is always false and stereo never turns on
			if ( stereo )
			{
				Baking = CoreShaderCompiler.Format.GlslStereo;
				whole &= BakePasses( portable: true );
				project?.Invoke();
			}
		}
		finally
		{
			Baking = null;
		}

		var all = new Dictionary<string, byte[]>( StringComparer.Ordinal );

		lock ( compiled )
		{
			foreach ( var key in recording )
			{
				if ( compiled.TryGetValue( key, out var words ) || entries.TryGetValue( key, out words ) )
					all[key] = words;
			}

			recording = null;
		}

		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path ) );
			File.WriteAllBytes( path, Serialise( all, stored: true ) );
		}
		catch ( IOException e )
		{
			log.Error( $"could not write the page's shader cache: {e.Message}" );
			return false;
		}

		log.Info( $"wrote {all.Count} shader stages for the page into {path}" );

		return whole;
	}

	//One frame's worth of a bake in Baking's language: that frame's engine passes and every shipped shader
	private static bool BakeFrame( bool portable )
	{
		var whole = BakePasses( portable );

		//And every shader the engine ships, at the features it declares as its defaults - which is what every material
		//that names one and chooses nothing asks for. See CoreShaderFile.Compile, which is the same call a material makes.
		foreach ( var shader in Shipped() )
		{
			//Written for a legacy shader compiler that is not here: nothing of ours builds or draws it
			if ( CoreShaderFile.IsForeign( shader ) )
			{
				log.Info( $"{shader} is another engine's shader - left out" );
				continue;
			}

			//Neither a surface nor a pass over the frame that is more than a point effect, which the portable frame carries
			if ( portable && !CoreResources.IsSurface( shader ) && !PortablePass( shader ) ) continue;

			//A surface that is not one draws nothing, and nothing else would say so.
			if ( !portable && CoreShaderFile.SurfaceDoubt( shader ) is { } doubt ) log.Warning( doubt );

			//A pass storing in the compact format, compiled both ways; the device picks - see CompactName
			var variants = CoreShaderFile.IsPass( shader ) ? new[] { "r11f_g11f_b10f", "rgba16f" } : new string[] { null };

			foreach ( var compact in variants )
			{
				CoreShaderFile.CompactName = compact;

				if ( CoreShaderFile.Compile( shader ) is null )
				{
					//Not a failure in the portable frame: a surface that reads the table of pictures - the terrain's,
					//a sprite's - cannot be said without one, and is a thing that frame does not draw yet.
					if ( portable )
					{
						log.Warning( $"{shader} is not in the portable frame: {CoreRenderDevice.ShaderError}" );
						continue;
					}

					log.Error( $"{shader} did not compile: {CoreRenderDevice.ShaderError}" );
					whole = false;
				}

				//And each entry of its own beyond every file's - bloom's three in one file; see EntryWords
				foreach ( var (entry, kind) in CoreShaderFile.EntriesOf( shader ) )
				{
					if ( portable && kind == CoreShaderCompiler.Stage.Compute ) continue;

					if ( CoreShaderFile.EntryWords( shader, null, entry, kind ) is not null ) continue;

					log.Error( $"{shader} {entry} did not compile: {CoreRenderDevice.ShaderError}" );
					whole = false;
				}
			}

			CoreShaderFile.CompactName = null;
		}

		//And the point effects among them: each one as a pass of its own, and the compose with the engine's
		//curve - see CorePointChain.Bake. The portable frame has no pictures for an effect to be laid over.
		if ( !portable && !CorePointChain.Bake( Shipped() ) ) whole = false;

		return whole;
	}

	/// <summary>The cache written as a file: what this run asked for, or everything; games ship WriteForGame's.</summary>
	public static bool WriteTo( string path, bool touchedOnly = true )
	{
		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path ) );
			//Not the tightest packing, six seconds against a fifth of one for 130 KB - see Serialise; only the file
			//a game ships is worth it, and WriteForGame asks
			File.WriteAllBytes( path, Write( touchedOnly ) );
		}
		catch ( IOException e )
		{
			log.Error( $"could not write the shader cache: {e.Message}" );
			return false;
		}

		log.Info( $"wrote {( touchedOnly ? "the shader stages this run asked for" : $"{Count} shader stages" )} into {path}" );

		return true;
	}

	//Whether a shipped shader is a pass over the frame the portable frame draws as one, rather than a point effect it carries
	private static bool PortablePass( string shader )
		=> CoreShaderFile.IsPass( shader ) && !CorePointChain.IsPoint( shader ) && !CoreResources.IsFullFrameOnly( shader );

	//The shipped passes a page draws as passes of their own, in Baking's language; one that will not is said and left out
	private static void BakeShippedPasses()
	{
		foreach ( var shader in Shipped() )
		{
			if ( CoreShaderFile.IsForeign( shader ) || !PortablePass( shader ) ) continue;

			if ( CoreShaderFile.Compile( shader ) is null )
				log.Warning( $"{shader} is not in the portable frame: {CoreRenderDevice.ShaderError}" );

			foreach ( var (entry, kind) in CoreShaderFile.EntriesOf( shader ) )
			{
				if ( kind == CoreShaderCompiler.Stage.Compute ) continue;

				if ( CoreShaderFile.EntryWords( shader, null, entry, kind ) is null )
					log.Warning( $"{shader} {entry} is not in the portable frame: {CoreRenderDevice.ShaderError}" );
			}
		}
	}

	//The .shader files under the engine's own shader folder.
	private static IEnumerable<string> Shipped()
	{
		var folder = Path.GetDirectoryName( CoreShaderFile.Shipped( "shaders/frame.slang" ) ?? "" );

		if ( string.IsNullOrEmpty( folder ) || !Directory.Exists( folder ) ) return Array.Empty<string>();

		var found = new List<string>( Directory.EnumerateFiles( folder, "*.shader", SearchOption.AllDirectories ) );
		found.Sort( StringComparer.Ordinal );

		return found;
	}
}

//Engine pass files and variant defines; a project file of the same name overrides one
internal static class EngineShaders
{
	internal const string Line = "shaders/engine/line.slang";
	internal const string SurfaceGrid = "shaders/engine/surface_grid.slang";
	internal const string Gizmo = "shaders/engine/gizmo.slang";
	internal const string Billboard = "shaders/engine/billboard.slang";
	internal const string Ui = "shaders/engine/ui.slang";
	internal const string Background = "shaders/engine/background.slang";
	internal const string Glow = "shaders/engine/glow.slang";

	internal const string Velocity = "shaders/engine/velocity.slang";
	internal const string Compose = "shaders/engine/compose.slang";

	internal const string MeshCull = "shaders/engine/mesh_cull.slang";
	internal const string MeshRaster = "shaders/engine/mesh_raster.slang";
	internal const string MeshSkin = "shaders/engine/mesh_skin.slang";
	internal const string ClassicPose = "shaders/engine/classic_pose.slang";
	internal const string Particles = "shaders/engine/particles.slang";
	internal const string MeshShapes = "shaders/engine/mesh_shapes.slang";
	internal const string ShadingRate = "shaders/engine/shading_rate.slang";
	internal const string DepthPyramid = "shaders/engine/depth_pyramid.slang";
	internal const string LightCull = "shaders/engine/light_cull.slang";
	internal const string Fold = "shaders/engine/picture_fold.slang";
	internal const string Upscale = "shaders/engine/upscale.slang";
	internal const string Fsr3 = "shaders/engine/fsr3.slang";
	internal const string FrameGeneration = "shaders/engine/framegen.slang";
	internal const string BakeTrace = "shaders/engine/bake_trace.slang";
	internal const string BakeDenoise = "shaders/engine/bake_denoise.slang";
	internal const string RayBounce = "shaders/engine/ray_bounce.slang";
	internal const string RayGrid = "shaders/engine/ray_grid.slang";
	internal const string SkyLight = "shaders/engine/sky_light.slang";
	internal const string Light2D = "shaders/engine/light2d.slang";
	internal const string Light2DSprites = "shaders/engine/light2d_sprites.slang";
	internal const string Light2DCascades = "shaders/engine/light2d_cascades.slang";

	//The 2D light's jump flood, which stores two half floats where the cascades store four - see light2d_cascades.slang.
	internal const string Light2DSeeds = "#define LIGHT2D_SEEDS 1\n";

	//The 2D lights' shadow maps, which store one whole float - see light2d_cascades.slang.
	internal const string Light2DShadows = "#define LIGHT2D_SHADOWS 1\n";

	//The tile maps' variant of the 2D light's sprite pass, which steps their animation - see CoreLight2D.IsTiles.
	internal const string Light2DTiles = "#define LIGHT2D_TILE 1\n";

	//UI shader for world-space panels; reads the frame block so stereo passes get each eye's view
	internal const string UiWorld = "#define UI_WORLD 1\n";

	//The compose upscaling a world drawn smaller than the window.
	internal const string Carry = "#define CARRY 1\n";

	//The raster for clusters whose material cuts out.
	internal const string Alpha = "#define ALPHA 1\n";

	//The raster painting each cluster a colour over the world - mat_clusters.
	internal const string View = "#define VIEW 1\n";

	//The format of the picture a fold stores into, which the shader has to name - see CorePasses.Chain.
	internal static string FoldFormat( string storage ) => $"#define FOLD_FORMAT \"{storage}\"\n";

	//A fold keeping the least, or the most, of each four rather than their average - see CorePasses.FoldMode.
	internal static string FoldMode( CorePasses.FoldMode mode ) => mode switch
	{
		CorePasses.FoldMode.Least => "#define FOLD_MIN 1\n",
		CorePasses.FoldMode.Most => "#define FOLD_MAX 1\n",
		_ => "",
	};

	private static readonly (string, CoreShaderCompiler.Stage)[] Draws =
		[("MainVs", CoreShaderCompiler.Stage.Vertex), ("MainPs", CoreShaderCompiler.Stage.Pixel)];

	//And a posed thing's, out of its posed positions - see velocity.slang
	private static readonly (string, CoreShaderCompiler.Stage)[] VelocityDraws =
		[("MainVs", CoreShaderCompiler.Stage.Vertex), ("PosedVs", CoreShaderCompiler.Stage.Vertex), ("MainPs", CoreShaderCompiler.Stage.Pixel)];

	//Whether the portable frame has a pass - see All, which says it of each of the engine's
	//own. A file that is not one of those is somebody else's to answer for, and is let through.
	internal static bool InPortableFrame( string file )
	{
		var listed = false;

		foreach ( var (known, _, _, everywhere) in All )
		{
			if ( !string.Equals( known, file, StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( everywhere ) return true;

			listed = true;
		}

		return !listed;
	}

	private static readonly (string, CoreShaderCompiler.Stage)[] Runs =
		[("main", CoreShaderCompiler.Stage.Compute)];

	//Every pass variant a bake compiles; Everywhere means the portable frame has it too
	internal static IReadOnlyList<(string File, string Defines, (string Entry, CoreShaderCompiler.Stage Stage)[] Stages, bool Everywhere)> All =>
	[
		( Line, "", Draws, true ),
		//The grid is an overlay like the others, so it is built for the portable frame too
		( SurfaceGrid, "", Draws, true ),
		( Gizmo, "", Draws, true ),
		( Billboard, "", Draws, true ),
		( Ui, "", Draws, true ),
		//A panel in the world and a headset's sheets - see uiWorldPipeline; without it a page drew no world
		//panels and no ScreenPanel in a browser headset
		( Ui, UiWorld, Draws, true ),
		( Background, "", Draws, true ),
		//The portable frame's bloom, in pixel passes - see CorePortableGlow
		( Glow, "", GlowDraws, true ),
		( Velocity, "", VelocityDraws, false ),
		( Compose, "", Draws, false ),
		( Compose, Carry, Draws, false ),
		( MeshRaster, "", Draws, false ),
		( MeshRaster, Alpha, Draws, false ),
		( MeshRaster, View, Draws, false ),
		( MeshCull, "", Runs, false ),
		( MeshSkin, "", Runs, false ),
		//The portable frame's on Vulkan; a page computes nothing - see BakePasses
		( ClassicPose, "", Runs, true ),
		( Particles, "", ParticleRuns, false ),
		( MeshShapes, "", Runs, false ),
		( ShadingRate, "", Runs, false ),
		( DepthPyramid, "", Runs, false ),
		( LightCull, "", Runs, false ),
		( Fold, FoldFormat( "r11f_g11f_b10f" ), Runs, false ),
		( Fold, FoldFormat( "rgba16f" ), Runs, false ),
		( Fold, FoldFormat( "rgba8" ), Runs, false ),
		//A depth's chains of nearest and farthest - see CorePasses.FoldMode.
		( Fold, FoldFormat( "r32f" ) + FoldMode( CorePasses.FoldMode.Least ), Runs, false ),
		( Fold, FoldFormat( "r32f" ) + FoldMode( CorePasses.FoldMode.Most ), Runs, false ),
		//The upscalers' passes - see CoreUpscaler; full frame only, as they need the world's picture and compute
		( Upscale, "", UpscaleRuns, false ),
		( Upscale, CoreUpscaler.MotionDefines, MotionRuns, false ),
		( Upscale, CoreUpscaler.SingleDefines, ReactiveRuns, false ),
		//And FSR 3's, one build a pass - see CoreFsr3.
		.. Fsr3Builds,
		.. FrameGenerationBuilds,
		//The baked lighting's rays - see BakeGpu. The editor's, and a full frame's.
		( BakeTrace, "", BakeRuns, false ),
		( BakeTrace, Rinten.Bake.BakeGpu.RaysDefines, BakeRuns, false ),
		( BakeDenoise, "", Runs, false ),
		//The probes' bounce through hardware rays - see CoreLiveBounce. Built everywhere, run where the card has rays.
		( RayBounce, "", Runs, false ),
		//The probe grid of a scene with no bake - see CoreProbeGrid
		( RayGrid, "", Runs, false ),
		//The sky light's filters - see CoreSkyLight. Full frame only: they draw into pictures
		( SkyLight, "", SkyLightDraws, false ),
		//The 2D lighting's debug view, its scene and its passes - see CoreLight2D. Full frame only
		( Light2D, "", Light2DDraws, false ),
		( Light2DSprites, "", Draws, false ),
		( Light2DSprites, Light2DTiles, Draws, false ),
		( Light2DCascades, Light2DSeeds, Light2DFloodRuns, false ),
		( Light2DCascades, Light2DShadows, Light2DShadowRuns, false ),
		( Light2DCascades, "", Light2DCascadeRuns, false ),
	];

	private static readonly (string, CoreShaderCompiler.Stage)[] Light2DFloodRuns =
		[("JumpCs", CoreShaderCompiler.Stage.Compute)];

	private static readonly (string, CoreShaderCompiler.Stage)[] Light2DShadowRuns = [("ShadowsCs", CoreShaderCompiler.Stage.Compute)];

	private static readonly (string, CoreShaderCompiler.Stage)[] Light2DCascadeRuns =
		[("CascadeCs", CoreShaderCompiler.Stage.Compute), ("IntegrateCs", CoreShaderCompiler.Stage.Compute)];

	private static readonly (string, CoreShaderCompiler.Stage)[] Light2DDraws =
	[
		("DebugVs", CoreShaderCompiler.Stage.Vertex),
		("DebugPs", CoreShaderCompiler.Stage.Pixel),
	];

	private static readonly (string, CoreShaderCompiler.Stage)[] SkyLightDraws =
	[
		("MainVs", CoreShaderCompiler.Stage.Vertex),
		("MainPrefilterPs", CoreShaderCompiler.Stage.Pixel),
		("MainIrradiancePs", CoreShaderCompiler.Stage.Pixel),
	];

	private static readonly (string, CoreShaderCompiler.Stage)[] BakeRuns =
	[
		("main", CoreShaderCompiler.Stage.Compute),
		("ProbeCs", CoreShaderCompiler.Stage.Compute),
	];

	private static IEnumerable<(string, string, (string, CoreShaderCompiler.Stage)[], bool)> Fsr3Builds
		=> CoreFsr3.Defines.Select( defines => ( Fsr3, defines, Runs, false ) );

	private static IEnumerable<(string, string, (string, CoreShaderCompiler.Stage)[], bool)> FrameGenerationBuilds
		=> CoreFrameGeneration.Defines.Select( defines => ( FrameGeneration, defines, Runs, false ) );

	private static readonly (string, CoreShaderCompiler.Stage)[] UpscaleRuns =
	[
		("TemporalCs", CoreShaderCompiler.Stage.Compute),
		("EasuCs", CoreShaderCompiler.Stage.Compute),
		("CopyCs", CoreShaderCompiler.Stage.Compute),
	];

	private static readonly (string, CoreShaderCompiler.Stage)[] GlowDraws =
	[
		("MainVs", CoreShaderCompiler.Stage.Vertex),
		("BrightPs", CoreShaderCompiler.Stage.Pixel),
		("DownPs", CoreShaderCompiler.Stage.Pixel),
		("UpPs", CoreShaderCompiler.Stage.Pixel),
		("ComposePs", CoreShaderCompiler.Stage.Pixel),
		("FinishPs", CoreShaderCompiler.Stage.Pixel),
	];

	private static readonly (string, CoreShaderCompiler.Stage)[] ParticleRuns =
		[("main", CoreShaderCompiler.Stage.Compute), ("SpawnCs", CoreShaderCompiler.Stage.Compute), ("SortCs", CoreShaderCompiler.Stage.Compute)];

	private static readonly (string, CoreShaderCompiler.Stage)[] MotionRuns =
		[("MotionCs", CoreShaderCompiler.Stage.Compute)];

	private static readonly (string, CoreShaderCompiler.Stage)[] ReactiveRuns =
		[("ReactiveCs", CoreShaderCompiler.Stage.Compute)];
}

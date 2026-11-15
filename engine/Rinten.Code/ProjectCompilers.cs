using Rinten.Engine;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Rinten;

//A project's compilers - its game code's and its editor code's - and the code that makes, fills and reads them.
//See ProjectCompiling, which the engine reaches them through, and CodeCompiling for the group they build in.
internal sealed class ProjectCompilers : ProjectCode
{
	private readonly Project project;

	internal Compiler Game;
	internal Compiler Editor;

	private int lastHash;

	internal override bool HasCompiler => Game is not null || Editor is not null;

	internal override void NotifyFastHotload( bool editorCode, Version version )
		=> ( editorCode ? Editor : Game )?.NotifyFastHotload( version );

	//A project's compilers, where it has any made.
	internal static ProjectCompilers Of( Project project ) => project.code as ProjectCompilers;

	//These package types should reference package.base
	private static HashSet<string> BaseReferencingTypes { get; } = new HashSet<string> { "game", "addon", "library", "tool" };

	//Cache compiled built-in assemblies in the editor; EngineFingerprint guards against a stale engine
	private bool CacheAssemblies => project.IsBuiltIn && Application.IsEditor;

	//What the engine assemblies the built-in projects compile against contain, so a cache is only refused when one of
	//them actually changed. Their timestamps move on every build - a no-op build rewrites Rinten.Engine.dll byte for
	//byte - so a time cannot answer this without discarding every built-in project's cache, and the fifteen seconds
	//recompiling them costs on the next start.
	internal static string EngineFingerprint => engineFingerprint ??= Fingerprint();

	private static string engineFingerprint;

	private static string Fingerprint()
	{
		try
		{
			var beside = Path.GetDirectoryName( typeof( Project ).Assembly.Location );

			if ( string.IsNullOrEmpty( beside ) || !Directory.Exists( beside ) )
				return Guid.NewGuid().ToString( "N" );

			var names = Directory.EnumerateFiles( beside, "*.dll" )
				.Select( Path.GetFileName )
				.Where( IsEngineAssembly )
				.OrderBy( x => x, StringComparer.OrdinalIgnoreCase )
				.ToArray();

			using var hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
			var buffer = new byte[1 << 16];

			foreach ( var name in names )
			{
				hash.AppendData( Encoding.UTF8.GetBytes( name ) );

				//Read-write behind a share that lets the editor keep the assembly open: it is loaded from this folder.
				using var stream = new FileStream( Path.Combine( beside, name ), FileMode.Open, FileAccess.Read,
					FileShare.ReadWrite | FileShare.Delete );

				int read;
				while ( ( read = stream.Read( buffer, 0, buffer.Length ) ) > 0 )
					hash.AppendData( buffer, 0, read );
			}

			return Convert.ToHexString( hash.GetHashAndReset() );
		}
		catch
		{
			//Nothing to read means nothing can be trusted: a cache that cannot be checked is a cache that is not used.
			//Unique per process, so it can never match what a previous one wrote into a cached assembly.
			return Guid.NewGuid().ToString( "N" );
		}
	}

	//The assemblies a built-in project compiles against. The engine's own, and the few it hands to a tool project
	//directly - Roslyn, Skia, ActionGraphs and the components its generated code imports. The host's own assemblies are
	//left out: nothing a built-in project compiles against lives in them, and a build rewrites them whether or not their
	//code changed, which would move this fingerprint and throw the caches away for no reason.
	private static bool IsEngineAssembly( string name )
	{
		if ( name.Equals( "Rinten.AppSystem.dll", StringComparison.OrdinalIgnoreCase )
			|| name.Equals( "Rinten.CodeUpgrader.dll", StringComparison.OrdinalIgnoreCase )
			|| name.Equals( "Rinten.NetCore.dll", StringComparison.OrdinalIgnoreCase ) )
			return false;

		return name.StartsWith( "Rinten.", StringComparison.OrdinalIgnoreCase )
			|| name.StartsWith( "Microsoft.AspNetCore.Components", StringComparison.OrdinalIgnoreCase )
			|| name.StartsWith( "Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase )
			|| name.Equals( "SkiaSharp.dll", StringComparison.OrdinalIgnoreCase )
			|| name.Equals( "Facepunch.ActionGraphs.dll", StringComparison.OrdinalIgnoreCase );
	}

	internal ProjectCompilers( Project project )
	{
		this.project = project;
	}

	//anything that means we need to re-create the compiler and recompile should be here
	private int Hash => HashCode.Combine( project.Active, Project.Current == project, Json.SerializeAsObject( project.Config.GetCompileSettings() ).ToJsonString(), project.Config.Org, project.Config.Ident, project.Config.Type, string.Join( ";", project.PackageReferences() ) );

	internal override void Forget()
	{
		Game?.Dispose();
		Game = null;

		Editor?.Dispose();
		Editor = null;

		lastHash = default;
	}

	//Makes, remakes or drops this project's compilers to match its config and its code
	//- see Project.UpdateCompiler , which decides whether there is anything here to do.
	internal override void Update()
	{
		//Only update the compiler if the config has changed
		var hash = Hash;
		if ( hash == lastHash ) return;
		lastHash = hash;

		Game?.Dispose();
		Game = null;

		if ( !project.Active )
			return;

		if ( project.HasCodePath() )
		{
			var compilerSettings = project.Config.GetCompileSettings();

			if ( project.Config.Type == "tool" )
			{
				//Allow unsafe - but only in tools projects
				compilerSettings.Unsafe = true;
			}

			//Anything with code can be serverside
			if ( Application.IsDedicatedServer )
			{
				var defines = compilerSettings.GetPreprocessorSymbols();
				if ( !defines.Contains( "SERVER" ) )
				{
					compilerSettings.DefineConstants += ";SERVER";
				}
			}

			if ( project.Config.Type == "game" )
			{
				compilerSettings.IgnoreFolders.Add( "editor" );
				compilerSettings.IgnoreFolders.Add( "unittest" );
			}

			var compilerName = $"{project.Config.Org}.{project.Config.Ident}".Trim( '.' );
			var codePath = project.GetCodePath();

			if ( compilerName == "local.base" ) compilerName = "base";
			if ( compilerName == "local.toolbase" ) compilerName = "toolbase";

			Log.Trace( $"Create Compiler `{compilerName}`" );

			Game = CodeCompiling.Group.CreateCompiler( compilerName, codePath, compilerSettings );

			if ( BaseReferencingTypes.Contains( project.Config.Type ) && compilerName != "base" )
			{
				Game.AddBaseReference();
			}

			Game.GeneratedCode.AppendLine( $"global using Microsoft.AspNetCore.Components;" );
			Game.GeneratedCode.AppendLine( $"global using Microsoft.AspNetCore.Components.Rendering;" );
			Game.GeneratedCode.AppendLine( $"global using static Rinten.Internal.GlobalGameNamespace;" );

			if ( project.Config.Type == "tool" )
			{
				Game.GeneratedCode.AppendLine( $"global using static Rinten.Internal.GlobalToolsNamespace;" );
				Game.AddReference( "Rinten.Tools" );
				Game.AddReference( "Rinten.Compiling" );
				Game.AddReference( "System.Diagnostics.Process" );
				Game.AddReference( "System.Net.WebSockets" );
				Game.AddReference( "System.Net.WebSockets.Client" );
				Game.AddReference( "Microsoft.Win32.Registry" );
				Game.AddReference( "System.Memory" );
				Game.AddReference( "Rinten.Bind" );
				Game.AddReference( "Facepunch.ActionGraphs" );
				Game.AddReference( "SkiaSharp" );
				Game.AddReference( "Microsoft.CodeAnalysis" );
				Game.AddReference( "Microsoft.CodeAnalysis.CSharp" );

				if ( compilerName != "toolbase" )
				{
					Game.AddToolBaseReference();
					//await Project.WaitFor( "toolbase" );
				}
			}

			Game.GeneratedCode.AppendLine( $"[assembly: global::System.Reflection.AssemblyMetadata( \"Ident\", {project.Config.FullIdent.QuoteSafe()} )]" );
			Game.GeneratedCode.AppendLine( $"[assembly: global::System.Reflection.AssemblyMetadata( \"EngineVersion\", {Engine.Protocol.Api.ToString().QuoteSafe()} )]" );
			Game.GeneratedCode.AppendLine( $"[assembly: global::System.Reflection.AssemblyMetadata( \"EngineMinorVersion\", {1.ToString().QuoteSafe()} )]" );
			Game.GeneratedCode.AppendLine( $"[assembly: global::System.Reflection.AssemblyMetadata( \"EngineFingerprint\", {EngineFingerprint.QuoteSafe()} )]" );

			foreach ( var reference in project.PackageReferences() )
			{
				Game.AddReference( reference );
			}

			foreach ( var reference in compilerSettings.DistinctAssemblyReferences )
			{
				Game.AddReference( reference );
			}

			//If we have a parent package, set up the reference provider so the compiler can resolve the parent
			//package's types. The addon's own ActivePackage Lookup() will find the parent in the other active packages.
			if ( project.Config.Type == "addon" )
			{
				var parentPackage = project.Config.GetMetaOrDefault<string>( "ParentPackage", null );
				if ( !string.IsNullOrWhiteSpace( parentPackage ) && Package.TryParseIdent( parentPackage, out var parentParts ) )
				{
					var currentAp = PackageManager.Find( project.Config.FullIdent, true, false );
					if ( currentAp is not null )
					{
						CodeCompiling.Group.ReferenceProvider = new ReferenceLookup( PackageCode.ReferenceFinder( currentAp ) );
						Game.AddReference( $"package.{parentParts.org}.{parentParts.package}" );
					}
				}
			}

			Game.WatchForChanges();
		}

		if ( Application.IsEditor )
		{
			//update editor compiler if we're running in the editor

			if ( project.Config.Type == "game" || project.Config.Type == "library" || project.Config.Type == "addon" )
			{
				UpdateEditor();
			}
		}

		if ( CacheAssemblies )
		{
			//see if we've got cached assemblies from a previous compile
			foreach ( var compiler in new[] { Game, Editor } )
			{
				if ( compiler is null ) continue;

				LoadCachedAssembly( compiler );

				if ( compiler.BuildSuccess )
					compiler.Group.ClearForRecompile( compiler );
			}
		}
	}

	private bool LoadCachedAssembly( Compiler compiler )
	{
		try
		{
			string directory = Path.Combine( project.GetRootPath(), Project.CacheFolder, "bin" );
			string binPath = Path.Combine( directory, $"{compiler.AssemblyName}.dll" );
			if ( !File.Exists( binPath ) )
				return false;

			byte[] bytes = File.ReadAllBytes( binPath );
			if ( bytes is null )
				return false;

			var attrs = AssemblyMetadata.GetCustomAttributes( bytes );
			var metadata = attrs
				.Where( a => a.AttributeFullName == "System.Reflection.AssemblyMetadataAttribute" )
				.Where( a => a.Arguments.Length == 2 )
				.ToDictionary(
					a => (string)a.Arguments[0],
					a => (string)a.Arguments[1]
				);

			//Check Fingerprint Can only be reused if it's from the same project and engine version, otherwise we'll need a full recompile.
			if ( metadata.TryGetValue( "Ident", out var ident ) == false || ident != project.Config.FullIdent )
				return false;

			if ( metadata.TryGetValue( "EngineVersion", out var ver ) == false || ver != Rinten.Engine.Protocol.Api.ToString() )
				return false;

			if ( metadata.TryGetValue( "CompileTime", out var timeStr ) == false ||
				!DateTime.TryParse( timeStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var compileTime ) )
				return false;

			//And against the engine it was compiled with, which in a build nobody shipped moves without the version moving. Code
			//compiled against a signature that has since changed loads without a complaint and throws somewhere else entirely.
			//By content, not by timestamp: a build that wrote the same assemblies again changes nothing that matters here.
			if ( metadata.TryGetValue( "EngineFingerprint", out var fingerprint ) == false || fingerprint != EngineFingerprint )
			{
				Log.Info( $"{compiler.Name}: the engine assemblies changed after the cached assembly. Forcing recompile." );
				return false;
			}

			//Finally, check if it's out of date. If any source files have been modified since the assembly was compiled, we can't use it.
			foreach ( var fs in compiler.SourceLocations )
			{
				var files = fs.FindFile( "/", "*.*", true );
				foreach ( var file in files )
				{
					string ext = Path.GetExtension( file );
					if ( ext != ".cs" && ext != ".razor" )
						continue; //only care about source files

					string path = fs.GetFullPath( file );
					if ( File.GetLastWriteTimeUtc( path ) > compileTime )
					{
						Log.Info( $"{compiler.Name}: Source file {file} was modified after the cached assembly was compiled. Forcing recompile." );
						return false;
					}

					if ( File.GetCreationTimeUtc( path ) > compileTime )
					{
						Log.Info( $"{compiler.Name}: Source file {file} was added after the cached assembly was compiled. Forcing recompile." );
						return false;
					}
				}
			}

			project.AssemblyFileSystem.CreateDirectory( "/.bin" );

			if ( project.Config.FullIdent == "local.base" )
			{
				//package.base gets sent to clients, so we need to have a .cll
				string cllPath = Path.Combine( directory, $"{compiler.AssemblyName}.cll" );
				if ( !File.Exists( cllPath ) )
					return false;

				byte[] cllBytes = File.ReadAllBytes( cllPath );
				if ( cllBytes is null )
					return false;

				project.AssemblyFileSystem.WriteAllBytes( $"/.bin/{compiler.AssemblyName}.cll", cllBytes );
			}

			//All good, swap in the assembly
			compiler.UpdateFromAssembly( bytes );
			project.AssemblyFileSystem.WriteAllBytes( $"/.bin/{compiler.AssemblyName}.dll", bytes );

			return true;
		}
		catch
		{
			return false;
		}
	}

//If required, create the editor compiler
	private void UpdateEditor()
	{
		Editor?.Dispose();
		Editor = null;

		if ( !project.Active )
			return;

		//Only want menu editor code if we're in the menu project. Current is set before the project is loaded the
		//second time, and whether this is it is part of the hash - so that load builds the compiler this one skipped.
		if ( project.Config.Ident == "menu" && Project.Current != project )
			return;

		if ( !project.HasEditorPath() )
			return;

		var compilerSettings = project.Config.GetCompileSettings();
		var compilerName = $"{project.Config.Org}.{project.Config.Ident}".Trim( '.' ) + ".editor";

		compilerSettings.Unsafe = true;

		Log.Trace( $"Create Editor Compiler `{compilerName}`" );

		Editor = CodeCompiling.Group.CreateCompiler( compilerName, project.GetEditorPath(), compilerSettings );

		if ( Game is not null )
		{
			//reference the main code assembly
			Editor.AddReference( Game );
		}

		Editor.AddBaseReference();
		Editor.AddToolBaseReference();

		Editor.AddReference( "Rinten.Tools" );
		Editor.AddReference( "Rinten.Compiling" );
		Editor.AddReference( "System.Diagnostics.Process" );
		Editor.AddReference( "System.Net.WebSockets" );
		Editor.AddReference( "System.Net.WebSockets.Client" );
		Editor.AddReference( "Microsoft.Win32.Registry" );
		Editor.AddReference( "System.Memory" );
		Editor.AddReference( "Rinten.Bind" );
		Editor.AddReference( "Facepunch.ActionGraphs" );
		Editor.AddReference( "SkiaSharp" );
		Editor.AddReference( "Microsoft.CodeAnalysis" );
		Editor.AddReference( "Microsoft.CodeAnalysis.CSharp" );

		Editor.GeneratedCode.AppendLine( $"global using static Rinten.Internal.GlobalToolsNamespace;" );
		Editor.GeneratedCode.AppendLine( $"global using static Rinten.Internal.GlobalGameNamespace;" );
		Editor.GeneratedCode.AppendLine( $"[assembly: global::System.Reflection.AssemblyMetadata( \"EngineFingerprint\", {EngineFingerprint.QuoteSafe()} )]" );

		foreach ( var reference in project.PackageReferences() )
		{
			Editor.AddReference( reference );
		}

		if ( (project.Config.Type == "game" || project.Config.Type == "addon") && !project.IsBuiltIn )
		{
			//editor libraries
			foreach ( var library in Project.Libraries.Where( x => x.HasEditorPath() ) )
			{
				Editor.AddEditorReference( library.Package );
			}
		}

		//Only tools that ship. A reference to a package that is not there is not a missing tool - it is a broken reference,
		//which fails the editor compiler, which fails every project depending on it, which is a game that will not open at all.
		Editor.AddReference( "package.local.actiongraph" );
		Editor.AddReference( "package.local.moviemaker" );

		foreach ( var reference in compilerSettings.DistinctAssemblyReferences )
		{
			Editor.AddReference( reference );
		}

		Editor.WatchForChanges();
	}
}

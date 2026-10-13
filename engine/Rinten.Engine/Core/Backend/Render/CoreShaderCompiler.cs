using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Rinten.Native.Slang;

namespace Rinten;

//Slang shader compiler (SPIR-V/WGSL out); loaded by name on first use, and its absence is not fatal
internal static unsafe partial class CoreShaderCompiler
{
	/// <summary>Which stage of the pipeline an entry point is.</summary>
	public enum Stage
	{
		Vertex = 0,
		Pixel = 1,
		Compute = 2,

		/// <summary>A mesh stage, where the device has them - see VkDevice.meshShaders.</summary>
		Mesh = 3,
	}

	//The numbers are in the shader cache's keys, so they are not renumbered.
	/// <summary>Which language a compiled shader is in, and which frame it was written for - see <see cref="RenderTier"/>.</summary>
	public enum Format
	{
		/// <summary>SPIR-V 1.5 for the full frame: Vulkan 1.3.</summary>
		Spirv = 0,

		/// <summary>WGSL for the portable frame: a browser.</summary>
		Wgsl = 1,

		/// <summary>SPIR-V 1.3 for the portable frame: Vulkan from 1.1, which takes no SPIR-V newer than that and has no table of pictures to index.</summary>
		SpirvPortable = 2,

		/// <summary>GLSL ES 3.00 for the portable frame: a browser with WebGL2 and no WebGPU. The portable SPIR-V, written out again - see <see cref="Glsl"/>.</summary>
		Glsl = 3,

		//A second run of every stage rather than a switch inside one, because a program written for two views cannot be drawn into a framebuffer that has one: the page holds both and picks when a session opens. See Glsl and Eye() in frame_data.slang.
		/// <summary>The same GLSL, for both of a headset's eyes in one pass: <c>OVR_multiview2</c>, two views, and the frame block an array of two.</summary>
		GlslStereo = 4,
	}

	/// <summary>Whether a format is one of those the portable frame is compiled to.</summary>
	public static bool IsPortable( Format format ) => format is Format.Wgsl or Format.SpirvPortable or Format.Glsl or Format.GlslStereo;

	/// <summary>Whether a format is GLSL, of either kind.</summary>
	public static bool IsGlsl( Format format ) => format is Format.Glsl or Format.GlslStereo;

	/// <summary>Which rewrite of Slang's output a format's words are - see Wgsl and Glsl. In the shader cache's keys, so raised whenever either rewrite changes what it writes: a cache made before is then missed rather than handed back. SPIR-V is Slang's own and has none.</summary>
	public static int Revision( Format format ) => format switch
	{
		//4 / 3: compiled with SPECIALIZED - WGSL `override`s and SPIRV-Cross's SPIRV_CROSS_CONSTANT_ID macros where they were plain constants.
		Format.Wgsl => 4,
		Format.Glsl => 3,
		Format.GlslStereo => 3,
		_ => 0,
	};

	/// <summary>Whether a format can bake frame.slang's specialization constants (SPIR-V, WGSL, GLSL macros)</summary>
	public static bool HasSpecialization( Format format )
		=> IsSpirv( format ) || format is Format.Wgsl || IsGlsl( format );

	/// <summary>Whether a format is SPIR-V, whichever frame it is for.</summary>
	public static bool IsSpirv( Format format ) => format is Format.Spirv or Format.SpirvPortable;

	//A loaded Slang, or the reason there is none.
	private sealed class Compiler
	{
		public bool Available;
		public string Info = "not loaded";
		public IGlobalSession* Session;
	}

	//One compiler per thread: Slang's global session is not thread safe
	[ThreadStatic]
	private static Compiler compiler;

	//Why the last compile on this thread failed, or why the compiler is missing; empty if it did not fail
	[ThreadStatic]
	private static string lastError;

	//Folders an include is looked for in besides the shader's own. The engine's own shader library, and the shader folder of every open project.
	//Shared rather than per-thread - it is the same list for everybody - and so it has a lock of its own, which the rest of this class does not need.
	private static readonly List<string> extraSearchPaths = new();
	private static readonly object searchLock = new();

	/// <summary>Whether the shader compiler is available at all.</summary>
	public static bool Available => Load().Available;

	/// <summary>What the compiler says about itself, or why it is not usable.</summary>
	public static string Info => Load().Info;

	/// <summary>Why the last compile on this thread failed. Empty if it did not.</summary>
	public static string LastError => lastError ?? string.Empty;

	/// <summary>Another folder to look in when a shader includes something.</summary>
	public static void AddSearchPath( string path, bool first = false )
	{
		if ( string.IsNullOrEmpty( path ) ) return;

		lock ( searchLock )
		{
			//Said twice is said once: the engine adds the same folders again whenever a project
			//is opened, and a session with one path in it four times searches it four times.
			if ( extraSearchPaths.Contains( path ) ) return;

			//First is ahead of everything said so far - a project's folder, so a file of its own is found before the engine's of the same name.
			if ( first ) extraSearchPaths.Insert( 0, path );
			else extraSearchPaths.Add( path );
		}
	}

	/// <summary>Forgets them all. For a host that opens a different set of projects without starting again.</summary>
	public static void ClearSearchPaths()
	{
		lock ( searchLock )
		{
			extraSearchPaths.Clear();
		}
	}

	//A copy of the search paths, taken while the lock is held - and what CoreResources.Mentions follows an include through.
	internal static string[] SearchPaths()
	{
		lock ( searchLock )
		{
			return extraSearchPaths.ToArray();
		}
	}

	/// <summary>Compile one entry point of a Slang source file.</summary>
	public static byte[] Compile( string path, string entryPoint, Stage stage, Format format = Format.Spirv )
	{
		lastError = string.Empty;

		if ( path is null || entryPoint is null )
		{
			lastError = "no path or entry point";
			return null;
		}

		if ( !Enum.IsDefined( format ) )
		{
			lastError = "unknown shader format";
			return null;
		}

		byte[] source;

		try
		{
			source = SealedFile.ReadAllBytes( path );
		}
		catch ( Exception )
		{
			lastError = $"cannot open {path}";
			return null;
		}

		return CompileSource( source, DirectoryOf( path ), path, entryPoint, stage, format );
	}

	/// <summary>The same, from source text that was never on the disk.</summary>
	public static byte[] CompileSource( string source, string directory, string entryPoint, Stage stage,
		Format format = Format.Spirv )
	{
		lastError = string.Empty;

		if ( source is null || entryPoint is null )
		{
			lastError = "no source or entry point";
			return null;
		}

		if ( !Enum.IsDefined( format ) )
		{
			lastError = "unknown shader format";
			return null;
		}

		var where = string.IsNullOrEmpty( directory ) ? "." : directory;

		return CompileSource( Encoding.UTF8.GetBytes( source ), where, "shader", entryPoint, stage, format );
	}

	//Find Slang and make a global session with it.
	private static Compiler MakeCompiler()
	{
		var made = new Compiler();

		//The layout of the two descriptors is the C++ header's, written out by hand; a mismatch here is a
		//session made from garbage, and worth refusing over the way a wrong API version is refused inside Slang.
		if ( sizeof( SessionDesc ) != 96 || sizeof( TargetDesc ) != 48 )
		{
			made.Info = "the Slang descriptors do not match the header";
			return made;
		}

		IGlobalSession* raw = null;

		try
		{
			//The API version is checked inside: a Slang too old or too new for these bindings refuses here rather than crashing later on a changed vtable.
			if ( Slang.SLANG_FAILED( Slang.slang_createGlobalSession( Slang.SLANG_API_VERSION, &raw ) ) || raw is null )
			{
				made.Info = "slang refused a session - the binary and these bindings disagree";
				return made;
			}
		}
		catch ( DllNotFoundException )
		{
			//Beside the engine's other native libraries, which is where the loader is already looking by the time anything
			//asks for a shader - or not there at all: Android has no Slang build, and the browser loads nothing by name.
			made.Info = $"{LibraryFileName} could not be loaded";
			return made;
		}
		catch ( EntryPointNotFoundException )
		{
			made.Info = $"{LibraryFileName} has no slang_createGlobalSession";
			return made;
		}

		made.Session = raw;
		made.Available = true;
		made.Info = "slang " + Marshal.PtrToStringUTF8( (IntPtr)raw->getBuildTagString() );

		return made;
	}

	private static string LibraryFileName
		=> OperatingSystem.IsWindows() ? "slang.dll" : OperatingSystem.IsMacOS() ? "libslang.dylib" : "libslang.so";

	//Turned off where RINTEN_NO_SHADER_COMPILER is set, which is a shipped game without libslang: every stage then
	//comes out of the baked cache or not at all, and whatever falls back to what was baked can be seen doing so.
	private static Compiler Load() => compiler ??=
		string.IsNullOrEmpty( Environment.GetEnvironmentVariable( "RINTEN_NO_SHADER_COMPILER" ) )
			? MakeCompiler()
			: new Compiler { Info = "turned off by RINTEN_NO_SHADER_COMPILER" };

	//Lets this thread's session go - a thread that compiles no more, see CoreShaderThreads. The next compile on it would make another.
	internal static void ReleaseThread()
	{
		if ( compiler is { Session: not null } made ) made.Session->release();
		compiler = null;
	}

	//Says why a compile failed on this thread when it failed on another - see CoreShaderFile.Compile.
	internal static void Failed( string why ) => lastError = why;

	private static SlangStage ToSlangStage( Stage stage ) => stage switch
	{
		Stage.Vertex => SlangStage.SLANG_STAGE_VERTEX,
		Stage.Pixel => SlangStage.SLANG_STAGE_FRAGMENT,
		Stage.Compute => SlangStage.SLANG_STAGE_COMPUTE,
		Stage.Mesh => SlangStage.SLANG_STAGE_MESH,
		_ => SlangStage.SLANG_STAGE_NONE,
	};

	//Pull the message out of a diagnostics blob, if there is one.
	private static void RecordDiagnostics( ISlangBlob* diagnostics, string fallback )
	{
		if ( diagnostics is not null && diagnostics->getBufferPointer() is not null )
		{
			var message = Encoding.UTF8.GetString( (byte*)diagnostics->getBufferPointer(), (int)diagnostics->getBufferSize() );

			//Slang's messages are newline-terminated; the caller is putting this on one line of a log.
			lastError = message.TrimEnd( '\n', '\r' );
			return;
		}

		lastError = fallback;
	}

	//Native UTF-8 strings, freed together when the compile is done.
	private ref struct Strings
	{
		private List<IntPtr> allocated;

		public byte* Make( string text )
		{
			var pointer = Marshal.StringToCoTaskMemUTF8( text );
			( allocated ??= new List<IntPtr>() ).Add( pointer );
			return (byte*)pointer;
		}

		/// <summary>Bytes as read from the file, with the terminator Slang expects.</summary>
		public byte* Make( byte[] bytes )
		{
			var pointer = Marshal.AllocCoTaskMem( bytes.Length + 1 );
			Marshal.Copy( bytes, 0, pointer, bytes.Length );
			((byte*)pointer)[bytes.Length] = 0;
			( allocated ??= new List<IntPtr>() ).Add( pointer );
			return (byte*)pointer;
		}

		public void Dispose()
		{
			if ( allocated is null ) return;

			foreach ( var pointer in allocated ) Marshal.FreeCoTaskMem( pointer );
			allocated = null;
		}
	}

	//Everything both entry points do, once the source is in hand.
	private static byte[] CompileSource( byte[] source, string directory, string name, string entryPoint,
		Stage stage, Format format )
	{
		var c = Load();
		if ( !c.Available )
		{
			lastError = c.Info;
			return null;
		}

		var slangStage = ToSlangStage( stage );
		if ( slangStage == SlangStage.SLANG_STAGE_NONE )
		{
			lastError = "unknown shader stage";
			return null;
		}

		using var strings = new Strings();

		var target = TargetDesc.Default;
		target.format = format == Format.Wgsl ? SlangCompileTarget.SLANG_WGSL : SlangCompileTarget.SLANG_SPIRV;

		//SPIR-V 1.5 is what a Vulkan 1.3 driver takes, and 1.3 is as far as a Vulkan 1.1 driver goes - and what
		//GLSL is written from. WGSL has no equivalent profile; Slang picks the right one from the target format.
		if ( format != Format.Wgsl )
			target.profile = c.Session->findProfile( strings.Make( format == Format.Spirv ? "spirv_1_5" : "spirv_1_3" ) );

		target.flags = 0;

		//Include lookup: the shader's own folder first, then every folder from AddSearchPath
		var extras = SearchPaths();
		var searchPaths = stackalloc byte*[extras.Length + 1];

		searchPaths[0] = strings.Make( directory );
		for ( var i = 0; i < extras.Length; i++ )
			searchPaths[i + 1] = strings.Make( extras[i] );

		//PORTABLE is the frame the shader is for - see RenderTier - and is what a shader asks about. WEBGPU and WEBGL are only ever the language,
		//for the rare thing it cannot say: WebGL has no storage buffers, so the frame block is a uniform block there - see frame_data.slang.
		var language = format switch
		{
			Format.Wgsl => "WEBGPU",
			Format.Glsl or Format.GlslStereo => "WEBGL",
			_ => null,
		};

		//And whether this run is the one that draws both of a headset's eyes at once - see Format.GlslStereo and Eye() in frame_data.slang.
		var stereo = format == Format.GlslStereo;

		var macros = stackalloc PreprocessorMacroDesc[3];
		macros[0] = new PreprocessorMacroDesc { name = strings.Make( "PORTABLE" ), value = strings.Make( "1" ) };

		if ( language is not null )
			macros[1] = new PreprocessorMacroDesc { name = strings.Make( language ), value = strings.Make( "1" ) };

		if ( stereo )
			macros[2] = new PreprocessorMacroDesc { name = strings.Make( "STEREO" ), value = strings.Make( "1" ) };

		var option = new CompilerOptionEntry
		{
			name = CompilerOptionName.VulkanUseEntryPointName,
			value = new CompilerOptionValue { kind = CompilerOptionValueKind.Int, intValue0 = 1 },
		};

		var sessionDesc = SessionDesc.Default;
		sessionDesc.targets = &target;
		sessionDesc.targetCount = 1;
		sessionDesc.searchPaths = searchPaths;
		sessionDesc.searchPathCount = extras.Length + 1;

		if ( IsPortable( format ) )
		{
			sessionDesc.preprocessorMacros = macros;
			sessionDesc.preprocessorMacroCount = ( language is not null ? 2 : 1 ) + ( stereo ? 1 : 0 );
		}

		if ( format == Format.Wgsl )
		{
			sessionDesc.compilerOptionEntries = &option;
			sessionDesc.compilerOptionEntryCount = 1;
		}

		ISession* session = null;
		if ( Slang.SLANG_FAILED( c.Session->createSession( &sessionDesc, &session ) ) || session is null )
		{
			lastError = "could not make a compile session";
			return null;
		}

		ISlangBlob* diagnostics = null;
		IEntryPoint* entry = null;
		IComponentType* program = null;
		IComponentType* linked = null;
		ISlangBlob* code = null;

		try
		{
			//From source text rather than by module name. loadModule takes a name and works out a path from it - which means it only ever opens
			//something called .slang, and our shaders are called .shader. The module belongs to the session and is not released on its own.
			var module = session->loadModuleFromSourceString( strings.Make( "shader" ), strings.Make( name ),
				strings.Make( source ), &diagnostics );

			if ( module is null )
			{
				RecordDiagnostics( diagnostics, "the module would not load" );
				return null;
			}

			var entryName = strings.Make( entryPoint );

			if ( Slang.SLANG_FAILED( module->findEntryPointByName( entryName, &entry ) ) || entry is null )
			{
				//A function without [shader(...)] can still be the entry, checked against the stage asked for.
				entry = null;
				Release( ref diagnostics );

				if ( Slang.SLANG_FAILED( module->findAndCheckEntryPoint( entryName, slangStage, &entry, &diagnostics ) )
					|| entry is null )
				{
					RecordDiagnostics( diagnostics, $"no entry point called {entryPoint}" );
					return null;
				}
			}

			//The module and the entry point are linked into one program: an entry point on its own does not carry the types and functions it uses.
			var parts = stackalloc IComponentType*[2];
			parts[0] = (IComponentType*)module;
			parts[1] = (IComponentType*)entry;

			Release( ref diagnostics );
			if ( Slang.SLANG_FAILED( session->createCompositeComponentType( parts, 2, &program, &diagnostics ) ) )
			{
				RecordDiagnostics( diagnostics, "the program would not link" );
				return null;
			}

			Release( ref diagnostics );
			if ( Slang.SLANG_FAILED( program->link( &linked, &diagnostics ) ) )
			{
				RecordDiagnostics( diagnostics, "the program would not link" );
				return null;
			}

			Release( ref diagnostics );
			if ( Slang.SLANG_FAILED( linked->getEntryPointCode( 0, 0, &code, &diagnostics ) ) )
			{
				RecordDiagnostics( diagnostics, "the entry point would not compile" );
				return null;
			}

			if ( code is null || code->getBufferSize() == 0 )
			{
				lastError = "the compiler produced nothing";
				return null;
			}

			var bytes = new byte[(int)code->getBufferSize()];
			new ReadOnlySpan<byte>( code->getBufferPointer(), bytes.Length ).CopyTo( bytes );

			return format switch
			{
				Format.Glsl => Glsl( bytes, stage, 0 ),
				Format.GlslStereo => Glsl( bytes, stage, 2 ),
				Format.Wgsl => Wgsl( bytes, stage ),
				_ => bytes,
			};
		}
		finally
		{
			Release( ref code );
			Release( ref linked );
			Release( ref program );
			if ( entry is not null ) { entry->release(); entry = null; }
			Release( ref diagnostics );
			session->release();
		}
	}

	private static void Release( ref ISlangBlob* blob )
	{
		if ( blob is null ) return;
		blob->release();
		blob = null;
	}

	private static void Release( ref IComponentType* component )
	{
		if ( component is null ) return;
		component->release();
		component = null;
	}

	//The directory part of a path, or "." where there is none.
	private static string DirectoryOf( string path )
	{
		var slash = path.LastIndexOfAny( ['/', '\\'] );
		return slash < 0 ? "." : path[..slash];
	}
}

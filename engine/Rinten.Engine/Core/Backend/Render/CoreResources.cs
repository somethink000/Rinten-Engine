using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Rinten;

//Device resources shared by all viewports (meshes, textures, materials, pipelines); kept until Clear
internal static class CoreResources
{
	private static readonly Dictionary<string, CoreSceneRenderer.SceneMesh> meshes = new();
	private static readonly Dictionary<string, ulong> textures = new();
	private static readonly Dictionary<string, ulong> materials = new();

	//The shader each of those was built against, by the same key: its numbers are packed to that shader's
	//PARAMETERS, so an edit of the shader is an edit of every material on it - see Forget
	private static readonly Dictionary<string, string> materialShaders = new();

	//A file material went into the cache, packed for this shader - none named is the standard surface. Held as the file
	//the name resolves to, as its PARAMETERS are read from it: an old name an alias stands for edits as that file.
	internal static void BuiltOn( string held, string shader )
		=> materialShaders[held] = ShaderFile( shader );

	//The shader file a material's shader name is read from - see Declared.
	private static string ShaderFile( string shader )
		=> CoreShaderFile.Real( string.IsNullOrWhiteSpace( shader ) ? CoreSceneRenderer.StandardShader : shader );

	//Meshes keyed by Model object, avoiding a per-frame string key for every renderer
	private static readonly Dictionary<Model, CoreSceneRenderer.SceneMesh> meshesByModel = new();

	//What a live material was built into, weak on the material so play-mode copies don't leak
	private static readonly ConditionalWeakTable<Material, LiveEntry> liveByMaterial = new();

	//Handles whose material nobody holds, waiting for the main thread
	private static readonly ConcurrentQueue<ulong> droppedLive = new();

	//The colour picture each live material was built with, by the handle it was built into - see LiveColour.
	private static readonly Dictionary<ulong, ulong> liveColours = new();

	//One live material's device material and what building it worked out; updated in place, never replaced,
	//since the finalizer gives back whatever handle it holds and a replaced one may be destroyed already
	private sealed class LiveEntry
	{
		public int Revision;
		public int ValuesRevision;
		public ulong Handle;
		public string Combo;
		public bool Masked, Translucent, TwoSided;

		//The finalizer thread may not touch the device; the handle is queued for the main thread instead.
		~LiveEntry()
		{
			if ( Handle != 0 ) droppedLive.Enqueue( Handle );
		}
	}

	//Destroys the device materials of live materials that have been collected. On the main thread,
	//from Live - which the scene asks every frame there is anything live to draw - and from Clear.
	private static void ReleaseDropped()
	{
		while ( droppedLive.TryDequeue( out var handle ) )
		{
			if ( handle != fallback ) CoreRenderDevice.DestroyTexture( handle );
			liveColours.Remove( handle );
		}
	}

	/// <summary>Gives back a live material's device material now, for an owner done with it.</summary>
	public static void Forget( Material material )
	{
		if ( material is null || !liveByMaterial.TryGetValue( material, out var entry ) ) return;

		if ( entry.Handle != 0 && entry.Handle != fallback )
			CoreRenderDevice.DestroyTexture( entry.Handle );

		liveColours.Remove( entry.Handle );
		entry.Handle = 0;
		liveByMaterial.Remove( material );
	}
	private static readonly Dictionary<CoreSceneRenderer.PipelineKind, ulong> pipelines = new();

	//Compiled shaders by path: every pipeline bakes from the same stages, and without a compiled file the
	//alternative is running Slang again per kind of draw
	private static readonly Dictionary<string, byte[][]> shaders = new();

	/// <summary>Magenta/black checkerboard for a material whose texture can't be read; made on first use</summary>
	public static ulong Fallback
	{
		get
		{
			if ( fallback == 0 ) fallback = MakeMissingTexture();

			return fallback;
		}
	}

	private static ulong fallback;

	/// <summary>What a material slot nobody filled in is worth: the value that changes nothing.</summary>
	public static ulong DefaultColour => Flat( ref defaultColour, 255, 255, 255, 255, srgb: true );

	/// <summary>Facing straight out of the surface: (0,0,1) packed into bytes.</summary>
	public static ulong DefaultNormal => Flat( ref defaultNormal, 128, 128, 255, 255, srgb: false );

	/// <summary>Emitting nothing.</summary>
	public static ulong DefaultEmission => Flat( ref defaultEmission, 0, 0, 0, 255, srgb: false );

	private static ulong defaultColour;
	private static ulong defaultNormal;
	private static ulong defaultEmission;

	//One pixel of one colour, made once and held.
	private static ulong Flat( ref ulong held, byte r, byte g, byte b, byte a, bool srgb )
	{
		if ( held != 0 ) return held;

		held = CoreRenderDevice.CreateTextureRgba( stackalloc byte[] { r, g, b, a }, 1, 1, srgb );

		return held;
	}

	//That handle, or the stand-in where there is no handle.
	private static ulong Or( ulong handle, ulong instead ) => handle != 0 ? handle : instead;

	//Checkered rather than flat, so a missing picture and broken UVs look different
	private static ulong MakeMissingTexture()
		=> CoreRenderDevice.CreateTextureRgba( MissingPixels( MissingSize, MissingCell ),
			MissingSize, MissingSize, srgb: true );

	//How wide the missing texture is, and how wide one of its squares is.
	internal const int MissingSize = 256;
	internal const int MissingCell = 32;

	//Its pixels, so that what it looks like can be asserted.
	internal static byte[] MissingPixels( int size, int cell )
	{
		var pixels = new byte[size * size * 4];

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var dark = ( ( x / cell ) + ( y / cell ) ) % 2 == 0;
				var at = ( ( y * size ) + x ) * 4;

				pixels[at + 0] = dark ? (byte)0 : (byte)255;
				pixels[at + 1] = 0;
				pixels[at + 2] = dark ? (byte)0 : (byte)255;
				pixels[at + 3] = 255;
			}
		}

		return pixels;
	}

	/// <summary>Material for a missing material: checkerboard colour and emission, neutral normal.</summary>
	public static ulong MissingMaterial
	{
		get
		{
			if ( missingMaterial != 0 ) return missingMaterial;

			System.Span<float> numbers = stackalloc float[CoreRenderDevice.MaterialParamFloats];

			numbers.Clear();

			//Matte, no metal, emission equal to albedo, so the picture itself shows
			numbers[0] = 1.0f;
			numbers[1] = 0.0f;
			numbers[2] = 1.0f;
			numbers[3] = 0.0f;

			//And every number the standard surface declares at whatever it said its default was -
			//g_vEmissiveColor above all, which is white and which nought would have turned the glow black.
			CoreShaderParams.Into( numbers, Declared( null ), _ => null );

			missingMaterial = CoreRenderDevice.CreateMaterial( Fallback, DefaultNormal,
				0, Fallback, numbers );

			//Where even that will not build, the bare picture
			if ( missingMaterial == 0 ) missingMaterial = Fallback;

			return missingMaterial;
		}
	}

	private static ulong missingMaterial;

	/// <summary>A model, loaded once however many ask for it.</summary>
	public static CoreSceneRenderer.SceneMesh Mesh( string resourcePath )
		=> CoreSceneRenderer.LoadMesh( resourcePath, meshes, textures, Fallback, materials );

	/// <summary>The same for a model in hand, the only way to reach one built at runtime.</summary>
	public static CoreSceneRenderer.SceneMesh Mesh( Model model )
	{
		if ( model is null ) return null;

		//A model that is loaded stays loaded, so a hit here is the answer. A miss falls through to the path
		//cache below, which is where a mesh is actually built - and where a failure is remembered as one.
		if ( meshesByModel.TryGetValue( model, out var cached ) && cached is not null )
			return cached;

		var made = CoreSceneRenderer.LoadMesh( model, meshes, textures, Fallback, materials );

		//A failed build may be a file still being written; forget the failure so the next ask retries
		if ( made is null )
		{
			CoreSceneRenderer.ForgetFailedMesh( model, meshes );
			AskAgain( model );
		}

		meshesByModel[model] = made;
		return made;
	}

	//The frame a failed build last asked the scene to try again on.
	private static long askedAgainAt = -1;

	//Bumps the generation so instances retry a model that failed to build; throttled to every few frames
	private static void AskAgain( Model model )
	{
		if ( model?.core is not { IsValid: true } ) return;

		var turn = EngineLoop.LoopFrames;
		if ( askedAgainAt >= 0 && turn - askedAgainAt < RetryFrames ) return;

		askedAgainAt = turn;
		Generation++;
	}

	//How long to leave it between one of those and the next.
	private const int RetryFrames = 10;

	//A texture a material names by path, compiled, streamed and shared; 0 if not a picture
	internal static ulong FileMap( string path, bool srgb, out int width, out int height )
	{
		width = height = 0;

		var handle = CoreSceneRenderer.ResolveMap( path, textures, srgb );
		if ( handle == 0 ) return 0;

		if ( !CoreTextureStreaming.SizeOf( handle, out width, out height ) &&
			!CoreSceneRenderer.MapSize( path, srgb, out width, out height ) )
		{
			width = height = 1;
		}

		return handle;
	}

	/// <summary>A material, as the descriptor set a draw binds.</summary>
	public static ulong Material( string materialPath )
	{
		if ( Rinten.Material.Edited( materialPath ) is { } edited )
		{
			var live = Live( edited );

			if ( live != 0 ) return live;
		}

		return CoreSceneRenderer.ResolveMaterial( materialPath, materials, textures, Fallback );
	}

	/// <summary>A GPU material from an in-memory Material, rebuilt when its Revision changes.</summary>
	public static ulong Live( Material material )
	{
		var core = material?.core;
		if ( core is null ) return 0;

		ReleaseDropped();

		//By material identity: a scene asks this of every renderer that overrides a
		//material, every frame, and the answer is the same object until somebody edits it.
		if ( liveByMaterial.TryGetValue( material, out var seen ) &&
			seen.Revision == core.Revision && seen.Handle != 0 )
		{
			return seen.Handle;
		}

		return Rebuild( material, core );
	}

	//A material's numbers as its GPU material carries them; returns the third picture a shader may declare among them
	private static ulong Numbers( CoreMaterial core, float cutoff, System.Span<float> numbers )
	{
		//With the defaults a material that never mentioned them would have: a material made in
		//code, or imported from a format that spells them differently, is not a black mirror.
		numbers.Clear();

		numbers[0] = core.Value( "Roughness", 0.5f );
		numbers[1] = core.Value( "Metalness", 0.0f );
		numbers[2] = core.Value( "EmissionStrength", 0.0f );
		numbers[3] = cutoff;

		//And whatever its shader declared for itself, a slot each, in the order it declared them -
		//see CoreShaderParams. A number the material never set is what the shader said it should be.
		var third = 0ul;

		CoreShaderParams.Into( numbers, Declared( core.Shader ),
			//By declared name or without its prefix (g_vTileArea or TileArea), as CorePostBlit reads them
			name => core.Values.TryGetValue( name, out var value ) || core.Values.TryGetValue( CoreShaderParams.Bare( name ), out value ) ? value : null,
			name => PictureNumber( Device( core, name, CoreShaderParams.Bare( name ) ), ref third ) );

		return third;
	}

	//Live materials with a number set on them since their numbers were last written - see WriteNumbers.
	private static readonly HashSet<Material> numbersChanged = new();

	//Kept between frames, so writing them makes nothing.
	private static readonly List<Material> numbersWriting = new();
	private static ulong[] numbersHandles = new ulong[16];
	private static float[] numbersValues = new float[16 * CoreRenderDevice.MaterialParamFloats];
	private static byte[] numbersWritten = new byte[16];

	//A number was set on a material already drawn live - see CoreMaterial.ValuesRevision.
	internal static void NumbersChanged( Material material )
	{
		lock ( numbersChanged ) numbersChanged.Add( material );
	}

	//Writes live materials' new numbers in place at frame start; ones that can't be written are rebuilt
	internal static void WriteNumbers()
	{
		lock ( numbersChanged )
		{
			if ( numbersChanged.Count == 0 ) return;

			numbersWriting.Clear();
			numbersWriting.AddRange( numbersChanged );
			numbersChanged.Clear();
		}

		var count = 0;
		var floats = CoreRenderDevice.MaterialParamFloats;

		if ( numbersHandles.Length < numbersWriting.Count )
		{
			numbersHandles = new ulong[numbersWriting.Count * 2];
			numbersValues = new float[numbersWriting.Count * 2 * floats];
			numbersWritten = new byte[numbersWriting.Count * 2];
		}

		foreach ( var material in numbersWriting )
		{
			var core = material?.core;
			if ( core is null ) continue;

			//Nothing drawn with it yet, or rebuilt since: it reads them when it is.
			if ( !liveByMaterial.TryGetValue( material, out var entry ) || entry.Handle == 0 || entry.Handle == Fallback ) continue;
			if ( entry.Revision != core.Revision || entry.ValuesRevision == core.ValuesRevision ) continue;

			var cutoff = entry.Masked ? core.Value( "AlphaCutoff", 0.5f ) : 0.0f;
			var third = Numbers( core, cutoff, numbersValues.AsSpan( count * floats, floats ) );

			//The third picture changed, which is what the material is made of, not a number
			if ( third != 0 && third != MaterialPart( entry.Handle, 2 ) )
			{
				core.Revision++;
				continue;
			}

			entry.ValuesRevision = core.ValuesRevision;
			numbersHandles[count++] = entry.Handle;
		}

		numbersWriting.Clear();

		if ( count == 0 ) return;

		var written = numbersWritten.AsSpan( 0, count );
		CoreRenderDevice.WriteMaterialNumbers( numbersHandles.AsSpan( 0, count ), numbersValues.AsSpan( 0, count * floats ), written );

		//Refused: made again with them, as an edit always was.
		for ( var i = 0; i < count; i++ )
		{
			if ( written[i] != 0 ) continue;

			foreach ( var material in liveMaterialsOf( numbersHandles[i] ) ) material.core.Revision++;
		}
	}

	//The live materials built into a handle - only ever asked for one the device refused.
	private static IEnumerable<Material> liveMaterialsOf( ulong handle )
	{
		foreach ( var (material, entry) in liveByMaterial )
			if ( entry.Handle == handle ) yield return material;
	}

	//One of the pictures a GPU material was made of - colour, normal, third, emission.
	private static ulong MaterialPart( ulong material, int which )
	{
		System.Span<ulong> parts = stackalloc ulong[4];
		CoreRender.MaterialParts( material, parts );
		return parts[which];
	}

	//Rebuild half of Live, split out so its capturing lambdas don't allocate a closure on every Live call
	private static ulong Rebuild( Material material, CoreMaterial core )
	{
		//Find the old handle by material, not name: names collide between the editor and play-mode copies
		var entry = liveByMaterial.GetValue( material, _ => new LiveEntry() );

		if ( entry.Handle != 0 && entry.Handle != Fallback )
			CoreRenderDevice.DestroyTexture( entry.Handle );

		liveColours.Remove( entry.Handle );

		var colour = Device( core, "Color", "g_tColor" );

		//No colour map named: plain white. Named but unreadable: the checkerboard, so the fault is visible
		if ( colour == 0 ) colour = Names( core, "Color", "g_tColor" ) ? Fallback : DefaultColour;

		var flags = FlagsOf( core );
		var cutoff = flags.Masked ? core.Value( "AlphaCutoff", 0.5f ) : 0.0f;

		System.Span<float> numbers = stackalloc float[CoreRenderDevice.MaterialParamFloats];
		var third = Numbers( core, cutoff, numbers );

		//A slot with nothing gets the value that changes nothing, not the colour map - see DefaultColour
		var handle = CoreRenderDevice.CreateMaterial(
			colour,
			Or( Device( core, "Normal", "g_tNormal" ), DefaultNormal ),
			third,
			Or( Device( core, "Emission", "g_tEmissive", "g_tEmission" ), DefaultEmission ),
			numbers );

		var made = handle != 0 ? handle : Fallback;

		if ( handle != 0 ) liveColours[handle] = colour;

		entry.Revision = core.Revision;
		entry.ValuesRevision = core.ValuesRevision;
		entry.Handle = made;
		entry.Combo = ComboOf( core );
		entry.Masked = flags.Masked;
		entry.Translucent = flags.Translucent;
		entry.TwoSided = flags.TwoSided;

		return made;
	}

	//A pass's materials with a picture of the frame in the colour slot, by material and picture - see LiveReading
	private static readonly Dictionary<(Material Material, ulong Picture), (int Revision, int Values, ulong Handle)> reading = new();

	/// <summary>A pass's material with a frame picture in its colour slot, for a frame with no table.</summary>
	internal static ulong LiveReading( Material material, ulong picture )
	{
		var core = material?.core;
		if ( core is null || picture == 0 ) return 0;

		if ( reading.TryGetValue( (material, picture), out var had ) )
		{
			if ( had.Revision == core.Revision && had.Values == core.ValuesRevision ) return had.Handle;
			if ( had.Handle != 0 ) CoreRenderDevice.DestroyTexture( had.Handle );
		}

		System.Span<float> numbers = stackalloc float[CoreRenderDevice.MaterialParamFloats];
		var third = Numbers( core, 0.0f, numbers );

		var handle = CoreRenderDevice.CreateMaterial( picture, DefaultNormal, third, DefaultEmission, numbers );
		reading[(material, picture)] = ( core.Revision, core.ValuesRevision, handle );

		return handle;
	}

	/// <summary>Lets go of every material LiveReading made, as the pictures they read are given back.</summary>
	internal static void ForgetReading()
	{
		foreach ( var (_, had) in reading )
		{
			if ( had.Handle != 0 ) CoreRenderDevice.DestroyTexture( had.Handle );
		}

		reading.Clear();
	}

	//Read after Live, which works this out
	/// <summary>The build of its shader a live material asks for, as of its last build.</summary>
	public static string LiveCombo( Material material )
		=> material is not null && liveByMaterial.TryGetValue( material, out var seen )
			? seen.Combo
			: null;

	//Read after Live, which works this out
	/// <summary>A live material's draw-state features (cut-out, see-through, two-sided).</summary>
	public static (bool Masked, bool Translucent, bool TwoSided) LiveFlags( Material material )
		=> material is not null && liveByMaterial.TryGetValue( material, out var seen )
			? (seen.Masked, seen.Translucent, seen.TwoSided)
			: (false, false, false);

	/// <summary>A feature as the material's shader build has it: said outright, or implied by a map it holds.</summary>
	public static int Feature( Material material, string name )
	{
		if ( material?.core is not { } core ) return 0;

		var (named, told) = FeatureInputs( core );
		var values = CoreSceneRenderer.FeatureValues( named, told, ( what, field ) => Has( core, what, field ) );

		return values.TryGetValue( name, out var value ) ? value : 0;
	}

	/// <summary>Alpha cutoff of a live material (AlphaCutoff from its file), defaulting to 0.5 when unset</summary>
	public static float LiveCutoff( Material material )
	{
		var core = material?.core;

		if ( core is null || !core.Values.TryGetValue( "AlphaCutoff", out var cutoff ) ) return 0.5f;

		return cutoff.x;
	}

	public static Color LiveTint( Material material )
	{
		var core = material?.core;

		if ( core is null || !core.Values.TryGetValue( "Tint", out var tint ) ) return Color.White;

		return new Color( tint.x, tint.y, tint.z, tint.w );
	}

	//A material's shader and feature combo, applying the same implied-feature rules as materials on disk
	internal static (string Shader, string Combo) ShaderAndCombo( Material material )
	{
		if ( material?.core is not { } core ) return (null, null);

		var (named, _) = FeatureInputs( core );

		return (named ?? CoreSceneRenderer.StandardShader, ComboOf( core ));
	}

	private static string ComboOf( CoreMaterial core )
	{
		var (named, told) = FeatureInputs( core );

		return CoreSceneRenderer.ComboFor( named, told, ( what, field ) => Has( core, what, field ) );
	}

	//Whether it cuts pixels out, is see-through and is lit from both sides: the features of its shader
	//that are the draw's own state rather than the surface's - see CoreSceneRenderer.DrawFlags.
	private static (bool Masked, bool Translucent, bool TwoSided) FlagsOf( CoreMaterial core )
	{
		var (named, told) = FeatureInputs( core );

		return CoreSceneRenderer.DrawFlags(
			CoreSceneRenderer.FeatureValues( named, told, ( what, field ) => Has( core, what, field ) ) );
	}

	//The shader a live material is built with, and what it said about that shader's features by name.
	//SetFeature writes into the same values as everything else, so a feature is a number whose name begins F_.
	private static (string Named, Dictionary<string, int> Told) FeatureInputs( CoreMaterial core )
	{
		var told = new Dictionary<string, int>( System.StringComparer.Ordinal );

		foreach ( var (name, value) in core.Values )
		{
			if ( !name.StartsWith( "F_", System.StringComparison.Ordinal ) ) continue;

			told[name] = (int)System.MathF.Round( value.x );
		}

		var named = string.IsNullOrWhiteSpace( core.Shader ) || core.Shader == "invalid"
			? null
			: core.Shader;

		return (named, told);
	}

	//Whether a live material has a field (like CoreSceneRenderer.Implies); checks our names then the legacy ones
	private static bool Has( CoreMaterial core, CoreShaderFeatures.Implied what, string field )
		=> what switch
		{
			CoreShaderFeatures.Implied.Map => field switch
			{
				"Normal" => Names( core, "Normal", "g_tNormal" ),
				"Emission" => Names( core, "Emission", "g_tEmissive", "g_tEmission" ),
				//Check the shader's field name too: complex.shader says Map( ColorMap ), live materials use "Color"
				"Color" or "ColorMap" => Names( core, "Color", "Colour", "ColorMap", "g_tColor" ),

				//Any other is one of the shader's own pictures, by its declared name or without its prefix
				_ => Names( core, field, CoreShaderParams.Bare( field ) ),
			},

			//Above nought rather than above a half: the switches are nought or one either
			//way, and the one that is a number - metalness - is on for any metal at all.
			CoreShaderFeatures.Implied.Flag => core.Value( field ).x > 0.0f,

			_ => false,
		};

	//Bindless index for a shader-declared picture; the portable frame puts the first in the third slot
	internal static uint PictureNumber( ulong handle, ref ulong third )
	{
		if ( handle == 0 ) return 0;

		//Its index is carried in the material's numbers, so it must not move: see CoreTextureStreaming.KeepWhole
		CoreTextureStreaming.KeepWhole( handle );

		if ( !CoreRenderDevice.PortableFrame ) return CoreRender.TextureIndex( handle );

		if ( third == 0 ) third = handle;

		return third == handle ? 1u : 0u;
	}

	static ulong Device( CoreMaterial core, params string[] names )
	{
		foreach ( var name in names )
		{
			var handle = core.Texture( name )?.core?.Device() ?? 0;
			if ( handle != 0 ) return handle;
		}

		return 0;
	}

	//Whether the material named any of these, even an unreadable one: a slot left out and a broken slot are both
	//zero to Device but want opposite answers
	static bool Names( CoreMaterial core, params string[] names )
	{
		foreach ( var name in names )
		{
			if ( core.Texture( name ) is not null ) return true;
		}

		return false;
	}

	//The numbers a shader declares, read from disk once and kept
	private static readonly Dictionary<string, CoreShaderParams.Parameter[]> declared = new();

	//Pictures a shader's compute pass stores into, read once, forgotten with declared
	private static readonly Dictionary<string, CoreShaderParams.Parameter[]> stored = new();

	//The named entries of shaders compiled so far, by path, combo, entry and stage - see Entry.
	private static readonly Dictionary<string, byte[]> entries = new();

	//What each shader's STATE block says, by path - see State.
	private static readonly Dictionary<string, CoreShaderState> states = new();

	//One entry of a shader by name (a pass of a multi-pass file), compiled and kept; null where it fails
	internal static byte[] Entry( string resourcePath, string combo, string entry, CoreShaderCompiler.Stage kind )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) || string.IsNullOrWhiteSpace( entry ) ) return null;

		var key = string.Concat( resourcePath, "#", combo, "#", entry, "#", ( (int)kind ).ToString() );

		if ( entries.TryGetValue( key, out var cached ) ) return cached;

		var onDisk = CoreShaderFile.Source( CoreShaderFile.Real( resourcePath ) );
		var words = onDisk is null ? null : CoreShaderFile.EntryWords( onDisk, combo, entry, kind );

		entries[key] = words;

		return words;
	}

	//One of a surface's screen stages compiled with a run of point effects after it - see CorePointChain.ScreenText.
	//Compiled beside the frame: null while it is, with waiting saying so, and null for good where it would not.
	internal static byte[] ScreenChainWords( string resourcePath, string combo, CoreShaderFile.Stage stage,
		string chainKey, out bool waiting )
	{
		waiting = false;

		if ( string.IsNullOrWhiteSpace( resourcePath ) || chainKey is null ) return null;

		var id = string.Concat( CoreShaderCache.Revision.ToString(), "|", resourcePath, "#", combo, "#",
			( (int)stage ).ToString(), "#", chainKey );

		return CoreShaderJobs.Words( id, () =>
			CorePointChain.ScreenText( chainKey ) is { } chain &&
			CoreShaderFile.ScreenChainText( resourcePath, combo, stage, chain ) is { } made
				? new CoreShaderJobs.Request( made.Text, made.Directory, made.Entry, made.Kind, CoreShaderCache.Target, resourcePath )
				: (CoreShaderJobs.Request?)null, out waiting );
	}

	//What a shader's STATE block says - how its passes land, and whether it is the curve.
	internal static CoreShaderState State( string shaderPath )
	{
		if ( string.IsNullOrWhiteSpace( shaderPath ) ) return CoreShaderState.None;

		if ( states.TryGetValue( shaderPath, out var found ) ) return found;

		found = CoreShaderState.Of( CoreShaderFile.Source( CoreShaderFile.Real( shaderPath ) ) );
		states[shaderPath] = found;

		return found;
	}

	internal static CoreShaderParams.Parameter[] Declared( string shaderPath )
	{
		var name = string.IsNullOrWhiteSpace( shaderPath )
			? CoreSceneRenderer.StandardShader
			: shaderPath;

		if ( declared.TryGetValue( name, out var found ) ) return found;

		//Resolve the shader file the same way as its stages, or the material gets another shader's numbers
		var onDisk = CoreShaderFile.Source( CoreShaderFile.Real( name ) );

		found = onDisk is null ? CoreShaderParams.None : CoreShaderParams.Of( onDisk );
		declared[name] = found;

		return found;
	}

	//A material number as the shader sees it: the material's value, else the shader-declared default
	internal static float Number( CoreMaterial core, string name, float fallback )
		=> Vector( core, name, new Vector4( fallback, 0.0f, 0.0f, 0.0f ) ).x;

	//The same for a vector the shader declares
	internal static Vector4 Vector( CoreMaterial core, string name, Vector4 fallback )
	{
		if ( core is null ) return fallback;

		if ( core.Values.TryGetValue( name, out var value ) ) return value;

		foreach ( var one in Declared( core.Shader ) )
		{
			if ( one.Name == name ) return one.Default;
		}

		return fallback;
	}

	//Pictures a shader's compute pass stores into, cached; re-parsing per dispatch cost a lot of garbage
	internal static CoreShaderParams.Parameter[] Stored( string shaderPath )
	{
		if ( string.IsNullOrWhiteSpace( shaderPath ) ) return CoreShaderParams.None;

		if ( stored.TryGetValue( shaderPath, out var found ) ) return found;

		var onDisk = CoreShaderFile.Source( CoreShaderFile.Real( shaderPath ) );

		found = onDisk is null ? CoreShaderParams.None : CoreShaderParams.OutputsOf( onDisk );
		stored[shaderPath] = found;

		return found;
	}

	//Whether the instanced build reads transforms from the instance buffer; INSTANCED alone doesn't say
	private static readonly Dictionary<string, bool> shades = new();

	//The shaders the standard surface ends up drawing because their own file will not build. Their meshes
	//shade from the picture whatever their file says, because the surface actually drawing them does.
	private static readonly HashSet<string> stands = new();

	//When a name was last answered by the guess below rather than by a file - so the
	//guess is made once in a while instead of once per part per instance per pass.
	private static readonly Dictionary<string, long> guessed = new();

	//How long a guess stands before the disk is asked again, in milliseconds.
	private const long GuessHolds = 250;

	internal static bool Shades( string shaderPath )
	{
		var name = string.IsNullOrWhiteSpace( shaderPath )
			? CoreSceneRenderer.StandardShader
			: shaderPath;

		if ( stands.Contains( name ) ) return true;

		if ( shades.TryGetValue( name, out var found ) ) return found;

		//An unremembered guess is held for a quarter second, as resolving it repeatedly is expensive
		var now = Environment.TickCount64;

		if ( guessed.TryGetValue( name, out var when ) && now - when < GuessHolds ) return true;

		//Through the alias first, or a name that stands for another shader is answered by a file that is not
		//there - sprite means engine/unlit, and there is no sprite.shader on any disk. See CoreShaderFile.Real.
		var written = CoreShaderFile.Real( System.IO.Path.ChangeExtension( name, ".shader" ).Replace( '\\', '/' ) );
		var onDisk = CoreModel.ResolveTextOnDisk( written );

		//Shader not found yet (filesystem still mounting): assume clusters, and don't remember the guess
		if ( onDisk is null )
		{
			guessed[name] = now;
			return true;
		}

		found = IsSurface( onDisk );
		shades[name] = found;
		guessed.Remove( name );

		return found;
	}

	//Whether a shader file is a world surface the engine builds a cluster pass around (two markers)
	internal static bool IsSurface( string onDisk )
		=> Mentions( onDisk, 0, WorldPushMarker ) && Mentions( onDisk, 0, DepthIdMarker );

	//What a world draw says before it includes frame.slang - the define, not the test frame.slang makes of it.
	private const string WorldPushMarker = "#define ENGINE_WORLD_PUSH";

	//Whether a shader file is a sky (ENGINE_SKY) declaring SkyLight, which the table stages are built round
	internal static bool IsSky( string onDisk ) => Mentions( onDisk, 0, SkyMarker );

	//What a sky says at the top of itself - see sky.shader.
	private const string SkyMarker = "#define ENGINE_SKY";

	//Whether only the engine makes materials on this shader (ENGINE_INTERNAL), so the material editor hides it
	internal static bool IsInternal( string onDisk ) => Mentions( onDisk, 0, InternalMarker );

	private const string InternalMarker = "#define ENGINE_INTERNAL";

	//Whether a pass is drawn by the full frame alone (ENGINE_FULL_FRAME): it reads what the portable frame has no table for
	internal static bool IsFullFrameOnly( string onDisk ) => Mentions( onDisk, 0, FullFrameMarker );

	private const string FullFrameMarker = "#define ENGINE_FULL_FRAME";

	//Whether a shader file is a compute pass, which no material draws with
	internal static bool IsCompute( string onDisk ) => Mentions( onDisk, 0, ComputeMarker );

	private const string ComputeMarker = "[shader(\"compute\")]";

	//Whether a sky is a panorama, whose table is made from its picture - see CorePanoramaSky
	internal static bool IsPanorama( string onDisk ) => Mentions( onDisk, 0, PanoramaMarker );

	//What a panorama sky says besides ENGINE_SKY, which this begins with - see sky_hdri.shader.
	private const string PanoramaMarker = "#define ENGINE_SKY_PANORAMA";

	//Whether a shader moves vertices (ENGINE_MOVES_VERTICES, maybe per feature); those skip the pre-pass
	internal static bool MovesVertices( string shaderPath, string combo )
	{
		if ( !Marked( shaderPath, MovesMarker, movers, moversAsked ) ) return false;

		lock ( moverFeatures )
		{
			if ( !moverFeatures.TryGetValue( shaderPath, out var feature ) )
			{
				feature = MoverFeature( shaderPath );
				moverFeatures[shaderPath] = feature;
			}

			if ( feature.Index < 0 ) return true;

			//A combo is the features' values in declared order, dot-separated; empty is every default
			if ( string.IsNullOrEmpty( combo ) ) return feature.Default != 0;

			var at = 0;
			for ( var i = 0; i < feature.Index; i++ )
			{
				at = combo.IndexOf( '.', at );
				if ( at < 0 ) return feature.Default != 0;
				at++;
			}

			var end = combo.IndexOf( '.', at );
			var value = combo.AsSpan( at, ( end < 0 ? combo.Length : end ) - at );

			return !int.TryParse( value, out var on ) ? feature.Default != 0 : on != 0;
		}
	}

	//The feature a mover moves by, as its index among the shader's features and its default; -1 if always
	private static (int Index, int Default) MoverFeature( string shaderPath )
	{
		var written = CoreShaderFile.Real( System.IO.Path.ChangeExtension( shaderPath, ".shader" ).Replace( '\\', '/' ) );
		var onDisk = CoreModel.ResolveTextOnDisk( written );
		if ( onDisk is null ) return ( -1, 1 );

		string text;
		try { text = CompiledFile.TextOf( onDisk ); }
		catch ( System.IO.IOException ) { return ( -1, 1 ); }

		var at = text.IndexOf( MovesMarker, StringComparison.Ordinal );
		if ( at < 0 ) return ( -1, 1 );

		var end = text.IndexOf( '\n', at );
		var said = text[( at + MovesMarker.Length )..( end < 0 ? text.Length : end )].Trim();

		if ( said.Length == 0 || char.IsDigit( said[0] ) ) return ( -1, 1 );

		var features = CoreShaderFeatures.Of( onDisk );

		for ( var i = 0; i < features.Length; i++ )
			if ( features[i].Name == said ) return ( i, features[i].Default );

		return ( -1, 1 );
	}

	//By shader: the feature its moving hangs on - see MovesVertices.
	private static readonly Dictionary<string, (int Index, int Default)> moverFeatures = new();

	//What a shader that moves its vertices says - see foliage.shader.
	private const string MovesMarker = "#define ENGINE_MOVES_VERTICES";

	//Whether a shader reads kFarShading (ENGINE_SHADES_FAR); only those get a separate far pipeline
	internal static bool ShadesFar( string shaderPath )
		=> Marked( shaderPath, ShadesFarMarker, farShaders, farShadersAsked );

	//What a shader that reads kFarShading says - see foliage.shader.
	private const string ShadesFarMarker = "#define ENGINE_SHADES_FAR";

	//Which shaders were found to shade the distance on their own, by name.
	private static readonly Dictionary<string, bool> farShaders = new();

	//When one of them was last asked about and could not be found on disk.
	private static readonly Dictionary<string, long> farShadersAsked = new();

	//Which shaders were found to move their vertices, by name.
	private static readonly Dictionary<string, bool> movers = new();

	//When one of them was last asked about and could not be found on disk.
	private static readonly Dictionary<string, long> moversAsked = new();

	//Whether a material's shader draws sprites, i.e. includes sprite/sprite.slang
	internal static bool IsSpriteShader( string shaderPath )
		=> Marked( shaderPath, SpriteMarker, sprites, spritesAsked );

	//What a sprite shader says - see sprite/sprite.slang, which says it for it.
	private const string SpriteMarker = "#define ENGINE_SPRITE";

	//Which shaders were found to draw sprites, by name.
	private static readonly Dictionary<string, bool> sprites = new();

	//And when one of those was asked about and could not be found.
	private static readonly Dictionary<string, long> spritesAsked = new();

	//Whether a shader's file or its includes contain a marker; unknown shaders answer no, uncached
	private static bool Marked( string shaderPath, string marker,
		Dictionary<string, bool> known, Dictionary<string, long> asked )
	{
		if ( string.IsNullOrWhiteSpace( shaderPath ) ) return false;

		if ( known.TryGetValue( shaderPath, out var found ) ) return found;

		var now = Environment.TickCount64;

		if ( asked.TryGetValue( shaderPath, out var when ) && now - when < GuessHolds ) return false;

		var written = CoreShaderFile.Real( System.IO.Path.ChangeExtension( shaderPath, ".shader" ).Replace( '\\', '/' ) );
		var onDisk = CoreModel.ResolveTextOnDisk( written );

		if ( onDisk is null )
		{
			asked[shaderPath] = now;
			return false;
		}

		found = Mentions( onDisk, 0, marker );

		known[shaderPath] = found;
		asked.Remove( shaderPath );

		return found;
	}

	//Which shaders were found to be skies, by name.
	private static readonly Dictionary<string, bool> skies = new();

	//And when one of those was asked about and could not be found.
	private static readonly Dictionary<string, long> skiesAsked = new();

	//Whether the shader a material wears is a sky - what SkyBox2D takes a material on, and what the
	//renderer draws the sky and its table with. Off the file and remembered by name - see Marked.
	internal static bool IsSkyShader( string shaderPath )
		=> Marked( shaderPath, SkyMarker, skies, skiesAsked );

	//Which shaders were found to be panoramas, by name, and when one could not be found.
	private static readonly Dictionary<string, bool> panoramas = new();
	private static readonly Dictionary<string, long> panoramasAsked = new();

	//Whether the shader a material wears is a panorama sky, off the file and remembered by name.
	internal static bool IsPanoramaShader( string shaderPath )
		=> Marked( shaderPath, PanoramaMarker, panoramas, panoramasAsked );

	//Specialization bits from marked material numbers (set where nonzero); ids below 13 are the frame's
	internal static int FoldBits( CoreMaterial core )
	{
		if ( core is null ) return 0;

		var bits = 0;

		foreach ( var one in Declared( core.Shader ) )
		{
			if ( one.Fold < FirstShaderFold || one.Fold >= ShaderFoldsEnd ) continue;

			if ( Number( core, one.Name, one.Default.x ) != 0.0f ) bits |= 1 << one.Fold;
		}

		return bits;
	}

	//The lowest specialization constant a shader may claim - everything under it is the frame's, said in frame.slang.
	internal const int FirstShaderFold = 15;

	//Past the shader's folds, the frame's again: kPanoramaOn - see CoreSceneRenderer.PanoramaFold
	internal const int ShaderFoldsEnd = 18;

	//What a surface's interpolants are declared with - see depth_id.slang.
	private const string DepthIdMarker = "DEPTH_ID_FIELD";

	//A surface multiplying albedo by the mesh's corner tint (vertex location one) says this; nothing compiles it
	private const string VertexTintMarker = "ENGINE_VERTEX_TINT";

	//Which shaders were found to tint per corner, by name.
	private static readonly Dictionary<string, bool> tints = new();

	//Whether the material's surface multiplies by vertex tint; not cached while the file can't be found
	internal static bool TintsPerVertex( string shaderPath )
	{
		var name = string.IsNullOrWhiteSpace( shaderPath )
			? CoreSceneRenderer.StandardShader
			: shaderPath;

		//Drawn by the standard surface, which reads the tint, whatever its own file says
		if ( stands.Contains( name ) ) name = CoreSceneRenderer.StandardShader;

		if ( tints.TryGetValue( name, out var found ) ) return found;

		var written = CoreShaderFile.Real( System.IO.Path.ChangeExtension( name, ".shader" ).Replace( '\\', '/' ) );
		var onDisk = CoreModel.ResolveTextOnDisk( written );

		if ( onDisk is null ) return false;

		found = Mentions( onDisk, 0, VertexTintMarker );
		tints[name] = found;

		return found;
	}

	//A shader whose file won't build is drawn by the standard surface, so its meshes use the cluster path
	internal static void StandsIn( string shaderPath )
	{
		var name = string.IsNullOrWhiteSpace( shaderPath ) ? CoreSceneRenderer.StandardShader : shaderPath;

		if ( !stands.Add( name ) ) return;

		shades[name] = true;

		//Every mesh drawn with it takes the other path now.
		Rinten.Rendering.DrawSettings.PathChanged();

		Log.Info( $"{name} is drawn by {CoreSceneRenderer.StandardShader}, so its meshes shade from the picture" );
	}

	internal static void CannotShade( string shaderPath )
	{
		const bool fromThePicture = false;

		var name = string.IsNullOrWhiteSpace( shaderPath ) ? CoreSceneRenderer.StandardShader : shaderPath;

		//A stood-in shader keeps its promotion even if a pass fails, or the two would flip-flop every frame
		if ( stands.Contains( name ) )
		{
			Log.Warning( $"{name} stands in for a shader that will not build and still could not bake its pass in the picture - what it draws may be unshaded" );
			return;
		}

		if ( shades.TryGetValue( name, out var found ) && found == fromThePicture ) return;

		shades[name] = fromThePicture;

		//Every instance drawn with it takes a different path now.
		Rinten.Rendering.DrawSettings.PathChanged();

		Log.Info( $"{name} cannot shade from the picture - its meshes are drawn the classic way" );
	}

	//Whether this file or one it includes says the marker.
	private static bool Mentions( string path, int depth, string marker )
	{
		if ( depth > 2 ) return false;

		string text;

		try
		{
			text = CompiledFile.TextOf( path );
		}
		catch ( System.IO.IOException )
		{
			return false;
		}

		if ( text.Contains( marker, StringComparison.Ordinal ) ) return true;

		var directory = System.IO.Path.GetDirectoryName( path ) ?? "";

		//Where an include is looked for: the file's own folder, then the folders above it, then the compiler's search paths.
		//Its own folder alone missed the engine's sprite shaders, which sit in shaders/sprite and include "sprite/sprite.slang";
		//the search paths alone are empty where everything came out of the shader cache and nothing was compiled. The
		//folders above are where the library's roots are - shaders/ and the assets above it - whether or not anything said so
		var folders = new List<string>();

		for ( var up = directory; !string.IsNullOrEmpty( up ) && folders.Count < 4; up = System.IO.Path.GetDirectoryName( up ) )
			folders.Add( up );

		folders.AddRange( CoreShaderCompiler.SearchPaths() );

		foreach ( var line in text.Split( '\n' ) )
		{
			var trimmed = line.Trim();

			if ( !trimmed.StartsWith( "#include", StringComparison.Ordinal ) ) continue;

			var first = trimmed.IndexOf( '"' );
			var last = trimmed.LastIndexOf( '"' );

			if ( first < 0 || last <= first ) continue;

			var included = trimmed[( first + 1 )..last];

			foreach ( var folder in folders )
			{
				var full = System.IO.Path.GetFullPath( System.IO.Path.Combine( folder, included ) );

				//The packed file counts as the file - see CompiledFile. The first found is the one the compiler takes.
				if ( !Rinten.CompiledFile.Exists( full ) ) continue;
				if ( Mentions( full, depth + 1, marker ) ) return true;

				break;
			}
		}

		return false;
	}

	public static byte[][] Shader( string resourcePath, string combo = null, bool stereo = false )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) ) return null;

		//One build per feature set, since a feature is a different shader, and one more for stereo's own stages
		var key = string.IsNullOrEmpty( combo ) ? resourcePath : resourcePath + "#" + combo;
		if ( stereo ) key += "@stereo";

		if ( shaders.TryGetValue( key, out var cached ) ) return cached;

		//The two-view run is optional: without one a build draws an eye at a time, so no warning
		var stages = stereo
			? CoreShaderFile.Stages( resourcePath, combo, CoreShaderCompiler.Format.GlslStereo, optional: true )
			: CoreShaderFile.Stages( resourcePath, combo );

		shaders[key] = stages;

		return stages;
	}

	//Whether this export's surfaces include two-view builds; one always-drawn shader answers for all
	internal static bool StereoBaked
	{
		get
		{
			if ( stereoBaked is { } known ) return known;

			//Before the cache is warm there is no answer worth keeping.
			if ( !CoreShaderCompiler.IsGlsl( CoreShaderCache.Target ) ) return false;

			var stages = Shader( StereoWitness, null, stereo: true );
			var baked = stages is not null && stages.Length > 0;

			stereoBaked = baked;

			Log.Info( baked
				? "surfaces carry a two-view build - both of a headset's eyes can be drawn in one pass"
				: "surfaces carry no two-view build - a headset draws an eye at a time. Export again for one." );

			return baked;
		}
	}

	private static bool? stereoBaked;

	//The shader the answer above is read from: the world's own surface, which every scene draws and every export bakes.
	private const string StereoWitness = "shaders/complex.shader";

	//Last two pipeline kinds asked and their answers, so sorted draws skip hashing; cleared with cache
	private static CoreSceneRenderer.PipelineKind lastKind;
	private static ulong lastPipeline;
	private static CoreSceneRenderer.PipelineKind priorKind;
	private static ulong priorPipeline;

	/// <summary>A pipeline for a kind of draw, baked once.</summary>
	public static ulong Pipeline( in CoreSceneRenderer.PipelineKind kind )
	{
		if ( lastPipeline != 0 && kind == lastKind ) return lastPipeline;

		if ( priorPipeline != 0 && kind == priorKind )
		{

			//The one asked for again is the one to keep in front.
			( lastKind, lastPipeline, priorKind, priorPipeline ) = ( priorKind, priorPipeline, lastKind, lastPipeline );

			return lastPipeline;
		}

		var made = CoreSceneRenderer.PipelineFor( kind, pipelines );

		if ( made != 0 )
		{
			priorKind = lastKind;
			priorPipeline = lastPipeline;

			lastKind = kind;
			lastPipeline = made;
		}

		return made;
	}

	//Forgets lastPipeline - for wherever the pipelines go.
	private static void ForgetLastPipeline()
	{
		lastKind = default;
		lastPipeline = 0;
		priorKind = default;
		priorPipeline = 0;
	}

	/// <summary>A material's bare colour map, whole, for draws that light nothing.</summary>
	public static ulong Colour( string materialPath )
		=> Whole( CoreSceneRenderer.ResolveMaterialTexture( materialPath, textures, Fallback ) );

	/// <summary>An image by path, uploaded once - whole, as <see cref="Colour"/> is.</summary>
	public static ulong Texture( string path, bool srgb )
		=> Whole( CoreSceneRenderer.ResolveMap( path, textures, srgb ) );

	private static ulong Whole( ulong handle )
	{
		CoreTextureStreaming.KeepWhole( handle );
		return handle;
	}

	/// <summary>The colour texture a material names, for the raster that cuts out by its alpha.</summary>
	public static ulong ColourTexture( string materialPath )
		=> string.IsNullOrWhiteSpace( materialPath ) ? 0 : CoreSceneRenderer.ResolveMaterialTexture( materialPath, textures, Fallback );

	/// <summary>A live material's colour texture; the depth pre-pass cuts masked pixels by it.</summary>
	public static ulong LiveColour( ulong liveHandle )
		=> liveHandle != 0 && liveColours.TryGetValue( liveHandle, out var colour ) ? colour : 0;

	public static CoreSceneRenderer.MeshDraw WithGroupMaterial( CoreSceneRenderer.MeshDraw part, int group )
		=> CoreSceneRenderer.WithGroupMaterial( part, group, textures, Fallback, materials );

	/// <summary>Drop a model's GPU mesh so the next draw reads the file again.</summary>
	public static void Forget( Model model )
	{
		if ( model is null ) return;

		if ( meshesByModel.Remove( model, out var held ) && held is not null )
			Destroy( held );

		var path = (model.ResourcePath ?? model.Name ?? "").Replace( '\\', '/' );
		if ( string.IsNullOrWhiteSpace( path ) )
		{
			Generation++;
			MeshesForgotten++;
			return;
		}

		List<string> drop = null;
		foreach ( var key in meshes.Keys )
		{
			if ( !SameMesh( key, path ) ) continue;
			drop ??= new List<string>();
			drop.Add( key );
		}

		if ( drop is not null )
		{
			foreach ( var key in drop )
			{
				if ( !meshes.Remove( key, out var mesh ) || mesh is null || mesh == held ) continue;
				Destroy( mesh );
				ForgetHolders( mesh );
			}
		}

		Generation++;
		MeshesForgotten++;
	}

	//Drops a destroyed mesh from the by-model cache for every model that shared its path
	private static void ForgetHolders( CoreSceneRenderer.SceneMesh mesh )
	{
		List<Model> holders = null;

		foreach ( var (model, byModel) in meshesByModel )
		{
			if ( byModel != mesh ) continue;

			holders ??= new List<Model>();
			holders.Add( model );
		}

		if ( holders is null ) return;

		foreach ( var model in holders ) meshesByModel.Remove( model );
	}

	/// <summary>Drops everything built from one file so it reloads, including meshes holding its material.</summary>
	public static void Forget( string resourcePath )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) ) return;

		var path = Stem( resourcePath );
		if ( path.Length == 0 ) return;

		//Material fields go wholesale, for edited shaders too: their cull numbers come from the shader's parameters
		CoreSceneRenderer.ForgetMaterialFields();

		//And where each shader a material names was found on disk, for the same reason and wholesale too: a
		//shader that came or went may be what a name answers to now - see MaterialDefinition.ForgetShaderSources.
		MaterialDefinition.ForgetShaderSources();

		var dropped = false;

		//The descriptor set built from it, and the colour map cached under its name by the texture-only path beside it.
		foreach ( var key in Naming( materials, path ) )
		{
			if ( !materials.Remove( key, out var material ) ) continue;

			materialShaders.Remove( key );
			Release( material );
			dropped = true;
		}

		//A shader's materials, built from other files but packed to its PARAMETERS: with a parameter added or moved,
		//the numbers they hold are in the old places, so they are built again rather than read by the new layout.
		HashSet<string> builtOn = null;

		//Only a shader file has materials packed to it, and the live ones' names are resolved on disk to ask
		var isShader = IsShaderFile( resourcePath );

		if ( isShader )
		{
			foreach ( var (key, shader) in materialShaders )
			{
				if ( SameFile( shader, path ) ) ( builtOn ??= new HashSet<string>( System.StringComparer.OrdinalIgnoreCase ) ).Add( key );
			}

			foreach ( var key in builtOn ?? EmptyBuiltOn )
			{
				materialShaders.Remove( key );
				if ( materials.Remove( key, out var material ) ) Release( material );

				dropped = true;
			}

			//And the live ones - edited in the editor, or made in code like a terrain's - which rebuild on their revision.
			foreach ( var (material, _) in liveByMaterial )
			{
				if ( material?.core is not { } core || !SameFile( ShaderFile( core.Shader ), path ) ) continue;

				core.Revision++;
				dropped = true;
			}
		}

		foreach ( var key in Naming( textures, path ) )
		{
			if ( !textures.Remove( key, out var texture ) ) continue;

			Release( texture );
			dropped = true;
		}

		//Any file under a shaders folder may feed engine passes, so their pipelines are remade
		if ( path.Replace( '\\', '/' ).Contains( "shaders/", System.StringComparison.OrdinalIgnoreCase ) ||
			resourcePath.EndsWith( ".slang", System.StringComparison.OrdinalIgnoreCase ) )
		{
			CoreShaderCache.Changed();
		}

		//A shader drops its pipelines; an include (.slang) drops every shader, as any might reach it
		var include = resourcePath.EndsWith( ".slang", System.StringComparison.OrdinalIgnoreCase );

		var gone = include ? new List<string>( shaders.Keys ) : Naming( shaders, path );

		if ( gone.Count > 0 )
		{
			foreach ( var key in gone ) shaders.Remove( key );

			ForgetLastPipeline();

			foreach ( var baked in pipelines.Values ) CoreRenderDevice.DestroyPipeline( baked );

			pipelines.Clear();
			CoreSceneRenderer.ForgetBuilds();
			CoreMaterialTable.ForgetPipelines();

			//And the drawer's own sky passes, which are not in the table above
			CoreSceneDrawer.ForgetSkyPipelines();

			dropped = true;
		}

		//And its named entries and what its STATE block says, which were read out of the same file.
		foreach ( var key in Naming( entries, path ) )
		{
			entries.Remove( key );
			dropped = true;
		}

		foreach ( var key in Naming( states, path ) ) states.Remove( key );

		//Declared parameters are keyed by shader name alone, so drop them separately - an alias's too, read from the same file
		var redeclared = new List<string>( Naming( declared, path ) );

		if ( isShader )
		{
			foreach ( var key in declared.Keys )
			{
				if ( !redeclared.Contains( key ) && SameFile( ShaderFile( key ), path ) ) redeclared.Add( key );
			}
		}

		foreach ( var key in redeclared )
		{
			declared.Remove( key );

			//And its numbers' place in a full-screen pass's push block, worked out from the same declaration
			CorePostBlit.Forget( key );
		}

		//And the pictures its compute pass stores into, read out of the same block of the same file - see Stored.
		foreach ( var key in Naming( stored, path ) ) stored.Remove( key );

		//Also drop path decisions (shading answer and stand-in), so a fixed shader can return to clusters
		var repath = false;

		foreach ( var key in Naming( shades, path ) )
		{
			shades.Remove( key );
			repath = true;
		}

		foreach ( var key in Naming( guessed, path ) ) guessed.Remove( key );

		//And whether it is a sky, and whether it sways, for the same reason and in the same
		//breath: a file edited into one, or out of one, is answered off the file again.
		foreach ( var key in Naming( skies, path ) ) skies.Remove( key );
		foreach ( var key in Naming( panoramas, path ) ) panoramas.Remove( key );
		foreach ( var key in Naming( panoramasAsked, path ) ) panoramasAsked.Remove( key );
		foreach ( var key in Naming( skiesAsked, path ) ) skiesAsked.Remove( key );
		foreach ( var key in Naming( movers, path ) ) movers.Remove( key );
		foreach ( var key in Naming( moversAsked, path ) ) moversAsked.Remove( key );
		lock ( moverFeatures ) foreach ( var key in Naming( moverFeatures, path ) ) moverFeatures.Remove( key );
		foreach ( var key in Naming( sprites, path ) ) sprites.Remove( key );
		foreach ( var key in Naming( spritesAsked, path ) ) spritesAsked.Remove( key );

		List<string> stood = null;

		foreach ( var name in stands )
		{
			if ( !SameFile( name, path ) ) continue;

			stood ??= new List<string>();
			stood.Add( name );
		}

		if ( stood is not null )
		{
			foreach ( var name in stood ) stands.Remove( name );
			repath = true;
		}

		//Every instance drawn with it is resolved again, or the corrected answer sits here and reaches nothing.
		if ( repath ) Rinten.Rendering.DrawSettings.PathChanged();

		//And every mesh that named it, because its draws hold the old handle.
		List<string> stale = null;

		foreach ( var (key, held) in meshes )
		{
			if ( held is null ) continue;

			var names = false;

			foreach ( var named in held.Materials )
			{
				if ( !SameFile( named, path ) && !BuiltOnIt( builtOn, named ) ) continue;

				names = true;
				break;
			}

			if ( !names ) continue;

			stale ??= new List<string>();
			stale.Add( key );
		}

		if ( stale is not null )
		{
			foreach ( var key in stale )
			{
				if ( !meshes.Remove( key, out var mesh ) || mesh is null ) continue;

				Destroy( mesh );
				ForgetHolders( mesh );
			}

			dropped = true;
		}

		if ( dropped ) Generation++;
	}

	private static readonly HashSet<string> EmptyBuiltOn = new();

	//A shader file, compiled or not - what a material's numbers are laid out by
	private static bool IsShaderFile( string resourcePath )
		=> resourcePath.EndsWith( ".shader", System.StringComparison.OrdinalIgnoreCase ) ||
			resourcePath.EndsWith( ".shader_c", System.StringComparison.OrdinalIgnoreCase );

	//Whether a mesh's material is one of the cached materials dropped above - keyed by its path, with the art's differences after a '#'
	private static bool BuiltOnIt( HashSet<string> builtOn, string named )
	{
		if ( builtOn is null || string.IsNullOrWhiteSpace( named ) ) return false;

		foreach ( var key in builtOn )
		{
			var cut = key.IndexOf( '#' );
			if ( SameFile( cut < 0 ? key : key[..cut], Stem( named ) ) ) return true;
		}

		return false;
	}

	//A path without its extension, to match a file against the names it's held under (.vmat/.mat, .tex_c)
	private static string Stem( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return "";

		var clean = path.Replace( '\\', '/' ).TrimStart( '/' );

		if ( clean.EndsWith( "_c", System.StringComparison.OrdinalIgnoreCase ) )
			clean = clean[..^2];

		var dot = clean.LastIndexOf( '.' );
		var slash = clean.LastIndexOf( '/' );

		return dot > slash ? clean[..dot] : clean;
	}

	private static bool SameFile( string key, string stem )
		=> string.Equals( Stem( key ), stem, System.StringComparison.OrdinalIgnoreCase );

	//The keys of a cache that name one file, gathered before it is edited.
	private static List<string> Naming<T>( Dictionary<string, T> held, string stem )
	{
		List<string> found = null;

		foreach ( var key in held.Keys )
		{
			if ( !SameFile( key, stem ) ) continue;

			found ??= new List<string>();
			found.Add( key );
		}

		return found ?? EmptyKeys;
	}

	private static readonly List<string> EmptyKeys = new();

	//Destroys an owned texture, but never the shared stand-ins (checkerboard, flat defaults)
	private static void Release( ulong handle )
	{
		if ( handle == 0 ) return;
		if ( handle == fallback ) return;
		if ( handle == defaultColour || handle == defaultNormal ) return;
		if ( handle == defaultEmission ) return;

		//Engine dev textures are shared too and owned by nobody
		if ( CoreDevTextures.Owns( handle ) ) return;

		CoreRenderDevice.DestroyTexture( handle );
	}

	static bool SameMesh( string key, string path )
		=> string.Equals( key, path, System.StringComparison.OrdinalIgnoreCase )
			|| key.EndsWith( ":" + path, System.StringComparison.OrdinalIgnoreCase );

	static void Destroy( CoreSceneRenderer.SceneMesh mesh ) => CoreSceneRenderer.ReleaseMesh( mesh );

	/// <summary>Raised before everything here goes, for the editor's own device resources.</summary>
	public static event System.Action Clearing;

	//Bumped when all device resources are released, so cached draws know their handles are stale
	internal static int Generation { get; private set; } = 1;

	//A material in use was refilled from its file, so draws built from it must be rebuilt
	internal static void MaterialRefilled() => Generation++;

	//Streamed textures got new images and sets; frames recorded before must not be resubmitted
	internal static void PicturesMoved()
	{
		Generation++;
		PictureGeneration++;
	}

	//Generation bumps that were only texture swaps, so scene tables needn't rebake shadows for them
	internal static int PictureGeneration { get; private set; } = 1;

	//Generation bumps that were only a model's meshes given back: what drew them looks again, nothing else
	internal static int MeshesForgotten { get; private set; } = 1;

	//Bumped only when the whole device goes (Clear), for holders of device pictures across Generation's other bumps
	internal static int Cleared { get; private set; } = 1;

	/// <summary>Lets go of all of it, for device shutdown and for tests starting from nothing.</summary>
	public static void Clear()
	{
		//Whatever else is held for the device goes with it. The editor keeps pipelines and images of its own
		//- the grid's lines, the gizmos, the icons - and they name a device that is about to stop existing.
		Clearing?.Invoke();

		Generation++;
		Cleared++;

		ForgetLastPipeline();

		foreach ( var baked in pipelines.Values ) CoreRenderDevice.DestroyPipeline( baked );

		foreach ( var mesh in meshes.Values ) CoreSceneRenderer.ReleaseMesh( mesh );

		//Materials before textures: a material names images it does not own, and freeing
		//the images first would leave it naming nothing for as long as it took to free it.
		foreach ( var material in materials.Values ) CoreRenderDevice.DestroyTexture( material );

		//Live materials belong to objects, not names; zeroed so their finalizers have nothing to give back
		foreach ( var (_, live) in liveByMaterial )
		{
			if ( live.Handle != 0 && live.Handle != fallback )
				CoreRenderDevice.DestroyTexture( live.Handle );

			live.Handle = 0;
		}

		ReleaseDropped();

		//With them, because it is one of them - a set built over the pictures below rather than a picture of its own.
		if ( missingMaterial != 0 && missingMaterial != fallback )
			CoreRenderDevice.DestroyTexture( missingMaterial );

		//The flat pictures are cached in here under the paths that named them as well as by the one that owns
		//them, so they go through their owner once rather than through this loop as many times as they were named.
		foreach ( var texture in textures.Values )
		{
			if ( !CoreDevTextures.Owns( texture ) ) CoreRenderDevice.DestroyTexture( texture );
		}

		CoreDevTextures.Drop();

		if ( fallback != 0 ) CoreRenderDevice.DestroyTexture( fallback );

		if ( defaultColour != 0 ) CoreRenderDevice.DestroyTexture( defaultColour );
		if ( defaultNormal != 0 ) CoreRenderDevice.DestroyTexture( defaultNormal );
		if ( defaultEmission != 0 ) CoreRenderDevice.DestroyTexture( defaultEmission );

		//And the probes' captures, a texture and bindless slot each, kept apart since nothing asks for them by name
		CoreEnvmap.Release();

		//And the scene's baked lighting, which is pictures a bake made rather than files, for the same reason.
		CoreBakedLighting.Release();

		//And the views of the cameras drawing into textures.
		CoreCameraTargets.Release();

		pipelines.Clear();
		CoreSceneRenderer.ForgetBuilds();
		CoreMaterialTable.ForgetPipelines();
		shaders.Clear();
		declared.Clear();
		entries.Clear();
		states.Clear();
		CorePostBlit.Forget( null );

		//And the engine passes' includes, which may have resolved before a project with its own file was mounted
		CoreShaderCache.Changed();
		meshes.Clear();
		meshesByModel.Clear();

		//What was decided about which path a shader's meshes take goes with the builds
		//it was decided from - see Forget, which drops the same three a file at a time.
		shades.Clear();
		stands.Clear();
		guessed.Clear();
		skies.Clear();
		skiesAsked.Clear();
		panoramas.Clear();
		panoramasAsked.Clear();
		movers.Clear();
		moversAsked.Clear();
		sprites.Clear();
		spritesAsked.Clear();
		CoreSceneRenderer.ForgetMaterialFields();

		//Those meshes' cluster runs go too: offsets into one buffer, and a reloaded mesh lands elsewhere
		CoreClusters.Drop();
		CoreGeometryPool.Drop();
		CoreLights.Drop();
		CoreMaterialTable.Clear();

		materials.Clear();
		materialShaders.Clear();
		liveByMaterial.Clear();
		liveColours.Clear();
		reading.Clear();
		textures.Clear();
		fallback = 0;
		missingMaterial = 0;
		defaultColour = 0;
		defaultNormal = 0;
		defaultEmission = 0;
	}
}

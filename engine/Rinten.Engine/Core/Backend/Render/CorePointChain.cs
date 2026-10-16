using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Rinten;

//Per-pixel post effects (an Apply function) chained into one pass, or folded into the compose
internal static partial class CorePointChain
{
	private static readonly Diagnostics.Logger log = new( "Render" );

	//One point effect asked for this frame: what draws it, its numbers, and where it was asked.
	internal readonly record struct Point( Material Material, RenderAttributes Attributes,
		Rinten.Rendering.CommandList List, string Shader, Rinten.Rendering.Stage Stage, int Order );

	//The engine's own curve, laid where a frame asked for none - see WithCurve.
	internal const string CurveShader = "shaders/postprocess/tonemapping.shader";

	//The engine's curve as a material, for the frame that asked for none.
	internal static Material CurveMaterial => field ??= Material.FromShader( CurveShader );

	//The curve a frame named without a Tonemapping component - nought, the engine's own choice, or linear for a camera with its post processing off
	//(CoreSceneDrawer.CurveOf) - as the attributes the curve's file reads its mode from. Kept per mode, since the same one or two are asked every frame.
	private static RenderAttributes CurveNamed( float mode )
	{
		if ( mode == 0.0f ) return null;

		if ( !curveModes.TryGetValue( mode, out var attributes ) )
		{
			attributes = new RenderAttributes();
			attributes.Set( "Mode", mode );
			curveModes[mode] = attributes;
		}

		return attributes;
	}

	private static readonly Dictionary<float, RenderAttributes> curveModes = new();

	//What one point effect's file is, read once and kept until a shader changes.
	private sealed class Effect
	{
		/// <summary>What to call it in the text a chain is compiled from - its resource path, the same on any machine.</summary>
		public string Name;

		/// <summary>The text of it, its blocks cut and its includes and entry attributes taken out.</summary>
		public string Body;

		/// <summary>What it includes, each line written by resource path so it finds the same file from anywhere.</summary>
		public string[] Includes;

		/// <summary>Its features at their defaults, defined and then undefined again.</summary>
		public string Features;
		public string[] FeatureNames;

		/// <summary>Its numbers and pictures, in the order it declared them, and the slot each starts at.</summary>
		public CoreShaderParams.Parameter[] Numbers;
		public int[] Slot;
		public int Slots;

		/// <summary>The pictures its compute passes store into - declared so the body compiles; never bound.</summary>
		public CoreShaderParams.Parameter[] Outputs;

		public bool Applies;
		public bool Moves;
		public bool Curve;

		//Passes of its own beside Apply (bloom, depth of field): their code needs the post library, so it never folds
		public bool Passes;
	}

	private static readonly Dictionary<string, Effect> effects = new( StringComparer.OrdinalIgnoreCase );
	private static readonly Dictionary<string, ulong> pipelines = new();
	private static readonly HashSet<string> said = new();
	private static int revision = -1;

	//Every run the frames of this session asked for, by the effects in it - what a bake compiles the variants of. See Remember and BakeVariants.
	private static readonly Dictionary<string, Point[]> asked = new( StringComparer.Ordinal );

	//The texts the world's shading is compiled with to carry a run, by the run's key - see ScreenText.
	private static readonly Dictionary<string, string> screens = new( StringComparer.Ordinal );

	//Runs that would not compile into some pass of the world's shading - drawn by the compose from then on.
	private static readonly HashSet<string> refused = new( StringComparer.Ordinal );

	//Whether the last Pass or Compose found its pipeline still compiling beside the
	//frame - so a caller drawing without it knows nothing is wrong. See CoreShaderJobs.
	internal static bool Waited { get; private set; }

	//Where the next chain's numbers go in this frame's block, in slots of four floats.
	private static int cursor;

	private static readonly float[] values = new float[CoreSceneRenderer.EffectSlots * 4];

	//A new frame block: its effects are empty again.
	internal static void NewFrame() => cursor = 0;

	//Everything made out of shader files let go, because one of them changed.
	internal static void Forget()
	{
		foreach ( var pipeline in pipelines.Values )
		{
			if ( pipeline != 0 ) CoreRenderDevice.DestroyPipeline( pipeline );
		}

		pipelines.Clear();
		effects.Clear();
		said.Clear();
		screens.Clear();
		refused.Clear();
	}

	private static void Fresh()
	{
		if ( revision == CoreShaderCache.Revision ) return;

		Forget();
		revision = CoreShaderCache.Revision;
	}

	//The compose with nothing but the engine's curve in it, onto the window and into the finished picture - what a frame with no effect of its own draws.
	internal static void WarmUp()
	{
		var curve = new List<Point>();
		var split = WithCurve( curve ) + 1;

		Compose( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( curve ), split, CoreRender.TargetKind.Screen, false );
		Compose( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( curve ), split, CoreRender.TargetKind.Final, false );
	}

	//Whether a shader is a point effect - has an Apply or a Where.
	internal static bool IsPoint( string shader ) => Of( shader ) is { } found && ( found.Applies || found.Moves );

	//Whether a shader's Apply is the frame's tonemap curve - see CoreShaderState.Curve.
	internal static bool IsCurve( string shader ) => Of( shader )?.Curve ?? false;

	//Whether any of these moves where the frame is read - which the edge filter has to know.
	internal static bool Moves( ReadOnlySpan<Point> chain )
	{
		foreach ( var point in chain )
		{
			if ( Of( point.Shader )?.Moves ?? false ) return true;
		}

		return false;
	}

	//Ensures the chain has a tonemap curve (first Curve() effect, else the engine's); returns its index
	internal static int WithCurve( List<Point> chain, float mode = 0.0f )
	{
		for ( var i = 0; i < chain.Count; i++ )
		{
			if ( IsCurve( chain[i].Shader ) ) return i;
		}

		var at = 0;

		while ( at < chain.Count && ( chain[at].Stage < Rinten.Rendering.Stage.Tonemapping ||
			( chain[at].Stage == Rinten.Rendering.Stage.Tonemapping && chain[at].Order < 0 ) ) )
		{
			at++;
		}

		chain.Insert( at, new Point( CurveMaterial, CurveNamed( mode ), null, CurveShader, Rinten.Rendering.Stage.Tonemapping, 0 ) );

		return at;
	}

	//The pipeline that draws a run of point effects over the frame as one pass, into target; nought where it cannot be built.
	internal static ulong Pass( ReadOnlySpan<Point> chain, CoreRender.TargetKind target )
	{
		//A point effect is laid over a picture of the frame, and the portable frame has none - see
		//RenderTier. Nought is what every caller already takes to mean the effect is not drawn.
		if ( CoreRenderDevice.PortableFrame ) return 0;

		Fresh();
		Remember( chain );

		var key = Key( "pass", chain, chain.Length, (int)target, false );

		Waited = false;

		if ( pipelines.TryGetValue( key, out var made ) ) return made;

		made = BuildPass( key, chain.ToArray(), target, out var waiting );

		//Still compiling beside the frame: nothing yet, and not remembered as nothing.
		if ( waiting )
		{
			Waited = true;
			return 0;
		}

		pipelines[key] = made;

		return made;
	}

	//The compose with a run of point effects in it: everything up to and with the curve on every tap the
	//edge filter takes, everything after it once, on what the filter made. Nought where it cannot be built.
	internal static ulong Compose( ReadOnlySpan<Point> chain, int split, CoreRender.TargetKind target, bool carry )
	{
		//As for Pass: there is no picture to compose out of.
		if ( CoreRenderDevice.PortableFrame ) return 0;

		Fresh();
		Remember( chain );

		var key = Key( "compose", chain, split, (int)target, carry );

		Waited = false;

		if ( pipelines.TryGetValue( key, out var made ) ) return made;

		made = BuildCompose( key, chain.ToArray(), split, target, carry, out var waiting );

		if ( waiting )
		{
			Waited = true;
			return 0;
		}

		pipelines[key] = made;

		return made;
	}

	//The text is made from a copy, later and maybe elsewhere - see Build - and in methods of their own so that Pass
	//and Compose capture nothing: a lambda over their chain was a closure made at every call, a pipeline found or not.
	private static ulong BuildPass( string key, Point[] chain, CoreRender.TargetKind target, out bool waiting )
		=> Build( key, () => Source( chain, chain.Length, "", PassTail ), target, out waiting );

	private static ulong BuildCompose( string key, Point[] chain, int split, CoreRender.TargetKind target, bool carry, out bool waiting )
		=> Build( key, () => ComposeSource( chain, split, carry ), target, out waiting );

	private static string ComposeSource( ReadOnlySpan<Point> chain, int split, bool carry )
	{
		var file = CoreShaderFile.Source( EngineShaders.Compose );

		if ( file is null ) return null;

		try
		{
			//Named by its resource path, not by where it is on this disk: the text is the key a compiled stage is
			//kept under, and a full path in it is a key no other machine can ask for. See CoreShaderFile.Named.
			return Source( chain, split, carry ? EngineShaders.Carry : "",
				"#line 1 \"" + EngineShaders.Compose + "\"\n" + CompiledFile.TextOf( file ) );
		}
		catch ( IOException )
		{
			return null;
		}
	}

	//What a run drawn as a pass of its own is: the frame read, then every effect in turn.
	private const string PassTail = """

		[shader("fragment")]
		float4 MainPs( PostV2F i ) : SV_Target0
		{
			float3 colour = SceneColour( ChainWhere( i.uv ) );

			colour = ChainBefore( colour, i.uv );

			return float4( ChainAfter( colour, i.uv ), 1.0 );
		}

		""";

	//Whether frames' runs are recorded for baking; only an export asks, as recording allocates per frame
	internal static bool Recording { get; set; }

	//Keeps what a run was made of, so a bake can compile it and the runs it turns into when an effect is switched off.
	//Without the materials: the run is a list of files, and holding a scene's materials past its frame is holding the scene.
	private static void Remember( ReadOnlySpan<Point> chain )
	{
		if ( !Recording || chain.Length == 0 ) return;

		var key = new StringBuilder();
		var copy = new Point[chain.Length];

		for ( var i = 0; i < chain.Length; i++ )
		{
			var point = chain[i];

			copy[i] = new Point( null, null, null, point.Shader, point.Stage, point.Order );

			key.Append( point.Shader ).Append( '|' ).Append( (int)point.Stage ).Append( '|' ).Append( point.Order ).Append( '\n' );
		}

		asked[key.ToString()] = copy;
	}

	//What a pipeline is kept under: the kind, the target, whether it carries, the split and the files in order - made once for each run that is asked for
	//and found again by comparing, since the same one or two runs are asked every frame and making the text each time was a builder and a string a call.
	private static string Key( string kind, ReadOnlySpan<Point> chain, int split, int target, bool carry )
	{
		foreach ( var memo in keys )
		{
			if ( memo.Kind != (object)kind || memo.Split != split || memo.Target != target || memo.Carry != carry ) continue;
			if ( memo.Shaders.Length != chain.Length ) continue;

			var same = true;

			for ( var i = 0; i < chain.Length && same; i++ )
				same = string.Equals( memo.Shaders[i], chain[i].Shader, StringComparison.Ordinal );

			if ( same ) return memo.Key;
		}

		var key = new StringBuilder( kind ).Append( '|' ).Append( target ).Append( '|' ).Append( carry ? 1 : 0 ).Append( '|' ).Append( split );
		var shaders = new string[chain.Length];

		for ( var i = 0; i < chain.Length; i++ )
		{
			shaders[i] = chain[i].Shader;
			key.Append( '|' ).Append( chain[i].Shader );
		}

		//A few dozen at most are asked of a session; the oldest go past that.
		if ( keys.Count >= 64 ) keys.RemoveAt( 0 );

		var made = key.ToString();
		keys.Add( new KeyMemo( kind, split, target, carry, shaders, made ) );

		return made;
	}

	private sealed record KeyMemo( string Kind, int Split, int Target, bool Carry, string[] Shaders, string Key );

	private static readonly List<KeyMemo> keys = new();

	//The text of a chain: the frame's own include, each file's body in a namespace of its own with its numbers pointed at
	//its run of the frame block, the calls in order, and whatever draws them - the compose, or a pass's own pixel stage.
	private static string Source( ReadOnlySpan<Point> chain, int split, string defines, string tail )
	{
		var text = new StringBuilder();

		text.Append( "#define SPECIALIZED 1\n" ).Append( defines );
		text.Append( "#define ENGINE_POINT_CHAIN 1\n" );
		text.Append( "#define ENGINE_CHAIN_BASE uint( g_vPostSlots[3].w )\n" );
		text.Append( "#include \"postprocess.slang\"\n" );

		AppendBodies( text, chain, screen: false );

		text.Append( "#line 1 \"the point effects\"\n" );

		text.Append( "float2 ChainWhere( float2 uv )\n{\n" );
		for ( var k = 0; k < chain.Length; k++ )
		{
			if ( Of( chain[k].Shader ) is { Moves: true } ) text.Append( "\tuv = ENGINE_fx" ).Append( k ).Append( "::Where( uv );\n" );
		}
		text.Append( "\treturn uv;\n}\n" );

		text.Append( "float3 ChainBefore( float3 colour, float2 uv )\n{\n" );
		for ( var k = 0; k < split && k < chain.Length; k++ )
		{
			if ( Of( chain[k].Shader ) is { Applies: true } ) text.Append( "\tcolour = ENGINE_fx" ).Append( k ).Append( "::Apply( colour, uv );\n" );
		}
		text.Append( "\treturn colour;\n}\n" );

		text.Append( "float3 ChainAfter( float3 colour, float2 uv )\n{\n" );
		for ( var k = split; k < chain.Length; k++ )
		{
			if ( Of( chain[k].Shader ) is { Applies: true } ) text.Append( "\tcolour = ENGINE_fx" ).Append( k ).Append( "::Apply( colour, uv );\n" );
		}
		text.Append( "\treturn colour;\n}\n" );

		text.Append( tail );

		return text.ToString();
	}

	//Each chain file in its own namespace, includes once in front; screen runs skip the postprocess lib
	private static void AppendBodies( StringBuilder text, ReadOnlySpan<Point> chain, bool screen )
	{
		var included = new HashSet<string>( StringComparer.Ordinal );

		foreach ( var point in chain )
		{
			if ( Of( point.Shader ) is not { } effect ) continue;

			foreach ( var line in effect.Includes )
			{
				if ( screen && ( line.EndsWith( "postprocess.slang\"", StringComparison.Ordinal ) ||
					line.EndsWith( "tonemap.slang\"", StringComparison.Ordinal ) ) ) continue;

				if ( included.Add( line ) ) text.Append( line ).Append( '\n' );
			}
		}

		var offset = 0;

		for ( var k = 0; k < chain.Length; k++ )
		{
			if ( Of( chain[k].Shader ) is not { } effect ) continue;

			var space = "ENGINE_fx" + k;

			text.Append( effect.Features );

			for ( var i = 0; i < effect.Numbers.Length; i++ )
				text.Append( "#define " ).Append( effect.Numbers[i].Name ).Append( ' ' ).Append( Read( effect.Numbers[i], offset + effect.Slot[i] ) ).Append( '\n' );

			foreach ( var output in effect.Outputs )
			{
				text.Append( "RWTexture2D<float4> " ).Append( space ).Append( '_' ).Append( output.Name ).Append( ";\n" );
				text.Append( "#define " ).Append( output.Name ).Append( ' ' ).Append( space ).Append( '_' ).Append( output.Name ).Append( '\n' );
			}

			text.Append( "namespace " ).Append( space ).Append( "\n{\n" );
			text.Append( "#line 1 \"" ).Append( effect.Name ).Append( "\"\n" );
			text.Append( effect.Body );
			text.Append( "\n}\n" );

			foreach ( var name in effect.FeatureNames ) text.Append( "#undef " ).Append( name ).Append( '\n' );
			foreach ( var number in effect.Numbers ) text.Append( "#undef " ).Append( number.Name ).Append( '\n' );
			foreach ( var output in effect.Outputs ) text.Append( "#undef " ).Append( output.Name ).Append( '\n' );

			offset += effect.Slots;
		}
	}

	//Source that lets world shading carry a run itself via EngineScreenChain (see frame.slang)
	private static string ScreenSource( ReadOnlySpan<Point> chain )
	{
		var text = new StringBuilder();

		text.Append( "\n#line 1 \"the frame's point effects\"\n" );
		text.Append( "#define ENGINE_CHAIN_BASE uint( g_frame[0].finish.z )\n" );
		text.Append( "#define g_vFrameSize float4( g_frame[0].settings.yz, 1.0 / max( g_frame[0].settings.yz, float2( 1.0, 1.0 ) ) )\n" );
		text.Append( "float Exposure() { return g_frame[0].finish.x; }\n" );

		AppendBodies( text, chain, screen: true );

		text.Append( "#line 1 \"the frame's point effects\"\n" );
		text.Append( "float3 EngineScreenChain( float3 colour, float2 uv )\n{\n" );

		for ( var k = 0; k < chain.Length; k++ )
		{
			if ( Of( chain[k].Shader ) is { Applies: true } ) text.Append( "\tcolour = ENGINE_fx" ).Append( k ).Append( "::Apply( colour, uv );\n" );
		}

		text.Append( "\treturn colour;\n}\n" );
		text.Append( "#undef g_vFrameSize\n#undef ENGINE_CHAIN_BASE\n" );

		return text.ToString();
	}

	//Whether world shading can carry the run: all pure per-pixel Applies with no passes, and more than just the curve
	internal static bool Foldable( ReadOnlySpan<Point> chain )
	{
		if ( chain.Length == 0 ) return false;

		var more = false;

		foreach ( var point in chain )
		{
			if ( Of( point.Shader ) is not { Applies: true, Moves: false, Passes: false } effect ) return false;
			if ( !effect.Curve ) more = true;
		}

		return more;
	}

	//What a run carried by the world's shading is known by, its text kept under it - see ScreenText.
	internal static string ScreenKeyFor( ReadOnlySpan<Point> chain )
	{
		Fresh();
		Remember( chain );

		var key = Key( "screen", chain, chain.Length, 0, false );

		if ( !screens.ContainsKey( key ) ) screens[key] = ScreenSource( chain );

		return key;
	}

	//The text a finishing pass is compiled with to carry the run known by key - null for none.
	internal static string ScreenText( string key ) => key is not null && screens.TryGetValue( key, out var text ) ? text : null;

	//Whether a run would not compile into some pass of the world's shading, and is drawn by the compose for good.
	internal static bool Refused( string key ) => key is not null && refused.Contains( key );

	//Says a run would not compile into the world's shading - see Refused.
	internal static void Refuse( string key )
	{
		if ( key is not null && refused.Add( key ) )
			log.Warning( $"the point effects {key} would not compile into the world's own shading - the frame keeps a compose for them" );
	}

	//How a number or a picture reads out of the frame block, from the chain's own run.
	private static string Read( CoreShaderParams.Parameter parameter, int slot )
	{
		var at = "g_frame[0].effects[ENGINE_CHAIN_BASE + " + slot + "u]";

		if ( parameter.IsTexture )
			return "( EngineTexture( " + at + ", g_frame[0].effects[ENGINE_CHAIN_BASE + " + ( slot + 1 ) + "u] ) )";

		return "(" + at + parameter.Kind switch
		{
			CoreShaderParams.Kind.Float2 => ".xy",
			CoreShaderParams.Kind.Float3 or CoreShaderParams.Kind.Colour => ".xyz",
			CoreShaderParams.Kind.Float4 => "",
			_ => ".x",
		} + ")";
	}

	private static ulong Build( string key, Func<string> source, CoreRender.TargetKind target, out bool waiting )
	{
		waiting = false;

		var directory = Path.GetDirectoryName( CoreShaderFile.Shipped( "shaders/postprocess.slang" ) ?? "" ) ?? "";
		var id = CoreShaderCache.Revision + "|" + key;

		//The text is only made for a compile nobody has asked for yet.
		string text = null;

		CoreShaderJobs.Request? Ask( string entry, CoreShaderCompiler.Stage stage )
			=> ( text ??= source() ) is { } made
				? new CoreShaderJobs.Request( made, directory, entry, stage, CoreShaderCompiler.Format.Spirv, "point effects",
					"the run is drawn a pass an effect instead - the same picture, a slower frame" )
				: (CoreShaderJobs.Request?)null;

		var vs = CoreShaderJobs.Words( id + "|MainVs", () => Ask( "MainVs", CoreShaderCompiler.Stage.Vertex ), out var vertexWaits );
		var ps = CoreShaderJobs.Words( id + "|MainPs", () => Ask( "MainPs", CoreShaderCompiler.Stage.Pixel ), out var pixelWaits );

		//Compiling beside the frame: nothing yet - see CoreShaderJobs.
		if ( vertexWaits || pixelWaits )
		{
			waiting = true;
			return 0;
		}

		if ( vs is null || ps is null )
		{
			if ( said.Add( key ) ) log.Warning( $"point effects {key} did not compile - drawn a pass each instead" );
			return 0;
		}

		var built = CoreRenderDevice.CreatePipeline( vs, ps, CoreRender.VertexLayoutKind.None, depthTest: false,
			CoreRender.Topology.Triangles, blend: false, depthWrite: false, cullBack: false, target: target );

		if ( built == 0 && said.Add( key ) ) log.Warning( $"point effects {key} would not bake: {CoreRenderDevice.Description}" );

		return built;
	}

	//Writes the chain's numbers into the frame block once; returns the start slot, or -1 when full
	private static int Fill( ReadOnlySpan<Point> chain )
	{
		var slots = 0;

		foreach ( var point in chain ) slots += Of( point.Shader )?.Slots ?? 0;

		if ( slots == 0 ) return 0;

		if ( cursor + slots > CoreSceneRenderer.EffectSlots )
		{
			if ( said.Add( "full" ) )
				log.Warning( $"more point effects' numbers this frame than the frame block holds ({CoreSceneRenderer.EffectSlots} slots) - the rest are drawn a pass each" );

			return -1;
		}

		System.Array.Clear( values, 0, slots * 4 );

		var at = 0;

		foreach ( var point in chain )
		{
			if ( Of( point.Shader ) is not { } effect ) continue;

			for ( var i = 0; i < effect.Numbers.Length; i++ )
			{
				var parameter = effect.Numbers[i];
				var slot = ( at + effect.Slot[i] ) * 4;

				if ( parameter.IsTexture )
				{
					values[slot] = Picture( point, parameter );

					var instead = parameter.Default;
					values[slot + 4] = instead.x;
					values[slot + 5] = instead.y;
					values[slot + 6] = instead.z;
					values[slot + 7] = instead.w;

					continue;
				}

				var value = CorePostBlit.Value( point.Material, point.Attributes, parameter, point.List );

				values[slot] = value.x;
				values[slot + 1] = value.y;
				values[slot + 2] = value.z;
				values[slot + 3] = value.w;
			}

			at += effect.Slots;
		}

		return slots;
	}

	//A chain's numbers into this frame's block, written again where it is drawn - see Fill.
	internal static int Upload( ReadOnlySpan<Point> chain )
	{
		var slots = Fill( chain );

		if ( slots <= 0 ) return slots;

		CoreRenderDevice.PatchFrame( CoreSceneRenderer.EffectsAt + ( cursor * 4 ), values.AsSpan( 0, slots * 4 ) );

		var start = cursor;
		cursor += slots;

		return start;
	}

	//The same into the block before it goes up - for a run the world's own shading
	//carries, which every draw of the frame reads. See CoreSceneDrawer.FoldChain.
	internal static int UploadInto( float[] frame, ReadOnlySpan<Point> chain )
	{
		var slots = Fill( chain );

		if ( slots <= 0 || frame is null ) return slots;

		values.AsSpan( 0, slots * 4 ).CopyTo( frame.AsSpan( CoreSceneRenderer.EffectsAt + ( cursor * 4 ) ) );

		var start = cursor;
		cursor += slots;

		return start;
	}

	//Where a picture a point effect declared is in the table: the one it was handed, by its name.
	private static float Picture( in Point point, CoreShaderParams.Parameter parameter )
	{
		var bare = CoreShaderParams.Bare( parameter.Name );

		return Given( point, parameter.Attribute ) ?? Given( point, bare ) ?? Given( point, parameter.Name ) ?? 0.0f;

		static float? Given( in Point point, string name )
		{
			if ( string.IsNullOrEmpty( name ) ) return null;

			if ( point.Attributes?.GetTexture( name ) is { } given && given.IsValid() ) return given.Index;
			if ( point.List?.Values.GetTexture( name ) is { } listed && listed.IsValid() ) return listed.Index;

			return null;
		}
	}

	//One point effect's file, read and kept; null where it is none, or not there.
	private static Effect Of( string shader )
	{
		if ( string.IsNullOrWhiteSpace( shader ) ) return null;

		Fresh();

		if ( effects.TryGetValue( shader, out var found ) ) return found;

		found = Read( shader );
		effects[shader] = found;

		return found;
	}

	private static Effect Read( string shader )
	{
		var real = CoreShaderFile.Real( shader );
		var file = CoreShaderFile.Source( real );

		if ( file is null ) return null;

		string text;

		try
		{
			text = CompiledFile.TextOf( file );
		}
		catch ( IOException )
		{
			return null;
		}

		var bare = Uncommented( text );

		var effect = new Effect
		{
			Name = Portable( real, file ),
			Applies = ApplyEntry().IsMatch( bare ),
			Moves = WhereEntry().IsMatch( bare ),
			Curve = CoreShaderState.Parse( text ).Curve,
			Passes = EntryAttribute().IsMatch( bare ),
		};

		if ( !effect.Applies && !effect.Moves ) return effect;

		//The blocks are not Slang, and cut rather than removed so an error's line is still the line in the file.
		var body = CoreShaderFeatures.Without( CoreShaderFeatures.Without( CoreShaderFeatures.Without( text ),
			CoreShaderParams.BlockName ), CoreShaderState.BlockName );

		//Includes go once in front of all namespaces, written by resource path so baked chains match
		var includes = new List<string>();
		var directory = Path.GetDirectoryName( file ) ?? "";
		var lines = body.Split( '\n' );

		for ( var i = 0; i < lines.Length; i++ )
		{
			var trimmed = lines[i].Trim();

			if ( !trimmed.StartsWith( "#include", StringComparison.Ordinal ) ) continue;

			var first = trimmed.IndexOf( '"' );
			var last = trimmed.LastIndexOf( '"' );

			if ( first >= 0 && last > first )
			{
				var named = trimmed[( first + 1 )..last];
				var beside = Path.GetFullPath( Path.Combine( directory, named ) );

				includes.Add( "#include \"" +
					( File.Exists( beside ) && !named.Equals( "postprocess.slang", StringComparison.Ordinal )
						? CoreShaderFile.Named( beside ) ?? Slashed( beside )
						: named ) + "\"" );
			}

			lines[i] = "";
		}

		//And its own entries are ordinary functions here - a bloom's compute passes sit in the same file as its Apply, and nothing in a chain runs them.
		body = EntryAttribute().Replace( string.Join( '\n', lines ), "" );

		effect.Body = body;
		effect.Includes = includes.ToArray();

		var features = CoreShaderFeatures.Of( file );
		effect.Features = CoreShaderFeatures.DefinesFor( features, null );
		effect.FeatureNames = System.Array.ConvertAll( features, x => x.Name );

		var numbers = new List<CoreShaderParams.Parameter>();
		var slots = new List<int>();
		var slot = 0;

		foreach ( var parameter in CoreShaderParams.Of( file ) )
		{
			numbers.Add( parameter );
			slots.Add( slot );
			slot += parameter.IsTexture ? 2 : 1;
		}

		effect.Numbers = numbers.ToArray();
		effect.Slot = slots.ToArray();
		effect.Slots = slot;
		effect.Outputs = CoreShaderParams.OutputsOf( file );

		return effect;
	}

	[GeneratedRegex( @"\bfloat3\s+Apply\s*\(" )]
	private static partial Regex ApplyEntry();

	[GeneratedRegex( @"\bfloat2\s+Where\s*\(" )]
	private static partial Regex WhereEntry();

	[GeneratedRegex( @"\[\s*(?:shader\s*\(\s*""[a-z]+""\s*\)|numthreads\s*\([^)]*\))\s*\]" )]
	private static partial Regex EntryAttribute();

	private static string Uncommented( string text )
	{
		text = Regex.Replace( text, @"/\*.*?\*/", " ", RegexOptions.Singleline );
		return Regex.Replace( text, @"//[^\n]*", "" );
	}

	private static string Slashed( string path ) => path.Replace( '\\', '/' );

	//Machine-independent name for a shader file: its resource path, else the full path as a last resort
	private static string Portable( string resource, string file )
		=> Path.IsPathRooted( resource )
			? CoreShaderFile.Named( file ) ?? Slashed( file )
			: Slashed( resource );

	//Compiles what a machine with no compiler will need of the point effects in files: each one as a pass of its own - what a frame whose chain was
	//never baked is drawn with - and the compose with nothing in it but the engine's curve, carried up and not. False where one would not compile.
	internal static bool Bake( IEnumerable<string> files )
	{
		Fresh();

		var whole = true;
		var count = 0;

		foreach ( var file in files )
		{
			if ( !IsPoint( file ) ) continue;

			count++;

			var alone = new[] { new Point( null, null, null, file, Rinten.Rendering.Stage.BeforePostProcess, 0 ) };

			if ( !Compiles( Source( alone, alone.Length, "", PassTail ) ) )
			{
				log.Error( $"{file} as a point effect did not compile: {CoreRenderDevice.ShaderError}" );
				whole = false;
			}
		}

		var curve = new List<Point>();
		var split = WithCurve( curve ) + 1;

		var bytes = 0;

		foreach ( var carry in new[] { false, true } )
		{
			var made = PixelWords( ComposeSource( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( curve ), split, carry ) );

			if ( made is not null )
			{
				if ( !carry ) bytes = made.Length;
				continue;
			}

			log.Error( $"the compose with the engine's curve{( carry ? ", carried up" : "" )} did not compile: {CoreRenderDevice.ShaderError}" );
			whole = false;
		}

		log.Info( $"{count} point effects, each as a pass of its own, and the compose with the engine's curve ({bytes / 1024} KB of SPIR-V)" );

		return whole;
	}

	//Bakes every asked run plus its subsets (effects switched off), nearest to whole first, up to `most`
	internal static (int Runs, int Variants) BakeVariants( int most = 64 )
	{
		Fresh();

		var runs = 0;
		var variants = 0;

		//Iterate a copy: baking records new runs, and mutating the dictionary mid-walk throws
		foreach ( var run in asked.Values.ToArray() )
		{
			if ( run.Length == 0 || run.Length > 30 ) continue;

			runs++;

			var made = 0;

			//Whole first, then one effect missing, then two - a run that is too long to enumerate is covered where it matters and not past that.
			for ( var gone = 0; gone < run.Length && made < most; gone++ )
			{
				for ( long mask = 0; mask < 1L << run.Length && made < most; mask++ )
				{
					if ( System.Numerics.BitOperations.PopCount( (ulong)mask ) != gone ) continue;

					var subset = new List<Point>( run.Length );

					for ( var i = 0; i < run.Length; i++ )
					{
						if ( ( mask & ( 1L << i ) ) == 0 ) subset.Add( run[i] );
					}

					if ( subset.Count == 0 ) continue;

					Variant( subset );

					made++;
				}
			}

			variants += made;
		}

		return ( runs, variants );
	}

	//Bakes whole foldable runs into each surface's finishing stages for the portable frame (no compiler)
	internal static int BakeScreens( IEnumerable<(string Shader, string Combo)> surfaces )
	{
		Fresh();

		var all = surfaces.Append( (CoreSceneRenderer.SkyShader, null) ).Distinct().ToArray();
		var runs = 0;
		var stages = 0;
		var failed = 0;

		//Iterate a copy: baking records new runs, and mutating the dictionary mid-walk throws
		foreach ( var run in asked.Values.ToArray() )
		{
			var chain = new List<Point>( run );
			WithCurve( chain );

			if ( !Foldable( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( chain ) ) ) continue;

			var text = ScreenText( ScreenKeyFor( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( chain ) ) );

			foreach ( var (shader, combo) in all )
			{
				foreach ( var stage in new[] { CoreShaderFile.Stage.ScreenPixel, CoreShaderFile.Stage.SkinnedScreenPixel } )
				{
					//The sky is never skinned.
					if ( stage == CoreShaderFile.Stage.SkinnedScreenPixel && shader == CoreSceneRenderer.SkyShader ) continue;

					if ( CoreShaderFile.ScreenChainText( shader, combo, stage, text ) is not { } made ) continue;

					if ( CoreShaderCache.Words( made.Text, made.Directory, made.Entry, made.Kind, CoreShaderCache.Target,
						"point effects in " + shader ) is null )
					{
						failed++;
						log.Warning( $"{shader} ({combo}) {stage} with the point effects did not compile for {CoreShaderCache.Target}: {CoreRenderDevice.ShaderError}" );
					}
					else stages++;
				}
			}

			//And the finishes of a frame drawn aside, which carry the run beside bloom or passes - see CorePortableGlow
			foreach ( var finish in CorePortableGlow.ChainRequests( ScreenKeyFor( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( chain ) ) ) )
			{
				if ( CoreShaderCache.Words( finish.Source, finish.Directory, finish.Entry, finish.Stage, CoreShaderCache.Target, "point effects in the finish" ) is null )
				{
					failed++;
					log.Warning( $"the glow's {finish.Entry} with the point effects did not compile for {CoreShaderCache.Target}: {CoreRenderDevice.ShaderError}" );
				}
				else stages++;
			}

			runs++;
		}

		log.Info( $"{runs} of {asked.Count} runs of point effects compiled into {stages} surface stages for {CoreShaderCache.Target} ({failed} did not)" );

		return runs;
	}

	//One run compiled every way a frame can draw it: as a pass of its own, and into the compose.
	private static void Variant( List<Point> chain )
	{
		Compiles( Source( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( chain ), chain.Count, "", PassTail ) );

		//Into the compose the way a frame builds it: the curve where the run has none, and the split at it - see CoreSceneDrawer.
		var composed = new List<Point>( chain );
		var split = WithCurve( composed ) + 1;

		foreach ( var carry in new[] { false, true } )
		{
			PixelWords( ComposeSource( System.Runtime.InteropServices.CollectionsMarshal.AsSpan( composed ), split, carry ) );
		}
	}

	private static bool Compiles( string source ) => PixelWords( source ) is not null;

	//A chain's source compiled, both stages: the pixel stage's words, or null where either failed.
	private static byte[] PixelWords( string source )
	{
		if ( source is null ) return null;

		var directory = Path.GetDirectoryName( CoreShaderFile.Shipped( "shaders/postprocess.slang" ) ?? "" ) ?? "";

		if ( CoreShaderCache.Words( source, directory, "MainVs", CoreShaderCompiler.Stage.Vertex, CoreShaderCompiler.Format.Spirv, "point effects" ) is null )
			return null;

		return CoreShaderCache.Words( source, directory, "MainPs", CoreShaderCompiler.Stage.Pixel, CoreShaderCompiler.Format.Spirv, "point effects" );
	}
}

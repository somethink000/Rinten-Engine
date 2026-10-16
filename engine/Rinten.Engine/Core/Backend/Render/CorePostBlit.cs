using System;

namespace Rinten;

//A project's full-screen pass: one material over a screen triangle, reading frame pictures bindlessly
internal static class CorePostBlit
{
	private static readonly Diagnostics.Logger log = new( "Render" );

	//What a blit may read and where it draws; set only during a render stage, so stray blits draw nothing
	internal readonly record struct Frame(
		CoreRender.TargetKind Target,
		int Colour, int Normals, int Depth, int Velocity,
		float Width, float Height, float Near, float Far, float Stage,
		ulong Viewport );

	private static Frame? open;

	//Which picture the chain writes and which it reads; they swap after each blit to avoid feedback
	private static CoreRender.TargetKind from;
	private static bool onScreen;

	//The pair this stage alternates between: world + spare before the compose, two screen-size ones after
	private static CoreRender.TargetKind first = CoreRender.TargetKind.Scene;
	private static CoreRender.TargetKind second = CoreRender.TargetKind.Post;

	//Which picture the chain left the frame in.
	internal static CoreRender.TargetKind Settled => from;

	//The portable frame's passes, which alternate between two frame pictures rather than the frame's targets, the
	//frame bound in each one's colour slot: the two, and where the frame is - the world drawn aside, kept, to begin
	private static bool portable;
	private static ulong portableFirst, portableSecond;
	private static ulong portableFrom, portableTexture;
	private static bool portableKept;

	/// <summary>What the portable frame's passes left the frame in, and whether it is light rather than kept.</summary>
	internal static (ulong Texture, bool Light) PortableSettled => ( portableTexture, !portableKept );


	//Point effects queued but not drawn: the next pass draws them as one, or the compose compiles them in
	private static readonly List<CorePointChain.Point> pending = new();

	//The ones being drawn, apart from the ones still being asked for.
	private static readonly List<CorePointChain.Point> drawing = new();
	private static readonly CorePointChain.Point[] alone = new CorePointChain.Point[1];

	//Whether any point effect is held.
	internal static bool Pending => pending.Count > 0;

	//The order the list being run was hung with - where a point effect it asks for sits.
	internal static int ListOrder { get; set; }

	//Whether this stage's point effects outlive it - see Begin.
	private static bool holding;

	//A frame begins: nothing is held from another, and its block's effects are empty.
	internal static void NewFrame()
	{
		pending.Clear();
		Landed = false;
		LastBeforeWindow = false;
		CorePointChain.NewFrame();
	}

	//A point effect asked for - held, see pending. On the screen, where the frame ends, drawn at once.
	internal static void Point( Material material, RenderAttributes attributes, Rinten.Rendering.CommandList list )
	{
		//The portable frame's are carried by its finish, gathered before the frame - see CoreSceneDrawer.FoldChain
		if ( open is not { } frame || portable ) return;

		var shader = material?.core?.Shader;

		if ( string.IsNullOrWhiteSpace( shader ) ) return;

		if ( !CorePointChain.IsPoint( shader ) )
		{
			Missing( material.Name, $"{shader} is not a point effect - it has no float3 Apply( float3 colour, float2 uv ) and no float2 Where( float2 uv )" );
			return;
		}

		pending.Add( new CorePointChain.Point( material, attributes, list, shader,
			(Rinten.Rendering.Stage)(int)frame.Stage, ListOrder ) );

		if ( onScreen ) Flush();
	}

	//The held ones, for the compose to be compiled with. Once taken, none are held.
	internal static void TakeChain( List<CorePointChain.Point> into )
	{
		into.AddRange( pending );
		pending.Clear();
	}

	//Draws the held point effects now, as one pass, for something about to read the frame - or a pass each, where the one could not be compiled.
	internal static void Flush()
	{
		if ( pending.Count == 0 || open is not { } frame || portable ) return;

		drawing.Clear();
		drawing.AddRange( pending );
		pending.Clear();

		CorePasses.Close();

		if ( Chain( frame, System.Runtime.InteropServices.CollectionsMarshal.AsSpan( drawing ) ) ) return;

		foreach ( var point in drawing ) Single( point );
	}

	//One point effect as a pass of its own - what a chain that would not compile is drawn as.
	internal static void Single( in CorePointChain.Point point )
	{
		if ( open is not { } frame ) return;

		alone[0] = point;

		if ( !Chain( frame, alone ) && !CorePointChain.Waited )
			Missing( point.Material?.Name ?? point.Shader, $"{point.Shader} would not compile as a point effect: {CoreRenderDevice.ShaderError}" );

		alone[0] = default;
	}

	//A run of point effects drawn over the frame as one pass. False where it could not be built.
	private static bool Chain( in Frame frame, ReadOnlySpan<CorePointChain.Point> chain )
	{
		var into = Into;
		var pipeline = CorePointChain.Pass( chain, into );

		if ( pipeline == 0 ) return false;

		var at = CorePointChain.Upload( chain );

		if ( at < 0 ) return false;

		var offscreen = !onScreen;

		//Every pixel is written: nothing of what was there is read in first.
		if ( offscreen && !CoreRenderDevice.BeginTarget( into, Color.Transparent, clearFirst: false, discard: true ) )
		{
			Missing( "point effects", $"the picture they draw into would not open: {CoreRenderDevice.Description}" );
			return true;
		}

		CoreRenderDevice.ZoneBegin( "point effects" );

		var bytes = Push( frame with { Colour = Reading( frame ) }, null, null, System.Array.Empty<CoreShaderParams.PostSlot>() );

		//Where the chain's numbers start in the frame block - see ENGINE_CHAIN_BASE.
		System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( bytes.AsSpan( 64 ) )[15] = at;

		CoreRenderDevice.Draw( pipeline, 3, bytes, CoreSceneDrawer.WhiteTexture );

		CoreRenderDevice.ZoneEnd();

		if ( offscreen ) CoreRenderDevice.EndTarget();

		Turn();

		return true;
	}

	//The compose's push block: every pass's, with the compose's own numbers in its tail - the size of what it
	//carries up, whether it softens the edges, and where its point effects' numbers start. See compose.slang.
	internal static Span<byte> ComposePush( Span<byte> into, Vector2 size, Vector2 source, bool soften, int chain,
		float near, float far, int colour, int normals, int depth, int velocity )
	{
		var span = into[..128];
		span.Clear();

		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( span );

		floats[0] = size.x;
		floats[1] = size.y;
		floats[2] = size.x > 0.0f ? 1.0f / size.x : 0.0f;
		floats[3] = size.y > 0.0f ? 1.0f / size.y : 0.0f;

		floats[4] = near;
		floats[5] = far;
		floats[6] = (float)(int)Rinten.Rendering.Stage.Tonemapping;

		floats[8] = colour;
		floats[9] = normals;
		floats[10] = depth;
		floats[11] = velocity;

		floats[12] = 1.0f;
		floats[13] = 1.0f;
		floats[14] = 1.0f;
		floats[15] = (float)( RealTime.Now % 1000.0 );

		floats[16] = source.x;
		floats[17] = source.y;
		floats[18] = soften ? 1.0f : 0.0f;

		floats[31] = chain;

		return span;
	}

	//Where the next blit draws; never TargetScene, whose normals would be left attached and undefined
	private static CoreRender.TargetKind Into
		=> onScreen
			? CoreRender.TargetKind.Screen
			: Writable( from == first ? second : first );

	//A blending blit draws over the picture the frame is in, not the other one, or it blends stale frames
	private static CoreRender.TargetKind Here
		=> onScreen
			? CoreRender.TargetKind.Screen
			: Writable( from );

	//Target that writes a pair picture; the world's goes through SceneColour so normals aren't attached
	private static CoreRender.TargetKind Writable( CoreRender.TargetKind kind )
		=> kind == CoreRender.TargetKind.Scene ? CoreRender.TargetKind.SceneColour : kind;

	//Opens the frame a stage's blits are drawn in.
	internal static void Begin( in Frame frame, CoreRender.TargetKind heldIn = CoreRender.TargetKind.Scene,
		(CoreRender.TargetKind First, CoreRender.TargetKind Second)? pair = null, bool holds = false )
	{
		open = frame;
		holding = holds;

		( first, second ) = pair ?? ( CoreRender.TargetKind.Scene, CoreRender.TargetKind.Post );

		//Where the frame is, which is what the first effect of the stage reads.
		from = heldIn == second ? second : first;

		onScreen = frame.Target == CoreRender.TargetKind.Screen;
	}

	/// <summary>Opens the portable frame's passes over the world drawn aside, which alternate between the two pictures.</summary>
	internal static void BeginPortable( in Frame frame, ulong world, (ulong First, ulong Second) pictures )
	{
		open = frame;
		holding = true;
		onScreen = false;
		portable = true;

		( portableFirst, portableSecond ) = pictures;
		portableFrom = 0;
		portableTexture = world;
		portableKept = true;
	}

	/// <summary>The stage the portable frame's passes are being asked for now.</summary>
	internal static void PortableStage( Rinten.Rendering.Stage stage )
	{
		if ( open is { } frame ) open = frame with { Stage = (float)(int)stage };
	}

	//And closes it, so nothing draws outside a stage - a list's picture included.
	internal static void End()
	{
		if ( !holding ) Flush();

		CorePasses.Close();
		open = null;
		holding = false;
		portable = false;
	}

	//Whether anything would be drawn at all.
	internal static bool Ready => open.HasValue;

	//Where a command list's own draws land when it named no picture: the frame's current one, in place
	internal static bool FrameTarget( bool depth, out CoreRender.TargetKind target, out bool opens,
		out bool withDepth, out bool finishes )
	{
		target = CoreRender.TargetKind.Screen;
		opens = false;
		withDepth = false;
		finishes = false;

		if ( open is not { } frame ) return false;

		//Before the world has been shaded its picture holds nothing yet, and the world's pass clears it when it opens: a draw into the frame here would be
		//wiped - or, writing the depth the world is about to be shaded against, punch holes in it. A list draws into a picture of its own at this stage.
		if ( (Rinten.Rendering.Stage)(int)frame.Stage == Rinten.Rendering.Stage.AfterDepthPrepass )
		{
			if ( said.Add( "frame before the world" ) )
				log.Warning( "a command list drew into the frame at AfterDepthPrepass, before the world is in it - draw into a render target of its own there" );

			return false;
		}

		if ( onScreen )
		{
			withDepth = true;
			finishes = true;
			return true;
		}

		opens = true;

		if ( from == CoreRender.TargetKind.Scene )
		{
			target = depth ? CoreRender.TargetKind.Scene : CoreRender.TargetKind.SceneColour;
			withDepth = depth;
			return true;
		}

		target = Writable( from );
		finishes = target is CoreRender.TargetKind.Final or CoreRender.TargetKind.FinalSecond;

		return true;
	}

	//The size of the picture this stage draws - the world's, or after the compose the window's.
	internal static Vector2 OpenSize => open is { } frame ? new Vector2( frame.Width, frame.Height ) : default;

	//Where the world's depth sits in the table for this stage - nought outside one.
	internal static int OpenDepthIndex => open?.Depth ?? 0;

	//Where the frame as it stands sits in the table for this stage - nought outside one.
	internal static int OpenColourIndex => open is { } frame ? Reading( frame ) : 0;

	//After a blit: what was written is what the next one reads.
	private static void Turn()
	{
		if ( onScreen ) return;

		from = from == first ? second : first;
	}

	//Draws one material over the whole screen; its pipeline is baked on first use and kept
	internal static void Draw( Material material, RenderAttributes attributes, Rinten.Rendering.CommandList list = null,
		string entry = null, int blend = 0, bool frameMips = false )
	{
		if ( open is null ) return;

		//A pass reads the frame, so the point effects held for it go on first.
		Flush();

		//The last thing the frame gets before the window: drawn on the window, and the copy onto it that would otherwise follow is not made - see Landed.
		if ( Lands( material, list, entry, blend, frameMips ) )
		{
			onScreen = true;

			try
			{
				Pass( material, attributes, list, entry: entry, asked: blend );
			}
			finally
			{
				onScreen = false;
			}

			Landed = true;
			return;
		}

		Pass( material, attributes, list, entry: entry, asked: blend, mips: frameMips );
	}

	//Set by the drawer while it runs the last list with anything in it, of the stage after the compose, on a frame that copies onto the window after it.
	internal static bool LastBeforeWindow { get; set; }

	//Whether the frame's last pass has drawn onto the window itself this frame, so there is nothing left to copy there - see CoreSceneDrawer.FinalToScreen.
	internal static bool Landed { get; set; }

	//Whether a pass can draw straight onto the window: last replacing entry, no mips, no own picture
	private static bool Lands( Material material, Rinten.Rendering.CommandList list, string entry, int blend, bool frameMips )
		=> LastBeforeWindow && list is not null && list.AtLast && !frameMips && !onScreen && !CorePasses.Wanting &&
			material?.core?.Shader is { } shader && !string.IsNullOrWhiteSpace( shader ) &&
			BlendOf( blend, shader, entry ) == 0;

	//A pass's readable name in the frame report (shader file and entry), cached to avoid per-frame allocs
	private static string Named( Material material, string shader, string entry )
	{
		if ( names.TryGetValue( (shader, entry), out var had ) ) return had;

		var name = material?.Name;

		//One the engine made for a shader says nothing the shader's own name does not say better.
		if ( name is null || name.StartsWith( "__shader_", StringComparison.Ordinal ) )
		{
			name = shader;

			var slash = name.LastIndexOf( '/' );
			if ( slash >= 0 ) name = name[(slash + 1)..];

			var dot = name.LastIndexOf( '.' );
			if ( dot > 0 ) name = name[..dot];
		}

		var made = string.IsNullOrWhiteSpace( entry ) ? name : $"{name} {entry}";

		names[(shader, entry)] = made;

		return made;
	}

	private static readonly Dictionary<(string Shader, string Entry), string> names = new();

	//One full-screen pass, drawn - see Draw.
	private static void Pass( Material material, RenderAttributes attributes, Rinten.Rendering.CommandList list = null, bool frameOnly = false,
		string entry = null, int asked = 0, bool mips = false )
	{
		if ( open is not { } frame ) return;
		if ( material is null || material.core is null ) return;

		var shader = material.core.Shader;

		if ( string.IsNullOrWhiteSpace( shader ) )
		{
			Missing( material.Name, "it names no shader" );
			return;
		}

		if ( portable )
		{
			PortablePass( material, attributes, list, shader, frame, entry, asked );
			return;
		}

		//Into the picture the list named with SetRenderTarget, the frame left as it was - see CorePasses. It reads the frame as the next pass over it would.
		if ( !frameOnly && CorePasses.Wanting )
		{
			PassIntoPicture( material, attributes, list, shader, frame, entry, asked );
			return;
		}

		//And nothing of a list's picture left open under a pass into the frame.
		CorePasses.Close();

		//How it lands: 0 replaces, others add light (see PipelineKind.PostBlend); set by shader or draw
		var blend = BlendOf( asked, shader, entry );

		//One that blends lays what it draws over the frame where the frame is, which therefore does not move - see Here. It cannot read the
		//frame, for the same reason: it is writing it. A pass at a smaller size is a pass into one of the frame's pictures - see CorePasses.
		var laid = blend != 0;

		var into = laid ? Here : Into;

		var kind = new CoreSceneRenderer.PipelineKind( false, false, false )
		{
			Shader = shader,

			//Where this one goes, which is not always where the stage started: a chain alternates,
			//so the second effect of a stage draws into the picture the first one read.
			Target = into,

			//No vertices, no depth: three of them out of nothing, covering everything, over a picture that is already finished.
			Pulled = true,
			Post = true,
			PostBlend = blend,
			Entry = entry,
		};

		var pipeline = CoreResources.Pipeline( kind );

		if ( pipeline == 0 )
		{
			Missing( material.Name, $"{shader} would not bake: {CoreRenderDevice.ShaderError}" );
			return;
		}

		//What the effect set this frame, under the names its shader declared. The numbers that fit the push block go
		//there - see Push - and only the rest touch the material, because touching the material is rebuilding it.
		var layout = Layout( shader );

		Numbers( material, attributes, layout );

		//Effects that read blurred copies (bloom) need the current picture's mip chain filled first
		if ( !onScreen && mips )
		{
			CoreRenderDevice.PictureMips( from );
		}

		var device = CoreResources.Live( material );

		if ( device == 0 )
		{
			Missing( material.Name, "it has nothing on the device" );
			return;
		}

		//Opened here as the target is only known now; not cleared, a full-screen pass covers every pixel
		var offscreen = !onScreen;

		//A pass that replaces the picture writes every pixel of it, so what was there need not be read in first. One that adds to it needs it.
		if ( offscreen && !CoreRenderDevice.BeginTarget( into, Color.Transparent, clearFirst: false, discard: blend == 0 ) )
		{
			Missing( material.Name, $"the picture it draws into would not open: {CoreRenderDevice.Description}" );
			return;
		}

		//Its own zone, named for the pass, so a frame with ten of these in it shows ten rows in the profiler and not one - see Named.
		CoreRenderDevice.ZoneBegin( Named( material, shader, entry ) );

		//And what it reads is where the frame has got to, which is the other picture once anything in this stage has drawn.
		CoreRenderDevice.Draw( pipeline, 3,
			Push( frame with { Colour = Reading( frame ) }, attributes, material, layout, list: list ), device );

		CoreRenderDevice.ZoneEnd();

		if ( offscreen ) CoreRenderDevice.EndTarget();

		if ( !laid ) Turn();
	}

	//One pass in the portable frame, from the picture the frame is in into the other, which it reads in its colour slot
	private static void PortablePass( Material material, RenderAttributes attributes, Rinten.Rendering.CommandList list, string shader,
		in Frame frame, string entry, int asked )
	{
		var blend = BlendOf( asked, shader, entry );

		//Into the picture the list named, reading the frame where it is, which stays there - see PassIntoPicture
		if ( CorePasses.Wanting )
		{
			if ( !CorePasses.Open( depth: false, out var wantedFormat ) )
			{
				Missing( material.Name, $"the picture {CorePasses.Wanted?.Name} would not open" );
				return;
			}

			var named = CorePasses.Wanted;

			PortableDraw( material, attributes, list, shader, frame, entry, blend, wantedFormat, named.Width, named.Height, portableTexture );
			return;
		}

		//Laid over the frame where it is, which has to be light to be added to: the world as drawn aside is copied over first
		if ( blend != 0 && portableKept )
		{
			PortablePass( CopyMaterial, null, null, CopyMaterial.core?.Shader, frame, null, 0 );
			if ( portableKept ) return;
		}

		var into = blend != 0 ? portableFrom : portableFrom == portableFirst ? portableSecond : portableFirst;

		if ( into == 0 || !CoreRenderDevice.PictureShape( into, out var wide, out var tall, out _, out var format ) ) return;

		CorePasses.Close();

		if ( !CoreRenderDevice.BeginPicture( into, 0, default, discard: blend == 0 ) )
		{
			Missing( material.Name, $"the picture it draws into would not open: {CoreRenderDevice.Description}" );
			return;
		}

		//One laid over the frame cannot read what it writes, and is given nothing of it to read
		var drawn = PortableDraw( material, attributes, list, shader, frame, entry, blend, format, wide, tall,
			blend != 0 ? CoreSceneDrawer.WhiteTexture : portableTexture );

		CoreRenderDevice.EndTarget();

		if ( blend != 0 || !drawn ) return;

		portableFrom = into;
		portableTexture = CoreRenderDevice.PictureTexture( into );
		portableKept = false;
	}

	//The draw of a portable pass into the picture open, reading the frame from the colour slot; false where it could not be made
	private static bool PortableDraw( Material material, RenderAttributes attributes, Rinten.Rendering.CommandList list, string shader,
		in Frame frame, string entry, int blend, int format, int wide, int tall, ulong reads )
	{
		var kind = new CoreSceneRenderer.PipelineKind( false, false, false )
		{
			Shader = shader,
			Target = CoreRender.TargetKind.Picture,
			PictureFormat = format,
			Pulled = true,
			Post = true,
			PostBlend = blend,
			Entry = entry,
		};

		var pipeline = CoreResources.Pipeline( kind );

		if ( pipeline == 0 )
		{
			Missing( material.Name, $"{shader} would not bake for the portable frame: {CoreRenderDevice.ShaderError}" );
			return false;
		}

		var layout = Layout( shader );

		Numbers( material, attributes, layout );

		var device = CoreResources.LiveReading( material, reads );

		if ( device == 0 )
		{
			Missing( material.Name, "it has nothing on the device" );
			return false;
		}

		CoreRenderDevice.ZoneBegin( Named( material, shader, entry ) );

		CoreRenderDevice.Draw( pipeline, 3,
			Push( frame with { Colour = 1, Normals = 0, Depth = 0, Velocity = 0 }, attributes, material, layout,
				pictureSize: new Vector2( wide, tall ), list: list, kept: portableKept && reads == portableTexture ), device );

		CoreRenderDevice.ZoneEnd();
		return true;
	}

	//One full-screen pass into the list's picture - see Pass.
	private static void PassIntoPicture( Material material, RenderAttributes attributes, Rinten.Rendering.CommandList list, string shader,
		in Frame frame, string entry, int asked )
	{
		var blend = BlendOf( asked, shader, entry );

		if ( !CorePasses.Open( depth: false, out var format ) )
		{
			Missing( material.Name, $"the picture {CorePasses.Wanted?.Name} would not open" );
			return;
		}

		var into = CorePasses.Wanted;

		var kind = new CoreSceneRenderer.PipelineKind( false, false, false )
		{
			Shader = shader,
			Target = CoreRender.TargetKind.Picture,
			PictureFormat = format,
			Pulled = true,
			Post = true,
			PostBlend = blend,
			Entry = entry,
		};

		var pipeline = CoreResources.Pipeline( kind );

		if ( pipeline == 0 )
		{
			Missing( material.Name, $"{shader} would not bake for {into.Name}: {CoreRenderDevice.ShaderError}" );
			return;
		}

		var layout = Layout( shader );

		Numbers( material, attributes, layout );

		var device = CoreResources.Live( material );

		if ( device == 0 )
		{
			Missing( material.Name, "it has nothing on the device" );
			return;
		}

		CoreRenderDevice.ZoneBegin( Named( material, shader, entry ) );

		CoreRenderDevice.Draw( pipeline, 3,
			Push( frame with { Colour = Reading( frame ) }, attributes, material, layout,
				pictureSize: new Vector2( into.Width, into.Height ), list: list ), device );

		CoreRenderDevice.ZoneEnd();
	}

	//Push block for a standalone pass that reads nothing of the frame (fog slices); runs outside stages
	internal static byte[] ApartPush( string shader, RenderAttributes attributes, int width, int height )
		=> Push( default, attributes, null, Layout( shader ),
			pictureSize: new Vector2( Math.Max( width, 1 ), Math.Max( height, 1 ) ) );

	//The push block of a compute pass written against postprocess.slang - the blit's own,
	//sized for the picture it stores into. Null outside a stage. See ComputeShader.RunInList.
	internal static byte[] ComputePush( string shader, RenderAttributes attributes, Rinten.Rendering.CommandList list, int width, int height )
	{
		if ( open is not { } frame ) return null;

		return Push( frame with { Colour = Reading( frame ) }, attributes, null, Layout( shader ),
			pictureSize: new Vector2( Math.Max( width, 1 ), Math.Max( height, 1 ) ), list: list );
	}

	private static Material CopyMaterial => field ??= Material.FromShader( "shaders/postprocess/copy.shader" );

	//The frame as it stands, copied into one of the frame's pictures - see CorePasses.Grab.
	internal static void CopyInto( CorePasses.Named into )
	{
		if ( open is null || into is null || into.Picture == 0 ) return;

		//The point effects held are part of the frame as it stands.
		Flush();

		var was = CorePasses.Wanted;

		CorePasses.Want( into );

		//Every pixel is written: nothing to clear first.
		into.Cleared = true;

		Pass( CopyMaterial, null );

		CorePasses.Want( was );
	}

	//Which picture this blit samples as "what has been drawn so far".
	private static int Reading( in Frame frame )
		=> from == first
			? frame.Colour
			: CoreSceneDrawer.Indexed( CoreRenderDevice.TargetTexture( frame.Viewport, from ) );

	//Shared push block rewritten per blit: frame sizes, picture indices, a free vector, shader numbers
	private static readonly byte[] push = new byte[128];

	private static byte[] Push( in Frame frame, RenderAttributes attributes,
		Material material, CoreShaderParams.PostSlot[] layout,
		Vector2? pictureSize = null, Rinten.Rendering.CommandList list = null, bool kept = false )
	{
		System.Array.Clear( push );

		//The size of the picture being written, which for a pass into one of the frame's pictures is that picture's own - or every texel step it takes would be
		//off by however much smaller it is. Not the frame's divided: a picture with a chain is rounded so its levels halve exactly - see CorePasses.Acquire.
		float wide = frame.Width;
		float tall = frame.Height;

		//And one of the frame's pictures, which is the size it was made.
		if ( pictureSize is { } given )
		{
			wide = given.x;
			tall = given.y;
		}

		var size = new System.Numerics.Vector4( wide, tall,
			wide > 0.0f ? 1.0f / wide : 0.0f,
			tall > 0.0f ? 1.0f / tall : 0.0f );

		var range = new System.Numerics.Vector4( frame.Near, frame.Far, frame.Stage, kept ? 1.0f : 0.0f );

		//Indices rather than handles: what a shader indexes the table with.
		var pictures = new System.Numerics.Vector4( frame.Colour, frame.Normals, frame.Depth, frame.Velocity );

		var asked = new System.Numerics.Vector4(
			Number( attributes, "blend", 1.0f ),
			Number( attributes, "amount", 1.0f ),
			Number( attributes, "strength", 1.0f ),
			Number( attributes, "time", (float)RealTime.Now ) );

		var bytes = push.AsSpan();

		System.Runtime.InteropServices.MemoryMarshal.Write( bytes, in size );
		System.Runtime.InteropServices.MemoryMarshal.Write( bytes[16..], in range );
		System.Runtime.InteropServices.MemoryMarshal.Write( bytes[32..], in pictures );
		System.Runtime.InteropServices.MemoryMarshal.Write( bytes[48..], in asked );

		//And the shader's own numbers, packed into the tail the way the defines in the shader expect them - see CoreShaderParams.PostLayout.
		//Each is what the effect set this frame, or what the material file says, or what the shader declared as its default, in that order.
		var slots = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( bytes[64..] );

		foreach ( var (parameter, offset) in layout )
		{
			var value = Value( material, attributes, parameter, list );

			slots[offset] = value.x;
			if ( parameter.Width > 1 ) slots[offset + 1] = value.y;
			if ( parameter.Width > 2 ) slots[offset + 2] = value.z;
			if ( parameter.Width > 3 ) slots[offset + 3] = value.w;
		}

		return push;
	}

	//Where one shader's numbers sit in the push block, remembered. Asked once a blit rather than worked out
	//once a blit: the parameters are parsed out of the file, and the file does not change between frames.
	private static CoreShaderParams.PostSlot[] Layout( string shader )
	{
		if ( layouts.TryGetValue( shader, out var found ) ) return found;

		var made = CoreShaderParams.PostLayout( CoreResources.Declared( shader ) );

		layouts[shader] = made;

		return made;
	}

	private static readonly System.Collections.Generic.Dictionary<string, CoreShaderParams.PostSlot[]> layouts = new();

	//Forgets where one shader's numbers sit - or every shader's, for null - because the shader was
	//edited or the device let go of everything. See CoreResources.Forget, which is what calls this.
	internal static void Forget( string shader )
	{
		if ( shader is null ) layouts.Clear();
		else layouts.Remove( shader );
	}

	//What one number is this frame.
	internal static Vector4 Value( Material material, RenderAttributes attributes, CoreShaderParams.Parameter parameter,
		Rinten.Rendering.CommandList list = null )
	{
		//Under the name the draw sets it by where the shader gave it one - Attribute( "Strength" ) - and under its own name otherwise.
		if ( attributes is not null && parameter.IsAttribute &&
			attributes.TryGetDeclared( parameter.Attribute, out var named ) )
		{
			return named;
		}

		if ( attributes is not null && attributes.TryGetDeclared( parameter.Name, out var set ) ) return set;

		//And what the list the pass was recorded in set on its own attributes - a picture it named, a number it set for every draw after it.
		if ( list?.Values is { } listValues )
		{
			if ( parameter.IsAttribute && listValues.TryGetDeclared( parameter.Attribute, out var listed ) ) return listed;
			if ( listValues.TryGetDeclared( parameter.Name, out listed ) ) return listed;
		}

		if ( material?.core?.Values is { } values &&
			( values.TryGetValue( parameter.Name, out var kept ) ||
				values.TryGetValue( CoreShaderParams.Bare( parameter.Name ), out kept ) ) )
		{
			return kept;
		}

		return parameter.Default;
	}

	//How a pass lands: as its draw asked, where it asked - somebody else's material laid a particular way - or else as its own file says.
	private static int BlendOf( int asked, string shader, string entry )
		=> asked != 0 ? asked : CoreResources.State( shader ).BlendOf( entry );

	private static float Number( RenderAttributes attributes, string name, float fallback )
		=> attributes is null ? fallback : attributes.GetFloat( name, fallback );

	//Copies the effect's attributes into its material by name, prefix stripped (g_flIntensity=Intensity)
	private static void Numbers( Material material, RenderAttributes attributes,
		CoreShaderParams.PostSlot[] layout )
	{
		if ( attributes is null || material?.core is null ) return;

		var declared = CoreResources.Declared( material.core.Shader );

		if ( declared is null || declared.Length == 0 ) return;

		foreach ( var parameter in declared )
		{
			//Carried in the push block, so the material is not to be touched for it: a write
			//here is a revision, and a revision is the material destroyed and made again.
			if ( Pushed( layout, parameter ) ) continue;

			if ( !attributes.TryGetDeclared( parameter.Name, out var value ) ) continue;

			if ( material.core.Values.TryGetValue( parameter.Name, out var was ) && was == value ) continue;

			material.core.Values[parameter.Name] = value;
			material.core.Revision++;
		}
	}

	private static bool Pushed( CoreShaderParams.PostSlot[] layout, CoreShaderParams.Parameter parameter )
	{
		foreach ( var slot in layout )
		{
			if ( string.Equals( slot.Parameter.Name, parameter.Name, System.StringComparison.Ordinal ) ) return true;
		}

		return false;
	}

	//Said once per material rather than once a frame: a post-process that will not draw would otherwise write sixty lines a second.
	private static void Missing( string material, string why )
	{
		if ( !said.Add( material ?? "" ) ) return;

		log.Warning( $"post-process {material}: {why}" );
	}

	private static readonly System.Collections.Generic.HashSet<string> said = new();
}

using System.Collections.Generic;
using Rinten.UI;

namespace Rinten;

//Draws the screen UI: walks the laid-out panel tree in paint order and emits SDF box quads
//Supports a CSS subset: borders, radii, images, gradients, backdrop filters, shadows, transforms
internal static class CoreUiRenderer
{

	/// <summary>Must match the core's SBC_VERTEX_UI layout; Pack = 4 because Mono aligns Vector4 to 16 bytes</summary>
	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4 )]
	public struct UiVertex
	{
		public Vector2 Position;
		public Vector2 Uv;
		public Vector4 Rect;
		public Vector4 Radii;
		public Color32 Fill;

		/// <summary>Per-side border colours packed RGBA8; WebGL2 allows only 16 vertex attributes</summary>
		public Color32 BorderLeft;
		public Color32 BorderTop;
		public Color32 BorderRight;
		public Color32 BorderBottom;

		/// <summary>Border width; what to do with the texture, if anything; the angle it is turned by, in radians; the blur radius in pixels.</summary>
		public Vector4 Misc;

		/// <summary>What an ancestor with overflow hidden let through: min xy, max xy, in screen pixels. All zeroes means nothing was clipped.</summary>
		public Vector4 Clip;

		/// <summary>Brightness, saturation, contrast, inversion.</summary>
		public Vector4 Filter;

		/// <summary>Hue rotation in radians, and sepia.</summary>
		public Vector2 Filter2;

		/// <summary>Where the corner was before a panel transform moved it. The shape is measured here; only the position is moved.</summary>
		public Vector2 Local;

		/// <summary>Bindless index of the box's picture, per vertex so batches don't split; 0 means the bound texture</summary>
		public uint Texture;

		/// <summary>The corners the clipping ancestor was rounded by.</summary>
		public Vector4 ClipRadii;

		/// <summary>Vertical corner radii of box then clip, as packed halves (see PackVertical); 0 means circular</summary>
		public uint VerticalTop;
		public uint VerticalBottom;
		public uint ClipVerticalTop;
		public uint ClipVerticalBottom;

		/// <summary>The same colour on all four sides.</summary>
		public Color32 Border
		{
			readonly get => BorderLeft;
			set
			{
				BorderLeft = value;
				BorderTop = value;
				BorderRight = value;
				BorderBottom = value;
			}
		}
	}

	//Four vertical radii as two words of half floats, the way the vertex carries them. A corner whose
	//vertical radius is its horizontal one is written as nought, which the shader reads as a circle.
	internal static (uint Top, uint Bottom) PackVertical( Vector4 horizontal, Vector4 vertical )
	{
		static uint Half( float h, float v ) => v == h || v <= 0.0f ? 0u
			: System.BitConverter.HalfToUInt16Bits( (System.Half)v );

		return (
			Half( horizontal.x, vertical.x ) | ( Half( horizontal.y, vertical.y ) << 16 ),
			Half( horizontal.z, vertical.z ) | ( Half( horizontal.w, vertical.w ) << 16 ) );
	}

	//What an ancestor with overflow hidden let through: the box in screen pixels, and the corners it was rounded by.
	internal readonly record struct UiClip( Vector4 Rect, Vector4 Radii, Vector4 RadiiV = default )
	{
		/// <summary>Whether anything was clipped at all.</summary>
		public bool IsSet => Rect.z > Rect.x || Rect.w > Rect.y;
	}

	/// <summary>How the pixel shader is to treat the texture it is given.</summary>
	public enum UiFill
	{
		/// <summary>Not at all: a plain box of colour.</summary>
		None = 0,

		/// <summary>Stretched across the box and clamped at its edges.</summary>
		Texture = 1,

		/// <summary>The copy of the frame behind the panel, blurred and filtered by whatever the style asked for.</summary>
		Backdrop = 2,

		/// <summary>Tiled rather than clamped.</summary>
		Repeated = 3,

		/// <summary>A shadow cast outside the box: the box itself, offset, spread and blurred, with nothing drawn where the box is not.</summary>
		Shadow = 4,

		/// <summary>The same, turned inside out and kept within the box - which is what an inset shadow is.</summary>
		ShadowInset = 5,

		/// <summary>Drawn at whatever size and offset it was given, and nowhere else: past its own edges the background shows through.</summary>
		Sized = 6,

		/// <summary>Letters: a picture whose colours were multiplied by their own transparency when it was made, and in the space it was made in.</summary>
		Text = 7,

		/// <summary>HSV saturation/value field at a fixed hue.</summary>
		ColorSv = 8,

		/// <summary>A CSS gradient worked out per pixel: the texture is its colours along the gradient line, and where each pixel falls on that line comes from the box.</summary>
		Gradient = 9,

		/// <summary>Triangles of a tessellated vector shape, edge-antialiased in the shader. See VectorShape.</summary>
		Vector = 10,
	}

	/// <summary>The texture a backdrop batch asks for, standing in until the frame has actually been copied - which cannot happen until everything behind the panels has been drawn, and so is settled after the walk rather than during it.</summary>
	public const ulong BackdropTexture = ulong.MaxValue;

	/// <summary>Not a texture either: a batch carrying this says "the world is drawn here", over the area it names, between the panels behind it and the panels over it. See Panel.WearsWorld.</summary>
	public const ulong WorldTexture = ulong.MaxValue - 1;

	/// <summary>A run of quads sharing one texture - zero for plain boxes - drawn in the order they were built.</summary>
	public record struct UiBatch( List<UiVertex> Vertices, ulong Texture, Rect Area = default )
	{
		/// <summary>Where these vertices already are on the device, when the walk they came from is kept there - a buffer and the first vertex in it; nought where they are only here. See CoreSceneDrawer.KeepOnDevice.</summary>
		public ulong Buffer { get; set; }
		public int First { get; set; }
	}

	/// <summary>Text into a texture, provided by whoever owns the texture cache - the renderer builds geometry and holds nothing.</summary>
	public delegate (ulong Texture, Vector2 Size) TextRasteriser( in TextRendering.Scope scope );

	/// <summary>One of the engine's own textures onto our device, provided by whoever owns the cache. Zero for one that cannot be read.</summary>
	public delegate ulong TextureUploader( Texture texture );

	/// <summary>Walks every screen panel of the scene, in paint order, into batches. How many pieces of text the last walk drew. For the log: a screen of panels with no text in it is a different fault to a screen with none of either.</summary>
	public static int LastTextCount { get; private set; }

	/// <summary>What the last walk actually did, in counts rather than in milliseconds.</summary>
	public struct WalkCounts
	{
		/// <summary>Panels reached by the walk, visible or not.</summary>
		public int Visited;

		/// <summary>Panels that were skipped whole, themselves and their children - invisible, or fully clipped away.</summary>
		public int Skipped;

		/// <summary>Boxes handed to the batches - the geometry the interface actually made this frame.</summary>
		public int Boxes;

		/// <summary>Batches made and how many splits were texture changes; boxes per batch shows batching quality</summary>
		public int Batches;
		public int TextureCuts;

		/// <summary>Distinct textures named, and boxes needing a frame copy for backdrop filters (a full-window copy)</summary>
		public int DistinctTextures;
		public int Backdrops;

		/// <summary>Panels that drew a baked background-image quad this frame.</summary>
		public int BakedBackgroundImages;

		/// <summary>Distinct bindless slots used and pictures with no slot; slots &lt; textures means two share one slot</summary>
		public int DistinctSlots;
		public int Unslotted;

		/// <summary>Distinct textures that did resolve to a slot. Compared against <see cref="DistinctSlots"/> and nothing else - deriving it by subtraction counted "no texture at all" as a texture and reported an aliasing that was not there.</summary>
		public int Slotted;

		/// <summary>Calls into the text rasteriser, cached or not.</summary>
		public int TextRasterised;

		/// <summary>Calls asking for a texture to be on the device.</summary>
		public int TextureUploads;

		/// <summary>Subtrees dropped because their bounds could not reach the clip they would have been drawn into. Counted within Skipped, and the measure of whether bounding a subtree was worth doing at all.</summary>
		public int Culled;

		/// <summary>Panels the walk could not bound, and so had to walk anyway.</summary>
		public int Unbounded;

		/// <summary>Panels whose own boxes came out of the cache, and panels whose boxes were made afresh and kept.</summary>
		public int Replayed;
		public int Recorded;

		/// <summary>How many frames in a row the window's whole frame was put out again since the walk these counts describe; nought for a walk of its own.</summary>
		public int Reused;

		/// <summary>How many pixels the boxes cover between them, before clipping - what the interface costs the card to fill, which a count of boxes says nothing about: one box over the whole window is a screen of blending and sixty labels are nothing.</summary>
		public double Area;

		/// <summary>Boxes taken out again because something opaque was drawn over the whole of them later in the order - a dock behind a viewport, the window's own ground under everything - and how many pixels they would have filled for nothing. See DropCovered.</summary>
		public int Covered;
		public double CoveredArea;

		/// <summary>The four biggest boxes still drawn after the cut, largest first: their area in pixels, what kind of fill they are, and whether they read a picture. What a screen of blending is made of, by name.</summary>
		public (float Area, UiFill Kind, bool Textured, byte Alpha)[] Biggest;

		/// <summary>The four biggest boxes as they were emitted, before the cut, with the panel that emitted each and where in the order it came - and where the world came, so a box after it is known to be over it.</summary>
		public (float Area, string Panel, int Order)[] BiggestEmitted;
		public int WorldOrder;
	}

	/// <summary>What the last walk did. Read by the profiler and the report.</summary>
	public static WalkCounts LastWalk { get; private set; }

	/// <summary>What the last walk of a window's own panels did - apart from the game's, whose walk happens inside the window's where the world is drawn, and used to overwrite these with its own nothing.</summary>
	public static WalkCounts LastWindowWalk { get; private set; }

	private static WalkCounts walk;

	//Whether the walk under way is a root's - whose boxes and frame are kept between frames - rather than the scene walk's, which keeps none.
	private static bool walkKeeps;

	//The root's Panel.TreeGeneration as the walk began; what a kept frame is stamped with.
	private static uint walkGeneration;

	//Throttles the walk readout under tracing.
	private static RealTimeSince sinceSaidWalk;

	//Which textures this walk has already seen, for the distinct count.
	private static readonly HashSet<ulong> seenTextures = new();

	//And which slots they resolved to, for the aliasing check.
	private static readonly HashSet<uint> seenSlots = new();

	//Whether the device has an indexed table of pictures to read from. Asked once. Where it has none, every box keeps its
	//own texture and the batches are cut where they always were - slower, and right, which is the order those two come in.
	private static bool Bindless => bindless ??= CoreRenderDevice.BindlessSlots > 0;

	private static bool? bindless;

	//-- a panel's own boxes, kept between frames -------------------------------

	/// <summary>Whether a panel's own boxes are put out again from the last frame while nothing they were made from moved - in editor windows only; a game's screen is rebuilt whole. <c>ui_geometry_cache 0</c> rebuilds every box every frame.</summary>
	[ConVar( "ui_geometry_cache", Help = "Reuse each editor UI panel's geometry between frames while nothing about it changed; editor windows only, never the game's screen" )]
	public static bool GeometryCache { get; set; } = true;

	//One panel's own vertices as last emitted, and what they were made from.
	internal sealed class PanelGeometry
	{
		public uint Version;
		public UiClip Clip;
		public float Opacity;
		public Matrix? Transform;

		/// <summary>The rest of what the boxes were resolved against: a label's caret scroll, the root's units, the device's resource generation.</summary>
		public Vector2 CaretScroll;
		public Vector2 RootSize;
		public Length RootFontSize;
		public float RootScale;
		public int Resources;

		/// <summary>How many pieces of text the boxes hold, for the walk's count.</summary>
		public int TextCount;

		/// <summary>The pictures the panel could draw, and whether each was fit to draw - a placeholder filled in later has the same reference and a new answer.</summary>
		public readonly List<(Texture Texture, bool Valid)> Pictures = new();

		public UiVertex[] Vertices = System.Array.Empty<UiVertex>();

		/// <summary>Runs that shared a batch: what the batch binds, and where the run lies in Vertices.</summary>
		public readonly List<(ulong Bound, int Start, int Count)> Segments = new();

		/// <summary>The pictures the boxes named, as they stood on the device; one moving stales the lot.</summary>
		public readonly List<(Texture Texture, ulong Handle)> Textures = new();

		/// <summary>And the slot of the indexed table each handle sat in.</summary>
		public readonly List<(ulong Handle, uint Slot)> Slots = new();
	}

	//The walk is one thread; a recording is the panel being emitted right now.
	private static bool recording;
	private static readonly List<(Texture, ulong)> recordTextures = new();
	private static readonly List<(ulong, uint)> recordSlots = new();

	//Text drawn while recording, and whether something drew by a path the cache cannot vouch for.
	private static int recordTexts;
	private static bool recordPoisoned;

	//Where the output stood when a panel's own emission began.
	private readonly record struct Mark( int Batches, int Vertices );

	private static Mark MarkOutput( List<UiBatch> output ) =>
		new( output.Count, output.Count == 0 ? 0 : output[^1].Vertices.Count );

	private static void BeginRecording()
	{
		recording = true;
		recordTexts = 0;
		recordPoisoned = false;
		recordTextures.Clear();
		recordSlots.Clear();
	}

	//The pictures a panel could draw, valid or not, so a placeholder that fills in later is noticed.
	private static void Pictures( Panel panel, Styles style, List<(Texture, bool)> into )
	{
		into.Clear();

		if ( style.BackgroundImage is { } background ) into.Add( (background, background.IsValid) );
		if ( style.BorderImageSource is { } border ) into.Add( (border, border.IsValid) );
		if ( panel is Rinten.UI.Image { Texture: { } picture } ) into.Add( (picture, picture.IsValid) );
	}

	private static readonly List<(Texture, bool)> picturesNow = new();

	//Puts a panel's kept boxes out again, if nothing they were made from has moved since.
	private static bool TryReplay( Panel panel, float opacity, UiClip clip, Matrix? transform,
		List<UiBatch> output, TextureUploader uploadTexture )
	{
		var kept = panel.Geometry;
		if ( kept is null ) return false;

		if ( kept.Version != panel.RenderVersion || kept.Opacity != opacity
			|| kept.Clip != clip || kept.Transform != transform )
			return false;

		if ( kept.Resources != CoreResources.Generation
			|| kept.RootSize != Length.RootSize || !kept.RootFontSize.Equals( Length.RootFontSize ) || kept.RootScale != Length.RootScale )
			return false;

		if ( panel is Label label && kept.CaretScroll != label.CaretScroll )
			return false;

		//The same pictures, each as fit to draw as it was; a placeholder that has since filled in is a box that was never made.
		Pictures( panel, panel.ComputedStyle, picturesNow );

		if ( picturesNow.Count != kept.Pictures.Count ) return false;

		for ( int i = 0; i < picturesNow.Count; i++ )
		{
			if ( !ReferenceEquals( picturesNow[i].Item1, kept.Pictures[i].Texture ) || picturesNow[i].Item2 != kept.Pictures[i].Valid )
				return false;
		}

		//A picture re-uploaded or moved to another slot is a different vertex; asking is what the live path pays anyway.
		for ( int i = 0; i < kept.Textures.Count; i++ )
		{
			var (texture, handle) = kept.Textures[i];
			if ( Uploaded( uploadTexture, texture ) != handle ) return false;
		}

		for ( int i = 0; i < kept.Slots.Count; i++ )
		{
			var (handle, slot) = kept.Slots[i];
			if ( CoreRender.TextureIndex( handle ) != slot ) return false;

			NoteSlot( handle, slot );
		}

		for ( int i = 0; i < kept.Segments.Count; i++ )
		{
			var (bound, start, count) = kept.Segments[i];

			if ( output.Count == 0 || output[^1].Texture != bound )
			{
				if ( output.Count > 0 ) walk.TextureCuts++;

				walk.Batches++;
				output.Add( new UiBatch( TakeList(), bound ) );
			}

			output[^1].Vertices.AddRange( kept.Vertices.AsSpan( start, count ) );
			walk.Boxes += count / 6;
		}

		LastTextCount += kept.TextCount;
		walk.Replayed++;
		return true;
	}

	//Keeps what the panel emitted from the mark on, with what it was made from.
	private static void Record( Panel panel, in Mark mark, float opacity, UiClip clip, Matrix? transform, List<UiBatch> output )
	{
		recording = false;

		//Drawn by a path the cache cannot vouch for this frame: nothing kept, and nothing older either.
		if ( recordPoisoned )
		{
			panel.Geometry = null;
			return;
		}

		var kept = panel.Geometry ??= new PanelGeometry();

		kept.Version = panel.RenderVersion;
		kept.Opacity = opacity;
		kept.Clip = clip;
		kept.Transform = transform;
		kept.CaretScroll = panel is Label label ? label.CaretScroll : default;
		kept.RootSize = Length.RootSize;
		kept.RootFontSize = Length.RootFontSize;
		kept.RootScale = Length.RootScale;
		kept.Resources = CoreResources.Generation;
		kept.TextCount = recordTexts;
		Pictures( panel, panel.ComputedStyle, kept.Pictures );
		kept.Segments.Clear();

		//The batch that was open at the mark, if any, holds the first of the new vertices.
		var first = mark.Batches - 1;
		var total = 0;

		for ( int i = System.Math.Max( first, 0 ); i < output.Count; i++ )
			total += output[i].Vertices.Count - ( i == first ? mark.Vertices : 0 );

		if ( kept.Vertices.Length < total ) kept.Vertices = new UiVertex[total];

		var at = 0;

		for ( int i = System.Math.Max( first, 0 ); i < output.Count; i++ )
		{
			var list = output[i].Vertices;
			var from = i == first ? mark.Vertices : 0;
			var count = list.Count - from;
			if ( count <= 0 ) continue;

			System.Runtime.InteropServices.CollectionsMarshal.AsSpan( list ).Slice( from, count ).CopyTo( kept.Vertices.AsSpan( at, count ) );
			kept.Segments.Add( (output[i].Texture, at, count) );
			at += count;
		}

		kept.Textures.Clear();
		kept.Textures.AddRange( recordTextures );
		kept.Slots.Clear();
		kept.Slots.AddRange( recordSlots );

		walk.Recorded++;
	}

	//-- a window's whole frame, kept between frames ----------------------------

	/// <summary>Whether an editor window's last frame is handed to the device again while nothing in its tree changed. <c>ui_reuse_frame 0</c> walks and rebuilds every frame.</summary>
	[ConVar( "ui_reuse_frame", Help = "Resubmit an editor window's last vertex buffer when nothing in its panel tree changed; 0 walks and rebuilds every frame" )]
	public static bool ReuseFrame { get; set; } = true;

	//A root's whole walk result plus what it depended on, so an unchanged window can skip the walk
	internal sealed class FrameGeometry
	{
		/// <summary>Whether a walk has filled this in at all.</summary>
		public bool Kept;

		/// <summary>The root's <see cref="Panel.TreeGeneration"/> as the walk began. A bump during the walk - the caret blink marking its label - is a mismatch next frame, which is the point.</summary>
		public uint Generation;

		/// <summary>The device's resource generation and the root's units, as the boxes were resolved against them.</summary>
		public int Resources;
		public Vector2 RootSize;
		public Length RootFontSize;
		public float RootScale;

		/// <summary>Something in the frame is drawn by time or by a path the walk cannot vouch for; such a frame is never put out again.</summary>
		public bool Live;

		/// <summary>The rectangle the batches were cut to after the walk, where the caller cuts them - a headset's sheet, see CoreSceneDrawer.HeadsetBatches.</summary>
		public Rect Cut;

		/// <summary>The batches' vertices in one buffer on the device, end to end, where the caller draws the kept frame from there rather than writing them out again every frame - a game's screen or headset sheet, see CoreSceneDrawer.KeepOnDevice. Each batch says where in it its own are. Nought where there is none.</summary>
		public ulong Vertices;

		/// <summary>This root's own batches, drawn as they stand while the frame is kept.</summary>
		public readonly List<UiBatch> Batches = new();

		/// <summary>Every picture the frame named, as it stood on the device; one moving is a different frame.</summary>
		public readonly List<(Texture Texture, ulong Handle)> Textures = new();

		/// <summary>What each of <see cref="Textures"/> showed when the frame last went out - CoreTexture.Content - so a frame put out again can say whether it is the same picture, or the same batches over new pixels.</summary>
		public readonly List<int> Contents = new();

		/// <summary>Whether the last time the frame was put out again it had new pixels under it.</summary>
		public bool Changed;

		/// <summary>And the slot of the indexed table each handle sat in.</summary>
		public readonly List<(ulong Handle, uint Slot)> Slots = new();

		/// <summary>The pictures every visited panel could draw, and whether each was fit to draw at the time.</summary>
		public readonly List<(Texture Texture, bool Valid)> Pictures = new();

		/// <summary>What the walk that made it did, so a reused frame still describes the picture.</summary>
		public WalkCounts Counts;
		public int TextCount;
	}

	//Set during the walk by anything that makes the frame unfit to keep - see FrameGeometry.Live.
	private static bool frameLive;

	//What the walk under way has named so far, each once - the frame's tables before they are kept.
	private static readonly List<(Texture, ulong)> frameTextures = new();
	private static readonly List<(ulong, uint)> frameSlots = new();
	private static readonly List<(Texture, bool)> framePictures = new();

	private static readonly HashSet<Texture> frameTextureSet = new( ReferenceEqualityComparer.Instance );
	private static readonly HashSet<Texture> framePictureSet = new( ReferenceEqualityComparer.Instance );
	private static readonly HashSet<ulong> frameSlotHandles = new();

	//Editor frames put out again without a walk, and editor frames walked. For the report.
	internal static long FramesReused, FramesWalked;

	//Why a kept frame could not be put out again, one count a reason, in the order the reasons are asked.
	internal static long RefusedGeneration, RefusedLive, RefusedResources, RefusedTextures, RefusedSlots, RefusedPictures;

	private static void BeginFrame()
	{
		frameLive = false;
		frameTextures.Clear();
		frameSlots.Clear();
		framePictures.Clear();
		frameTextureSet.Clear();
		framePictureSet.Clear();
		frameSlotHandles.Clear();
	}

	private static void NoteTexture( Texture texture, ulong handle )
	{
		if ( walkKeeps && frameTextureSet.Add( texture ) ) frameTextures.Add( (texture, handle) );
	}

	private static void NoteSlot( ulong handle, uint slot )
	{
		if ( walkKeeps && frameSlotHandles.Add( handle ) ) frameSlots.Add( (handle, slot) );
	}

	private static void NotePictures( Panel panel, Styles style )
	{
		Pictures( panel, style, picturesNow );

		for ( int i = 0; i < picturesNow.Count; i++ )
		{
			if ( framePictureSet.Add( picturesNow[i].Item1 ) ) framePictures.Add( picturesNow[i] );
		}
	}

	//Reuses a window's last frame if nothing it depends on changed; asking the uploader also re-uploads
	internal static bool TryReuseFrame( RootPanel root, TextureUploader uploadTexture )
	{
		var frame = root.Frame;
		if ( frame is null || !frame.Kept || !ReuseFrame || uploadTexture is null ) return false;

		if ( frame.Generation != root.TreeGeneration )
		{
			RefusedGeneration++;
			return false;
		}

		if ( frame.Live )
		{
			RefusedLive++;
			return false;
		}

		if ( frame.Resources != CoreResources.Generation
			|| frame.RootSize != Length.RootSize || !frame.RootFontSize.Equals( Length.RootFontSize ) || frame.RootScale != Length.RootScale )
		{
			RefusedResources++;
			return false;
		}

		for ( int i = 0; i < frame.Textures.Count; i++ )
		{
			var (texture, handle) = frame.Textures[i];

			if ( uploadTexture( texture ) != handle )
			{
				RefusedTextures++;
				return false;
			}

			//Drawn again as surely as by a walk, so a gradient's ramp isn't aged out from under it.
			if ( texture?.core is { } core ) core.UiDrawnAt = Rinten.EngineLoop.LoopFrames;
		}

		for ( int i = 0; i < frame.Slots.Count; i++ )
		{
			var (handle, slot) = frame.Slots[i];

			if ( CoreRender.TextureIndex( handle ) != slot )
			{
				RefusedSlots++;
				return false;
			}
		}

		for ( int i = 0; i < frame.Pictures.Count; i++ )
		{
			var (texture, valid) = frame.Pictures[i];

			if ( texture.IsValid != valid )
			{
				RefusedPictures++;
				return false;
			}
		}

		//The same batches. Whether they are the same picture is whether every texture on them shows what it showed - which
		//a window that only presents when its picture changes has to know, and one that presents every frame never did.
		var changed = false;

		for ( int i = 0; i < frame.Textures.Count; i++ )
		{
			var content = frame.Textures[i].Texture?.core?.Content ?? 0;
			if ( content == frame.Contents[i] ) continue;

			frame.Contents[i] = content;
			changed = true;
		}

		frame.Changed = changed;

		//The counts still describe what is on the screen; only the tally says it was not
		//walked for. The window's, not the game's: only an editor window's frame is ever kept.
		frame.Counts.Reused++;
		LastWindowWalk = frame.Counts;
		LastTextCount = frame.TextCount;
		FramesReused++;

		if ( Panel.TracingTick && sinceSaidWalk > 0.5f )
		{
			sinceSaidWalk = 0;
			Log.Trace( $"ui build: frame put out again, {frame.Counts.Reused:n0} in a row; {FrameTally()}" );
		}

		return true;
	}

	//Keeps what the walk that just ran made of the root, for TryReuseFrame to put out again.
	internal static void KeepFrame( RootPanel root )
	{
		var frame = root.Frame;
		if ( frame is null ) return;

		frame.Kept = true;
		frame.Generation = walkGeneration;
		frame.Resources = CoreResources.Generation;
		frame.RootSize = Length.RootSize;
		frame.RootFontSize = Length.RootFontSize;
		frame.RootScale = Length.RootScale;
		frame.Live = frameLive;

		frame.Textures.Clear();
		frame.Textures.AddRange( frameTextures );
		frame.Contents.Clear();

		foreach ( var (texture, _) in frameTextures )
			frame.Contents.Add( texture?.core?.Content ?? 0 );

		frame.Changed = true;
		frame.Slots.Clear();
		frame.Slots.AddRange( frameSlots );
		frame.Pictures.Clear();
		frame.Pictures.AddRange( framePictures );

		frame.Counts = walk;
		frame.TextCount = LastTextCount;

		FramesWalked++;
	}

	//The frame reuse counts as one line, for the trace and the report.
	internal static string FrameTally() =>
		$"frames reused {FramesReused:n0}, walked {FramesWalked:n0}; refused for generation {RefusedGeneration:n0}, live {RefusedLive:n0}, " +
		$"resources or units {RefusedResources:n0}, textures {RefusedTextures:n0}, slots {RefusedSlots:n0}, pictures {RefusedPictures:n0}";

	/// <summary>The scene's other cameras, each a box in its rectangle of the screen - what <see cref="Build(Scene, List{UiBatch}, TextRasteriser, TextureUploader)"/> puts under every panel, for a caller that walks the panels itself, root by root.</summary>
	public static void BuildCameraLayers( Scene scene, List<UiBatch> output )
	{
		if ( !scene.IsValid() ) return;

		foreach ( var (picture, area) in CoreCameraTargets.Layers( scene ) )
		{
			AddBox( output, area, default, Color.White, Color.Transparent, 0, picture, default, UiFill.Texture, fills: true );
		}
	}

	//One picture across a box, and nothing else - for a caller composing a frame
	//of its own out of the batches, as a window's screenshot does with its world.
	internal static void Picture( List<UiBatch> output, Rect area, ulong picture )
		=> AddBox( output, area, default, Color.White, Color.Transparent, 0, picture, default, UiFill.Texture, fills: true );

	public static void Build( Scene scene, List<UiBatch> output,
		TextRasteriser rasteriseText, TextureUploader uploadTexture = null )
	{
		if ( !scene.IsValid() ) return;

		LastTextCount = 0;
		PathMeshes.NextWalk();
		walk = default;
		seenTextures.Clear();
		seenSlots.Clear();

		//The game's screen: nothing here is kept between frames.
		walkKeeps = false;

		//The scene's other cameras first, each in its rectangle of the screen: over the world and under every panel - see CoreCameraTargets.
		foreach ( var (picture, area) in CoreCameraTargets.Layers( scene ) )
		{
			AddBox( output, area, default, Color.White, Color.Transparent, 0, picture, default, UiFill.Texture, fills: true );
		}

		foreach ( var screen in scene.GetAllComponents<ScreenPanel>() )
		{
			if ( !screen.IsValid() ) continue;

			var root = screen.GetPanel();
			if ( root is null ) continue;

			//A sheet in front of a headset is drawn in the eyes, not on the window - see CoreSceneDrawer.DrawHeadsetPanels.
			if ( root is GameRootPanel { InHeadset: true } ) continue;

			WalkPanel( root, 1.0f, default, output, rasteriseText, uploadTexture );
		}

		DropCovered( output );

		LastWalk = walk;
	}

	//Vertex lists recycled from finished walks to avoid per-frame garbage; owners return them via Recycle
	private static readonly Stack<List<UiVertex>> spare = new();

	/// <summary>Empties a finished set of batches and keeps their lists for the next walk.</summary>
	public static void Recycle( List<UiBatch> batches )
	{
		if ( batches is null ) return;

		foreach ( var batch in batches )
		{
			if ( batch.Vertices is null ) continue;
			if ( spare.Count >= SpareCeiling ) break;

			batch.Vertices.Clear();
			spare.Push( batch.Vertices );
		}

		batches.Clear();
	}

	//How many lists are worth keeping. A window's walk makes a few dozen batches; anything far above that is not coming back.
	private const int SpareCeiling = 256;

	private static List<UiVertex> TakeList()
	{
		return spare.Count > 0 ? spare.Pop() : new List<UiVertex>( 256 );
	}

	public static void Build( Panel root, List<UiBatch> output,
		TextRasteriser rasteriseText, TextureUploader uploadTexture = null )
	{
		if ( root is null ) return;

		LastTextCount = 0;
		PathMeshes.NextWalk();
		walk = default;
		seenTextures.Clear();
		seenSlots.Clear();

		//Keep boxes for any root panel, games included (VR HUDs rebuilt every frame otherwise)
		walkKeeps = root is RootPanel;

		//Sampled before the walk, so a panel marked dirty while it runs is a different generation by the time the frame is asked for again.
		walkGeneration = root.TreeGeneration;
		BeginFrame();

		WalkPanel( root, 1.0f, default, output, rasteriseText, uploadTexture );

		DropCovered( output );
		NoteBiggest( output );

		//The window's own walk, kept apart from the game's: the game's screen is walked inside
		//this one, where the world is drawn, and used to overwrite these with its own nothing.
		LastWindowWalk = walk;

		//The counters the walk keeps are worth nothing unless somebody reads them. Boxes against Batches is the whole of how well the interface batches; Visited
		//against Skipped is how much of the tree was worth walking at all; and the rasterise and upload counts say whether the caches beneath it are working.
		if ( Rinten.UI.Panel.TracingTick && sinceSaidWalk > 0.5f )
		{
			sinceSaidWalk = 0;

			Log.Trace( $"ui build: visited {walk.Visited:n0}, skipped {walk.Skipped:n0}, boxes {walk.Boxes:n0}, " +
				$"batches {walk.Batches:n0} ({walk.TextureCuts:n0} texture cuts), " +
				$"textures {walk.DistinctTextures:n0} ({walk.Slotted:n0} slotted, {walk.Unslotted:n0} not), " +
				$"rasterised {walk.TextRasterised:n0}, uploads {walk.TextureUploads:n0}, " +
				$"culled {walk.Culled:n0}, unbounded {walk.Unbounded:n0}, " +
				$"replayed {walk.Replayed:n0}, recorded {walk.Recorded:n0}, path meshes made {PathMeshes.Made:n0}, shapes tessellated {SvgShape.Tessellated:n0}; {FrameTally()}" );
		}
	}

	//Drops boxes fully hidden by later opaque rectangles, saving overdraw; draw order is unchanged
	private static void DropCovered( List<UiBatch> output )
	{
		if ( output.Count == 0 ) return;

		covers.Clear();

		for ( var b = output.Count - 1; b >= 0; b-- )
		{
			var batch = output[b];

			if ( batch.Texture == WorldTexture )
			{
				var area = batch.Area;

				if ( area.Width > 0 && area.Height > 0 )
					covers.Add( new Vector4( area.Left, area.Top, area.Right, area.Bottom ) );

				continue;
			}

			var vertices = batch.Vertices;
			if ( vertices is null || vertices.Count < 6 ) continue;

			var quads = vertices.Count / 6;
			var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( vertices );

			//Rebuilt into a list of its own only once something changes: most batches are left exactly as they were.
			List<UiVertex> rebuilt = null;

			for ( var q = quads - 1; q >= 0; q-- )
			{
				var quad = span.Slice( q * 6, 6 );

				//A vector shape's triangles, six at a time: never a cover, never cut, kept as they are.
				if ( ( (int)quad[0].Misc.y & 15 ) == (int)UiFill.Vector )
				{
					if ( rebuilt is not null )
					{
						for ( var v = 5; v >= 0; v-- ) rebuilt.Add( quad[v] );
					}

					continue;
				}

				var left = float.MaxValue;
				var top = float.MaxValue;
				var right = float.MinValue;
				var bottom = float.MinValue;
				var moved = false;

				for ( var v = 0; v < 6; v++ )
				{
					var at = quad[v].Position;

					if ( at.x < left ) left = at.x;
					if ( at.y < top ) top = at.y;
					if ( at.x > right ) right = at.x;
					if ( at.y > bottom ) bottom = at.y;

					if ( at != quad[v].Local ) moved = true;
				}

				ref readonly var first = ref quad[0];

				//What of it can be seen at all: a clipping ancestor cuts it down.
				var seenLeft = left;
				var seenTop = top;
				var seenRight = right;
				var seenBottom = bottom;

				if ( first.Clip.z > first.Clip.x || first.Clip.w > first.Clip.y )
				{
					seenLeft = MathF.Max( left, first.Clip.x );
					seenTop = MathF.Max( top, first.Clip.y );
					seenRight = MathF.Min( right, first.Clip.z );
					seenBottom = MathF.Min( bottom, first.Clip.w );
				}

				//What is left of it once everything solid over it is taken away: nothing, all of it, or a few pieces.
				var pieces = Remaining( seenLeft, seenTop, seenRight, seenBottom );

				var wholeArea = MathF.Max( 0.0f, seenRight - seenLeft ) * MathF.Max( 0.0f, seenBottom - seenTop );
				var leftArea = 0.0f;

				for ( var i = 0; i < pieces; i++ )
					leftArea += ( remains[i].z - remains[i].x ) * ( remains[i].w - remains[i].y );

				//Cut only where it pays: a box that lost less than a quarter of itself, or that would become a heap of slivers, is
				//drawn as it was. A turned box is not cut - its pieces would be axis-aligned rectangles of a shape that is not.
				var cut = pieces == 0 || ( !moved && pieces <= MostPieces && leftArea < wholeArea * 0.75f );

				if ( cut )
				{
					if ( rebuilt is null )
					{
						rebuilt = TakeList();

						//Everything after this quad, already decided, kept as it is - last first, like everything else put in this list.
						for ( var v = vertices.Count - 1; v >= ( q + 1 ) * 6; v-- ) rebuilt.Add( span[v] );
					}

					walk.Covered++;
					walk.CoveredArea += wholeArea - leftArea;

					//The pieces go in this quad's place, in front of what is already in the rebuilt list - which is filled back to front.
					for ( var i = pieces - 1; i >= 0; i-- ) AddPiece( rebuilt, quad, remains[i] );
				}
				else if ( rebuilt is not null )
				{
					for ( var v = 5; v >= 0; v-- ) rebuilt.Add( quad[v] );
				}

				//Solid: plain opaque fill, square, unrotated, square clip; no picture in both batch and box slot
				var marks = (int)first.Misc.y;
				var fill = (UiFill)( marks & 15 );

				//Never query the device for picture opacity here: touching its texture table from this thread crashes
				var picture = fill is UiFill.Texture or UiFill.Repeated;

				var plain = fill == UiFill.None ||
					( picture && first.Texture == 0 && batch.Texture == 0 ) ||
					( picture && ( marks & OpaquePicture ) != 0 );

				var solid = !moved &&
					plain &&
					first.Fill.a == 255 &&
					first.Radii == Vector4.Zero &&
					first.ClipRadii == Vector4.Zero &&
					seenRight - seenLeft >= 1.0f && seenBottom - seenTop >= 1.0f;

				if ( solid ) covers.Add( new Vector4( seenLeft, seenTop, seenRight, seenBottom ) );
			}

			if ( rebuilt is null ) continue;

			//Filled back to front, so turned round to be paint order again.
			rebuilt.Reverse();

			vertices.Clear();
			spare.Push( vertices );

			output[b] = batch with { Vertices = rebuilt };
		}
	}

	//Subtracts solid covers from a box into up to a few rectangles; 0 if fully covered
	private static int Remaining( float left, float top, float right, float bottom )
	{
		if ( right - left <= 0.0f || bottom - top <= 0.0f ) return 0;

		remains[0] = new Vector4( left, top, right, bottom );
		var count = 1;

		//Only covers over 5% of the box area, so small ones don't shred it into too many pieces
		var boxArea = ( right - left ) * ( bottom - top );
		var worthwhile = boxArea * 0.05f;

		for ( var c = 0; c < covers.Count && count > 0; c++ )
		{
			var cover = covers[c];

			var overlapWidth = MathF.Min( right, cover.z ) - MathF.Max( left, cover.x );
			var overlapHeight = MathF.Min( bottom, cover.w ) - MathF.Max( top, cover.y );

			if ( overlapWidth <= 0.0f || overlapHeight <= 0.0f ) continue;
			if ( overlapWidth * overlapHeight < worthwhile ) continue;

			var next = 0;

			for ( var i = 0; i < count; i++ )
			{
				var piece = remains[i];

				//Apart, or wholly inside: kept as it is, or gone.
				if ( piece.z <= cover.x + 0.01f || piece.x >= cover.z - 0.01f ||
					piece.w <= cover.y + 0.01f || piece.y >= cover.w - 0.01f )
				{
					scratch[next++] = piece;
					continue;
				}

				if ( piece.x >= cover.x - 0.01f && piece.y >= cover.y - 0.01f &&
					piece.z <= cover.z + 0.01f && piece.w <= cover.w + 0.01f )
					continue;

				//Four at most: a strip above, a strip below, and what is left of the middle band to either side.
				if ( next + 4 > scratch.Length ) return count;

				if ( piece.y < cover.y ) scratch[next++] = new Vector4( piece.x, piece.y, piece.z, cover.y );
				if ( piece.w > cover.w ) scratch[next++] = new Vector4( piece.x, cover.w, piece.z, piece.w );

				var bandTop = MathF.Max( piece.y, cover.y );
				var bandBottom = MathF.Min( piece.w, cover.w );

				if ( piece.x < cover.x ) scratch[next++] = new Vector4( piece.x, bandTop, cover.x, bandBottom );
				if ( piece.z > cover.z ) scratch[next++] = new Vector4( cover.z, bandTop, piece.z, bandBottom );
			}

			for ( var i = 0; i < next; i++ ) remains[i] = scratch[i];
			count = next;
		}

		return count;
	}

	//Adds one piece of a cut box: same box data on a smaller quad, corners reversed (list is reversed)
	private static void AddPiece( List<UiVertex> into, ReadOnlySpan<UiVertex> quad, Vector4 piece )
	{
		//The quad's own corners, and the texture coordinates at them: the first and last vertices are the top left and bottom right.
		var a = quad[0];
		var z = quad[5];

		var quadWidth = MathF.Max( z.Position.x - a.Position.x, 0.0001f );
		var quadHeight = MathF.Max( z.Position.y - a.Position.y, 0.0001f );

		UiVertex Corner( float x, float y )
		{
			var u = a.Uv.x + ( ( x - a.Position.x ) / quadWidth ) * ( z.Uv.x - a.Uv.x );
			var v = a.Uv.y + ( ( y - a.Position.y ) / quadHeight ) * ( z.Uv.y - a.Uv.y );

			var corner = a;
			corner.Position = new Vector2( x, y );
			corner.Local = new Vector2( x, y );
			corner.Uv = new Vector2( u, v );
			return corner;
		}

		//The same six as AddBox, last first.
		into.Add( Corner( piece.z, piece.w ) );
		into.Add( Corner( piece.z, piece.y ) );
		into.Add( Corner( piece.x, piece.w ) );
		into.Add( Corner( piece.x, piece.w ) );
		into.Add( Corner( piece.z, piece.y ) );
		into.Add( Corner( piece.x, piece.y ) );
	}

	//Max pieces per cut box; high enough for the window background under docks, viewport and bars
	private const int MostPieces = 24;

	private static readonly Vector4[] remains = new Vector4[64];
	private static readonly Vector4[] scratch = new Vector4[64];

	//The panel being walked, so a box can say whose it is.
	private static Panel walking;

	//The biggest boxes left after the cut, for the report.
	private static readonly bool dumpBoxes = System.Environment.GetEnvironmentVariable( "RINTEN_UI_DEBUG" ) == "1";
	private static int dumpFrame;

	//Under RINTEN_UI_DEBUG=1, every box left after the covered ones were cut, once in a while.
	private static void DumpBoxes( List<UiBatch> output )
	{
		if ( !dumpBoxes || ( ++dumpFrame % 240 ) != 30 ) return;

		var text = new System.Text.StringBuilder(
			$"ui boxes left ({seenSlots.Count} slots seen):" );

		foreach ( var batch in output )
		{
			if ( batch.Texture == WorldTexture ) { text.Append( $"\n  [world {batch.Area.Width:F0}x{batch.Area.Height:F0}]" ); continue; }
			var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( batch.Vertices );

			for ( var q = 0; q + 6 <= span.Length; q += 6 )
			{
				ref readonly var a = ref span[q];
				ref readonly var z = ref span[q + 5];
				var w = z.Position.x - a.Position.x;
				var h = z.Position.y - a.Position.y;
				if ( w * h < 2000 ) continue;
				text.Append( $"\n  {a.Position.x:F0},{a.Position.y:F0} {w:F0}x{h:F0} fill {a.Misc.y} alpha {a.Fill.a} tex {batch.Texture} slot {a.Texture} clip {a.Clip.x:F0},{a.Clip.y:F0},{a.Clip.z:F0},{a.Clip.w:F0} radii {a.Radii.x:F0} border {a.Misc.x:F0} {a.Border.a}" );
			}
		}

		Log.Info( text.ToString() );
	}

	private static void NoteBiggest( List<UiBatch> output )
	{
		DumpBoxes( output );

		var biggest = walk.Biggest ??= new (float, UiFill, bool, byte)[4];
		Array.Clear( biggest );

		foreach ( var batch in output )
		{
			if ( batch.Vertices is null ) continue;

			var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( batch.Vertices );

			for ( var q = 0; q + 6 <= span.Length; q += 6 )
			{
				ref readonly var a = ref span[q];
				ref readonly var z = ref span[q + 5];

				var area = MathF.Abs( z.Position.x - a.Position.x ) * MathF.Abs( z.Position.y - a.Position.y );

				for ( var i = 0; i < biggest.Length; i++ )
				{
					if ( area <= biggest[i].Item1 ) continue;

					for ( var j = biggest.Length - 1; j > i; j-- ) biggest[j] = biggest[j - 1];

					biggest[i] = ( area, (UiFill)( (int)a.Misc.y & 15 ), a.Texture != 0 || batch.Texture != 0, a.Fill.a );
					break;
				}
			}
		}
	}

	//The solid rectangles seen so far by DropCovered, last to first.
	private static readonly List<Vector4> covers = new();

	//One panel and its children, in paint order. The clip is what an ancestor with overflow hidden let through,
	//in screen pixels. It only ever narrows going down the tree - a child cannot escape what its parent cut off.
	private static void WalkPanel( Panel panel, float opacity, UiClip clip, List<UiBatch> output,
		TextRasteriser rasteriseText, TextureUploader uploadTexture, Matrix? inherited = null )
	{
		if ( panel is null ) return;

		if ( !panel.IsVisible )
		{
			walk.Skipped++;
			return;
		}

		//A subtree that cannot reach the clip it would be drawn into is dropped whole. Its bounds were built after the layout and take in its shadows;
		//a subtree that could not be bounded - something in it transformed, or painting quads of its own - has none, and is walked as before.
		if ( clip.IsSet && inherited is null
			&& panel.SubtreeBounds is { } bounds
			&& ( bounds.Right <= clip.Rect.x || bounds.Bottom <= clip.Rect.y
				|| bounds.Left >= clip.Rect.z || bounds.Top >= clip.Rect.w ) )
		{
			walk.Skipped++;
			walk.Culled++;
			return;
		}

		walk.Visited++;

		if ( panel.SubtreeBounds is null ) walk.Unbounded++;

		walking = panel;

		var style = panel.ComputedStyle;
		var transform = inherited;

		//What the children other than the scrollbars are clipped to. The same as the clip,
		//unless a scrollbar gutter is reserved - content stops short of it, the bar sits in it.
		var contentClip = clip;

		if ( style is not null )
		{
			//A panel's own transform turns about its origin - the middle of it unless the style says otherwise -
			//and then everything below it is turned along with it, because that is what a transformed subtree is.
			if ( panel.TransformMatrix != Matrix.Identity )
			{
				var box = panel.Box.Rect;

				var origin = new Vector3(
					box.Left + ( style.TransformOriginX?.GetPixels( box.Width ) ?? box.Width * 0.5f ),
					box.Top + ( style.TransformOriginY?.GetPixels( box.Height ) ?? box.Height * 0.5f ),
					0.0f );

				var local = Matrix.CreateTranslation( -origin )
					* panel.TransformMatrix
					* Matrix.CreateTranslation( origin );

				transform = inherited is null ? local : local * inherited.Value;
			}

			opacity *= style.Opacity ?? 1.0f;
			if ( opacity <= 0.001f ) return;

			var rect = panel.Box.Rect;

			var corners = new Vector4(
				style.BorderTopLeftRadius?.GetPixels( rect.Width ) ?? 0,
				style.BorderTopRightRadius?.GetPixels( rect.Width ) ?? 0,
				style.BorderBottomRightRadius?.GetPixels( rect.Width ) ?? 0,
				style.BorderBottomLeftRadius?.GetPixels( rect.Width ) ?? 0 );

			//The vertical radii, where a stylesheet gave any - nought for a panel whose corners
			//are circles, which is every panel that never wrote a slash. See ResolveVertical.
			var cornersV = ResolveVertical( style, rect, ref corners );

			//Overflow hidden clips children to the panel's bounds and corners, but not its own shadow
			var given = clip;
			var clipsOwn = style.OverflowX == OverflowMode.Hidden || style.OverflowY == OverflowMode.Hidden ||
				style.OverflowX == OverflowMode.Scroll || style.OverflowY == OverflowMode.Scroll;

			if ( clipsOwn )
			{
				//The clip is tested where pixels end up, so a transformed panel clips by where its box ended up: the box around its turned
				//or scaled corners. Its own rounding is lost in that; a turned plate cuts square. Untransformed, the box is the box.
				if ( transform is { } t )
				{
					var a = t.Transform( new Vector2( rect.Left, rect.Top ) );
					var b = t.Transform( new Vector2( rect.Right, rect.Top ) );
					var c = t.Transform( new Vector2( rect.Right, rect.Bottom ) );
					var d = t.Transform( new Vector2( rect.Left, rect.Bottom ) );

					clip = Narrow( clip, new UiClip( new Vector4(
						MathF.Min( MathF.Min( a.x, b.x ), MathF.Min( c.x, d.x ) ), MathF.Min( MathF.Min( a.y, b.y ), MathF.Min( c.y, d.y ) ),
						MathF.Max( MathF.Max( a.x, b.x ), MathF.Max( c.x, d.x ) ), MathF.Max( MathF.Max( a.y, b.y ), MathF.Max( c.y, d.y ) ) ),
						Vector4.Zero ) );
				}
				else
				{
					clip = Narrow( clip, new UiClip(
						new Vector4( rect.Left, rect.Top, rect.Right, rect.Bottom ), corners, cornersV ) );
				}
			}

			contentClip = clip;

			if ( clipsOwn && transform is null && panel.LayoutTree?.Gutter is { } gutter && ( gutter.Left > 0 || gutter.Right > 0 ) )
			{
				var content = panel.ContentClipRect;
				contentClip = Narrow( clip, new UiClip( new Vector4( content.Left, content.Top, content.Right, content.Bottom ), Vector4.Zero ) );
			}

			//Kept boxes are keyed by the narrowed clip, and a shadow drawn under the wider one would come back from
			//the cache under whatever it was given when it was kept. So a panel like that is drawn live; there are few.
			var shadowsOutside = false;

			if ( clipsOwn && style.BoxShadow is { Count: > 0 } cast )
			{
				for ( var i = 0; i < cast.Count && !shadowsOutside; i++ )
					shadowsOutside = !cast[i].Inset && cast[i].Color.a > 0.001f;
			}

			//An outline lies outside the box as well, and is not cut by the panel's own overflow either - the same reason, the same answer.
			var outline = Outline( style, rect, opacity );

			if ( clipsOwn && outline.Width > 0.0f ) shadowsOutside = true;

			//Reuse last frame's boxes when unchanged; colour pickers bypass it since their hue isn't versioned
			var colorSv = TryGetColorSvHue( panel, out _ );
			if ( colorSv ) frameLive = true;

			if ( walkKeeps ) NotePictures( panel, style );

			//World-wearing panels can't be geometry-cached: the world marker is emitted only by a live walk
			var wearsWorld = panel.WearsWorld;
			var retained = panel.RetainsDrawing && !panel.DrawnByWalk;

			var cacheable = GeometryCache && walkKeeps && !panel.HasBackdropFilter && !colorSv && !wearsWorld && !shadowsOutside;
			var mark = MarkOutput( output );

			if ( !( cacheable && TryReplay( panel, opacity, clip, transform, output, uploadTexture ) ) )
			{
				if ( cacheable ) BeginRecording();

				var background = style.BackgroundColor ?? Color.Transparent;

				//Each side's own width, which is what a stylesheet actually says: an underline is a bottom border and nothing else, and a seam
				//down a panel is a left one. Only the left width used to be read, so an underline drew nothing and a left border drew a ring.
				var borderSides = new Vector4(
					style.BorderLeftWidth?.GetPixels( rect.Width ) ?? 0.0f,
					style.BorderTopWidth?.GetPixels( rect.Height ) ?? 0.0f,
					style.BorderRightWidth?.GetPixels( rect.Width ) ?? 0.0f,
					style.BorderBottomWidth?.GetPixels( rect.Height ) ?? 0.0f );

				//None and hidden draw nothing, whatever width was written.
				var borderStyle = style.BorderStyle ?? BorderStyle.Solid;

				if ( borderStyle is BorderStyle.None or BorderStyle.Hidden ) borderSides = Vector4.Zero;

				var borderWidth = MathF.Max(
					MathF.Max( borderSides.x, borderSides.y ),
					MathF.Max( borderSides.z, borderSides.w ) );

				//Each side in its own colour, as the web draws them: the shader cuts each corner along the line from its outer point to its inner
				//one and gives each half to its side. A side with no width has no colour that anything could see, so its colour is left as it is.
				var borderColours = new UiSides(
					( style.BorderLeftColor ?? Color.Transparent ).WithAlphaMultiplied( opacity ),
					( style.BorderTopColor ?? Color.Transparent ).WithAlphaMultiplied( opacity ),
					( style.BorderRightColor ?? Color.Transparent ).WithAlphaMultiplied( opacity ),
					( style.BorderBottomColor ?? Color.Transparent ).WithAlphaMultiplied( opacity ) );

				var borderSeen = borderColours.Seen( borderSides );

				//A border image replaces the plain border, whose widths the parser set to the image slices
				var borderPicture = style.BorderImageSource;

				if ( borderPicture is not null )
				{
					borderWidth = 0.0f;
					borderSides = Vector4.Zero;
					borderSeen = false;
				}

				//Outer shadows lie behind everything the panel draws, in the order they were written - the first one on top of the rest.
				if ( style.BoxShadow is { Count: > 0 } shadows )
				{
					for ( var i = shadows.Count - 1; i >= 0; i-- )
					{
						var shadow = shadows[i];
						if ( shadow.Inset || shadow.Color.a <= 0.001f ) continue;

						//The quad has to hold the blur as well as the box, and the blur reaches a little past its own radius.
						var grow = shadow.Spread + shadow.Blur + 2.0f;

						var quad = new Rect(
							rect.Left + shadow.OffsetX - grow, rect.Top + shadow.OffsetY - grow,
							rect.Width + ( grow * 2.0f ), rect.Height + ( grow * 2.0f ) );

						AddBox( output, rect, corners,
							shadow.Color.WithAlpha( shadow.Color.a * opacity ),
							Color.Transparent, 0, texture: 0, given, UiFill.Shadow, 0.0f,
							new Vector4( shadow.OffsetX, shadow.OffsetY, shadow.Spread, 0 ),
							default, shadow.Blur, quad, transform, radiiV: cornersV );
					}
				}

				//What is behind the panel comes first, because everything the panel itself draws is meant to sit on top of it.
				if ( Backdrop( panel, style, rect, out var blur, out var filter, out var filter2 ) )
				{
					AddBox( output, rect, corners, Color.White.WithAlpha( opacity ),
						Color.Transparent, 0, BackdropTexture, clip,
						UiFill.Backdrop, 0.0f, filter, filter2, blur, null, transform, radiiV: cornersV );
				}

				if ( TryGetColorSvHue( panel, out var svHue ) )
				{
					AddBox( output, rect, corners, Color.White.WithAlpha( opacity ),
						Color.Transparent, 0, texture: 0, clip, UiFill.ColorSv, 0.0f,
						new Vector4( svHue / 360f, 0, 0, 0 ), default, 0.0f, null, transform, radiiV: cornersV );
				}
				else if ( background.a > 0.001f || borderSeen )
				{
					//The sides ride in the filter slot, which a plain box has no other use for - filters belong to a backdrop and offsets to a shadow, and this is neither.
					AddBox( output, rect, corners,
						background.WithAlpha( background.a * opacity ),
						Color.Transparent, borderSeen ? borderWidth : 0.0f,
						texture: 0, clip, filter: borderSides, transform: transform,
						radiiV: cornersV, sides: borderColours, borderStyle: borderStyle );
				}

				//The world, where this panel is. A batch of its own with nothing in it, so that whoever draws the batches
				//draws the world at this point in the order - after what is behind the panel, before its children.
				if ( wearsWorld )
				{
					walk.WorldOrder = walk.Boxes;

					var area = clip.IsSet
						? new Rect( MathF.Max( rect.Left, clip.Rect.x ), MathF.Max( rect.Top, clip.Rect.y ),
							MathF.Min( rect.Right, clip.Rect.z ) - MathF.Max( rect.Left, clip.Rect.x ),
							MathF.Min( rect.Bottom, clip.Rect.w ) - MathF.Max( rect.Top, clip.Rect.y ) )
						: rect;

					if ( area.Width > 0 && area.Height > 0 )
						output.Add( new UiBatch( TakeList(), WorldTexture, area ) );
				}

				//The picture behind it, if it has one. Drawn over the background colour, tinted the way the style
				//asks, and stretched to the panel - which is what a background does when nobody said otherwise.
				if ( uploadTexture is not null && !style.BackgroundGradient.ColorOffsets.IsDefaultOrEmpty
					&& ( !BackgroundSized( style, rect ) || style.BackgroundImage is not { IsValid: true } ) )
				{
					GradientBox( style, rect, corners, cornersV, opacity, clip, transform, output, uploadTexture );
				}
				else if ( uploadTexture is not null && style.BackgroundImage is { IsValid: true } )
				{
					walk.BakedBackgroundImages++;

					var image = Uploaded( uploadTexture, style.BackgroundImage );

					if ( image != 0 )
					{
						var tint = ( style.BackgroundTint ?? Color.White );
						tint = tint.WithAlpha( tint.a * opacity );

						//A linear gradient arrives as a strip of colour baked into a picture, with the angle left on the style -
						//so the angle is what turns a top-to-bottom strip into a gradient pointing wherever it was asked to point.
						var angle = ( style.BackgroundAngle?.GetPixels( 360.0f ) ?? 0.0f ).DegreeToRadian();

						var repeated = style.BackgroundRepeat is BackgroundRepeat.Repeat
							or BackgroundRepeat.RepeatX or BackgroundRepeat.RepeatY;

						//How much of the box one copy of the picture covers, and where that copy starts. Nothing said
						//means the whole box, which is the stretch everything relied on before there was a choice.
						var sizeX = style.BackgroundSizeX?.GetPixels( rect.Width ) ?? rect.Width;
						var sizeY = style.BackgroundSizeY?.GetPixels( rect.Height ) ?? rect.Height;

						var atX = style.BackgroundPositionX?.GetPixels( rect.Width ) ?? 0.0f;
						var atY = style.BackgroundPositionY?.GetPixels( rect.Height ) ?? 0.0f;

						var sized = MathF.Abs( sizeX - rect.Width ) > 0.5f
							|| MathF.Abs( sizeY - rect.Height ) > 0.5f
							|| MathF.Abs( atX ) > 0.5f || MathF.Abs( atY ) > 0.5f;

						//The corners' texture coordinates carry it: one copy of the picture spans sizeX
						//by sizeY of the box, starting at the offset, and the rest is repeat or nothing.
						var uv = new Vector4(
							-atX / MathF.Max( sizeX, 0.001f ),
							-atY / MathF.Max( sizeY, 0.001f ),
							( rect.Width - atX ) / MathF.Max( sizeX, 0.001f ),
							( rect.Height - atY ) / MathF.Max( sizeY, 0.001f ) );

						var kind = repeated
							? UiFill.Repeated
							: ( sized ? UiFill.Sized : UiFill.Texture );

						//Whether this picture hides what is under it: a view's own output, drawn over the
						//whole box, with nothing taken out of it by the tint. See AddBox.OpaquePicture.
						var fills = tint.a >= 1.0f
							&& kind is UiFill.Texture or UiFill.Repeated
							&& style.BackgroundImage.core is { Opaque: true };

						AddBox( output, rect, corners, tint, Color.Transparent, 0, image, clip,
							kind, angle, default, default, 0.0f, null, transform, uv, fills, radiiV: cornersV,
							pixelated: style.ImageRendering == ImageRendering.Point );
					}
				}

				//An image panel's own texture, drawn stretched like a background picture
				if ( uploadTexture is not null && panel is Rinten.UI.Image picture
					&& picture.Texture is { IsValid: true } && style.BackgroundImage is null )
				{
					var image = Uploaded( uploadTexture, picture.Texture );

					if ( image != 0 )
					{
						var tint = ( style.BackgroundTint ?? Color.White );
						tint = tint.WithAlpha( tint.a * opacity );

						AddBox( output, rect, corners, tint, Color.Transparent, 0, image, clip,
							UiFill.Texture, 0.0f, default, default, 0.0f, null, transform, radiiV: cornersV,
							pixelated: style.ImageRendering == ImageRendering.Point );
					}
				}

				//An <svg src>'s picture, once it has been drawn: the panel's content, like an <img>'s.
				if ( uploadTexture is not null && panel is SvgPanel { Picture: { IsValid: true } svgPicture } && style.BackgroundImage is null )
				{
					var image = Uploaded( uploadTexture, svgPicture );

					if ( image != 0 )
					{
						var tint = style.BackgroundTint ?? Color.White;
						tint = tint.WithAlpha( tint.a * opacity );

						AddBox( output, rect, corners, tint, Color.Transparent, 0, image, clip,
							UiFill.Texture, 0.0f, default, default, 0.0f, null, transform, radiiV: cornersV );
					}
				}

				//A vector shape in an <svg>: its fill and then its stroke, as triangles.
				if ( panel is SvgShape shape )
					VectorShape( shape, style, opacity, clip, transform, output );

				//The picture cut into nine over the panel's edges, which is what an interface made of frames and plates is drawn with.
				if ( uploadTexture is not null && borderPicture is not null )
				{
					BorderImage( style, borderPicture, rect, opacity, clip, transform, output, uploadTexture );
				}

				//Inset shadows go over the background, inside the panel's own box.
				if ( style.BoxShadow is { Count: > 0 } inner )
				{
					for ( var i = inner.Count - 1; i >= 0; i-- )
					{
						var shadow = inner[i];
						if ( !shadow.Inset || shadow.Color.a <= 0.001f ) continue;

						AddBox( output, rect, corners,
							shadow.Color.WithAlpha( shadow.Color.a * opacity ),
							Color.Transparent, 0, texture: 0, clip, UiFill.ShadowInset, 0.0f,
							new Vector4( shadow.OffsetX, shadow.OffsetY, shadow.Spread, 0 ),
							default, shadow.Blur, null, transform, radiiV: cornersV );
					}
				}

				if ( panel is Label label && !string.IsNullOrEmpty( label._text ) )
					Text( label, style, opacity, clip, transform, output, rasteriseText, uploadTexture );

				//The outline, over everything the panel drew itself and under its children: a ring outside the border box, pushed out by its
				//offset, following the corners - and cut only by what the panel was given, never by its own overflow, which it lies outside of.
				if ( outline.Width > 0.0f )
				{
					var grow = outline.Offset + outline.Width;
					var ring = new Rect( rect.Left - grow, rect.Top - grow,
						rect.Width + ( grow * 2.0f ), rect.Height + ( grow * 2.0f ) );

					if ( ring.Width > 0.0f && ring.Height > 0.0f )
					{
						AddBox( output, ring, Rounder( corners, grow ),
							Color.Transparent, outline.Color, outline.Width, texture: 0, given,
							transform: transform, radiiV: Rounder( cornersV, grow ),
							borderStyle: outline.Style );
					}
				}

				//A panel whose drawing follows its render version is drawn with its boxes, so what it drew is kept and put out again with them.
				if ( retained ) CustomContent( panel, opacity, clip, transform, output, uploadTexture, live: false );

				if ( cacheable ) Record( panel, mark, opacity, clip, transform, output );
			}

			if ( panel is Label caretLabel )
			{
				//A focused field's caret blinks by time. Its off phase marks the label and so moves the generation;
				//its on phase says nothing, and a kept frame would hold the caret lit for as long as it was kept.
				if ( caretLabel.Parent is ITextCaretHost && caretLabel.Parent.HasFocus ) frameLive = true;

				DrawTextCaret( caretLabel, opacity, clip, transform, output );
			}

			if ( !retained ) CustomContent( panel, opacity, clip, transform, output, uploadTexture, live: true );
		}

		//A tree that goes down further than any interface has reason to is a tree with a cycle in it, or one that grows a level every frame; walked to
		//the end, either takes the process down with a stack overflow and leaves no stack to read. Stopped here instead, and said once, with the way down.
		if ( walkDepth >= MaxWalkDepth )
		{
			ReportTooDeep( panel );
			return;
		}

		walkDepth++;

		try
		{
			if ( !AnyLayered( panel ) )
			{
				foreach ( var child in panel.Children )
				{
					WalkPanel( child, opacity, child is ScrollBar ? clip : contentClip, output, rasteriseText, uploadTexture, transform );
				}
			}
			else
			{
				var order = zOrdered.Count > 0 ? zOrdered.Pop() : new List<Panel>();

				try
				{
					InZOrder( panel, order );

					foreach ( var child in order )
					{
						WalkPanel( child, opacity, child is ScrollBar ? clip : contentClip, output, rasteriseText, uploadTexture, transform );
					}
				}
				finally
				{
					order.Clear();
					zOrdered.Push( order );
				}
			}
		}
		finally
		{
			walkDepth--;
		}
	}

	//Whether any child sets a z-index; paint order must match input order, and most panels skip sorting
	private static bool AnyLayered( Panel panel )
	{
		foreach ( var child in panel.Children )
		{
			if ( ( child.ComputedStyle?.ZIndex ?? 0 ) != 0 ) return true;
		}

		return false;
	}

	//A panel's children lowest storey first, and in the order they were added within a storey - which is what they were painted in before storeys counted.
	private static void InZOrder( Panel panel, List<Panel> into )
	{
		foreach ( var child in panel.Children )
		{
			var z = child.ComputedStyle?.ZIndex ?? 0;
			var at = into.Count;

			//Past everything above it, never past its equal: a stable insertion, and a storey holds a handful at most.
			while ( at > 0 && ( into[at - 1].ComputedStyle?.ZIndex ?? 0 ) > z ) at--;

			into.Insert( at, child );
		}
	}

	//Lists to sort children into, one per level of the walk that needs one.
	private static readonly Stack<List<Panel>> zOrdered = new();

	//How deep the walk goes before it is taken for a runaway. The editor's deepest real trees are a few dozen.
	private const int MaxWalkDepth = 200;

	private static int walkDepth;
	private static bool reportedTooDeep;

	//Once per run: the panel the walk gave up on, and every ancestor up to the root, so the log names what keeps nesting.
	private static void ReportTooDeep( Panel panel )
	{
		if ( reportedTooDeep ) return;

		reportedTooDeep = true;

		var chain = new System.Text.StringBuilder();
		var steps = 0;

		for ( var p = panel; p is not null && steps < 64; p = p.Parent, steps++ )
		{
			chain.Append( "\n    " ).Append( p.GetType().Name );

			if ( !string.IsNullOrEmpty( p.Classes ) ) chain.Append( " ." ).Append( p.Classes.Replace( ' ', '.' ) );
		}

		Log.Error( $"ui build: a panel tree is deeper than {MaxWalkDepth}, so it is drawn no further. From the deepest up:{chain}" );
	}

	//Intersects two clips: exact if one contains the other, else the shared box without rounding
	private static UiClip Narrow( UiClip outer, UiClip inner )
	{
		if ( !outer.IsSet ) return inner;

		if ( Holds( outer.Rect, inner.Rect ) ) return inner;
		if ( Holds( inner.Rect, outer.Rect ) ) return outer;

		var left = MathF.Max( outer.Rect.x, inner.Rect.x );
		var top = MathF.Max( outer.Rect.y, inner.Rect.y );
		var right = MathF.Min( outer.Rect.z, inner.Rect.z );
		var bottom = MathF.Min( outer.Rect.w, inner.Rect.w );

		//Two boxes that do not meet let nothing through. A rect with no area reads as "nobody
		//clipped" to the shader, so this one is given one pixel of it, off where anything could be.
		if ( right <= left || bottom <= top )
			return new UiClip( new Vector4( -1.0f, -1.0f, -0.5f, -0.5f ), Vector4.Zero );

		return new UiClip( new Vector4( left, top, right, bottom ), Vector4.Zero );
	}

	//Whether the first box contains the second whole.
	private static bool Holds( Vector4 outer, Vector4 inner )
	{
		return outer.x <= inner.x && outer.y <= inner.y
			&& outer.z >= inner.z && outer.w >= inner.w;
	}

	//Nine-slice border image; quads overlap half a pixel at seams so edge antialiasing leaves no gaps
	private static void BorderImage( Styles style, Texture picture, Rect rect, float opacity,
		UiClip clip, Matrix? transform, List<UiBatch> output, TextureUploader uploadTexture )
	{
		if ( picture.Width <= 0 || picture.Height <= 0 ) return;

		var image = Uploaded( uploadTexture, picture );
		if ( image == 0 ) return;

		var tint = style.BorderImageTint ?? Color.White;
		tint = tint.WithAlpha( tint.a * opacity );

		if ( tint.a <= 0.001f ) return;

		//Where the picture is cut, in its own pixels, and how much of the panel each piece is given.
		//The two are the same unless the style said otherwise - a slice with no width beside it is both.
		var cutLeft = style.BorderImageWidthLeft?.GetPixels( picture.Width ) ?? 0.0f;
		var cutRight = style.BorderImageWidthRight?.GetPixels( picture.Width ) ?? 0.0f;
		var cutTop = style.BorderImageWidthTop?.GetPixels( picture.Height ) ?? 0.0f;
		var cutBottom = style.BorderImageWidthBottom?.GetPixels( picture.Height ) ?? 0.0f;

		var drawLeft = style.BorderLeftWidth?.GetPixels( rect.Width ) ?? cutLeft;
		var drawRight = style.BorderRightWidth?.GetPixels( rect.Width ) ?? cutRight;
		var drawTop = style.BorderTopWidth?.GetPixels( rect.Height ) ?? cutTop;
		var drawBottom = style.BorderBottomWidth?.GetPixels( rect.Height ) ?? cutBottom;

		//A panel narrower than its own edges: both sides give way in proportion, rather than one of them drawing over the other.
		Squeeze( ref drawLeft, ref drawRight, rect.Width );
		Squeeze( ref drawTop, ref drawBottom, rect.Height );

		var filled = ( style.BorderImageFill ?? UI.BorderImageFill.Unfilled ) == UI.BorderImageFill.Filled;

		//The panel's own columns and rows, and the picture's.
		Span<float> x = stackalloc float[4] { rect.Left, rect.Left + drawLeft, rect.Right - drawRight, rect.Right };
		Span<float> y = stackalloc float[4] { rect.Top, rect.Top + drawTop, rect.Bottom - drawBottom, rect.Bottom };

		Span<float> u = stackalloc float[4] { 0.0f, cutLeft / picture.Width, 1.0f - ( cutRight / picture.Width ), 1.0f };
		Span<float> v = stackalloc float[4] { 0.0f, cutTop / picture.Height, 1.0f - ( cutBottom / picture.Height ), 1.0f };

		const float Seam = 0.5f;

		for ( var row = 0; row < 3; row++ )
		{
			for ( var column = 0; column < 3; column++ )
			{
				//The middle is the picture's own, and only drawn where the style asked for it - a frame with a hole in it is the ordinary case.
				if ( row == 1 && column == 1 && !filled ) continue;

				var width = x[column + 1] - x[column];
				var height = y[row + 1] - y[row];

				//An edge nobody left room for, or a picture with nothing to cut there. Either way there is no piece.
				if ( width <= 0.01f || height <= 0.01f ) continue;
				if ( u[column + 1] - u[column] <= 0.0f || v[row + 1] - v[row] <= 0.0f ) continue;

				var quad = new Rect( x[column], y[row], width, height );

				//Grown into its neighbours, and only into them: the outside of the panel keeps its own antialiased edge.
				var measured = Rect.FromPoints(
					new Vector2(
						x[column] - ( column > 0 ? Seam : 0.0f ),
						y[row] - ( row > 0 ? Seam : 0.0f ) ),
					new Vector2(
						x[column + 1] + ( column < 2 ? Seam : 0.0f ),
						y[row + 1] + ( row < 2 ? Seam : 0.0f ) ) );

				AddBox( output, measured, Vector4.Zero, tint, Color.Transparent, 0, image, clip,
					UiFill.Texture, 0.0f, default, default, 0.0f, quad, transform,
					new Vector4( u[column], v[row], u[column + 1], v[row + 1] ) );
			}
		}
	}

	//Two edges that together want more room than there is, cut down in proportion until
	//they fit - which is what CSS does with a border wider than the box it is around.
	private static void Squeeze( ref float first, ref float second, float room )
	{
		var total = first + second;

		if ( total <= room || total <= 0.0f ) return;

		var scale = room / total;

		first *= scale;
		second *= scale;
	}

	//Draws a label's pre-rendered text picture as UiFill.Text, which undoes the premultiplied alpha
	private static void Text( Label label, Styles style, float opacity, UiClip clip, Matrix? transform,
		List<UiBatch> output, TextRasteriser rasteriseText, TextureUploader uploadTexture )
	{
		var block = label._textBlock;

		if ( block is not null && uploadTexture is not null )
		{
			var picture = block.FinishedTexture();

			//Nothing yet: the text is measured and drawn during layout, and a label that has not been laid out
			//has no letters to show. Falling back to the raw string here would flash the markup for a frame.
			if ( picture is null ) return;

			var handle = Uploaded( uploadTexture, picture );
			if ( handle == 0 ) return;

			LastTextCount++;
			if ( recording ) recordTexts++;

			//Where the label put its text, less the room the picture keeps around it for whatever spills out of the letters, and less how far
			//the text is scrolled to keep the caret in view - which is what the caret is placed by, so the text has to move by the same amount.
			var at = new Rect( label._textRect.Position - block.TextureMargin - label.CaretScroll, picture.Size ).Floor();

			AddBox( output, at, Vector4.Zero, Color.White.WithAlpha( opacity ), Color.Transparent, 0,
				handle, clip, UiFill.Text, transform: transform );

			return;
		}

		//No text engine behind this label: one string, one style, no markup. Drawn from the shared text cache, which retires
		//what nobody asks for - a kept copy of this box would name a picture that is gone, and so would a kept frame.
		if ( recording ) recordPoisoned = true;
		frameLive = true;

		var colour = style.FontColor ?? Color.White;

		var scope = new TextRendering.Scope( label._text,
			colour.WithAlpha( colour.a * opacity ),
			style.FontSize?.GetPixels( 100 ) ?? 13.0f,
			style.FontFamily ?? "Roboto",
			style.FontWeight ?? 400 );

		walk.TextRasterised++;

		var (texture, size) = rasteriseText( scope );

		if ( texture == 0 ) return;

		LastTextCount++;

		AddBox( output, new Rect( label._textRect.Position - label.CaretScroll, size ), Vector4.Zero, Color.White,
			Color.Transparent, 0, texture, clip, transform: transform );
	}

	//Text caret for focused text fields — drawn after label text.
	private static void DrawTextCaret( Label label, float opacity, UiClip clip, Matrix? transform,
		List<UiBatch> output )
	{
		if ( !TextCaretDraw.TryGet( label, out var index, out var color ) )
			return;

		color = color.WithAlpha( color.a * opacity );
		if ( color.a <= 0.001f )
			return;

		var caret = TextCaretDraw.GetRect( label, index );
		AddBox( output, caret, Vector4.Zero, color, Color.Transparent, 0, texture: 0, clip, transform: transform );
	}

	//The box a rectangle occupies once a transform has moved its corners: the smallest upright rectangle that still holds all four of them.
	private static Rect Moved( Rect rect, Matrix transform )
	{
		var a = Place( transform, rect.Left, rect.Top );
		var b = Place( transform, rect.Right, rect.Top );
		var c = Place( transform, rect.Left, rect.Bottom );
		var d = Place( transform, rect.Right, rect.Bottom );

		var min = Vector2.Min( Vector2.Min( a, b ), Vector2.Min( c, d ) );
		var max = Vector2.Max( Vector2.Max( a, b ), Vector2.Max( c, d ) );

		return new Rect( min, max - min );
	}

	//Transforms a panel corner with perspective divide, since panel transforms may be perspective
	private static Vector2 Place( Matrix transform, float x, float y )
	{
		var moved = transform.Transform( new Vector4( x, y, 0, 1 ) );

		//Behind the eye, or on the plane of it. There is no picture of that, and dividing by it throws the corner across the screen.
		if ( moved.w <= 0.0001f ) return new Vector2( moved.x, moved.y );

		return new Vector2( moved.x / moved.w, moved.y / moved.w );
	}

	//Reads a panel's backdrop filter; numbers and percentages mean the same (0.7 == 70%)
	private static bool Backdrop( Panel panel, Styles style, Rect rect,
		out float blur, out Vector4 filter, out Vector2 filter2 )
	{
		blur = style.BackdropFilterBlur?.GetPixels( rect.Width ) ?? 0.0f;

		filter = new Vector4(
			style.BackdropFilterBrightness?.GetPixels( 1.0f ) ?? 1.0f,
			style.BackdropFilterSaturate?.GetPixels( 1.0f ) ?? 1.0f,
			style.BackdropFilterContrast?.GetPixels( 1.0f ) ?? 1.0f,
			style.BackdropFilterInvert?.GetPixels( 1.0f ) ?? 0.0f );

		//A turn of the colour wheel rather than degrees of one, which is what the shader adds to a hue.
		filter2 = new Vector2(
			( style.BackdropFilterHueRotate?.GetPixels( 1.0f ) ?? 0.0f ) / 360.0f,
			style.BackdropFilterSepia?.GetPixels( 1.0f ) ?? 0.0f );

		//Only the panel knows if a filter was written; comparing values to defaults gets it wrong
		if ( !panel.HasBackdropFilter ) return false;

		//Asked for, but for nothing: no sense copying the frame back.
		return blur > 0.01f
			|| MathF.Abs( filter.x - 1.0f ) > 0.001f
			|| MathF.Abs( filter.y - 1.0f ) > 0.001f
			|| MathF.Abs( filter.z - 1.0f ) > 0.001f
			|| filter.w > 0.001f
			|| MathF.Abs( filter2.x ) > 0.001f
			|| filter2.y > 0.001f;
	}

	//Whether background size or position moves the picture; then gradients use the baked picture path
	private static bool BackgroundSized( Styles style, Rect rect )
	{
		if ( style.BackgroundSizeX is null && style.BackgroundSizeY is null
			&& style.BackgroundPositionX is null && style.BackgroundPositionY is null )
			return false;

		var sizeX = style.BackgroundSizeX?.GetPixels( rect.Width ) ?? rect.Width;
		var sizeY = style.BackgroundSizeY?.GetPixels( rect.Height ) ?? rect.Height;
		var atX = style.BackgroundPositionX?.GetPixels( rect.Width ) ?? 0.0f;
		var atY = style.BackgroundPositionY?.GetPixels( rect.Height ) ?? 0.0f;

		return MathF.Abs( sizeX - rect.Width ) > 0.5f || MathF.Abs( sizeY - rect.Height ) > 0.5f
			|| MathF.Abs( atX ) > 0.5f || MathF.Abs( atY ) > 0.5f;
	}

	private static void GradientBox( Styles style, Rect rect, Vector4 corners, Vector4 cornersV, float opacity, UiClip clip,
		Matrix? transform, List<UiBatch> output, TextureUploader uploadTexture )
	{
		var g = style.BackgroundGradient;
		var size = new Vector2( MathF.Max( rect.Width, 1 ), MathF.Max( rect.Height, 1 ) );

		//Type in filter2.x; centre and radii in filter; direction or start angle in the angle slot.
		var angle = 0.0f;
		var filter = Vector4.Zero;
		var length = 1.0f;

		switch ( g.GradientType )
		{
			case GradientInfo.GradientTypes.Linear:
			{
				angle = g.Angle.DegreeToRadian();
				length = MathF.Abs( size.x * MathF.Sin( angle ) ) + MathF.Abs( size.y * MathF.Cos( angle ) );
				break;
			}

			case GradientInfo.GradientTypes.Radial:
			{
				var c = new Vector2( g.OffsetX.GetPixels( size.x ), g.OffsetY.GetPixels( size.y ) );

				var near = new Vector2( MathF.Min( c.x, size.x - c.x ), MathF.Min( c.y, size.y - c.y ) );
				var far = new Vector2( MathF.Max( c.x, size.x - c.x ), MathF.Max( c.y, size.y - c.y ) );

				Vector2 radius;

				if ( g.Circle )
				{
					//One radius: the nearest/farthest side, or the distance to that corner
					var r = g.SizeMode switch
					{
						GradientInfo.RadialSizeMode.FarthestSide => MathF.Max( far.x, far.y ),
						GradientInfo.RadialSizeMode.ClosestSide => MathF.Min( near.x, near.y ),
						GradientInfo.RadialSizeMode.ClosestCorner => near.Length,
						_ => far.Length,
					};

					radius = new Vector2( r, r );
				}
				else
				{
					//A corner ellipse keeps the matching side ellipse's aspect and is scaled to touch the corner, which always works out to sqrt(2) larger
					radius = g.SizeMode switch
					{
						GradientInfo.RadialSizeMode.FarthestSide => far,
						GradientInfo.RadialSizeMode.ClosestSide => near,
						GradientInfo.RadialSizeMode.ClosestCorner => near * 1.41421356f,
						_ => far * 1.41421356f,
					};
				}

				radius = new Vector2( MathF.Max( radius.x, 0.0001f ), MathF.Max( radius.y, 0.0001f ) );
				filter = new Vector4( c.x, c.y, radius.x, radius.y );

				//The gradient ray runs to the ending shape, so that's what a pixel position is along
				length = radius.x;
				break;
			}

			case GradientInfo.GradientTypes.Conic:
			{
				var c = new Vector2( g.OffsetX.GetPixels( size.x ), g.OffsetY.GetPixels( size.y ) );
				filter = new Vector4( c.x, c.y, 0, 0 );
				angle = g.Angle.DegreeToRadian();
				break;
			}
		}

		var ramp = GradientRamp( g, length );
		var image = Uploaded( uploadTexture, ramp );
		if ( image == 0 ) return;

		var tint = style.BackgroundTint ?? Color.White;
		tint = tint.WithAlpha( tint.a * opacity );

		AddBox( output, rect, corners, tint, Color.Transparent, 0, image, clip,
			UiFill.Gradient, angle, filter, new Vector2( (int)g.GradientType, GradientRampWidth ), 0.0f, null, transform,
			radiiV: cornersV );
	}

	//A colour a side: left, top, right, bottom.
	internal readonly record struct UiSides( Color Left, Color Top, Color Right, Color Bottom )
	{
		/// <summary>The same colour all round.</summary>
		public static UiSides All( Color colour ) => new( colour, colour, colour, colour );

		/// <summary>Whether any side with a width has a colour anyone could see.</summary>
		public bool Seen( Vector4 widths ) =>
			( widths.x > 0.0f && Left.a > 0.001f ) || ( widths.y > 0.0f && Top.a > 0.001f ) ||
			( widths.z > 0.0f && Right.a > 0.001f ) || ( widths.w > 0.0f && Bottom.a > 0.001f );
	}

	//Vertical corner radii resolved CSS-style (scaled to fit), or all zero for plain circles
	private static Vector4 ResolveVertical( Styles style, Rect rect, ref Vector4 corners )
	{
		var tl = style.BorderTopLeftRadiusV;
		var tr = style.BorderTopRightRadiusV;
		var br = style.BorderBottomRightRadiusV;
		var bl = style.BorderBottomLeftRadiusV;

		//Unset, or each the same as its horizontal radius (which a plain radius sets them to): four circles, drawn the way they always have been.
		static bool Circle( Length? v, Length? h ) => v is null || v == h;

		if ( Circle( tl, style.BorderTopLeftRadius ) && Circle( tr, style.BorderTopRightRadius )
			&& Circle( br, style.BorderBottomRightRadius ) && Circle( bl, style.BorderBottomLeftRadius ) )
			return Vector4.Zero;

		var h = corners;
		var v = new Vector4(
			tl?.GetPixels( rect.Height ) ?? h.x,
			tr?.GetPixels( rect.Height ) ?? h.y,
			br?.GetPixels( rect.Height ) ?? h.z,
			bl?.GetPixels( rect.Height ) ?? h.w );

		//A corner with either radius at nought is square in both.
		var round = new Vector4(
			h.x > 0.0f && v.x > 0.0f ? 1.0f : 0.0f,
			h.y > 0.0f && v.y > 0.0f ? 1.0f : 0.0f,
			h.z > 0.0f && v.z > 0.0f ? 1.0f : 0.0f,
			h.w > 0.0f && v.w > 0.0f ? 1.0f : 0.0f );

		h *= round;
		v *= round;

		static float Fit( float f, float side, float sum ) => sum > side && sum > 0.0f ? MathF.Min( f, side / sum ) : f;

		var scale = 1.0f;
		scale = Fit( scale, rect.Width, h.x + h.y );
		scale = Fit( scale, rect.Width, h.w + h.z );
		scale = Fit( scale, rect.Height, v.x + v.w );
		scale = Fit( scale, rect.Height, v.y + v.z );

		corners = h * scale;
		return v * scale;
	}

	//Corners pushed out by grow, as an outline's are: a rounded corner grows with the ring, a square one stays square.
	private static Vector4 Rounder( Vector4 radii, float grow )
	{
		static float One( float r, float grow ) => r > 0.0f ? MathF.Max( r + grow, 0.0f ) : 0.0f;

		return new Vector4( One( radii.x, grow ), One( radii.y, grow ), One( radii.z, grow ), One( radii.w, grow ) );
	}

	//What a panel's outline is, if it has one that can be seen: no width means none.
	private static (float Width, float Offset, Color Color, BorderStyle Style) Outline( Styles style, Rect rect, float opacity )
	{
		var outlineStyle = style.OutlineStyle ?? BorderStyle.Solid;
		if ( outlineStyle is BorderStyle.None or BorderStyle.Hidden ) return default;

		var colour = style.OutlineColor ?? Color.Transparent;
		if ( colour.a * opacity <= 0.001f ) return default;

		//Measured against the box's mean side, as the panel renderer measures it.
		var size = ( rect.Width + rect.Height ) * 0.5f;
		var width = style.OutlineWidth?.GetPixels( size ) ?? 0.0f;
		if ( width <= 0.0f ) return default;

		return ( width, style.OutlineOffset?.GetPixels( size ) ?? 0.0f, colour.WithAlphaMultiplied( opacity ), outlineStyle );
	}

	//Texels in a gradient's ramp.
	private const int GradientRampWidth = 256;

	//Gradient ramp pictures shared by stops, evicted when not drawn for a while
	private static readonly Dictionary<long, Texture> gradientRamps = new();

	//The loop frame the ramps were last swept on - once a frame at most.
	private static long gradientRampsSweptAt = -1;

	//Loop frames a ramp can go unasked for before it's let go. Far more than there are frames in flight, and the picture goes through
	//Texture.Dispose, whose device copy is retired through the device's graveyard, so no frame still on the card samples a freed slot.
	private const long GradientRampKeptFor = 600;

	//Past this many ramps the keep drops to GradientRampKeptForFull frames.
	private const int GradientRampCap = 512;

	//The keep once the cap is passed - still well past the frames in flight.
	private const long GradientRampKeptForFull = 30;

	private static readonly List<long> gradientRampsGoing = new();

	//Lets go of the ramps nobody has asked for in a while; at most once a loop frame, and only every sixty frames unless the cap has been passed.
	private static void SweepGradientRamps()
	{
		var now = Rinten.EngineLoop.LoopFrames;
		if ( gradientRampsSweptAt == now ) return;

		var full = gradientRamps.Count > GradientRampCap;
		if ( !full && now - gradientRampsSweptAt < 60 ) return;

		gradientRampsSweptAt = now;

		var keep = full ? GradientRampKeptForFull : GradientRampKeptFor;

		foreach ( var (key, ramp) in gradientRamps )
		{
			var used = ramp?.core?.UiDrawnAt ?? 0;
			if ( now - used > keep ) gradientRampsGoing.Add( key );
		}

		foreach ( var key in gradientRampsGoing )
		{
			if ( gradientRamps.Remove( key, out var gone ) ) gone?.Dispose();
		}

		gradientRampsGoing.Clear();
	}

	//The ramp for a gradient's stops. A stop written in pixels lands at a different fraction on a
	//gradient line of a different length, so only those are keyed by the length, in whole pixels.
	private static Texture GradientRamp( GradientInfo g, float length )
	{
		var hash = new HashCode();
		var pixels = false;

		foreach ( var stop in g.ColorOffsets )
		{
			hash.Add( stop.color );
			hash.Add( stop.offset );
			hash.Add( stop.offsetIsPixels );
			pixels |= stop.offsetIsPixels;
		}

		SweepGradientRamps();

		//A box being resized with a pixel stop in it would otherwise make a ramp a frame
		//for as long as it moved; past the cap the length is only kept to sixteen pixels.
		var rounded = !pixels ? 0 : gradientRamps.Count < GradientRampCap ? (int)MathF.Round( length ) : (int)MathF.Round( length / 16 ) * 16;
		var key = ((long)hash.ToHashCode() << 32) | (uint)rounded;

		if ( gradientRamps.TryGetValue( key, out var ramp ) && ramp.IsValid )
			return ramp;

		var data = Styles.BakeRamp( g.ColorOffsets, pixels ? MathF.Max( rounded, 1 ) : 1, GradientRampWidth );

		ramp?.Dispose();

		ramp = Texture.Create( GradientRampWidth, 1 )
			.WithName( "gradient-ramp" )
			.WithData( data )
			.Finish();

		//Counted as drawn from the start, so a sweep between now and its upload can't take it.
		if ( ramp.core is { } made ) made.UiDrawnAt = Rinten.EngineLoop.LoopFrames;

		gradientRamps[key] = ramp;
		return ramp;
	}

	private static ulong Uploaded( TextureUploader uploadTexture, Texture texture )
	{
		if ( uploadTexture is null ) return 0;

		walk.TextureUploads++;

		if ( texture?.core is { } core ) core.UiDrawnAt = Rinten.EngineLoop.LoopFrames;

		ulong handle;

		//Zoned apart from the walk under tracing, because a slow build is either geometry
		//or a driver wait; not otherwise, because this runs three hundred times a frame.
		if ( !Rinten.UI.Panel.TracingTick )
		{
			handle = uploadTexture( texture );
		}
		else
		{
			using ( Rinten.Diagnostics.Performance.Scope( "ui texture" ) )
			{
				handle = uploadTexture( texture );
			}
		}

		if ( recording ) recordTextures.Add( (texture, handle) );
		NoteTexture( texture, handle );

		return handle;
	}

	private static bool TryGetColorSvHue( Panel panel, out float hue )
	{
		if ( panel is IColorSvPanel colorSv )
		{
			hue = colorSv.SvHue;
			return true;
		}

		if ( panel.HasClass( "color-sv" ) && panel.UserData is float h )
		{
			hue = h;
			return true;
		}

		hue = 0;
		return false;
	}

	//A panel's own Panel.Draw calls, as quads of this walk. One instance, reconfigured per panel:
	//the walk is one thread and a panel has finished drawing before its children are reached.
	private sealed class CustomDraw : IUiDrawCollector
	{
		public List<UiBatch> Output;
		public UiClip Clip;
		public Matrix? Transform;
		public TextureUploader Upload;

		//Panel.Draw counts its corners from the bottom right; this walk starts at the top left.
		static Vector4 Corners( Vector4 radii ) => new( radii.w, radii.y, radii.x, radii.z );

		//A box's own transform ahead of the walk's, where it has one.
		Matrix? With( in Matrix? local )
		{
			if ( !local.HasValue ) return Transform;

			return Transform.HasValue ? local.Value * Transform.Value : local;
		}

		public void Box( in BoxDrawDescriptor desc )
		{
			var texture = 0ul;

			//Through the walk's own upload, so a kept copy of the box knows the picture it named.
			if ( desc.HasImage && Upload is not null )
				texture = Uploaded( Upload, desc.BackgroundImage );

			var kind = UiFill.None;

			if ( texture != 0 )
			{
				kind = desc.PremultiplyAlpha ? UiFill.Text
					: desc.BackgroundRepeat == BackgroundRepeat.Repeat ? UiFill.Repeated
					: UiFill.Texture;
			}

			//A textured box is tinted rather than filled, which is what the tint is for.
			var fill = kind == UiFill.None ? desc.Color : desc.BackgroundTint;

			//Each side its own width and colour, left, top, right, bottom - the order the descriptor was filled in.
			var sides = desc.BorderSize;
			var colours = new UiSides( desc.BorderColorL, desc.BorderColorT, desc.BorderColorR, desc.BorderColorB );
			var borderWidth = colours.Seen( sides ) ? MathF.Max( MathF.Max( sides.x, sides.y ), MathF.Max( sides.z, sides.w ) ) : 0.0f;

			//The sides ride in the filter slot, as the walk's own boxes do; a picture kind has no use for it.
			AddBox( Output, desc.PanelRect, Corners( desc.BorderRadius ),
				fill, Color.Transparent, borderWidth, texture, Clip,
				kind, desc.BackgroundAngle, borderWidth > 0.0f ? sides : default, default, 0.0f, null, With( desc.LocalTransform ),
				sides: colours, pixelated: desc.FilterMode == Rinten.Rendering.FilterMode.Point );
		}

		public void Outline( in OutlineDrawDescriptor desc )
		{
			if ( desc.Width <= 0 ) return;

			var rect = desc.PanelRect.Grow( desc.Offset );

			AddBox( Output, rect, Corners( desc.BorderRadius ),
				Color.Transparent, desc.Color, desc.Width, 0, Clip,
				UiFill.None, 0.0f, default, default, 0.0f, null, Transform );
		}

		//Contours for the path being tessellated; one panel draws at a time.
		readonly List<VectorContour> contours = new();

		/// <summary>A path from OnDraw, with a fringe of one screen pixel where the walk's transform puts it. Its triangles come out of <see cref="PathMeshes"/> while the same path is drawn the same way at the same scale; only a new one is tessellated. The colour is not part of it.</summary>
		public void Path( VectorPath path, Color color, in VectorStrokeStyle? stroke, FillRule rule )
		{
			if ( path.IsEmpty ) return;

			var aa = PixelSize( Transform, path.Points.Count > 0 ? path.Points[0] : Vector2.Zero );

			//Held to steps of a quarter octave, as a shape's is, so a zoom does not make a mesh a frame.
			aa = MathF.Pow( 2.0f, MathF.Round( MathF.Log2( Math.Clamp( aa, 1.0f / 64.0f, 64.0f ) ) * 4.0f ) / 4.0f );

			var key = PathMeshes.Key( path, stroke, rule, aa );

			if ( !PathMeshes.TryGet( key, out var mesh ) )
			{
				mesh = PathMeshes.Add( key );

				contours.Clear();
				VectorTessellator.Flatten( path, VectorTransform.Identity, 0.25f * aa, contours );

				mesh.Alpha = stroke is { } line
					? VectorTessellator.Stroke( contours, line, aa, mesh.Triangles )
					: FillMesh( contours, rule, aa, mesh.Triangles );
			}

			color = color.WithAlpha( color.a * mesh.Alpha );
			if ( color.a <= 0.001f ) return;

			EmitVector( Output, mesh.Triangles, color, Vector2.Zero, Clip, Transform );
		}

		static float FillMesh( List<VectorContour> contours, FillRule rule, float aa, List<VectorVertex> into )
		{
			VectorTessellator.Fill( contours, rule, aa, into );
			return 1.0f;
		}

		public void Shadow( in ShadowDrawDescriptor desc )
		{
			if ( desc.Color.a <= 0.001f ) return;

			var rect = desc.PanelRect;

			//The quad has to hold the blur as well as the box, and the blur reaches a little past its own radius.
			var grow = desc.Spread + desc.Blur + 2.0f;

			var quad = desc.Inset ? (Rect?)null : new Rect(
				rect.Left + desc.Offset.x - grow, rect.Top + desc.Offset.y - grow,
				rect.Width + (grow * 2.0f), rect.Height + (grow * 2.0f) );

			AddBox( Output, rect, Corners( desc.BorderRadius ),
				desc.Color, Color.Transparent, 0, texture: 0, Clip,
				desc.Inset ? UiFill.ShadowInset : UiFill.Shadow, 0.0f,
				new Vector4( desc.Offset.x, desc.Offset.y, desc.Spread, 0 ),
				default, desc.Blur, quad, Transform );
		}
	}

	private static readonly CustomDraw custom = new();

	//Cached tessellations of OnDraw paths keyed by their inputs; unused ones are dropped after seconds
	internal static class PathMeshes
	{
		internal sealed class Mesh
		{
			public readonly List<VectorVertex> Triangles = new();
			public float Alpha = 1.0f;
			public int Used;
		}

		private static readonly Dictionary<ulong, Mesh> meshes = new();
		private static readonly Stack<Mesh> spare = new();
		private static readonly List<ulong> stale = new();

		//Walks so far; a mesh remembers the last one it was drawn in.
		private static int walks;

		//How many walks a mesh is kept without being drawn, and how many meshes at most.
		private const int KeepWalks = 600;
		private const int MostMeshes = 8192;

		//Meshes made since the process began, for the walk's trace.
		internal static long Made;

		//Once a walk: the count moves on, and now and then what nobody drew is let go.
		internal static void NextWalk()
		{
			walks++;

			if ( ( walks & 63 ) != 0 && meshes.Count < MostMeshes ) return;

			stale.Clear();

			foreach ( var (key, mesh) in meshes )
			{
				if ( walks - mesh.Used > KeepWalks || meshes.Count - stale.Count > MostMeshes ) stale.Add( key );
			}

			foreach ( var key in stale )
			{
				if ( !meshes.Remove( key, out var mesh ) ) continue;

				mesh.Triangles.Clear();
				spare.Push( mesh );
			}
		}

		internal static bool TryGet( ulong key, out Mesh mesh )
		{
			if ( !meshes.TryGetValue( key, out mesh ) ) return false;

			mesh.Used = walks;
			return true;
		}

		internal static Mesh Add( ulong key )
		{
			var mesh = spare.Count > 0 ? spare.Pop() : new Mesh();

			mesh.Triangles.Clear();
			mesh.Alpha = 1.0f;
			mesh.Used = walks;
			meshes[key] = mesh;
			Made++;

			return mesh;
		}

		private const ulong Prime = 1099511628211UL;

		private static ulong Mix( ulong h, uint value ) => ( h ^ value ) * Prime;

		private static ulong Mix( ulong h, float value ) => Mix( h, System.BitConverter.SingleToUInt32Bits( value ) );

		//Sixty-four bits of FNV over everything the triangles are made from.
		internal static ulong Key( VectorPath path, in VectorStrokeStyle? stroke, FillRule rule, float aa )
		{
			var h = 14695981039346656037UL;

			h = Mix( h, (uint)path.Verbs.Count );
			foreach ( var verb in path.Verbs ) h = Mix( h, (uint)verb );

			foreach ( var point in path.Points )
			{
				h = Mix( h, point.x );
				h = Mix( h, point.y );
			}

			h = Mix( h, aa );

			if ( stroke is { } line )
			{
				h = Mix( h, 1u );
				h = Mix( h, line.Width );
				h = Mix( h, (uint)line.Cap );
				h = Mix( h, (uint)line.Join );
				h = Mix( h, line.MiterLimit );
				h = Mix( h, line.DashOffset );

				if ( line.Dashes is { } dashes )
				{
					h = Mix( h, (uint)dashes.Length );
					foreach ( var d in dashes ) h = Mix( h, d );
				}
			}
			else
			{
				h = Mix( h, 2u + (uint)rule );
			}

			return h;
		}
	}

	//Lets a panel draw whatever it likes over its own box, the same way it would on screen.
	private static void CustomContent( Panel panel, float opacity, UiClip clip, Matrix? transform,
		List<UiBatch> output, TextureUploader uploadTexture, bool live )
	{
		if ( panel.DrawnByWalk ) return;

		var buffer = UIDrawBuffer.Current;

		custom.Output = output;
		custom.Clip = clip;
		custom.Transform = transform;
		custom.Upload = uploadTexture;

		buffer.Collector = custom;
		buffer.ScaleToScreen = panel.ScaleToScreen;
		buffer.Opacity = opacity;

		//Where the output stood before the panel drew. What OnDraw puts out is made from state the walk cannot
		//see, so a frame holding any of it is not kept; a panel whose OnDraw drew nothing costs the frame nothing.
		var before = MarkOutput( output );

		try
		{
			panel.OnDraw();
		}
		catch ( System.Exception e )
		{
			Log.Error( e, $"drawing {panel}" );
		}
		finally
		{
			buffer.Collector = null;
			custom.Output = null;
			custom.Upload = null;
		}

		//A retaining panel says when its drawing changes, so what it drew keeps the frame keepable.
		if ( live && MarkOutput( output ) != before ) frameLive = true;
	}

	//-- vector shapes ------------------------------------------------------------------------

	//How many of the panel's pixels one pixel of the screen is, at a point, under a transform - the size of the fringe a shape
	//is tessellated with. A transform that shrinks the panel makes each screen pixel more of its pixels; nought transform, one.
	private static float PixelSize( Matrix? transform, Vector2 at )
	{
		if ( transform is not { } t ) return 1.0f;

		var o = Place( t, at.x, at.y );
		var x = ( Place( t, at.x + 1.0f, at.y ) - o ).Length;
		var y = ( Place( t, at.x, at.y + 1.0f ) - o ).Length;
		var scale = MathF.Min( x, y );

		return scale > 1e-4f && float.IsFinite( scale ) ? 1.0f / scale : 1.0f;
	}

	//A vector shape's fill and stroke, from its tessellation - which is only made again
	//when the shape, its view box or the size of a screen pixel moved, never for a colour.
	private static void VectorShape( SvgShape shape, Styles style, float opacity, UiClip clip, Matrix? transform, List<UiBatch> output )
	{
		if ( shape.Svg is not { } svg ) return;

		var origin = svg.ContentOrigin;
		var tessellation = shape.Tessellate( PixelSize( transform, origin ) );
		if ( tessellation is null ) return;

		//Wholly outside what an ancestor allows: nothing to draw.
		if ( clip.IsSet )
		{
			var area = tessellation.Bounds;
			area.Position += origin;

			var bounds = transform is null ? area : Moved( area, transform.Value );

			if ( bounds.Right <= clip.Rect.x || bounds.Left >= clip.Rect.z ||
				bounds.Bottom <= clip.Rect.y || bounds.Top >= clip.Rect.w )
				return;
		}

		if ( tessellation.Filled )
		{
			var fill = ( style.Fill ?? Color.Black );
			fill = fill.WithAlpha( fill.a * ( style.FillOpacity ?? 1.0f ) * opacity );

			if ( fill.a > 0.001f ) EmitVector( output, tessellation.Fill, fill, origin, clip, transform );
		}

		if ( tessellation.Stroked )
		{
			var stroke = ( style.Stroke ?? Color.Transparent );
			stroke = stroke.WithAlpha( stroke.a * ( style.StrokeOpacity ?? 1.0f ) * tessellation.StrokeAlpha * opacity );

			if ( stroke.a > 0.001f ) EmitVector( output, tessellation.Stroke, stroke, origin, clip, transform );
		}
	}

	//Emits solid triangles into the open box batch, padded to multiples of six vertices
	internal static void EmitVector( List<UiBatch> output, List<VectorVertex> triangles, Color colour, Vector2 origin,
		UiClip clip, Matrix? transform )
	{
		if ( triangles.Count < 3 ) return;

		if ( output.Count == 0 || output[^1].Texture != 0 )
		{
			if ( output.Count > 0 ) walk.TextureCuts++;

			walk.Batches++;
			output.Add( new UiBatch( TakeList(), 0 ) );
		}

		var vertices = output[^1].Vertices;
		var clipVertical = PackVertical( clip.Radii, clip.RadiiV );
		var misc = new Vector4( 0.0f, (float)(int)UiFill.Vector, 0.0f, 0.0f );
		Color32 fill = colour;

		var count = triangles.Count - ( triangles.Count % 3 );
		vertices.EnsureCapacity( vertices.Count + count + 6 );

		UiVertex last = default;

		for ( var i = 0; i < count; i++ )
		{
			var t = triangles[i];
			var local = t.Position + origin;

			last = new UiVertex
			{
				Position = transform is null ? local : Place( transform.Value, local.x, local.y ),
				Local = local,
				Uv = new Vector2( t.Across, t.Half ),
				Fill = fill,
				Misc = misc,
				Clip = clip.Rect,
				ClipRadii = clip.Radii,
				ClipVerticalTop = clipVertical.Top,
				ClipVerticalBottom = clipVertical.Bottom,
				Filter = new Vector4( t.End, 0.0f, 0.0f, 0.0f ),
			};

			vertices.Add( last );
		}

		//Whole sixes: the rest is the last vertex again, triangles with no area.
		while ( vertices.Count % 6 != 0 ) vertices.Add( last );

		walk.Boxes += ( count + 5 ) / 6;
	}

	//Misc.y bit: the box's picture is known opaque, so DropCovered can skip what's under it (CPU only)
	internal const int OpaquePicture = 16;

	//Where the border's style sits in Misc.y: above the fill kind and the opaque mark, which the shader and DropCovered mask off below it.
	internal const int StyleShift = 5;

	//Misc.y bit for image-rendering: pixelated, done in the shader (see PixelatedUv in ui.slang)
	internal const int Pixelated = 512;

	private static void AddBox( List<UiBatch> output, Rect rect, Vector4 radii,
		Color fill, Color border, float borderWidth, ulong texture, UiClip clip,
		UiFill kind = UiFill.None, float angle = 0.0f,
		Vector4 filter = default, Vector2 filter2 = default, float blur = 0.0f,
		Rect? geometry = null, Matrix? transform = null, Vector4? uvRect = null,
		bool fills = false, Vector4 radiiV = default, UiSides? sides = null,
		BorderStyle borderStyle = BorderStyle.Solid, bool pixelated = false )
	{
		walk.Boxes++;

		var quad = geometry ?? rect;

		var quadArea = MathF.Max( 0.0f, quad.Width ) * MathF.Max( 0.0f, quad.Height );
		walk.Area += quadArea;

		{
			var emitted = walk.BiggestEmitted ??= new (float, string, int)[4];

			for ( var i = 0; i < emitted.Length; i++ )
			{
				if ( quadArea <= emitted[i].Item1 ) continue;

				for ( var j = emitted.Length - 1; j > i; j-- ) emitted[j] = emitted[j - 1];

				emitted[i] = ( quadArea, walking?.ElementName ?? "?", walk.Boxes );
				break;
			}
		}

		//Wholly outside what an ancestor allows: nothing to draw, and no reason to make the pixel shader work it
		//out per fragment. A transformed panel is tested where it ends up, which is the only place it can be seen.
		var bounds = transform is null ? quad : Moved( quad, transform.Value );

		if ( clip.IsSet &&
			( bounds.Right <= clip.Rect.x || bounds.Left >= clip.Rect.z ||
				bounds.Bottom <= clip.Rect.y || bounds.Top >= clip.Rect.w ) )
		{
			return;
		}

		//Which picture in the indexed table, if it is in it. A box that names one goes into whatever batch is open,
		//whatever that batch's own texture is - which is what lets a batch hold boxes with different pictures at all.
		var slot = 0u;

		if ( texture != 0 && texture != BackdropTexture && Bindless )
		{
			slot = CoreRender.TextureIndex( texture );

			if ( recording ) recordSlots.Add( (texture, slot) );
			NoteSlot( texture, slot );
		}

		//What the batch has to bind. Nought for anything reading the table: those boxes
		//carry their own picture and the bound one is never sampled, so they all share.
		var bound = slot != 0 ? 0ul : texture;

		//Runs of boxes wanting the same binding share a batch. Order is paint order either way, which is why they
		//are runs rather than groups: two panels that overlap have to be drawn in the order they were walked.
		if ( output.Count == 0 || output[^1].Texture != bound )
		{
			if ( output.Count > 0 ) walk.TextureCuts++;

			walk.Batches++;

			if ( texture == BackdropTexture ) walk.Backdrops++;

			output.Add( new UiBatch( TakeList(), bound ) );
		}

		if ( seenTextures.Add( texture ) )
		{
			walk.DistinctTextures++;

			if ( slot != 0 )
			{
				walk.Slotted++;

				if ( seenSlots.Add( slot ) ) walk.DistinctSlots++;
			}
			else if ( texture != 0 && texture != BackdropTexture )
			{
				walk.Unslotted++;
			}
		}

		var vertices = output[^1].Vertices;

		var box = new Vector4( rect.Left, rect.Top, rect.Width, rect.Height );

		//A caller who passed a texture and said nothing about it means the ordinary thing: stretch it across the box.
		if ( kind == UiFill.None && texture != 0 ) kind = UiFill.Texture;

		var misc = new Vector4( borderWidth,
			(float)( (int)kind | ( fills ? OpaquePicture : 0 ) | ( (int)borderStyle << StyleShift ) | ( pixelated ? Pixelated : 0 ) ), angle, blur );

		var colours = sides ?? UiSides.All( border );
		var vertical = PackVertical( radii, radiiV );
		var clipVertical = PackVertical( clip.Radii, clip.RadiiV );

		UiVertex Corner( float x, float y, float u, float v ) => new()
		{
			//Moved to wherever the transforms put it, and remembering where it was: the shape is measured where it was drawn, not where it landed.
			Position = transform is null
				? new Vector2( x, y )
				: Place( transform.Value, x, y ),

			Local = new Vector2( x, y ),
			Uv = new Vector2( u, v ),
			Rect = box,
			Radii = radii,
			Fill = fill,
			BorderLeft = colours.Left,
			BorderTop = colours.Top,
			BorderRight = colours.Right,
			BorderBottom = colours.Bottom,
			VerticalTop = vertical.Top,
			VerticalBottom = vertical.Bottom,
			ClipVerticalTop = clipVertical.Top,
			ClipVerticalBottom = clipVertical.Bottom,
			Misc = misc,
			Clip = clip.Rect,
			ClipRadii = clip.Radii,
			Filter = filter,
			Filter2 = filter2,
			Texture = slot,
		};

		var left = quad.Left;
		var top = quad.Top;
		var right = quad.Right;
		var bottom = quad.Bottom;

		var uv = uvRect ?? new Vector4( 0, 0, 1, 1 );

		void Piece( float l, float t, float r, float b )
		{
			if ( r <= l || b <= t ) return;

			float U( float x ) => uv.x + ( ( uv.z - uv.x ) * ( x - left ) / MathF.Max( right - left, 0.0001f ) );
			float V( float y ) => uv.y + ( ( uv.w - uv.y ) * ( y - top ) / MathF.Max( bottom - top, 0.0001f ) );

			vertices.Add( Corner( l, t, U( l ), V( t ) ) );
			vertices.Add( Corner( r, t, U( r ), V( t ) ) );
			vertices.Add( Corner( l, b, U( l ), V( b ) ) );
			vertices.Add( Corner( l, b, U( l ), V( b ) ) );
			vertices.Add( Corner( r, t, U( r ), V( t ) ) );
			vertices.Add( Corner( r, b, U( r ), V( b ) ) );
		}

		//An outer shadow is masked out under its own box, so the part of the box clear of its corners is left out of the
		//geometry: a ring of four bands round it instead of a quad whose middle every pixel shades only to discard
		if ( kind == UiFill.Shadow )
		{
			var corner = MathF.Max( MathF.Max( MathF.Max( radii.x, radii.y ), MathF.Max( radii.z, radii.w ) ),
				MathF.Max( MathF.Max( radiiV.x, radiiV.y ), MathF.Max( radiiV.z, radiiV.w ) ) ) + 1.0f;

			var holeLeft = MathF.Max( rect.Left + corner, left );
			var holeTop = MathF.Max( rect.Top + corner, top );
			var holeRight = MathF.Min( rect.Right - corner, right );
			var holeBottom = MathF.Min( rect.Bottom - corner, bottom );

			if ( holeRight - holeLeft >= 8.0f && holeBottom - holeTop >= 8.0f )
			{
				Piece( left, top, right, holeTop );
				Piece( left, holeBottom, right, bottom );
				Piece( left, holeTop, holeLeft, holeBottom );
				Piece( holeRight, holeTop, right, holeBottom );
				return;
			}
		}

		Piece( left, top, right, bottom );
	}
}

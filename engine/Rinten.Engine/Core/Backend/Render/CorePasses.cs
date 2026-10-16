namespace Rinten;

//Named per-frame pictures and which one the next draw goes into; pooled across frames
internal static class CorePasses
{
	private static readonly Diagnostics.Logger log = new( "Render" );

	//One name of this frame's.
	internal sealed class Named
	{
		public string Name;

		/// <summary>The picture, or nought for something the engine already has - the depth.</summary>
		public ulong Picture;

		/// <summary>Where it sits in the table a shader indexes.</summary>
		public int Index;

		public int Width;
		public int Height;
		public int Levels;

		/// <summary>The format it was really made in - what a pipeline into it is baked against.</summary>
		public int Format;

		public CoreRender.PictureKind Kind;

		/// <summary>Whether it has been cleared this frame - the first draw into it clears it.</summary>
		public bool Cleared;

		/// <summary>The texture of the game's this picture stands for, where a list set a render target of its own - see <see cref="WantTexture"/> - and whether anything has been drawn into it since what it holds last went there.</summary>
		public Texture CopiesInto;
		public bool Drawn;
	}

	private static readonly Dictionary<string, Named> named = new( StringComparer.OrdinalIgnoreCase );
	private static readonly Stack<Named> spare = new();
	private static readonly HashSet<string> said = new( StringComparer.OrdinalIgnoreCase );

	//A draw into a picture that tests against the world's depth - see VkDevice.TargetPicture.
	internal const int DepthBit = 0x40000000;

	internal static Named Find( string name )
		=> !string.IsNullOrEmpty( name ) && named.TryGetValue( name, out var found ) ? found : null;

	//Where a name's picture sits in the table - nought for a name nobody made.
	internal static int IndexOf( string name ) => Find( name )?.Index ?? 0;

	//The frame is done: every picture goes back to the pool. Also the start of a frame, for whatever a frame that stopped half way left behind.
	internal static void EndFrame()
	{
		Settle();
		Close();
		wanted = null;

		foreach ( var one in named.Values )
		{
			if ( one.Picture != 0 ) CoreRenderDevice.ReleasePicture( one.Picture );
			spare.Push( one );
		}

		named.Clear();
	}

	//A picture for this frame under a name, of this shape - the one it already has where the shape is the same.
	internal static Named Acquire( string name, CoreRender.PictureKind kind, int width, int height, int levels )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return null;

		width = Math.Max( width, 1 );
		height = Math.Max( height, 1 );
		levels = Math.Max( levels, 1 );

		//Pad the size so every mip down to ~16 texels is exactly half the one above
		if ( levels > 1 )
		{
			var exact = 1;

			for ( var side = Math.Max( width, height ); side > 16 && exact < levels; side = ( side + 1 ) / 2 ) exact++;

			var grain = 1 << ( exact - 1 );

			width = ( ( width + grain - 1 ) / grain ) * grain;
			height = ( ( height + grain - 1 ) / grain ) * grain;

			//Cap levels at `exact` for correctness: past it a level isn't exactly half the one above
			levels = Math.Min( levels, exact );
		}

		if ( Find( name ) is { } had )
		{
			if ( had.Picture != 0 && had.Kind == kind && had.Width == width && had.Height == height && had.Levels >= levels )
				return had;

			Release( name );
		}

		//Stored into by a compute pass or not, it is made so that one may: the name says nothing about which,
		//and a format that cannot be stored into is made in half floats instead - see VkPictures.FormatOf. The portable frame has no compute.
		var picture = CoreRenderDevice.AcquirePicture( kind, width, height, levels, storage: !CoreRenderDevice.PortableFrame );

		if ( picture == 0 )
		{
			if ( said.Add( name ) ) log.Warning( $"the picture {name} ({kind}, {width}x{height}) would not be made: {CoreRenderDevice.Description}" );
			return null;
		}

		CoreRenderDevice.PictureShape( picture, out var w, out var h, out var l, out var f );

		var made = spare.Count > 0 ? spare.Pop() : new Named();

		made.Name = name;
		made.Picture = picture;
		made.Index = CoreSceneDrawer.Indexed( CoreRenderDevice.PictureTexture( picture ) );
		made.Width = w;
		made.Height = h;
		made.Levels = l;
		made.Format = f;
		made.Kind = kind;
		made.Cleared = false;
		made.CopiesInto = null;
		made.Drawn = false;

		named[name] = made;

		return made;
	}

	//Draws into a game texture go via a frame picture, then blitted across; starts each frame cleared
	internal static bool WantTexture( RenderTarget target )
	{
		var texture = target?.ColorTarget;

		if ( texture?.core is null || target.Width <= 0 || target.Height <= 0 ) return false;

		var made = Acquire( target.PictureName, Rinten.Rendering.CommandList.PictureKindOf( texture.ImageFormat ),
			target.Width, target.Height, 1 );

		if ( made is null ) return false;

		made.CopiesInto = texture;

		Want( made );

		return true;
	}

	//What the picture standing for a render target holds, copied into its texture - where anything was drawn
	//into it since the last copy. Before draws go anywhere else, and before the frame gives its pictures back.
	internal static void Settle()
	{
		var one = wanted;

		if ( one?.CopiesInto is null || !one.Drawn || one.Picture == 0 ) return;

		Close();

		one.Drawn = false;

		var device = one.CopiesInto.core?.Device() ?? 0;

		if ( device == 0 || !CoreRenderDevice.PictureToTexture( one.Picture, device ) )
		{
			if ( said.Add( "settle " + one.Name ) )
				log.Warning( $"what was drawn into {one.Name} did not reach its texture: {CoreRenderDevice.Description}" );
		}
	}

	//A name for something the engine already has, read and never drawn into - the depth.
	internal static void Alias( string name, int index, int width, int height )
	{
		if ( string.IsNullOrWhiteSpace( name ) || index == 0 ) return;

		Release( name );

		var made = spare.Count > 0 ? spare.Pop() : new Named();

		made.Name = name;
		made.Picture = 0;
		made.Index = index;
		made.Width = width;
		made.Height = height;
		made.Levels = 1;
		made.Format = 0;
		made.Cleared = true;

		named[name] = made;
	}

	//A name given back before the frame is done with it.
	internal static void Release( string name )
	{
		if ( Find( name ) is not { } had ) return;

		if ( ReferenceEquals( opened, had ) ) Close();
		if ( ReferenceEquals( wanted, had ) ) wanted = null;
		if ( had.Picture != 0 ) CoreRenderDevice.ReleasePicture( had.Picture );

		named.Remove( name );
		spare.Push( had );
	}

	//The size a picture asked for by a divisor comes to: the picture this stage draws - the world's, or after the compose the window's - divided.
	internal static (int Width, int Height) Sized( int divisor )
	{
		var size = CorePostBlit.OpenSize;
		divisor = Math.Max( divisor, 1 );

		return ( Math.Max( (int)size.x / divisor, 1 ), Math.Max( (int)size.y / divisor, 1 ) );
	}

	//-- where draws go -------------------------------------------------------

	private static Named wanted;
	private static Named opened;
	private static bool openedWithDepth;
	private static Color? clearTo;

	//Whether a list has named a picture for its draws.
	internal static bool Wanting => wanted is not null;

	internal static Named Wanted => wanted;

	//The next draws go into the picture of this name. False where there is none.
	internal static bool Want( string name )
	{
		Settle();
		Close();
		wanted = Find( name );

		return wanted is not null && wanted.Picture != 0;
	}

	//The same, for one already in hand - or none.
	internal static void Want( Named one )
	{
		if ( !ReferenceEquals( one, wanted ) ) Settle();

		Close();
		wanted = one is { Picture: not 0 } ? one : null;
	}

	//Draws go back to the frame.
	internal static void Unwant()
	{
		Settle();
		Close();
		wanted = null;
		clearTo = null;
	}

	//The next draw into the wanted picture clears it to this first.
	internal static void ClearWanted( Color colour )
	{
		if ( wanted is null ) return;

		Close();
		clearTo = colour;
		wanted.Cleared = false;
	}

	//The wanted picture open as the target, and the format a pipeline into it is baked against - with DepthBit where the world's depth is attached. False
	//where it would not open, which with a depth asked for is a picture not the world's size: nothing that tests against the depth can draw into that.
	internal static bool Open( bool depth, out int format )
	{
		format = 0;

		if ( wanted is null || wanted.Picture == 0 ) return false;

		if ( ReferenceEquals( opened, wanted ) && openedWithDepth == depth )
		{
			format = wanted.Format | ( depth ? DepthBit : 0 );
			return true;
		}

		Close();

		var clear = !wanted.Cleared || clearTo.HasValue;
		var colour = clearTo ?? Color.Transparent;

		if ( !CoreRenderDevice.BeginPicture( wanted.Picture, 0, colour, clearFirst: clear, withDepth: depth ) )
		{
			if ( said.Add( "open " + wanted.Name ) )
				log.Warning( $"the picture {wanted.Name} would not open as a target{( depth ? " with the world's depth - it has to be the world's size" : "" )}: {CoreRenderDevice.Description}" );

			return false;
		}

		wanted.Cleared = true;
		wanted.Drawn = true;
		clearTo = null;

		opened = wanted;
		openedWithDepth = depth;
		format = wanted.Format | ( depth ? DepthBit : 0 );

		return true;
	}

	//Whatever picture is open as the target, closed.
	internal static void Close()
	{
		if ( openedFrame is not null )
		{
			openedFrame = null;
			CoreRenderDevice.EndTarget();
		}

		if ( opened is null ) return;

		opened = null;
		CoreRenderDevice.EndTarget();
	}

	//The frame's own picture, where a list's draws opened it - see OpenForDraw.
	private static CoreRender.TargetKind? openedFrame;

	//Opens the list's picture or the frame picture for drawing, with world depth where asked and possible
	//A draw needing depth into a target that can't have it is refused unless orNone allows going without
	internal static bool OpenForDraw( bool depth, bool orNone, out CoreRender.TargetKind target, out int format,
		out bool withDepth, out bool finishes )
	{
		format = 0;
		withDepth = false;
		finishes = false;
		target = CoreRender.TargetKind.Picture;

		if ( Wanting )
		{
			if ( depth && Open( depth: true, out format ) )
			{
				withDepth = true;
				return true;
			}

			return ( !depth || orNone ) && Open( depth: false, out format );
		}

		if ( !CorePostBlit.FrameTarget( depth, out target, out var opens, out withDepth, out finishes ) ) return false;

		if ( depth && !withDepth && !orNone )
		{
			if ( said.Add( "no depth " + target ) )
				log.Warning( $"a command list's draw tests against the world's depth, and the frame is in {target} at this stage, which has none - it is skipped" );

			return false;
		}

		//The screen, which the first draw with nowhere else to go opens.
		if ( !opens )
		{
			Close();
			return true;
		}

		if ( openedFrame == target ) return true;

		Close();

		if ( !CoreRenderDevice.BeginTarget( target, Color.Transparent, clearFirst: false ) )
		{
			if ( said.Add( "open frame " + target ) )
				log.Warning( $"the frame's picture ({target}) would not open for a command list's draws: {CoreRenderDevice.Description}" );

			return false;
		}

		openedFrame = target;

		return true;
	}

	//The frame as it is now, copied into a picture of its own under a name, its chain filled where asked - what GrabFrameTexture is.
	internal static Named Grab( string name, bool chain )
	{
		var (width, height) = Sized( 1 );
		var made = Acquire( name, CoreRender.PictureKind.Hdr, width, height, chain ? 16 : 1 );

		if ( made is null ) return null;

		CorePostBlit.CopyInto( made );

		if ( chain ) Chain( made );

		return made;
	}

	private static readonly Dictionary<(int, FoldMode), ulong> folds = new();

	//What each texel of a level above keeps of the four under it.
	internal enum FoldMode
	{
		/// <summary>Their average: a picture seen from further off.</summary>
		Average,

		/// <summary>The least of them: a depth's nearest, for a march that skips the empty.</summary>
		Least,

		/// <summary>The most of them: a depth's farthest, for a test that must be certain.</summary>
		Most,
	}

	//A picture's chain of halvings, out of its top: folded by compute - see picture_fold.slang - where the picture can be stored into, blitted
	//down where there is no fold for its format. Blitted is an average whatever was asked: a chain of the least or the most has no other way down.
	internal static void Chain( Named picture, FoldMode mode = FoldMode.Average )
	{
		if ( picture is not { Picture: not 0 } ) return;

		Close();
		CoreRenderDevice.PictureChain( picture.Picture, FoldFor( picture.Format, mode ) );
	}

	private static ulong FoldFor( int format, FoldMode mode )
	{
		if ( folds.TryGetValue( (format, mode), out var made ) ) return made;

		var name = CoreRenderDevice.StorageName( format );
		var spirv = name is null ? null
			: CoreShaderCache.Pass( EngineShaders.Fold, "main", CoreShaderCompiler.Stage.Compute,
				EngineShaders.FoldFormat( name ) + EngineShaders.FoldMode( mode ) );

		made = spirv is null ? 0 : CoreRenderDevice.CreateFoldPipeline( spirv );

		if ( made == 0 && name is not null ) log.Warning( $"the fold for {name} did not compile - chains in it are blitted: {CoreRenderDevice.ShaderError}" );

		folds[(format, mode)] = made;

		return made;
	}

	//The folds, let go with the device's pipelines - see CoreSceneDrawer.ReleaseResources.
	internal static void ReleasePipelines()
	{
		foreach ( var one in folds.Values )
		{
			if ( one != 0 ) CoreRenderDevice.DestroyPipeline( one );
		}

		folds.Clear();
	}
}

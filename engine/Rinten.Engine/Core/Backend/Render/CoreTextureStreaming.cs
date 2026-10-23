using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Rinten;

//Texture streaming: material textures keep small mips resident; finer mips load only while wanted
//Wants come from drawn clusters and queued parts; drops wait Settle seconds; r_texture_budget caps it
internal static class CoreTextureStreaming
{
	//Texels along a texture's longer side that are always kept: below it a level is not worth the swap.
	private const int Tail = 128;

	//How long a coarser level has to be wanted before the finer ones go, in seconds.
	private const double Settle = 4.0;

	//How often the materials' wants are read back and worked out onto the pictures, in seconds.
	internal const double Every = 0.25;

	//Pictures read at once.
	private const int AtOnce = 4;

	//How long a material's want read back counts, in seconds - every scene drawn in it has a say.
	private const double Watched = 1.0;

	private sealed class Streamed
	{
		public ulong Handle;
		public string Path;
		public int Width, Height, Levels;

		/// <summary>The finest level the quality setting lets on the device - see VkDevice.textureMaxEdge.</summary>
		public int Finest;

		/// <summary>The finest level on the device now.</summary>
		public int Top;

		/// <summary>The finest level wanted, and since when a coarser one than Top has been.</summary>
		public int Wanted;
		public double CoarserSince = -1.0;

		/// <summary>Texels across a unit of coordinates the nearest wearer is drawn at; nought while nothing does.</summary>
		public float Need;

		/// <summary>Whether its levels follow <see cref="Need"/>; one that arrived whole follows it once something wore it.</summary>
		public bool Seen;

		/// <summary>Read by something that is not a material set: whole for good - see KeepWhole.</summary>
		public bool Whole;

		/// <summary>A page's KTX2 rather than a .tex - read where it is asked, see Read.</summary>
		public bool Universal;

		/// <summary>A read under way, the level it starts at.</summary>
		public Task<ulong> Reading;
		public int ReadingTop;

		/// <summary>A read finished whose levels a page is still transcoding - see BrowserTextures.Ready.</summary>
		public ulong Arriving;

		public long Bytes;
	}

	private static readonly Dictionary<ulong, Streamed> streamed = new();

	//How big a streamed picture is whole, whatever of it is on the device now; false for one that is not streamed.
	internal static bool SizeOf( ulong handle, out int width, out int height )
	{
		lock ( streamed )
		{
			if ( streamed.TryGetValue( handle, out var found ) )
			{
				width = found.Width;
				height = found.Height;
				return true;
			}
		}

		width = height = 0;
		return false;
	}

	private static double workedAt = -1e9;

	//By material: the most texels a unit of its coordinates was wanted at lately, and when.
	private static float[] wanted = new float[256];
	private static double[] wantedAt = new double[256];

	//By material, from the parts drawn the classic way: the most texels a unit of its coordinates was wanted at lately, and when - see Wear.
	private static readonly Dictionary<ulong, (float Texels, double At)> worn = new();

	//Whether the parts queued this turn of the loop say what they want - for one turn every Every seconds, see Step. CoreSceneDrawer asks.
	internal static bool Collecting { get; private set; }

	/// <summary>How many level changes have been put in place, and how many bytes the streamed textures hold - for the report.</summary>
	public static long Swaps;
	public static long HeldBytes;
	public static int Count => streamed.Count;

	private static readonly Logger log = new( "Textures" );

	//RINTEN_STREAM_LOG=1: a line a texture registered and a line a level change put in place.
	private static readonly bool talk = System.Environment.GetEnvironmentVariable( "RINTEN_STREAM_LOG" ) == "1";

	internal static bool On => Rinten.Rendering.DrawSettings.TextureStreaming;

	//Blocks under way that draw a picture of a thing - see Disabled.
	private static int disabled;

	/// <summary>Runs a block whose loaded textures go up whole; already streamed ones keep their current levels</summary>
	public static void Disabled( Action action )
	{
		Interlocked.Increment( ref disabled );

		try
		{
			action?.Invoke();
		}
		finally
		{
			Interlocked.Decrement( ref disabled );
		}
	}

	//Reads for textures that went before they finished: the pictures they make are let go of.
	private static readonly List<Task<ulong>> orphans = new();

	/// <summary>A material's picture from its file: its tail alone where it is one worth streaming and streaming is on, whole otherwise - and streamed from now either way, if it is worth it.</summary>
	public static ulong Create( string path, CoreTextureFile file )
	{
		if ( file is null ) return 0;

		if ( !Worth( path, file.Width, file.Height, file.MipCount ) ) return CoreRenderDevice.CreateTexture( file );

		var finest = FinestOf( file );
		var top = Top( file.Width, file.Height, file.MipCount, finest );
		var handle = top > 0 ? MadeFrom( file, top ) : CoreRenderDevice.CreateTexture( file );

		return Streams( handle, path, file.Width, file.Height, file.MipCount, finest, top, universal: false );
	}

	/// <summary>A page's picture from its KTX2 - the same, its small levels cut out of the file it holds.</summary>
	public static ulong Create( string path, CoreUniversalTexture picture )
	{
		if ( picture is null ) return 0;

		if ( !Worth( path, picture.Width, picture.Height, picture.Levels ) ) return CoreRender.TextureCreateUniversal( picture );

		var top = Top( picture.Width, picture.Height, picture.Levels, 0 );
		var handle = CoreRender.TextureCreateUniversal( top > 0 ? picture.From( top ) ?? picture : picture );

		return Streams( handle, path, picture.Width, picture.Height, picture.Levels, 0, top, universal: true );
	}

	private static bool Worth( string path, int width, int height, int levels )
		=> !string.IsNullOrEmpty( path ) && levels >= 2 && Math.Max( width, height ) > Tail;

	//The level a picture goes up from: its tail - or its finest where streaming is off, or inside
	//a block that draws a picture of a thing, which is drawn before any want could come back.
	private static int Top( int width, int height, int levels, int finest )
		=> On && disabled == 0 ? Math.Max( finest, TailLevel( width, height, levels ) ) : finest;

	//A picture made from its finest level top, streamed from now.
	private static ulong Streams( ulong handle, string path, int width, int height, int levels, int finest, int top, bool universal )
	{
		if ( handle == 0 || !CoreRender.TextureHeld( handle, out _, out var bytes ) ) return handle;

		lock ( streamed )
		{
			streamed[handle] = new Streamed
			{
				Handle = handle,
				Path = path,
				Width = width,
				Height = height,
				Levels = levels,
				Universal = universal,
				Finest = finest,
				Top = top,
				Wanted = top,

				//Up as its tail, it is wanted at its tail until something says otherwise; up whole, it stays whole until something wears it.
				Seen = top > finest,
				Bytes = bytes,
			};
		}

		if ( talk ) log.Info( $"streamed {path}: {width}x{height}, {levels} levels, up from level {top}" );

		return handle;
	}

	/// <summary>Pins a texture read by table index (decal, terrain, shader) whole now, so its slot never moves</summary>
	public static void KeepWhole( ulong handle )
	{
		if ( handle == 0 ) return;

		Streamed one;

		lock ( streamed )
		{
			if ( !streamed.TryGetValue( handle, out one ) || one.Whole ) return;
			one.Whole = true;
		}

		if ( one.Top <= one.Finest || one.Universal ) return;

		lock ( stepping )
		{
			//A read under way for it finishes first, and what it made goes: the whole picture is made here.
			if ( one.Reading is { } under )
			{
				try { under.Wait(); } catch ( AggregateException ) { }
				if ( under.Status == TaskStatus.RanToCompletion && under.Result != 0 ) CoreRender.TextureDestroy( under.Result );
				one.Reading = null;
			}

			if ( one.Arriving != 0 )
			{
				CoreRender.TextureDestroy( one.Arriving );
				one.Arriving = 0;
			}

			var made = CoreTextureFile.Load( one.Path ) is { } file && one.Finest < file.MipCount ? MadeFrom( file, one.Finest ) : 0ul;
			if ( made != 0 ) Apply( [(one, one.Finest, made)] );
		}
	}

	//What Step and a picture made whole out of turn take, so the two never work on one picture at once.
	private static readonly object stepping = new();

	/// <summary>A texture gone.</summary>
	public static void Forget( ulong handle )
	{
		if ( handle == 0 ) return;

		lock ( streamed )
		{
			if ( !streamed.Remove( handle, out var gone ) ) return;
			if ( gone.Reading is not null ) orphans.Add( gone.Reading );
			if ( gone.Arriving != 0 ) orphans.Add( Task.FromResult( gone.Arriving ) );
		}
	}

	/// <summary>A camera's wants read back - a float a material, its instance's scale over how near the nearest cluster wearing it came, nought for none - with the pixels a metre is across at a metre from it. On the main thread, see CoreReadbacks.Pump.</summary>
	public static void Wants( ReadOnlySpan<float> wants, float pixelsAtOneMetre )
	{
		if ( !( pixelsAtOneMetre > 0.0f ) ) return;

		var now = RealTime.NowDouble;

		if ( wants.Length > wanted.Length )
		{
			Array.Resize( ref wanted, Math.Max( wants.Length, wanted.Length * 2 ) );
			Array.Resize( ref wantedAt, wanted.Length );
		}

		for ( var m = 0; m < wants.Length; m++ )
		{
			if ( !( wants[m] > 0.0f ) ) continue;

			var metres = CoreMaterialTable.At( m )?.MetresPerUv ?? 0.0f;

			//Nothing to tell its density by: wanted whole.
			var texels = metres > 0.0f ? pixelsAtOneMetre * wants[m] * metres : float.MaxValue;

			if ( texels >= wanted[m] || now - wantedAt[m] > Watched )
			{
				wanted[m] = texels;
				wantedAt[m] = now;
			}
		}
	}

	/// <summary>A part drawn the classic way wears <paramref name="material"/> at <paramref name="texels"/> a unit of its coordinates - its camera's pixels a metre at its distance times the metres a unit covers; float.MaxValue where that cannot be told. Only while <see cref="Collecting"/>.</summary>
	public static void Wear( ulong material, float texels )
	{
		if ( material == 0 || !( texels > 0.0f ) ) return;

		var now = RealTime.NowDouble;

		lock ( worn )
		{
			if ( !worn.TryGetValue( material, out var had ) || texels >= had.Texels || now - had.At > Watched )
				worn[material] = (texels, now);
		}
	}

	/// <summary>Once a turn of the loop, outside any frame.</summary>
	public static void Tick()
	{
		try
		{
			lock ( stepping ) Step();
		}
		catch ( Exception e )
		{
			log.Warning( $"texture streaming: {e.Message}" );
		}
	}

	//What a step walks and what it hands over, kept between frames: a step runs every one.
	private static readonly List<Streamed> stepAll = new();
	private static readonly List<(Streamed One, int Top, ulong Made)> stepReady = new();

	private static void Step()
	{
		var now = RealTime.NowDouble;

		lock ( streamed )
		{
			for ( var i = orphans.Count - 1; i >= 0; i-- )
			{
				if ( !orphans[i].IsCompleted ) continue;
				if ( orphans[i].Status == TaskStatus.RanToCompletion && orphans[i].Result != 0 ) CoreRender.TextureDestroy( orphans[i].Result );
				orphans.RemoveAt( i );
			}
		}

		//Off: everything streamed goes back to whole, a few at a time.
		var on = On;

		//A turn every quarter of a second the classic draws say what they wear, and the turn after it everything said is put onto the pictures.
		if ( Collecting )
		{
			Collecting = false;
			Take( now );
		}
		else if ( on && now - workedAt >= Every )
		{
			workedAt = now;
			Collecting = true;
		}

		var all = stepAll;
		all.Clear();
		lock ( streamed ) all.AddRange( streamed.Values );

		var budget = (long)Rinten.Rendering.DrawSettings.TextureBudget * 1024L * 1024L;
		long held = 0, wantedBytes = 0;

		foreach ( var one in all )
		{
			held += one.Bytes;
			one.Wanted = on && one.Seen && !one.Whole ? LevelFor( one ) : one.Finest;
			wantedBytes += BytesAt( one, one.Wanted );
		}

		//Over budget: least wanted textures drop a level at a time; derived from wants only, so deterministic
		var pressed = false;

		if ( on && budget > 0 && wantedBytes > budget )
		{
			pressed = true;
			all.Sort( ( a, b ) => b.Wanted.CompareTo( a.Wanted ) );

			var gave = true;

			while ( wantedBytes > budget && gave )
			{
				gave = false;

				foreach ( var one in all )
				{
					if ( wantedBytes <= budget ) break;
					if ( one.Whole || one.Wanted >= TailLevel( one ) ) continue;

					wantedBytes -= BytesAt( one, one.Wanted ) - BytesAt( one, one.Wanted + 1 );
					one.Wanted++;
					gave = true;
				}
			}
		}

		HeldBytes = held;

		var reading = 0;
		foreach ( var one in all ) if ( one.Reading is not null || one.Arriving != 0 ) reading++;

		var ready = stepReady;
		ready.Clear();

		foreach ( var one in all )
		{
			if ( one.Reading is not null )
			{
				if ( !one.Reading.IsCompleted ) continue;

				one.Arriving = one.Reading.Status == TaskStatus.RanToCompletion ? one.Reading.Result : 0ul;
				one.Reading = null;

				if ( one.Arriving == 0 )
				{
					reading--;
					continue;
				}
			}

			//Its levels on the device - at once on Vulkan, once a page's transcoder has written them - and given to the texture then.
			if ( one.Arriving != 0 )
			{
				var state = CoreRender.TextureReady( one.Arriving );
				if ( state == 0 ) continue;

				var made = one.Arriving;
				one.Arriving = 0;
				reading--;

				if ( state > 0 ) ready.Add( (one, one.ReadingTop, made) );
				else CoreRender.TextureDestroy( made );

				continue;
			}

			if ( one.Wanted < one.Top )
			{
				one.CoarserSince = -1.0;
				if ( reading >= AtOnce ) continue;

				Read( one, one.Wanted );
				reading++;
			}
			else if ( one.Wanted > one.Top )
			{
				if ( one.CoarserSince < 0.0 ) one.CoarserSince = now;

				//Over the budget, or pinned, it goes at once; otherwise once it has stayed coarser.
				if ( !pressed && now - one.CoarserSince < Settle ) continue;
				if ( reading >= AtOnce ) continue;

				Read( one, one.Wanted );
				reading++;
			}
			else
			{
				one.CoarserSince = -1.0;
			}
		}

		if ( ready.Count > 0 ) Apply( ready );
	}

	//Finished pictures given to their textures - see VkTextures.Swap - and the material table's numbers for them looked up again.
	private static void Apply( List<(Streamed One, int Top, ulong Made)> ready )
	{
		var swaps = new (ulong, ulong)[ready.Count];
		for ( var i = 0; i < ready.Count; i++ ) swaps[i] = (ready[i].One.Handle, ready[i].Made);

		if ( CoreRender.TextureSwap( swaps ) > 0 )
		{
			CoreMaterialTable.Renumber();
			CoreResources.PicturesMoved();
		}

		foreach ( var (one, top, made) in ready )
		{
			//Whatever the swap did not take is a picture of its own still.
			if ( CoreRender.TextureHeld( made, out _, out _ ) )
			{
				CoreRender.TextureDestroy( made );
				continue;
			}

			if ( talk ) log.Info( $"levels of {one.Path}: from {one.Top} to {top} (wanted {one.Wanted}, {one.Need:0.#} texels a unit)" );

			one.Top = top;
			if ( CoreRender.TextureHeld( one.Handle, out _, out var bytes ) ) one.Bytes = bytes;
			Swaps++;
		}
	}

	//The level a texture is wanted at: the one whose texel is a pixel where its nearest wearer is drawn, one finer.
	private static int LevelFor( Streamed one )
	{
		if ( !( one.Need > 0.0f ) ) return Math.Max( one.Finest, TailLevel( one ) );

		var edge = Math.Max( one.Width, one.Height );
		var level = (int)MathF.Floor( MathF.Log2( edge / one.Need ) ) - 1;

		return Math.Clamp( level, one.Finest, TailLevel( one ) );
	}

	private static int TailLevel( Streamed one ) => TailLevel( one.Width, one.Height, one.Levels );

	//The coarsest level that is still at least the tail wide - or the last.
	private static int TailLevel( int width, int height, int levels )
	{
		var level = 0;
		var edge = Math.Max( width, height );

		while ( level < levels - 1 && ( edge >> ( level + 1 ) ) >= Tail ) level++;
		return level;
	}

	//The finest level the quality setting lets on the device - the rule VkTextures.CreateMips applies.
	private static int FinestOf( CoreTextureFile file )
	{
		var limit = VkDevice.textureMaxEdge;
		if ( limit == 0 ) return 0;

		var level = 0;
		var edge = Math.Max( file.Width, file.Height );

		while ( level < file.MipCount - 1 && ( edge >> level ) > limit ) level++;
		return level;
	}

	//What a texture holds on the device with level its finest: what it holds now, a quarter as
	//much for every level coarser - a level and everything under it is four thirds of the level.
	private static long BytesAt( Streamed one, int level )
	{
		var shift = 2 * ( level - one.Top );
		return shift >= 0 ? one.Bytes >> Math.Min( shift, 62 ) : one.Bytes << Math.Min( -shift, 20 );
	}

	//The levels from top down, read out of the file and made a picture of their own, on a worker.
	private static void Read( Streamed one, int top )
	{
		var path = one.Path;
		one.ReadingTop = top;

		//A page's KTX2 is in memory already and the cut is a copy; its levels are turned in the page's worker - and a page has no thread to run a task on.
		if ( one.Universal )
		{
			var made = CoreUniversalTexture.Load( path ) is { } picture && top < picture.Levels && picture.From( top ) is { } cut
				? CoreRender.TextureCreateUniversal( cut )
				: 0ul;

			one.Reading = Task.FromResult( made );
			return;
		}

		one.Reading = Task.Run( () => CoreTextureFile.Load( path ) is { } file && top < file.MipCount ? MadeFrom( file, top ) : 0ul );
	}

	//A picture of a file's levels from top down.
	private static unsafe ulong MadeFrom( CoreTextureFile file, int top )
	{
		var count = file.MipCount;
		Span<int> info = count * 3 <= 96 ? stackalloc int[count * 3] : new int[count * 3];
		file.Levels( info, out var pixels );

		//The levels lie back to back from the finest: the run from `top` is the bytes after the ones before it.
		var skip = 0;
		for ( var k = 0; k < top; k++ ) skip += info[( k * 3 ) + 2];

		var kept = info[( top * 3 )..];
		var bytes = pixels[skip..];

		fixed ( int* levels = kept )
		fixed ( byte* data = bytes )
		{
			return CoreRender.TextureCreateMips( Math.Max( 1, file.Width >> top ), Math.Max( 1, file.Height >> top ),
				(int)file.Format, file.IsSrgb ? 1 : 0, count - top, levels, data, bytes.Length );
		}
	}

	private static void Onto( Dictionary<ulong, float> byPicture, ReadOnlySpan<ulong> pictures, float need )
	{
		foreach ( var picture in pictures )
		{
			if ( picture == 0 ) continue;
			if ( !byPicture.TryGetValue( picture, out var had ) || had < need ) byPicture[picture] = need;
		}
	}

	//The materials' wants onto their pictures: each the most any material wearing it was wanted at lately, nought for none.
	private static void Take( double now )
	{
		var byPicture = new Dictionary<ulong, float>();

		for ( var m = 0; m < CoreMaterialTable.Count; m++ )
		{
			if ( CoreMaterialTable.At( m ) is not { Uses: > 0 } info ) continue;

			var need = m < wanted.Length && now - wantedAt[m] <= Watched ? wanted[m] : 0.0f;
			if ( need > 0.0f ) Onto( byPicture, info.Pictures, need );
		}

		//And what the classic draws wore, by the material they were drawn with.
		Span<ulong> parts = stackalloc ulong[4];

		lock ( worn )
		{
			foreach ( var (material, said) in worn )
			{
				if ( now - said.At > Watched ) continue;

				CoreRender.MaterialParts( material, parts );
				Onto( byPicture, parts, said.Texels );
			}
		}

		lock ( streamed )
		{
			foreach ( var one in streamed.Values )
			{
				if ( byPicture.TryGetValue( one.Handle, out var need ) )
				{
					one.Need = need;
					one.Seen = true;
				}
				else if ( one.Seen )
				{
					one.Need = 0.0f;
				}
			}
		}
	}
}

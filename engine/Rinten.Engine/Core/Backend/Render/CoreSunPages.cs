using System;
using System.Collections.Generic;

namespace Rinten;

//Sun shadow: static world cached in toroidal clipmap pages, moving casters redrawn each frame
//Pages are double-buffered so rebakes don't blank shadows; planned before the frame block uploads
internal sealed class CoreSunPages : IDisposable
{
	//A level is Grid by Grid pages; the full frame's pages are 32 texels (2048 a level, the finest reaching ~4 m),
	//the portable frame's 16 - a quarter of the memory, each level half as far, at the same texels a metre
	internal const int Grid = 64;
	private static int PageTexels => CoreRenderDevice.PortableFrame ? 16 : 32;
	private static int Side => Grid * PageTexels;

	//Max levels (~4 m to ~1 km at default detail); only those reaching the far plane are made
	internal const int MostLevels = 9;

	//Readable distance from the eye in pages; must match SunReachPages in frame.slang or taps wrap the torus
	private static float ReachPages => ( Grid / 2 ) - 1.0f - ( 4.0f / PageTexels );

	//The movers' map: texels across, how far from the eye movers still cast, and its least square, in metres
	internal const int MoverSide = 1024;
	internal const float MoverReach = 64.0f;
	internal const float MoverLeast = 2.0f;

	//Per-frame bake limits; the rest waits, and a level isn't read until fully baked
	internal const int MostBakes = 16;
	internal const long MostBakeTriangles = 1L << 21;

	//The most views one frame adds to the cull: the bakes and the map of what moves.
	internal const int MostViews = MostBakes + 1;

	private readonly CoreCasters casters;

	//The pages twice over and which the bakes draw into (see Restart), and the movers' map - see VkTextures.ShadowMapCreate
	private readonly ulong[] maps = new ulong[2];
	private int drawn;
	private ulong movers;
	private int levels;

	//The sun's turn, as a cosine, before its pages bake again: a tenth of a degree, a few centimetres at the tip of a
	//twenty-metre shadow. A sun that stops is baked where it stopped, however little it moved.
	private static readonly float TurnCosine = MathF.Cos( 0.1f * MathF.PI / 180.0f );

	//Last sun direction and when it turned, by clock not frame, to tell a stopped sun from a turning one
	private Vector3 lastTowards;
	private double lastTurned;

	//How long the sun holds still before it counts as stopped, in seconds.
	private const double StoppedAfter = 0.25;

	//What the pixels read while the pages bake again from nothing: the other map as the bakes left it - see Restart
	private struct Shown
	{
		public bool Valid;
		public int Map;
		public System.Numerics.Matrix4x4 Basis;
		public float PageWorld, ZLow, ZHigh;
		public Vector3 Eye;
		public uint Mask;
	}

	private Shown shown;

	//Whether the pages are being baked again from nothing and the pixels read shown meanwhile.
	private bool rebaking;

	//The sun's frame - rows are the axes, the last along the light - and the direction it was made for.
	private System.Numerics.Matrix4x4 basis = System.Numerics.Matrix4x4.Identity;
	private Vector3 bakedFor;

	//How wide a page of the first level is, in metres.
	private float pageWorld;

	//What the pages' depth runs over along the light, and what the scene was last seen to span.
	private float zLow, zHigh = -1.0f;
	private float spanLow = float.MaxValue, spanHigh = float.MinValue;
	private bool spanKnown;

	//Whether the footprints and the span are to be worked out again from every caster - see Plan.
	private bool rebuild;

	//This frame's casters the tables do not hold, for the span - see Cover.
	private readonly List<System.Numerics.Vector4> hand = new();

	//Which tile each slot holds and whether it has been baked since - a level at a time, a row at a time.
	private readonly int[] heldX = new int[MostLevels * Grid * Grid];
	private readonly int[] heldY = new int[MostLevels * Grid * Grid];
	private readonly bool[] held = new bool[MostLevels * Grid * Grid];
	private readonly bool[] wanted = new bool[Grid * Grid];

	//Slots whose casters changed: baked again when there is room, read as they were until then - a page given up at once
	//leaves its level unreadable, and every shadow drawn from it drops a level for the frames the bake waits
	private readonly bool[] stale = new bool[MostLevels * Grid * Grid];

	//Of a level's wanted slots, those holding nothing a pixel may read - see Wanted.
	private readonly bool[] empty = new bool[Grid * Grid];

	//Where each level's region starts, in its own tiles.
	private readonly int[] fromX = new int[MostLevels];

	//Whether a level's slots changed since its region was last found whole, and where that region was - see Plan. An
	//unmoved region with untouched slots is still whole, and its slots need not be walked to say so.
	private readonly bool[] levelTouched = new bool[MostLevels];
	private readonly bool[] levelWhole = new bool[MostLevels];
	private readonly int[] wholeFromX = new int[MostLevels];
	private readonly int[] wholeFromY = new int[MostLevels];
	private readonly int[] fromY = new int[MostLevels];

	//Spheres whose pages have to be baked again - from what the casters said changed, applied on the next plan.
	private readonly List<System.Numerics.Vector4> dirty = new();

	//The still world's footprints in the sun's frame, for a bake's count of what lands in it; radius < 0 where unused
	private float[] footX = [], footY = [], footR = [];

	//One bake: a rectangle of a level's tiles and where in the layer its slots are.
	private readonly struct Bake
	{
		public readonly int Level, X0, Y0, Width, Height, SlotX, SlotY;
		public readonly long Triangles;

		public Bake( int level, int x0, int y0, int width, int height, int slotX, int slotY, long triangles )
		{
			Level = level; X0 = x0; Y0 = y0; Width = width; Height = height; SlotX = slotX; SlotY = slotY; Triangles = triangles;
		}
	}

	private readonly List<Bake> bakes = new();
	private readonly CoreMeshRender.ViewSetup[] planned = new CoreMeshRender.ViewSetup[MostViews];
	private int plannedCount;
	private int moverViews;

	private uint validMask;

	//Levels whole but for their outer EdgePages, as a moving eye leaves them until the strip it moved into is baked;
	//read short of that rim rather than not at all, or every shadow on screen drops a level each step - see SunStill
	private uint edgeMask;
	internal const int EdgePages = 2;
	private bool casts;
	private Vector3 eyeSun;

	//The square of the sun's frame this frame's map of what moves covers: its corner and its side.
	private System.Numerics.Vector2 moverCorner;
	private float moverWide;

	//Bakes drawn since the process started, and pages, for the report.
	internal static long Bakes, PagesBaked;

	//Frames since the process started on which a level was not to be read even short of its rim, outside a rebake -
	//shadows there fell to a coarser level, which shows as a flicker on a moving eye
	internal static long FramesShort, FramesNotWhole;

	//Still-world changes the pages were told of, and the pages they left to bake again - for the report
	internal static long ChangesNoted, PagesStaled;

	//Times the pages began again from nothing, and the last reason - for the report
	internal static long Restarts;
	internal static string RestartWhy = "";

	//How many views this frame adds to the cull, after the camera.
	internal int ViewCount => plannedCount;

	//How many triangles this frame's views can hold between them - what the lights' run of indices is sized by.
	internal long Triangles { get; private set; }

	internal CoreSunPages( CoreCasters casters )
	{
		this.casters = casters;
	}

	//The sun's frame: a turn of the world so that z runs along the light, from the direction the sun's light is seen from.
	internal static System.Numerics.Matrix4x4 Basis( Vector3 towardsSun )
	{
		var ( right, up, forward ) = LightAxes( -towardsSun.Normal );

		//Rows are the axes: a row-vector times this is (right, up, along).
		return new System.Numerics.Matrix4x4(
			right.x, up.x, forward.x, 0.0f,
			right.y, up.y, forward.y, 0.0f,
			right.z, up.z, forward.z, 0.0f,
			0.0f, 0.0f, 0.0f, 1.0f );
	}

	//A light's view axes (right, up, forward); must match LampAxes in frame.slang bit for bit
	internal static (Vector3 Right, Vector3 Up, Vector3 Forward) LightAxes( Vector3 forward )
	{
		var up = MathF.Abs( forward.Dot( Vector3.Up ) ) < 0.9f ? Vector3.Up : Vector3.Forward;
		var right = up.Cross( forward ).Normal;
		up = forward.Cross( right ).Normal;

		return (right, up, forward);
	}

	private Vector3 ToSun( Vector3 world ) => ToSun( world, in basis );

	private static Vector3 ToSun( Vector3 world, in System.Numerics.Matrix4x4 frame )
	{
		var at = System.Numerics.Vector3.Transform( new System.Numerics.Vector3( world.x, world.y, world.z ), frame );
		return new Vector3( at.X, at.Y, at.Z );
	}

	//A tile's torus slot, tile mod Grid and never negative; a mask since Grid is a power of two
	private static int Slot( int tile ) => tile & ( Grid - 1 );

	//Which tile a slot of a level holds, given where the level's region starts.
	[System.Runtime.CompilerServices.MethodImpl( System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining )]
	private static int TileAt( int from, int slot ) => from + ( ( slot - from ) & ( Grid - 1 ) );

	private float PageOf( int level ) => pageWorld * ( 1 << level );

	private void ForgetAll()
	{
		Array.Clear( held );
		Array.Clear( stale );
		validMask = 0;
		edgeMask = 0;
		Array.Fill( levelTouched, true );
	}

	//Rebakes every page into the other map while pixels keep reading the current, still-whole one
	private void Restart( [System.Runtime.CompilerServices.CallerLineNumber] int line = 0 )
	{
		Restarts++;
		RestartWhy = $"line {line}";

		//What this map holds, where the pixels have been reading it: only a map the pixels were reading
		//is worth keeping. A restart during a rebake keeps the bake that was on screen before it.
		if ( !rebaking && validMask != 0 )
		{
			shown = new Shown
			{
				Valid = true,
				Map = drawn,
				Basis = basis,
				PageWorld = pageWorld,
				ZLow = zLow,
				ZHigh = zHigh,
				Eye = eyeSun,
				Mask = validMask,
			};
		}

		if ( shown.Valid && shown.Map == drawn ) drawn ^= 1;

		ForgetAll();
		rebaking = true;
	}

	//Decides this frame's bakes and writes what a pixel needs into the frame block before it goes up;
	//the casters are last frame's
	internal void Plan( float[] frame, bool sunCasts, Vector3 towardsSun, float density, Vector3 eye, float far, float lodPixels )
	{
		bakes.Clear();
		plannedCount = 0;
		moverViews = 0;
		Triangles = 0;
		casts = sunCasts;

		if ( !casts )
		{
			hand.Clear();
			Write( frame );
			return;
		}

		//The first level's page at the asked density; the rest follow it.
		var asked = PageTexels / Math.Clamp( density, 4.0f, 512.0f );

		if ( MathF.Abs( asked - pageWorld ) > 1e-6f )
		{
			Restart();
			pageWorld = asked;
		}

		//As many levels as reach the far plane, and a map with that many layers - a new count is a new pair of maps
		var reachFirst = pageWorld * ReachPages;
		var wantLevels = Math.Clamp( (int)MathF.Ceiling( MathF.Log2( MathF.Max( far, reachFirst ) / reachFirst ) ) + 1, 1, MostLevels );

		if ( levels != wantLevels )
		{
			CoreRenderDevice.DestroyShadowMap( maps[0] );
			CoreRenderDevice.DestroyShadowMap( maps[1] );
			maps[0] = maps[1] = 0;
			levels = wantLevels;
			shown.Valid = false;
			ForgetAll();
		}

		//The second map is made the first time a restart turns to it.
		if ( maps[drawn] == 0 ) maps[drawn] = CoreRenderDevice.CreateShadowMap( Side, levels, readDepth: true );

		if ( maps[drawn] == 0 )
		{
			casts = false;
			hand.Clear();
			Write( frame );
			return;
		}

		//Sun turned: restart pages, but not mid-rebake and not for less than a step unless it stopped
		if ( ( towardsSun - lastTowards ).Length > 1e-6f )
		{
			lastTowards = towardsSun;
			lastTurned = RealTime.Now;
		}

		var stopped = RealTime.Now - lastTurned > StoppedAfter;

		if ( ( !rebaking || !shown.Valid ) && ( towardsSun - bakedFor ).Length > 1e-5f &&
			( stopped || bakedFor.Length < 1e-3f || towardsSun.Normal.Dot( bakedFor.Normal ) < TurnCosine ) )
		{
			Restart();
			bakedFor = towardsSun;
			basis = Basis( towardsSun );
			spanKnown = false;
		}

		//The footprints and the span, from everything, where this sun's frame has not seen the casters yet - a new drawer,
		//a sun that turned, a whole look. The casters have looked this frame already, so a drawer's first frame bakes.
		if ( rebuild || !spanKnown )
		{
			rebuild = false;
			spanLow = float.MaxValue;
			spanHigh = float.MinValue;

			for ( var slot = 0; slot < casters.Count; slot++ )
			{
				Foot( slot );
				Reach( casters.SphereOf( slot ) );
			}
		}

		//And what is drawn by hand, which the tables do not hold - see Cover.
		foreach ( var sphere in hand ) Reach( sphere );
		hand.Clear();

		//The depth the pages are drawn over: the scene's span along the light, with room to grow
		//before everything has to be baked again. Nothing is baked for a scene with nothing in it.
		spanKnown = spanLow <= spanHigh;

		if ( !spanKnown )
		{
			Write( frame );
			return;
		}

		if ( spanLow < zLow || spanHigh > zHigh )
		{
			Restart();

			var margin = MathF.Max( 2.0f, ( spanHigh - spanLow ) * 0.1f );
			zLow = spanLow - margin;
			zHigh = spanHigh + margin;
		}

		eyeSun = ToSun( eye );

		ApplyDirty();

		//Every level, the coarsest first: a pixel reads the finest level that is whole, so on
		//a frame that cannot bake everything the coarse ones are what is worth having first.
		var total = 0L;
		validMask = 0;
		edgeMask = 0;

		for ( var level = levels - 1; level >= 0; level-- )
		{
			var page = PageOf( level );
			fromX[level] = (int)MathF.Floor( eyeSun.x / page ) - ( Grid / 2 );
			fromY[level] = (int)MathF.Floor( eyeSun.y / page ) - ( Grid / 2 );

			//Skip the slot walk for levels already whole, unmoved and untouched since last check
			if ( levelWhole[level] && !levelTouched[level] &&
				wholeFromX[level] == fromX[level] && wholeFromY[level] == fromY[level] )
			{
				validMask |= 1u << level;
				continue;
			}

			var missing = Wanted( level );
			var whole = missing == 0 || PlanLevel( level, ref total );

			//Read wherever every slot holds its tile, stale or not; short of the rim where only the rim is empty
			if ( whole || !EmptyIn( level, rimToo: true ) ) validMask |= 1u << level;
			else if ( !EmptyIn( level, rimToo: false ) ) edgeMask |= 1u << level;

			//Known from here: what the plan just took is what the level holds.
			levelTouched[level] = false;
			levelWhole[level] = whole;
			wholeFromX[level] = fromX[level];
			wholeFromY[level] = fromY[level];
		}

		//A rebake ends on the frame its last level is taken: the pixels turn from the kept map to this one, whole
		if ( rebaking && validMask == ( 1u << levels ) - 1u ) rebaking = false;

		if ( !rebaking && ( validMask | edgeMask ) != ( 1u << levels ) - 1u ) FramesShort++;
		if ( !rebaking && validMask != ( 1u << levels ) - 1u ) FramesNotWhole++;

		//Bakes first, then the movers map, in the order Draw walks them, or the view lists get mismatched
		for ( var i = 0; i < bakes.Count; i++ )
			planned[plannedCount++] = BakeView( bakes[i], lodPixels );

		Movers( lodPixels, eye );

		Triangles = total + ( moverViews * casters.MoverTriangles );

		if ( debug && ( ( ++debugFrame % 120 ) == 5 || ( traceBakes && bakes.Count > 0 ) ) )
			Log.Info( $"sun pages: {levels} levels of {pageWorld:F3} m pages, far {far:F0}, whole 0x{validMask:X}{( rebaking ? ( shown.Valid ? $", rebaking - showing map {shown.Map} 0x{shown.Mask:X}" : ", rebaking" ) : "" )}, depth {zLow:F1}..{zHigh:F1} over a span of {spanLow:F1}..{spanHigh:F1}, eye {eyeSun.x:F1},{eyeSun.y:F1},{eyeSun.z:F1}, {bakes.Count} bakes of {total} triangles, {casters.Movers.Count} moving{TraceCasters()}" );

		Write( frame );
	}

	private static readonly bool debug = Environment.GetEnvironmentVariable( "RINTEN_MESH_DEBUG" ) == "1";
	private int debugFrame;

	//The casters for the trace: how many, how many still with a footprint, and their triangles, which size the lights'
	//run of indices. Under RINTEN_SUN_TRACE, which also logs every frame that bakes rather than one in 120.
	private string TraceCasters()
	{
		if ( !traceBakes ) return "";

		var still = 0; var feet = 0; var sum = 0L; var most = 0;

		for ( var i = 0; i < casters.Count; i++ )
		{
			if ( casters.Still( i ) ) still++;
			if ( i < footR.Length && footR[i] >= 0.0f ) feet++;
			sum += casters.TrianglesOf( i );
			most = Math.Max( most, casters.TrianglesOf( i ) );
		}

		return $"; casters {casters.Count}, still {still}, footprints {feet}, triangles {sum} (most {most})";
	}
	private static readonly bool traceBakes = Environment.GetEnvironmentVariable( "RINTEN_SUN_TRACE" ) == "1";

	//Marks the slots of a level that do not hold what its region wants now; how many.
	private int Wanted( int level )
	{
		var missing = 0;
		var at = level * Grid * Grid;
		var fx = fromX[level];
		var fy = fromY[level];

		for ( var sy = 0; sy < Grid; sy++ )
		{
			var ty = TileAt( fy, sy );
			var row = sy * Grid;

			for ( var sx = 0; sx < Grid; sx++ )
			{
				var tx = TileAt( fx, sx );
				var slot = at + row + sx;

				var nothing = !held[slot] || heldX[slot] != tx || heldY[slot] != ty;
				var need = nothing || stale[slot];
				wanted[row + sx] = need;
				empty[row + sx] = nothing;
				if ( need ) missing++;
			}
		}

		return missing;
	}

	//Whether a level still wants, after its plan, a slot holding nothing - anywhere, or short of its region's outer EdgePages
	private bool EmptyIn( int level, bool rimToo )
	{
		var seamX = Slot( fromX[level] );
		var seamY = Slot( fromY[level] );

		for ( var sy = 0; sy < Grid; sy++ )
		{
			var y = ( sy - seamY ) & ( Grid - 1 );
			var rimY = y < EdgePages || y >= Grid - EdgePages;

			for ( var sx = 0; sx < Grid; sx++ )
			{
				if ( !wanted[( sy * Grid ) + sx] || !empty[( sy * Grid ) + sx] ) continue;
				if ( rimToo ) return true;
				if ( rimY ) continue;

				var x = ( sx - seamX ) & ( Grid - 1 );
				if ( x >= EdgePages && x < Grid - EdgePages ) return true;
			}
		}

		return false;
	}

	//Plans a level's wanted slots as rectangles not crossing the torus seam; returns whether all fit
	private bool PlanLevel( int level, ref long total )
	{
		//Where the tiles wrap: the slot the region's first tile falls in. The slots from
		//there to the end and from the start to there are each a run of neighbouring tiles.
		var seamX = Slot( fromX[level] );
		var seamY = Slot( fromY[level] );

		Span<(int From, int To)> spansX = stackalloc (int, int)[2] { ( seamX, Grid ), ( 0, seamX ) };
		Span<(int From, int To)> spansY = stackalloc (int, int)[2] { ( seamY, Grid ), ( 0, seamY ) };

		foreach ( var ( y0, y1 ) in spansY )
		{
			foreach ( var ( x0, x1 ) in spansX )
			{
				if ( x0 >= x1 || y0 >= y1 ) continue;

				//Greedy: a run along the row, then as many rows below it as want the same run.
				for ( var sy = y0; sy < y1; sy++ )
				{
					for ( var sx = x0; sx < x1; sx++ )
					{
						if ( !wanted[( sy * Grid ) + sx] ) continue;

						var ex = sx;
						while ( ex + 1 < x1 && wanted[( sy * Grid ) + ex + 1] ) ex++;

						var ey = sy;
						while ( ey + 1 < y1 && RowWants( ey + 1, sx, ex ) ) ey++;

						if ( !Take( level, sx, sy, ex - sx + 1, ey - sy + 1, ref total ) ) return false;

						for ( var y = sy; y <= ey; y++ )
							for ( var x = sx; x <= ex; x++ )
								wanted[( y * Grid ) + x] = false;
					}
				}
			}
		}

		return true;
	}

	private bool RowWants( int sy, int from, int to )
	{
		for ( var x = from; x <= to; x++ )
			if ( !wanted[( sy * Grid ) + x] ) return false;

		return true;
	}

	//One rectangle of slots onto this frame's bakes, if the frame has room; its slots then hold their tiles.
	private bool Take( int level, int sx, int sy, int width, int height, ref long total )
	{
		if ( bakes.Count >= MostBakes ) return false;

		var tx = TileAt( fromX[level], sx );
		var ty = TileAt( fromY[level], sy );

		var page = PageOf( level );
		var triangles = TrianglesUnder( tx * page, ty * page, ( tx + width ) * page, ( ty + height ) * page );

		if ( total + triangles > MostBakeTriangles && bakes.Count > 0 ) return false;

		total += triangles;
		bakes.Add( new Bake( level, tx, ty, width, height, sx, sy, triangles ) );

		var at = level * Grid * Grid;

		for ( var y = 0; y < height; y++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var slot = at + ( ( sy + y ) * Grid ) + sx + x;
				held[slot] = true;
				stale[slot] = false;
				heldX[slot] = tx + x;
				heldY[slot] = ty + y;
			}
		}

		levelTouched[level] = true;

		return true;
	}

	//The finest levels of everything still that stands over a square of the sun's frame - the
	//most a bake of it can hold, since no level of a chain holds more than its finest level.
	private long TrianglesUnder( float x0, float y0, float x1, float y1 )
	{
		var sum = 0L;
		var count = Math.Min( footR.Length, casters.Count );

		for ( var i = 0; i < count; i++ )
		{
			var r = footR[i];
			if ( r < 0.0f ) continue;
			if ( footX[i] + r < x0 || footX[i] - r > x1 || footY[i] + r < y0 || footY[i] - r > y1 ) continue;

			sum += casters.TrianglesOf( i );
		}

		return sum;
	}

	//The spheres the casters said changed, as slots of every level that no longer hold what they show.
	private void ApplyDirty()
	{
		foreach ( var sphere in dirty )
		{
			var centre = ToSun( new Vector3( sphere.X, sphere.Y, sphere.Z ) );
			var r = sphere.W;

			for ( var level = 0; level < levels; level++ )
			{
				var page = PageOf( level );
				var x0 = (int)MathF.Floor( ( centre.x - r ) / page );
				var x1 = (int)MathF.Floor( ( centre.x + r ) / page );
				var y0 = (int)MathF.Floor( ( centre.y - r ) / page );
				var y1 = (int)MathF.Floor( ( centre.y + r ) / page );

				//No wider than the grid: past that every slot is touched anyway.
				x1 = Math.Min( x1, x0 + Grid - 1 );
				y1 = Math.Min( y1, y0 + Grid - 1 );

				var at = level * Grid * Grid;

				for ( var ty = y0; ty <= y1; ty++ )
					for ( var tx = x0; tx <= x1; tx++ )
					{
						var slot = at + ( Slot( ty ) * Grid ) + Slot( tx );

						if ( heldX[slot] == tx && heldY[slot] == ty && held[slot] )
						{
							if ( !stale[slot] ) PagesStaled++;
							stale[slot] = true;
							levelTouched[level] = true;
						}
					}
			}
		}

		dirty.Clear();
	}

	//Movers' shadow map: one square fitted to moving casters near the eye, texel-snapped to avoid crawl
	private void Movers( float lodPixels, Vector3 eye )
	{
		if ( casters.MovingCount == 0 ) return;

		//In the frame the pixels read this frame - the kept bake's while the pages are
		//being baked again, since one reader reads both maps with one matrix and one depth.
		var reading = rebaking && shown.Valid;
		var frame = reading ? shown.Basis : basis;
		var low = reading ? shown.ZLow : zLow;
		var high = reading ? shown.ZHigh : zHigh;
		var eyeThere = reading ? ToSun( eye, in frame ) : eyeSun;

		float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;

		for ( var i = 0; i < casters.MovingCount; i++ )
		{
			var sphere = casters.MovingSphere( i );
			if ( sphere.W < 0.0f ) continue;

			var centre = ToSun( new Vector3( sphere.X, sphere.Y, sphere.Z ), in frame );
			var r = sphere.W;

			if ( MathF.Abs( centre.x - eyeThere.x ) - r > MoverReach || MathF.Abs( centre.y - eyeThere.y ) - r > MoverReach ) continue;

			x0 = MathF.Min( x0, centre.x - r );
			y0 = MathF.Min( y0, centre.y - r );
			x1 = MathF.Max( x1, centre.x + r );
			y1 = MathF.Max( y1, centre.y + r );
		}

		if ( x0 > x1 ) return;

		//No further out than the reach: a mover straddling it keeps the half of its shadow nearer the eye.
		x0 = MathF.Max( x0, eyeThere.x - MoverReach );
		y0 = MathF.Max( y0, eyeThere.y - MoverReach );
		x1 = MathF.Min( x1, eyeThere.x + MoverReach );
		y1 = MathF.Min( y1, eyeThere.y + MoverReach );

		if ( movers == 0 ) movers = CoreRenderDevice.CreateShadowMap( MoverSide, 1, readDepth: true );
		if ( movers == 0 ) return;

		//Room for the filter at the edges, then the side up to a power of two.
		var wide = MathF.Max( x1 - x0, y1 - y0 ) * ( MoverSide / ( MoverSide - 8.0f ) );
		var side = MathF.Pow( 2.0f, MathF.Ceiling( MathF.Log2( MathF.Max( wide, MoverLeast ) ) ) );
		var texel = side / MoverSide;

		var middleX = ( x0 + x1 ) * 0.5f;
		var middleY = ( y0 + y1 ) * 0.5f;
		moverCorner = new System.Numerics.Vector2(
			MathF.Floor( ( middleX - ( side * 0.5f ) ) / texel ) * texel,
			MathF.Floor( ( middleY - ( side * 0.5f ) ) / texel ) * texel );
		moverWide = side;

		planned[plannedCount++] = LightView( moverCorner.X, moverCorner.Y, moverCorner.X + side, moverCorner.Y + side,
			in frame, low, high, casters: 2, texelsPerMetre: 1.0f / texel, lodPixels );

		moverViews = 1;
	}

	private CoreMeshRender.ViewSetup BakeView( in Bake bake, float lodPixels )
	{
		var page = PageOf( bake.Level );

		return LightView( bake.X0 * page, bake.Y0 * page, ( bake.X0 + bake.Width ) * page, ( bake.Y0 + bake.Height ) * page,
			in basis, zLow, zHigh, casters: 1, texelsPerMetre: PageTexels / page, lodPixels );
	}

	//A view from the sun over a square of its frame: xy across the square to the viewport's edges, z along the light
	private static CoreMeshRender.ViewSetup LightView( float x0, float y0, float x1, float y1,
		in System.Numerics.Matrix4x4 basis, float zLow, float zHigh, int casters, float texelsPerMetre, float lodPixels )
	{
		var sx = 2.0f / ( x1 - x0 );
		var sy = 2.0f / ( y1 - y0 );
		var sz = 1.0f / ( zHigh - zLow );

		var square = new System.Numerics.Matrix4x4(
			sx, 0.0f, 0.0f, 0.0f,
			0.0f, sy, 0.0f, 0.0f,
			0.0f, 0.0f, sz, 0.0f,
			-( x0 * sx ) - 1.0f, -( y0 * sy ) - 1.0f, -zLow * sz, 1.0f );

		var worldToClip = basis * square;

		Span<System.Numerics.Vector4> planes = stackalloc System.Numerics.Vector4[6];
		CoreSceneRenderer.FrustumPlanes( worldToClip, planes );

		var view = new CoreMeshRender.ViewSetup
		{
			WorldToClip = worldToClip,
			Camera = false,
			Casters = casters,
			ErrorScale = texelsPerMetre,
			LodPixels = lodPixels,
		};

		view.SetPlanes( planes );
		return view;
	}

	//This frame's views, after the ones already in the cull's list; how many there are now.
	internal int AddViews( CoreMeshRender.ViewSetup[] into, int count )
	{
		for ( var i = 0; i < plannedCount && count < into.Length; i++ )
			into[count++] = planned[i];

		return count;
	}

	//Applies caster changes to the pages and empties the per-frame cover list, before the cull
	internal void Note()
	{
		hand.Clear();

		var count = casters.Count;

		if ( footR.Length < count )
		{
			var room = Math.Max( count, footR.Length * 2 );
			var from = footR.Length;

			Array.Resize( ref footX, room );
			Array.Resize( ref footY, room );
			Array.Resize( ref footR, room );

			for ( var i = from; i < room; i++ ) footR[i] = -1.0f;
		}

		//A whole caster refresh restarts all pages, rebaked into the other map so shadows don't vanish
		if ( casters.Whole )
		{
			rebuild = true;
			Restart();
			return;
		}

		//Nothing to keep up where they are about to be worked out again anyway.
		if ( rebuild || !spanKnown ) return;

		ChangesNoted += casters.Changes.Count;

		foreach ( var change in casters.Changes )
		{
			if ( change.Was.W >= 0.0f ) dirty.Add( change.Was );
			if ( change.Now.W >= 0.0f ) dirty.Add( change.Now );

			//A slot of the tables, whose footprint moves with it; the ground's changes have none - see CoreCasters.Stir.
			if ( change.Slot >= 0 ) Foot( change.Slot );
			Reach( change.Now );
		}

		//How deep the scene reaches along the light - what moves included, since it is drawn over the same depth.
		for ( var i = 0; i < casters.MovingCount; i++ ) Reach( casters.MovingSphere( i ) );
	}

	//A slot's footprint in the sun's frame, where it is part of the still world.
	private void Foot( int slot )
	{
		if ( !casters.Still( slot ) )
		{
			footR[slot] = -1.0f;
			return;
		}

		var sphere = casters.SphereOf( slot );
		var centre = ToSun( new Vector3( sphere.X, sphere.Y, sphere.Z ) );
		footX[slot] = centre.x;
		footY[slot] = centre.y;
		footR[slot] = sphere.W;
	}

	//The span along the light, grown to hold a caster.
	private void Reach( System.Numerics.Vector4 sphere )
	{
		if ( sphere.W < 0.0f ) return;

		var along = ToSun( new Vector3( sphere.X, sphere.Y, sphere.Z ) ).z;
		spanLow = MathF.Min( spanLow, along - sphere.W );
		spanHigh = MathF.Max( spanHigh, along + sphere.W );
	}

	//Draws this frame's bakes and the movers' map from the cull's lists for the views this added. After the bin pass
	//fills the lights' lists (CoreMeshRender.Bin), before the materials, which read the maps.
	internal void Draw( CoreMeshRender mesh, int firstView, CoreMeshRender.LooseView loose )
	{
		if ( plannedCount == 0 ) return;

		var clusters = mesh is not null && mesh.Ready;

		//Nothing to draw with, no bakes - and slots marked baked are given back, or they would read as a shadow nobody drew
		if ( !clusters && loose is null )
		{
			foreach ( var bake in bakes ) Unbake( bake );
			return;
		}

		CoreRenderDevice.ZoneBegin( "sun pages" );

		var open = -1;

		for ( var i = 0; i < bakes.Count; i++ )
		{
			var bake = bakes[i];

			if ( bake.Level != open )
			{
				if ( open >= 0 ) CoreRenderDevice.EndTarget();
				open = CoreRenderDevice.BeginShadow( maps[drawn], bake.Level ) ? bake.Level : -1;
			}

			if ( open < 0 )
			{
				Unbake( bake );
				continue;
			}

			CoreRenderDevice.ShadowArea( bake.SlotX * PageTexels, bake.SlotY * PageTexels,
				bake.Width * PageTexels, bake.Height * PageTexels, clear: true );

			//The view's clusters and its classic draws - in planned order, the bakes first and the movers' map after them
			if ( clusters ) mesh.DrawView( firstView + i );
			loose?.Invoke( in planned[i] );

			Bakes++;
			PagesBaked += bake.Width * bake.Height;
		}

		if ( open >= 0 ) CoreRenderDevice.EndTarget();

		if ( moverViews > 0 && CoreRenderDevice.BeginShadow( movers, 0 ) )
		{
			CoreRenderDevice.ShadowArea( 0, 0, MoverSide, MoverSide, clear: true );

			if ( clusters ) mesh.DrawView( firstView + bakes.Count );
			loose?.Invoke( in planned[bakes.Count] );

			CoreRenderDevice.EndTarget();
		}

		CoreRenderDevice.ZoneEnd();
	}

	//A caster this frame draws that is not in the scene's tables - the ground's blocks, drawn by hand each
	//frame - for the span along the light. Before the plan, which widens the pages' depth to hold it.
	internal void Cover( Vector3 centre, float radius )
	{
		hand.Add( new System.Numerics.Vector4( centre.x, centre.y, centre.z, radius ) );
	}

	private void Unbake( in Bake bake )
	{
		var at = bake.Level * Grid * Grid;

		for ( var y = 0; y < bake.Height; y++ )
			for ( var x = 0; x < bake.Width; x++ )
				held[at + ( ( bake.SlotY + y ) * Grid ) + bake.SlotX + x] = false;

		validMask &= ~( 1u << bake.Level );
		edgeMask &= ~( 1u << bake.Level );
		levelTouched[bake.Level] = true;
	}

	//What a pixel needs to read both maps - see SunShadow in frame.slang. While the pages are baked again from nothing,
	//that is the bake that was kept: its frame, its depth, where the eye stood in it and which of its levels were whole.
	private void Write( float[] frame )
	{
		var reading = rebaking && shown.Valid;

		var low = reading ? shown.ZLow : zLow;
		var high = reading ? shown.ZHigh : zHigh;
		var eyeThere = reading ? shown.Eye : eyeSun;

		var read = reading ? shown.Map : drawn;

		//The portable frame binds the three maps where its set has room and reads them by that place - see ShadowCompare
		var portable = CoreRenderDevice.PortableFrame;

		if ( portable )
		{
			CoreRenderDevice.PortableShadowMap( 0, maps[0] );
			CoreRenderDevice.PortableShadowMap( 1, maps[1] );
			CoreRenderDevice.PortableShadowMap( 2, movers );
		}

		CoreSceneRenderer.SetSunPages( frame, reading ? shown.Basis : basis,
			reading ? shown.PageWorld : pageWorld, levels, low, high > low ? 1.0f / ( high - low ) : 0.0f,
			eyeThere.x, eyeThere.y, casts ? ( reading ? shown.Mask : validMask | ( edgeMask << MostLevels ) ) : 0u, casts,
			portable ? (uint)read : CoreRenderDevice.ShadowMapIndex( maps[read] ), portable ? 2u : CoreRenderDevice.ShadowMapIndex( movers ),
			portable ? 0u : CoreRenderDevice.ShadowMapIndex( maps[read], depth: true ), portable ? 0u : CoreRenderDevice.ShadowMapIndex( movers, depth: true ),
			moverCorner.X, moverCorner.Y, casts && moverViews > 0 ? moverWide : 0.0f );
	}

	public void Dispose()
	{
		CoreRenderDevice.DestroyShadowMap( maps[0] );
		CoreRenderDevice.DestroyShadowMap( maps[1] );
		CoreRenderDevice.DestroyShadowMap( movers );
		maps[0] = maps[1] = movers = 0;
		levels = 0;
		shown.Valid = false;
		rebaking = false;
		ForgetAll();
	}
}

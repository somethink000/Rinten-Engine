using System;
using System.Collections.Generic;

namespace Rinten;

//Lamp shadow maps per drawer: faces rebaked only on change, movers drawn into a separate map
//Faces sized by screen coverage, only visible faces drawn, with a Guard border for filtering
internal sealed class CoreLampMaps : IDisposable
{
	//Layer width (finest face) and guard border; match LampLayerTexels/LampGuardTexels - the portable frame's layer is
	//half as wide, a quarter of the memory, its faces the same classes of it
	private static int Side => CoreRenderDevice.PortableFrame ? 1024 : 2048;
	internal const int Guard = 4;

	//How many sizes a face comes in: Side halved this many times less one.
	private const int Sizes = 4;

	//16 MB a layer, as much again for the movers' map: one lamp at the finest and two beside it
	internal const int MostLayers = 8;

	//Face views one frame draws
	internal const int MostFaces = 14;

	//Where a face's depth starts, in metres from the lamp - LampNear in frame.slang is the same number.
	internal const float Near = 0.05f;

	//How much of a face's side its ninety degrees take at this many texels across: the rest is the border.
	internal static float FitOf( int size ) => ( size - ( 2.0f * Guard ) ) / size;

	//How many texels a face of a size class is across - nought is Side, each after it half.
	private static int SizeOf( int sizeClass ) => Side >> sizeClass;

	//How many faces of a size class one layer holds.
	private static int CellsOf( int sizeClass ) => 1 << ( 2 * sizeClass );

	//The widest a spot's shadow is kept for, as a half-angle: past it the texels spread too thin to be worth drawing.
	private const float WidestSpotDegrees = 80.0f;

	//The widest a movers frustum may be, as a half-angle; wider and the faces they cross take them instead
	private const float WidestMoversDegrees = 60.0f;

	//Texels kept clear round what a movers frustum holds, as wide as the softest filter reaches - ShadowSoftestTexels
	private const float MoversMargin = 16.0f;

	private readonly CoreCasters casters;

	private ulong map;
	private int layers;

	//What moves in each face, square for square where map has it
	private ulong movers;

	//Each layer's size class, below nought where unused; and which of its squares are taken, a bit each
	private readonly int[] layerClass = [-1, -1, -1, -1, -1, -1, -1, -1];
	private readonly ulong[] layerCells = new ulong[MostLayers];

	//One casting lamp: its size class, first square (counted layer by layer), its baked pose and face states
	private sealed class Lamp
	{
		public Light Light;
		public int First, Faces, Class;
		public bool Spot;

		/// <summary>Texels a face of it is across.</summary>
		public int Size => SizeOf( Class );

		/// <summary>Squares from before a size change, read until the new ones are baked; below nought for none.</summary>
		public int OldFirst = -1, OldClass = -1;
		public Vector3 Position, Forward;
		public float Range, Outer;
		public long Seen;

		/// <summary>Which faces' still world in the map is as the world stands.</summary>
		public readonly bool[] Baked = new bool[6];

		/// <summary>Where in each face's movers square something was last drawn - what the next draw clears.</summary>
		public readonly Area[] Moved = new Area[6];

		/// <summary>Which faces have what moves in them drawn this frame, a bit each - the record's shape.y.</summary>
		public int Moving;

		/// <summary>Whether what moves in reach is in one frustum this frame rather than the faces - see FitMovers.</summary>
		public bool Fitted;

		/// <summary>The frame block's slot of the frustum drawn this frame; below nought for none.</summary>
		public int Slot = -1;

		/// <summary>The movers frustum: down its middle, how wide a metre out, its projection and planes.</summary>
		public Vector3 MoverForward;
		public float MoverTan;
		public System.Numerics.Matrix4x4 MoverClip;
		public readonly System.Numerics.Vector4[] MoverPlanes = new System.Numerics.Vector4[6];

		/// <summary>Whether it moved this frame: its faces are drawn whole each frame instead of baked.</summary>
		public bool Travelling;

		/// <summary>Faces with a valid picture from the current pose, still read while being rebaked</summary>
		public readonly bool[] Held = new bool[6];

		/// <summary>Which faces the camera can see into this frame - see Meets.</summary>
		public readonly bool[] Visible = new bool[6];
		public readonly System.Numerics.Matrix4x4[] Clip = new System.Numerics.Matrix4x4[6];
		public readonly System.Numerics.Vector4[] Planes = new System.Numerics.Vector4[6 * 6];

		/// <summary>Finest still triangles in reach, the most a face can hold; below nought to recount.</summary>
		public long Triangles = -1;

		/// <summary>Whether every face the camera can see into holds a picture - see <see cref="Held"/>.</summary>
		public bool Whole()
		{
			for ( var f = 0; f < Faces; f++ )
				if ( Visible[f] && !Held[f] ) return false;

			return true;
		}

		public ReadOnlySpan<System.Numerics.Vector4> PlanesOf( int face ) => Planes.AsSpan( face * 6, 6 );
	}

	private readonly Dictionary<Light, Lamp> lamps = new();
	private readonly List<Lamp> gone = new();
	private long frameNumber;
	private bool warnedFull;

	//Whether a lamp found no room for the size it wanted last frame
	private bool crowded;
	private bool crowding;

	//A rectangle of a face's square, in its texels from the square's corner; nothing where it is empty.
	private readonly record struct Area( int X, int Y, int Width, int Height )
	{
		public bool Empty => Width <= 0 || Height <= 0;

		public static Area Whole( int size ) => new( 0, 0, size, size );

		public Area With( Area other )
		{
			if ( Empty ) return other;
			if ( other.Empty ) return this;

			var left = Math.Min( X, other.X );
			var top = Math.Min( Y, other.Y );

			return new Area( left, top, Math.Max( X + Width, other.X + other.Width ) - left, Math.Max( Y + Height, other.Y + other.Height ) - top );
		}
	}

	//One face this frame: its still world baked into the map, and what moves drawn into the movers' map;
	//Index below nought is the lamp's movers frustum, drawn in its first square
	private readonly struct Face
	{
		public readonly Lamp Lamp;
		public readonly int Index;

		/// <summary>The views its still world is baked from and its movers drawn from; below nought for none.</summary>
		public readonly int BakeView, MoverView;

		/// <summary>What of its square in the movers' map is cleared, and where what moves is drawn.</summary>
		public readonly Area Clear, Movers;

		/// <summary>The face's state going into this frame - what it holds again if the bake does not happen.</summary>
		public readonly bool WasHeld;

		public Face( Lamp lamp, int index, int bakeView, int moverView, Area clear, Area movers )
		{
			Lamp = lamp; Index = index; BakeView = bakeView; MoverView = moverView; Clear = clear; Movers = movers;
			WasHeld = index >= 0 && lamp.Held[index];
		}
	}

	private readonly List<Face> faces = new();
	private readonly CoreMeshRender.ViewSetup[] planned = new CoreMeshRender.ViewSetup[MostFaces];
	private int viewCount;

	//Movers frustums given a slot this frame
	private int slots;

	//Faces drawn since the process started, for the report - a bake or what moves, each one.
	internal static long FacesDrawn;

	//Of what moves, the faces drawn and the frustums drawn instead, for the report - see FitMovers
	internal static long MoverFacesDrawn, MoverFrustumsDrawn;

	//How many views this frame adds to the cull.
	internal int ViewCount => viewCount;

	//How many triangles this frame's faces can hold between them - see CoreMeshRender.ShadowTriangles.
	internal long Triangles { get; private set; }

	internal CoreLampMaps( CoreCasters casters )
	{
		this.casters = casters;
	}

	//Plans this frame's faces and writes each record's first layer: nought or more when all are baked, else below
	//Runs before the lights and frame block go up; the casters are the last frame's
	internal void Plan( float[] frame, float[] records, IReadOnlyList<Light> lights, int count, bool casts, float lodPixels, int room,
		Vector3 eye, float pixelsAtAMetre, ReadOnlySpan<System.Numerics.Vector4> seen, in System.Numerics.Matrix4x4 clipToWorld )
	{
		faces.Clear();
		viewCount = 0;
		Triangles = 0;

		slots = 0;

		foreach ( var lamp in lamps.Values )
		{
			lamp.Moving = 0;
			lamp.Fitted = false;
			lamp.Slot = -1;
		}

		frameNumber++;
		Frustum( seen, clipToWorld );
		crowded = crowding;
		crowding = false;

		count = Math.Min( count, Math.Min( lights.Count, records.Length / CoreLights.LightFloats ) );

		//A whole look: every face baked again, each read as it stands meanwhile.
		if ( casters.Whole )
		{
			foreach ( var lamp in lamps.Values )
			{
				Array.Clear( lamp.Baked );
				lamp.Triangles = -1;
			}
		}
		else Stirred();

		for ( var i = 0; i < count; i++ )
		{
			var at = i * CoreLights.LightFloats;
			records[at + 14] = -1.0f;
			records[at + 15] = 0.0f;
			records[at + 17] = 0.0f;

			if ( !casts ) continue;

			var kind = records[at + 7];
			var light = lights[i];

			if ( kind < 0.5f || light is null || !light.Shadows ) continue;

			var lamp = Keep( light, kind > 1.5f, records, at, eye, pixelsAtAMetre );
			if ( lamp is null ) continue;

			lamp.Seen = frameNumber;

			//A panel or disc lights only forward: a face wholly behind it is read by nothing - see AreaLamp
			var shape = kind < 1.5f ? records[at + 11] : 0.0f;
			var front = new Vector3( records[at + 8], records[at + 9], records[at + 10] );

			for ( var f = 0; f < lamp.Faces; f++ )
				lamp.Visible[f] = Meets( lamp, f ) && !( shape > 0.5f && shape < 2.5f && Behind( lamp, f, front ) );
		}

		//What left the frame gives its layers back.
		gone.Clear();

		foreach ( var lamp in lamps.Values )
			if ( lamp.Seen != frameNumber ) gone.Add( lamp );

		foreach ( var lamp in gone )
		{
			Release( lamp );
			lamps.Remove( lamp.Light );
		}

		//The faces to draw, nearest lamp first: what this frame cannot do without, then bakes that can wait for room.
		room = Math.Min( room, MostFaces );

		for ( var pass = 0; pass < 2; pass++ )
		{
			for ( var i = 0; i < count && viewCount < room; i++ )
			{
				//A light that is gone leaves a null in the list, and a null key throws from the dictionary
				if ( lights[i] is not { } asking ) continue;

				if ( !lamps.TryGetValue( asking, out var lamp ) || lamp.Seen != frameNumber ) continue;

				if ( pass == 0 && lamp.Travelling ) PlanTravelling( lamp, room, lodPixels );
				else if ( !lamp.Travelling )
				{
					if ( pass == 0 ) PlanFrustum( lamp, frame, room, lodPixels );
					PlanStill( lamp, room, lodPixels, bakes: pass == 1 );
				}
			}
		}

		//Each lamp's first square and size class once all its faces are baked - or the old ones until then
		for ( var i = 0; i < count; i++ )
		{
			if ( lights[i] is not { } asking ) continue;
			if ( !lamps.TryGetValue( asking, out var lamp ) || lamp.Seen != frameNumber ) continue;

			if ( lamp.Whole() && lamp.OldClass >= 0 )
			{
				Free( lamp.OldClass, lamp.OldFirst, lamp.Faces );
				lamp.OldClass = -1;
			}

			var at = i * CoreLights.LightFloats;

			if ( lamp.Whole() )
			{
				records[at + 14] = lamp.First;
				records[at + 15] = lamp.Class;

				//Which faces read the movers' map too - only where the squares read are the ones movers were drawn into;
				//below nought, the slot of the one frustum they were drawn in instead
				records[at + 17] = lamp.Slot >= 0 ? -( lamp.Slot + 1 ) : lamp.Moving;
			}
			else if ( lamp.OldClass >= 0 )
			{
				records[at + 14] = lamp.OldFirst;
				records[at + 15] = lamp.OldClass;
			}
		}

		//The portable frame binds the two in its own places 3 and 4 and reads them by those - see ShadowCompare in frame.slang
		if ( CoreRenderDevice.PortableFrame )
		{
			CoreRenderDevice.PortableShadowMap( 3, map );
			CoreRenderDevice.PortableShadowMap( 4, movers );

			CoreSceneRenderer.SetLampMaps( frame, map != 0 ? 3u : 0u, 0u, movers != 0 ? 4u : 0u, 0u );
			return;
		}

		CoreSceneRenderer.SetLampMaps( frame, CoreRenderDevice.ShadowMapIndex( map ), CoreRenderDevice.ShadowMapIndex( map, depth: true ),
			CoreRenderDevice.ShadowMapIndex( movers ), CoreRenderDevice.ShadowMapIndex( movers, depth: true ) );
	}

	//A moving lamp: each visible face drawn whole, still and moving in one view, or none at all
	//Left unbaked, so the still world is baked once it stops
	private void PlanTravelling( Lamp lamp, int room, float lodPixels )
	{
		var wanted = 0;
		for ( var f = 0; f < lamp.Faces; f++ ) if ( lamp.Visible[f] ) wanted++;

		if ( viewCount + wanted > room ) return;

		for ( var f = 0; f < lamp.Faces; f++ )
		{
			if ( !lamp.Visible[f] ) continue;

			faces.Add( new Face( lamp, f, View( lamp, f, Everything, lodPixels ), -1, default, default ) );
			lamp.Held[f] = true;
		}
	}

	//A lamp standing still: first what moves in faces already baked, each frame; then, with bakes, the faces not baked.
	private void PlanStill( Lamp lamp, int room, float lodPixels, bool bakes )
	{
		//Out of sight faces are left as they are: no pixel reads them.
		for ( var f = 0; f < lamp.Faces && viewCount < room; f++ )
		{
			if ( !lamp.Visible[f] || lamp.Baked[f] != !bakes ) continue;

			var moving = !lamp.Fitted && Moving( lamp, f );
			if ( !moving && !bakes ) continue;

			var wanted = ( bakes ? 1 : 0 ) + ( moving ? 1 : 0 );
			if ( viewCount + wanted > room ) return;

			//What moves, where it lands in the square - and what was drawn there before cleared away with it.
			var movers = moving ? MoversIn( lamp, f ) : default;
			var clear = movers.With( lamp.Moved[f] );

			var bakeView = bakes ? View( lamp, f, StillOnly, lodPixels ) : -1;
			var moverView = moving && !movers.Empty ? View( lamp, f, MovingOnly, lodPixels ) : -1;

			faces.Add( new Face( lamp, f, bakeView, moverView, clear, movers ) );

			lamp.Baked[f] = true;
			lamp.Held[f] = true;

			if ( moverView >= 0 )
			{
				lamp.Moving |= 1 << f;
				lamp.Moved[f] = movers;
			}
		}
	}

	//What moves in a still lamp's reach, in one frustum of its own where it fits - drawn once, where the faces it
	//crosses would draw it up to three times, and finer for the frustum being only as wide as it
	private void PlanFrustum( Lamp lamp, float[] frame, int room, float lodPixels )
	{
		lamp.Fitted = FitMovers( lamp );

		if ( !lamp.Fitted || viewCount >= room || slots >= CoreSceneRenderer.MostMoverFrustums ) return;

		//Nothing the camera sees is in its shadow
		if ( !Meets( lamp, lamp.MoverForward, lamp.MoverTan, lamp.MoverPlanes ) ) return;

		var size = lamp.Size;
		var view = View( lamp, lamp.MoverClip, lamp.MoverPlanes, lamp.MoverTan, MovingOnly, lodPixels );

		faces.Add( new Face( lamp, -1, -1, view, Area.Whole( size ), Area.Whole( size ) ) );

		lamp.Slot = slots++;

		//Its square is face nought's, so the next of that face's movers draws clears all of it
		lamp.Moved[0] = Area.Whole( size );

		var at = CoreSceneRenderer.LampMoversAt + ( lamp.Slot * 4 );
		frame[at + 0] = lamp.MoverForward.x;
		frame[at + 1] = lamp.MoverForward.y;
		frame[at + 2] = lamp.MoverForward.z;
		frame[at + 3] = lamp.MoverTan;
	}

	//One frustum from the lamp round everything moving in its reach, as UE's per-object shadows are. False where
	//nothing moves in reach, or something stands too near the lamp or too far round it for one frustum to hold
	private bool FitMovers( Lamp lamp )
	{
		var forward = Vector3.Zero;
		var widest = -1.0f;
		var widestAllowed = WidestMoversDegrees * ( MathF.PI / 180.0f );

		for ( var i = 0; i < casters.MovingCount; i++ )
		{
			var sphere = casters.MovingSphere( i );
			if ( sphere.W < 0.0f ) continue;

			var centre = new Vector3( sphere.X, sphere.Y, sphere.Z );
			var away = ( centre - lamp.Position ).Length;

			if ( away - sphere.W > lamp.Range ) continue;

			//Nearer the lamp than its maps begin, or round it: no one frustum holds it
			var boxed = casters.MovingBox( i );
			if ( boxed is { } near ? near.Grow( Near ).Contains( lamp.Position ) : away - sphere.W <= Near ) return false;

			//Round its box's corners where it has one - the box lies inside the sphere, so the cone is never wider
			if ( boxed is { } box )
			{
				for ( var corner = 0; corner < 8; corner++ )
				{
					var point = new Vector3(
						( corner & 1 ) != 0 ? box.Maxs.x : box.Mins.x,
						( corner & 2 ) != 0 ? box.Maxs.y : box.Mins.y,
						( corner & 4 ) != 0 ? box.Maxs.z : box.Mins.z );

					if ( !Grow( ref forward, ref widest, ( point - lamp.Position ).Normal, 0.0f, widestAllowed ) ) return false;
				}

				continue;
			}

			if ( !Grow( ref forward, ref widest, ( centre - lamp.Position ) / away, MathF.Asin( Math.Min( sphere.W / away, 1.0f ) ), widestAllowed ) ) return false;
		}

		if ( widest < 0.0f ) return false;

		//Widened so its edge lies the margin inside the square's fit, where the filter's taps still find the square
		var fitted = FitOf( lamp.Size ) * lamp.Size;

		lamp.MoverForward = forward;
		lamp.MoverTan = MathF.Tan( widest ) * fitted / MathF.Max( fitted - ( 2.0f * MoversMargin ), 1.0f );
		lamp.MoverClip = ClipOf( lamp, forward, lamp.MoverTan );
		CoreSceneRenderer.FrustumPlanes( lamp.MoverClip, lamp.MoverPlanes );

		return true;
	}

	//A cone grown round a cap of directions - toward, round wide - as Ritter's sphere is grown round a point: the axis turned
	//toward one outside by as much as it widens. False past the widest allowed
	private static bool Grow( ref Vector3 forward, ref float widest, Vector3 toward, float round, float allowed )
	{
		if ( widest < 0.0f )
		{
			forward = toward;
			widest = round;
			return widest <= allowed;
		}

		var off = MathF.Acos( Math.Clamp( forward.Dot( toward ), -1.0f, 1.0f ) );
		if ( off + round <= widest ) return true;

		var grown = ( widest + off + round ) * 0.5f;
		if ( grown > allowed ) return false;

		//Turned along the great circle between the two, by what the cone grew
		if ( off > 1e-5f )
		{
			var turn = ( grown - widest ) / off;
			forward = ( ( forward * MathF.Sin( ( 1.0f - turn ) * off ) ) + ( toward * MathF.Sin( turn * off ) ) ) / MathF.Sin( off );
			forward = forward.Normal;
		}

		widest = grown;
		return true;
	}

	//A light's lamp: made if new, given squares at its wanted size, marked to bake where it or its size changed
	private Lamp Keep( Light light, bool spot, float[] records, int at, Vector3 eye, float pixelsAtAMetre )
	{
		var position = new Vector3( records[at + 0], records[at + 1], records[at + 2] );
		var range = MathF.Max( records[at + 3], Near + 0.01f );
		var forward = new Vector3( records[at + 8], records[at + 9], records[at + 10] );
		var outer = records[at + 12];
		var wanted = spot ? 1 : 6;
		var sizeClass = ClassFor( position, range, eye, pixelsAtAMetre );

		if ( lamps.TryGetValue( light, out var lamp ) && lamp.Faces != wanted )
		{
			Release( lamp );
			lamps.Remove( light );
			lamp = null;
		}

		if ( lamp is null )
		{
			lamp = new Lamp { Light = light, Faces = wanted, Spot = spot, Class = -1 };

			while ( !Place( lamp, sizeClass, coarser: true ) && Demote() ) { }

			if ( lamp.Class != sizeClass ) crowding = true;

			if ( lamp.Class < 0 )
			{
				if ( !warnedFull )
				{
					warnedFull = true;
					Log.Warning( $"the lamps' shadow maps are full at {MostLayers} layers - the furthest lamps light without a shadow" );
				}

				return null;
			}

			lamps[light] = lamp;
			Aim( lamp, position, forward, range, outer );
			return lamp;
		}

		//Finer at once, coarser only two sizes on; old squares stay its own and are read until the new are baked
		//Where there is no room for both, it stays
		if ( lamp.OldClass < 0 && ( sizeClass < lamp.Class || sizeClass >= lamp.Class + ( crowded ? 1 : 2 ) ) )
		{
			var was = lamp.Class;
			var first = lamp.First;

			//Only the wanted size: asking again for a finer one each frame would rebake each frame
			if ( Place( lamp, sizeClass, coarser: sizeClass > was ) )
			{
				if ( lamp.Whole() ) { lamp.OldFirst = first; lamp.OldClass = was; }
				else Free( was, first, lamp.Faces );

				Aim( lamp, position, forward, range, outer );
			}
			else
			{
				lamp.Class = was;
				lamp.First = first;
				crowding = true;
			}
		}

		var moved = ( position - lamp.Position ).Length > 1e-4f || MathF.Abs( range - lamp.Range ) > 1e-4f ||
			( spot && ( ( forward - lamp.Forward ).Length > 1e-5f || MathF.Abs( outer - lamp.Outer ) > 1e-5f ) );

		if ( moved ) Aim( lamp, position, forward, range, outer );

		lamp.Travelling = moved;
		return lamp;
	}

	//Size class from the lamp's on-screen reach, finest when the camera is inside, coarsest if off-screen
	private int ClassFor( Vector3 position, float range, Vector3 eye, float pixelsAtAMetre )
	{
		if ( hasFrustum && !CoreSceneRenderer.SphereVisible( frustum, position, range ) ) return Sizes - 1;

		//A camera with no perspective has no size for it on the screen.
		if ( pixelsAtAMetre <= 0.0f ) return 1;

		var away = ( eye - position ).Length;
		if ( away <= range ) return 0;

		var pixels = range * pixelsAtAMetre / away;

		for ( var sizeClass = Sizes - 1; sizeClass > 0; sizeClass-- )
			if ( SizeOf( sizeClass ) >= pixels ) return sizeClass;

		return 0;
	}

	//Room made by the lamp with the finest squares going a size coarser, to be baked again there; false where all are coarsest
	private bool Demote()
	{
		for ( var sizeClass = 0; sizeClass < Sizes - 1; sizeClass++ )
		{
			foreach ( var lamp in lamps.Values )
			{
				if ( lamp.Class != sizeClass ) continue;

				Release( lamp );

				//Where a layer it shared keeps its class, the smaller squares may not fit: it takes its own back
				var moved = Place( lamp, sizeClass + 1, coarser: true );
				if ( !moved ) Place( lamp, sizeClass, coarser: false );

				Forget( lamp );

				if ( moved ) return true;
			}
		}

		return false;
	}

	//Finds a run of squares for a lamp's faces at this size (or coarser if allowed); false if no room
	private bool Place( Lamp lamp, int sizeClass, bool coarser )
	{
		for ( var c = sizeClass; c < ( coarser ? Sizes : sizeClass + 1 ); c++ )
		{
			var first = Room( c, lamp.Faces );
			if ( first < 0 ) continue;

			Claim( lamp, c, first );

			//A map with the layers the squares are in - made again bigger where it is short, which forgets every face.
			var last = ( first + lamp.Faces - 1 ) / CellsOf( c );
			if ( !EnsureMap( last + 1 ) )
			{
				Free( c, first, lamp.Faces );
				lamp.Class = -1;
				return false;
			}

			return true;
		}

		return false;
	}

	//The first run of free squares of a class this long; below nought where there is none.
	private int Room( int sizeClass, int wanted )
	{
		var cells = CellsOf( sizeClass );

		for ( var first = 0; first + wanted <= MostLayers * cells; first++ )
		{
			var free = true;

			for ( var f = 0; f < wanted && free; f++ )
			{
				var square = first + f;
				var layer = square / cells;

				if ( layerClass[layer] >= 0 && layerClass[layer] != sizeClass ) free = false;
				else if ( ( layerCells[layer] & ( 1ul << ( square % cells ) ) ) != 0 ) free = false;
			}

			if ( free ) return first;
		}

		return -1;
	}

	//A lamp's squares taken: these, at this class.
	private void Claim( Lamp lamp, int sizeClass, int first )
	{
		var cells = CellsOf( sizeClass );

		for ( var f = 0; f < lamp.Faces; f++ )
		{
			var square = first + f;
			layerClass[square / cells] = sizeClass;
			layerCells[square / cells] |= 1ul << ( square % cells );
		}

		lamp.Class = sizeClass;
		lamp.First = first;

		//New squares hold another lamp's movers or a new map's garbage: the first movers' draw clears all of them
		Array.Fill( lamp.Moved, Area.Whole( SizeOf( sizeClass ) ) );
	}

	//A lamp's squares given back, old and new.
	private void Release( Lamp lamp )
	{
		if ( lamp.Class >= 0 ) Free( lamp.Class, lamp.First, lamp.Faces );
		if ( lamp.OldClass >= 0 ) Free( lamp.OldClass, lamp.OldFirst, lamp.Faces );

		lamp.Class = -1;
		lamp.OldClass = -1;
	}

	//A run of squares given back - and a layer nobody has a square of left uncut.
	private void Free( int sizeClass, int first, int faces )
	{
		var cells = CellsOf( sizeClass );

		for ( var f = 0; f < faces; f++ )
		{
			var square = first + f;
			layerCells[square / cells] &= ~( 1ul << ( square % cells ) );
			if ( layerCells[square / cells] == 0 ) layerClass[square / cells] = -1;
		}
	}

	//Where a face of a lamp is in the map: its layer and its square's corner, in texels.
	private static (int Layer, int X, int Y) WhereIs( Lamp lamp, int face )
	{
		var cells = CellsOf( lamp.Class );
		var square = lamp.First + face;
		var within = square % cells;
		var perRow = 1 << lamp.Class;

		return ( square / cells, ( within % perRow ) * lamp.Size, ( within / perRow ) * lamp.Size );
	}

	//A map with at least this many layers - made again bigger where it is short, which forgets every face.
	private bool EnsureMap( int needed )
	{
		if ( map != 0 && layers >= needed ) return true;

		var want = Math.Min( MostLayers, Math.Max( needed, layers + 2 ) );

		CoreRenderDevice.DestroyShadowMap( map );
		CoreRenderDevice.DestroyShadowMap( movers );

		//Read as depth too: a lamp's edge is softened by what stands in front of a point - see LampShadow.
		map = CoreRenderDevice.CreateShadowMap( Side, want, readDepth: true );

		//And what moves, square for square, read the same way.
		movers = map != 0 ? CoreRenderDevice.CreateShadowMap( Side, want, readDepth: true ) : 0;

		if ( movers == 0 )
		{
			CoreRenderDevice.DestroyShadowMap( map );
			map = 0;
		}

		layers = map != 0 ? want : 0;

		//Every face and every old square with it: the map they were in is gone, and the new one holds garbage.
		foreach ( var lamp in lamps.Values )
		{
			Forget( lamp );
			Array.Fill( lamp.Moved, Area.Whole( lamp.Size ) );

			if ( lamp.OldClass >= 0 ) Free( lamp.OldClass, lamp.OldFirst, lamp.Faces );
			lamp.OldClass = -1;
		}

		return map != 0;
	}

	private static void Forget( Lamp lamp )
	{
		Array.Clear( lamp.Baked );
		Array.Clear( lamp.Held );
		lamp.Triangles = -1;
	}

	//A lamp at its pose: each face's projection and planes again, and every face to bake
	private void Aim( Lamp lamp, Vector3 position, Vector3 forward, float range, float outer )
	{
		lamp.Position = position;
		lamp.Forward = forward;
		lamp.Range = range;
		lamp.Outer = outer;

		for ( var f = 0; f < lamp.Faces; f++ )
		{
			lamp.Clip[f] = FaceClip( lamp, f );
			CoreSceneRenderer.FrustumPlanes( lamp.Clip[f], lamp.Planes.AsSpan( f * 6, 6 ) );
		}

		Forget( lamp );
	}

	//A face's direction: world axes for a point light in LampShadow's order, down the cone for a spot
	private static Vector3 FaceForward( Lamp lamp, int face ) => lamp.Spot ? lamp.Forward : face switch
	{
		0 => new Vector3( 1.0f, 0.0f, 0.0f ),
		1 => new Vector3( -1.0f, 0.0f, 0.0f ),
		2 => new Vector3( 0.0f, 1.0f, 0.0f ),
		3 => new Vector3( 0.0f, -1.0f, 0.0f ),
		4 => new Vector3( 0.0f, 0.0f, 1.0f ),
		_ => new Vector3( 0.0f, 0.0f, -1.0f ),
	};

	//How far across a face is at a metre out: one for ninety degrees, the tangent of the cone's half-angle for a spot.
	private static float HalfTan( Lamp lamp )
	{
		if ( !lamp.Spot ) return 1.0f;

		var widest = MathF.Cos( WidestSpotDegrees * ( MathF.PI / 180.0f ) );
		var outer = Math.Clamp( lamp.Outer, widest, 0.9998f );
		return MathF.Sqrt( 1.0f - ( outer * outer ) ) / outer;
	}

	private static System.Numerics.Matrix4x4 FaceClip( Lamp lamp, int face ) => ClipOf( lamp, FaceForward( lamp, face ), HalfTan( lamp ) );

	//A square's projection down `down`: across and up over its half-width less the border, depth from Near to its reach
	//Written out rather than look-at times perspective so LampShadow can invert the same sums
	private static System.Numerics.Matrix4x4 ClipOf( Lamp lamp, Vector3 down, float halfTan )
	{
		var ( right, up, forward ) = CoreSunPages.LightAxes( down );
		var p = lamp.Position;

		var s = FitOf( lamp.Size ) / halfTan;
		var far = lamp.Range;
		var a = far / ( far - Near );
		var b = Near * far / ( far - Near );

		//Row-vector: clip = (across·s, up·s, a·along - b, along), along the distance down the face's axis.
		return new System.Numerics.Matrix4x4(
			right.x * s, up.x * s, forward.x * a, forward.x,
			right.y * s, up.y * s, forward.y * a, forward.y,
			right.z * s, up.z * s, forward.z * a, forward.z,
			-p.Dot( right ) * s, -p.Dot( up ) * s, ( -p.Dot( forward ) * a ) - b, -p.Dot( forward ) );
	}

	//The camera's frustum this frame, as planes and as corners - see Meets.
	private readonly System.Numerics.Vector4[] frustum = new System.Numerics.Vector4[6];
	private readonly Vector3[] corners = new Vector3[8];
	private bool hasFrustum;

	private void Frustum( ReadOnlySpan<System.Numerics.Vector4> seen, in System.Numerics.Matrix4x4 clipToWorld )
	{
		hasFrustum = seen.Length == 6;
		if ( !hasFrustum ) return;

		seen.CopyTo( frustum );

		for ( var c = 0; c < 8; c++ )
		{
			var clip = new System.Numerics.Vector4( ( c & 1 ) != 0 ? 1.0f : -1.0f, ( c & 2 ) != 0 ? 1.0f : -1.0f, ( c & 4 ) != 0 ? 1.0f : 0.0f, 1.0f );
			var world = System.Numerics.Vector4.Transform( clip, clipToWorld );
			corners[c] = new Vector3( world.X, world.Y, world.Z ) / ( MathF.Abs( world.W ) > 1e-8f ? world.W : 1e-8f );
		}
	}

	//Whether a face's pyramid and the camera's frustum can meet - not where all of one lies outside a plane of
	//the other. Where they cannot, no pixel on the screen reads the face. True where there is no frustum to ask.
	private bool Meets( Lamp lamp, int face ) => Meets( lamp, FaceForward( lamp, face ), HalfTan( lamp ), lamp.PlanesOf( face ) );

	//The same for any square's pyramid: down `down`, halfTan wide a metre out, bounded by these planes
	private bool Meets( Lamp lamp, Vector3 down, float halfTan, ReadOnlySpan<System.Numerics.Vector4> planes )
	{
		if ( !hasFrustum ) return true;

		var ( right, up, forward ) = CoreSunPages.LightAxes( down );
		var across = lamp.Range * halfTan;
		var far = lamp.Position + ( forward * lamp.Range );

		Span<Vector3> pyramid = stackalloc Vector3[5];
		pyramid[0] = lamp.Position;
		pyramid[1] = far + ( right * across ) + ( up * across );
		pyramid[2] = far + ( right * across ) - ( up * across );
		pyramid[3] = far - ( right * across ) + ( up * across );
		pyramid[4] = far - ( right * across ) - ( up * across );

		foreach ( var plane in frustum )
			if ( Outside( plane, pyramid ) ) return false;

		foreach ( var plane in planes )
			if ( Outside( plane, corners ) ) return false;

		return true;
	}

	//Whether every direction a face looks down points away from where a one-sided shape lights
	private static bool Behind( Lamp lamp, int face, Vector3 front )
	{
		var ( right, up, forward ) = CoreSunPages.LightAxes( FaceForward( lamp, face ) );

		for ( var c = 0; c < 4; c++ )
		{
			var corner = forward + ( ( c & 1 ) != 0 ? right : -right ) + ( ( c & 2 ) != 0 ? up : -up );
			if ( corner.Dot( front ) > 0.0f ) return false;
		}

		return true;
	}

	private static bool Outside( System.Numerics.Vector4 plane, ReadOnlySpan<Vector3> points )
	{
		foreach ( var p in points )
			if ( ( plane.X * p.x ) + ( plane.Y * p.y ) + ( plane.Z * p.z ) + plane.W >= 0.0f ) return false;

		return true;
	}

	//Whether anything that moves stands in a face's frustum
	private bool Moving( Lamp lamp, int face )
	{
		var planes = lamp.PlanesOf( face );

		for ( var i = 0; i < casters.MovingCount; i++ )
		{
			var sphere = casters.MovingSphere( i );
			if ( sphere.W < 0.0f ) continue;
			if ( CoreSceneRenderer.SphereVisible( planes, new Vector3( sphere.X, sphere.Y, sphere.Z ), sphere.W ) ) return true;
		}

		return false;
	}

	//The still world's changes since the last plan, as faces to bake again.
	private void Stirred()
	{
		foreach ( var change in casters.Changes )
		{
			foreach ( var lamp in lamps.Values )
			{
				Touch( lamp, change.Was );
				Touch( lamp, change.Now );
			}
		}
	}

	private static void Touch( Lamp lamp, System.Numerics.Vector4 sphere )
	{
		if ( sphere.W < 0.0f ) return;

		var centre = new Vector3( sphere.X, sphere.Y, sphere.Z );
		if ( ( centre - lamp.Position ).Length - sphere.W > lamp.Range ) return;

		lamp.Triangles = -1;

		for ( var f = 0; f < lamp.Faces; f++ )
			if ( CoreSceneRenderer.SphereVisible( lamp.PlanesOf( f ), centre, sphere.W ) ) lamp.Baked[f] = false;
	}

	//Where in a face's square everything moving in it lands - its bounding spheres through the face's projection,
	//widened by the border the filter reads past an edge - or all of it where one reaches behind the lamp.
	private Area MoversIn( Lamp lamp, int face )
	{
		var size = lamp.Size;
		var planes = lamp.PlanesOf( face );
		var clip = lamp.Clip[face];

		var left = float.MaxValue;
		var top = float.MaxValue;
		var right = float.MinValue;
		var bottom = float.MinValue;

		for ( var i = 0; i < casters.MovingCount; i++ )
		{
			var sphere = casters.MovingSphere( i );
			if ( sphere.W < 0.0f ) continue;

			var centre = new Vector3( sphere.X, sphere.Y, sphere.Z );
			if ( !CoreSceneRenderer.SphereVisible( planes, centre, sphere.W ) ) continue;

			for ( var corner = 0; corner < 8; corner++ )
			{
				var point = new System.Numerics.Vector4(
					sphere.X + ( ( corner & 1 ) != 0 ? sphere.W : -sphere.W ),
					sphere.Y + ( ( corner & 2 ) != 0 ? sphere.W : -sphere.W ),
					sphere.Z + ( ( corner & 4 ) != 0 ? sphere.W : -sphere.W ), 1.0f );

				var at = System.Numerics.Vector4.Transform( point, clip );

				//Reaching round behind the lamp: no rectangle bounds it.
				if ( at.W <= Near * 0.5f ) return Area.Whole( size );

				//Texels the way LampShadow reads them: the square's corner plus half of clip space across it, no flip.
				var x = ( ( at.X / at.W ) * 0.5f + 0.5f ) * size;
				var y = ( ( at.Y / at.W ) * 0.5f + 0.5f ) * size;

				left = MathF.Min( left, x );
				top = MathF.Min( top, y );
				right = MathF.Max( right, x );
				bottom = MathF.Max( bottom, y );
			}
		}

		if ( right < left ) return default;

		//The filter's taps reach this far past what casts.
		const int border = ( Guard * 2 ) + 2;

		var x0 = Math.Clamp( (int)MathF.Floor( left ) - border, 0, size );
		var y0 = Math.Clamp( (int)MathF.Floor( top ) - border, 0, size );
		var x1 = Math.Clamp( (int)MathF.Ceiling( right ) + border, x0, size );
		var y1 = Math.Clamp( (int)MathF.Ceiling( bottom ) + border, y0, size );

		return new Area( x0, y0, x1 - x0, y1 - y0 );
	}

	//ViewCasters in the cull: everything, what stands still, what moves.
	private const int Everything = 0, StillOnly = 1, MovingOnly = 2;

	//A view of one face for this frame's cull: of the still world, to bake, of what moves, for the movers' map, or of both.
	private int View( Lamp lamp, int face, int casters, float lodPixels )
		=> View( lamp, lamp.Clip[face], lamp.PlanesOf( face ), HalfTan( lamp ), casters, lodPixels );

	private int View( Lamp lamp, in System.Numerics.Matrix4x4 clip, ReadOnlySpan<System.Numerics.Vector4> planes, float halfTan,
		int casters, float lodPixels )
	{
		if ( casters != MovingOnly && lamp.Triangles < 0 ) lamp.Triangles = TrianglesNear( lamp );

		Triangles += ( casters == MovingOnly ? 0 : lamp.Triangles ) + ( casters == StillOnly ? 0 : this.casters.MoverTriangles );

		var view = new CoreMeshRender.ViewSetup
		{
			WorldToClip = clip,
			Camera = false,
			Perspective = true,
			Casters = casters,
			Eye = lamp.Position,
			ErrorScale = FitOf( lamp.Size ) / halfTan * lamp.Size * 0.5f,
			LodPixels = lodPixels,
			Near = Near,
			Far = lamp.Range,
		};

		view.SetPlanes( planes );
		planned[viewCount] = view;

		return viewCount++;
	}

	//The finest levels of everything still within a lamp's reach - the most a face of it can hold.
	private long TrianglesNear( Lamp lamp )
	{
		var sum = 0L;

		for ( var slot = 0; slot < casters.Count; slot++ )
		{
			if ( !casters.Still( slot ) ) continue;

			var sphere = casters.SphereOf( slot );
			if ( ( new Vector3( sphere.X, sphere.Y, sphere.Z ) - lamp.Position ).Length - sphere.W > lamp.Range ) continue;

			sum += casters.TrianglesOf( slot );
		}

		return sum;
	}

	//This frame's faces after the views already in the cull's list; returns the new count
	internal int AddViews( CoreMeshRender.ViewSetup[] into, int count )
	{
		for ( var i = 0; i < viewCount && count < into.Length; i++ )
			into[count++] = planned[i];

		return count;
	}

	//Draws this frame's faces from the lists the cull made for the views this added - after the bin pass has
	//filled them, before the materials read the map. With no lists, the faces planned are given back unbaked.
	internal void Draw( CoreMeshRender mesh, int firstView, CoreMeshRender.LooseView loose )
	{
		if ( faces.Count == 0 ) return;

		var clusters = mesh is not null && mesh.Ready;

		if ( !clusters && loose is null )
		{
			foreach ( var face in faces )
			{
				Unbake( face );
				if ( face.MoverView >= 0 ) face.Lamp.Moved[Math.Max( face.Index, 0 )] = Area.Whole( face.Lamp.Size );
			}

			foreach ( var lamp in lamps.Values )
			{
				lamp.Moving = 0;
				lamp.Slot = -1;
			}

			return;
		}

		CoreRenderDevice.ZoneBegin( "lamp maps" );

		foreach ( var face in faces )
		{
			var ( layer, x, y ) = WhereIs( face.Lamp, Math.Max( face.Index, 0 ) );
			var size = face.Lamp.Size;

			//The still world into the map, where it is kept.
			if ( face.BakeView >= 0 )
			{
				CoreRenderDevice.ZoneBegin( "lamp bake" );
				var baked = Into( map, layer, x, y, size, Area.Whole( size ), mesh, clusters, firstView, face.BakeView, loose, Area.Whole( size ) );
				CoreRenderDevice.ZoneEnd();

				if ( !baked ) Unbake( face );
			}

			//Movers into the movers' map: their old and new area cleared, drawn where they are
			if ( face.MoverView >= 0 )
			{
				CoreRenderDevice.ZoneBegin( "lamp movers" );
				var drawn = Into( movers, layer, x, y, size, face.Clear, mesh, clusters, firstView, face.MoverView, loose, face.Movers );
				CoreRenderDevice.ZoneEnd();

				//Not drawn: what is there is stale, so the next draw clears all of it.
				if ( !drawn ) face.Lamp.Moved[Math.Max( face.Index, 0 )] = Area.Whole( size );
				else if ( face.Index < 0 ) MoverFrustumsDrawn++;
				else MoverFacesDrawn++;
			}
		}

		CoreRenderDevice.ZoneEnd();
	}

	//One view's casters into a face's square of a map: this part of it cleared, and the draws landing only in that part.
	private bool Into( ulong target, int layer, int x, int y, int size, Area clear, CoreMeshRender mesh, bool clusters,
		int firstView, int view, CoreMeshRender.LooseView loose, Area only )
	{
		if ( !CoreRenderDevice.BeginShadow( target, layer ) ) return false;

		//The whole face's projection.
		CoreRenderDevice.ShadowArea( x, y, size, size, clear: false );

		if ( !clear.Empty ) CoreRenderDevice.ShadowScissor( x + clear.X, y + clear.Y, clear.Width, clear.Height, clear: true );
		CoreRenderDevice.ShadowScissor( x + only.X, y + only.Y, only.Width, only.Height );

		if ( clusters ) mesh.DrawView( firstView + view );
		loose?.Invoke( in planned[view] );

		CoreRenderDevice.EndTarget();

		FacesDrawn++;

		return true;
	}

	//A face whose bake did not happen: baked next frame, and its old picture read meanwhile
	private static void Unbake( in Face face )
	{
		if ( face.Index < 0 ) return;

		face.Lamp.Baked[face.Index] = false;
		face.Lamp.Held[face.Index] = face.WasHeld;
	}

	public void Dispose()
	{
		CoreRenderDevice.DestroyShadowMap( map );
		CoreRenderDevice.DestroyShadowMap( movers );
		map = 0;
		movers = 0;
		layers = 0;
		lamps.Clear();
		Array.Fill( layerClass, -1 );
		Array.Clear( layerCells );
	}
}

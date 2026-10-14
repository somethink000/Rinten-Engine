using System;
using System.Collections.Generic;
using System.Linq;

namespace Rinten;

//Envmap probes: six views folded into one octahedral picture, blurred bands stacked under it. A baked probe's picture
//comes from the scene's envmaps asset, a runtime probe's from its own captures
internal static class CoreEnvmap
{

	//How many probes one frame can carry. A pixel takes the nearest one whose box it is inside, so this is not "how many
	//a level may have" but "how many may overlap what is on screen". Four is a room, its doorway and two neighbours.
	internal const int MostProbes = 4;

	//Width of one octahedral picture, separate from the face Resolution; 128 is sharp enough
	internal const int Side = 128;

	//Roughness levels stacked as bands of one tall texture (no mip upload); the shader mixes two
	internal const int Bands = 5;

	//How much light one of these can hold before it clips.
	internal const float RgbmRange = 8.0f;

	//One probe's picture on the card.
	private sealed class Held
	{
		public ulong Texture;
		public int Index;

		/// <summary>The baked pixels it was made from; null for a runtime probe's capture.</summary>
		public byte[] From;
	}

	//Folds running on workers, by probe.
	private static readonly Dictionary<Guid, System.Threading.Tasks.Task<Scratch>> folding = new();

	//When the last capture was drawn - see MayCapture.
	private static float capturedAt = -1.0f;

	//Whether a probe may draw now: not in the same few ms as any probe's last capture, so runtime probes take turns
	//rather than stalling one frame together
	internal static bool MayCapture => job is null && ( capturedAt < 0.0f || RealTime.Now - capturedAt > 0.03f || RealTime.Now < capturedAt );

	//The folds that are done, onto the device - asked once a frame, see Write.
	internal static void Settle()
	{
		lock ( folding )
		{
			if ( folding.Count == 0 ) return;

			done.Clear();

			foreach ( var (id, fold) in folding )
				if ( fold.IsCompleted ) done.Add( id );

			foreach ( var id in done )
			{
				var fold = folding[id];
				folding.Remove( id );

				if ( fold.IsCompletedSuccessfully ) Keep( id, fold.Result );
				else Log.Warning( $"an envmap probe's fold failed: {fold.Exception?.GetBaseException().Message}" );
			}
		}
	}

	private static readonly List<Guid> done = new();

	//A colour brighter than one, in four bytes: the colour divided by a multiplier the
	//fourth byte carries. The probes are the only thing that keeps light in eight bits now.
	internal static (byte R, byte G, byte B, byte A) EncodeRgbm( float r, float g, float b, float range )
	{
		range = MathF.Max( range, 0.001f );

		var cr = MathF.Max( r, 0.0f ) / range;
		var cg = MathF.Max( g, 0.0f ) / range;
		var cb = MathF.Max( b, 0.0f ) / range;

		var m = Math.Clamp( MathF.Max( cr, MathF.Max( cg, cb ) ), 1.0f / 255.0f, 1.0f );
		m = MathF.Ceiling( m * 255.0f ) / 255.0f;

		byte Byte( float value ) => (byte)Math.Clamp( (int)MathF.Round( value / m * 255.0f ), 0, 255 );

		return (Byte( cr ), Byte( cg ), Byte( cb ), (byte)Math.Clamp( (int)MathF.Round( m * 255.0f ), 1, 255 ));
	}

	private static readonly Dictionary<Guid, Held> held = new();

	//Whether anything has been captured at all.
	internal static bool Any => held.Count > 0;

	//Whether a probe can be drawn here: not in the portable frame, where no pixel samples it - see kProbesOn
	private static bool CanDraw( Scene scene, EnvmapProbe probe )
		=> scene.IsValid() && probe.IsValid() && probe.GameObject.IsValid() && CoreNative.Available && CoreRenderDevice.Initialize()
			&& !CoreRenderDevice.PortableFrame && CoreSceneDrawer.EnsureResources();

	//A runtime probe's capture: six frames of its own, so never while one is open - reading a view waits for the device.
	//A face a tick, folded on a worker; or all of it now, for whoever waits on it
	internal static bool Capture( Scene scene, EnvmapProbe probe, bool wait = false )
	{
		if ( !CanDraw( scene, probe ) ) return false;

		job = Begin( probe );

		if ( !wait ) return Step( scene, probe );

		var done = Finish( scene );
		if ( done is null ) return false;

		Keep( done.Id, Fold( done.Work, done.Where ) );

		lock ( folding ) folding.Remove( done.Id );

		return true;
	}

	/// <summary>A baked probe's picture, drawn and folded now for the scene's bake; null where it could not be.</summary>
	internal static byte[] Take( Scene scene, EnvmapProbe probe )
	{
		if ( !CanDraw( scene, probe ) || job is not null ) return null;

		job = Begin( probe );

		var done = Finish( scene );
		if ( done is null ) return null;

		var work = Fold( done.Work, done.Where );
		var pixels = (byte[])work.Picture.Clone();
		Scratch.Give( work );

		return pixels;
	}

	private static Job Begin( EnvmapProbe probe )
	{
		var size = Math.Clamp( (int)probe.Resolution, 64, 512 );

		return new Job { Probe = probe, Id = probe.GameObject.Id, Size = size, Work = Scratch.Take( size ) };
	}

	//Every face of the capture under way now; the job, or null where a face failed
	private static Job Finish( Scene scene )
	{
		using ( Rinten.Engine.Bootstrap.StartupTiming?.ScopeTimer( "Envmap: Six Draws" ) )
		{
			while ( job is not null && job.Next < 6 )
				if ( !DrawFace( scene, job ) ) { job = null; return null; }
		}

		var done = job;
		job = null;

		return done;
	}

	//A capture under way: the probe, faces drawn so far with their matrices, and where it stood
	private sealed class Job
	{
		public EnvmapProbe Probe;
		public Guid Id;
		public int Size;
		public Scratch Work;
		public int Next;
		public Vector3 Where;
	}

	//The capture under way, if one is - there is only ever one.
	private static Job job;

	//Reusable capture buffers, returned once the picture is on the device; avoids per-capture garbage
	private sealed class Scratch
	{
		public readonly byte[][] Faces = new byte[6][];
		public readonly System.Numerics.Matrix4x4[] Clips = new System.Numerics.Matrix4x4[6];
		public readonly Vector3[] Sharp = new Vector3[Side * Side];
		public readonly Vector3[] Blur = new Vector3[Side * Side];
		public readonly byte[] Picture = new byte[Side * Side * Bands * 4];
		public int Size;

		private static readonly Stack<Scratch> spare = new();

		/// <summary>One for a capture of faces <paramref name="size"/> on a side.</summary>
		public static Scratch Take( int size )
		{
			Scratch one;
			lock ( spare ) spare.TryPop( out one );
			one ??= new Scratch();

			if ( one.Size != size )
			{
				for ( var face = 0; face < 6; face++ ) one.Faces[face] = new byte[size * size * 4];
				one.Size = size;
			}

			return one;
		}

		/// <summary>Done with: its picture is on the device.</summary>
		public static void Give( Scratch one )
		{
			lock ( spare ) if ( spare.Count < 4 ) spare.Push( one );
		}
	}

	//Whether this probe's capture is under way - its next face owed on the next tick.
	internal static bool Capturing( EnvmapProbe probe ) => job is not null && probe is not null && job.Probe == probe;

	//Draws one capture face per tick; after the sixth, folds on a worker (see Settle)
	internal static bool Step( Scene scene, EnvmapProbe probe )
	{
		if ( job is null || job.Probe != probe ) return false;

		if ( !scene.IsValid() || !probe.IsValid() || !DrawFace( scene, job ) )
		{
			job = null;
			return false;
		}

		if ( job.Next < 6 ) return true;

		var done = job;
		job = null;

		var folded = System.Threading.Tasks.Task.Run( () => Fold( done.Work, done.Where ) );

		lock ( folding ) folding[done.Id] = folded;

		return true;
	}

	//The next face of a capture, drawn and read back.
	private static bool DrawFace( Scene scene, Job at )
	{
		var probe = at.Probe;
		var face = at.Next;

		//Where the probe stands, from its first face; a move mid-capture is caught next time
		if ( face == 0 ) at.Where = probe.WorldPosition;

		var camera = Borrowed( scene, out var made, out var was );
		if ( camera is null ) return false;

		var view = View( at.Size );

		try
		{
			if ( view == 0 ) return false;

			Aim( camera, at.Where, probe, face );
			at.Work.Clips[face] = Clip( at.Where, probe, face );

			if ( !CoreRenderDevice.BeginViewFrame( view, Color.Black ) ) return false;

			Drawer.Draw( scene, camera, view, at.Size, at.Size );

			if ( !CoreRenderDevice.EndViewFrame( view ) ) return false;

			if ( !CoreRenderDevice.ReadView( view, at.Work.Faces[face] ) ) return false;

			at.Next++;
			capturedAt = RealTime.Now;

			return true;
		}
		catch ( Exception e )
		{
			//Said, not swallowed: a probe that captured nothing looks like one reflecting a black room
			Log.Warning( $"the envmap probe on {probe.GameObject?.Name} did not capture: {e.Message}" );
			return false;
		}
		finally
		{
			Give( camera, made, was );
		}
	}

	//The view faces are drawn into, kept from one to the next for as long as their size does not change.
	private static ulong View( int size )
	{
		if ( view != 0 && viewSize == size ) return view;

		if ( view != 0 ) CoreRenderDevice.DestroyView( view );

		view = CoreRenderDevice.CreateView( size, size );
		viewSize = view != 0 ? size : 0;

		return view;
	}

	private static ulong view;
	private static int viewSize;

	//Forgets one probe's picture.
	internal static void Forget( EnvmapProbe probe )
	{
		if ( probe is null ) return;

		var id = probe.GameObject.IsValid() ? probe.GameObject.Id : Guid.Empty;

		lock ( folding ) folding.Remove( id );

		if ( job is not null && job.Probe == probe ) job = null;

		if ( !held.Remove( id, out var was ) ) return;

		if ( was.Texture != 0 ) CoreRenderDevice.DestroyTexture( was.Texture );
	}

	//Everything, because the device is going away and every handle with it.
	internal static void Release()
	{
		drawer?.Release();
		drawer = null;

		if ( view != 0 ) CoreRenderDevice.DestroyView( view );
		view = 0;
		viewSize = 0;
		job = null;

		foreach ( var one in held.Values )
		{
			if ( one.Texture != 0 ) CoreRenderDevice.DestroyTexture( one.Texture );
		}

		held.Clear();
	}

	//Dedicated drawer for captures, kept until the device goes so its caches survive between faces
	private static CoreSceneDrawer Drawer => drawer ??= new CoreSceneDrawer { Watched = false };
	private static CoreSceneDrawer drawer;

	//----------------------------------------------------------- the frame

	//Probe count in the last written frame block, nearest first; 0 selects pipelines without probes
	internal static int Written { get; private set; }

	internal static void Write( float[] frame, Scene scene, Vector3 camera )
	{
		if ( frame is null || frame.Length < CoreSceneRenderer.FrameFloats ) return;

		Settle();

		var at = CoreSceneRenderer.ProbesAt;

		for ( var i = 0; i < 4 + ( MostProbes * 16 ); i++ ) frame[at + i] = 0.0f;

		Written = 0;

		//Nothing reads a probe in the portable frame - kProbesOn is nought in frame.slang, which says
		//why - so the slots stay at nought and the scene's probes are not gone through. See RenderTier.
		if ( CoreRenderDevice.PortableFrame ) return;

		if ( !scene.IsValid() ) return;

		var baked = scene.LightingIfAny?.Envmaps.Asset?.Data;
		var found = Chosen( scene, camera, baked );

		var written = 0;

		foreach ( var probe in found )
		{
			var one = held[probe.GameObject.Id];

			if ( one.Index <= 0 ) continue;

			var slot = at + 4 + ( written * 16 );
			var where = probe.WorldPosition;

	//The box in world space: local bounds moved to where it stands. Rotation is left out - box projection
	//divides by axes, and a rotated box would need a matrix per probe
			var mins = where + probe.Bounds.Mins;
			var maxs = where + probe.Bounds.Maxs;

			frame[slot + 0] = where.x;
			frame[slot + 1] = where.y;
			frame[slot + 2] = where.z;
			frame[slot + 3] = one.Index;

			frame[slot + 4] = mins.x;
			frame[slot + 5] = mins.y;
			frame[slot + 6] = mins.z;
			frame[slot + 7] = probe.Projection == EnvmapProbe.ProjectionMode.Box ? 1.0f : 0.0f;

			frame[slot + 8] = maxs.x;
			frame[slot + 9] = maxs.y;
			frame[slot + 10] = maxs.z;
			frame[slot + 11] = MathF.Max( probe.Feathering, 0.0f );

			frame[slot + 12] = probe.TintColor.r;
			frame[slot + 13] = probe.TintColor.g;
			frame[slot + 14] = probe.TintColor.b;
			frame[slot + 15] = probe.TintColor.a;

			written++;
		}

		frame[at + 0] = written;

		Written = written;

		frame[at + 1] = Side;
		frame[at + 2] = Bands;
		frame[at + 3] = RgbmRange;
	}

	//Probes for the frame: highest priority, then nearest, up to MostProbes, without allocating. A baked one is put
	//on the card from the bake as it is first chosen, and again after a new bake
	private static List<EnvmapProbe> Chosen( Scene scene, Vector3 camera, BakedEnvmapsData baked )
	{
		chosen.Clear();

		foreach ( var probe in scene.GetAllComponents<EnvmapProbe>() )
		{
			if ( !probe.IsValid() || !probe.Enabled || !probe.GameObject.IsValid() ) continue;
			if ( probe.Mode == EnvmapProbe.EnvmapProbeMode.Baked && !OnCard( probe, baked ) ) continue;
			if ( !held.ContainsKey( probe.GameObject.Id ) ) continue;

			var distance = ( probe.WorldPosition - camera ).LengthSquared;
			var at = chosen.Count;

			while ( at > 0 && Beats( probe, distance, chosen[at - 1], camera ) ) at--;

			if ( at >= MostProbes ) continue;

			chosen.Insert( at, probe );

			if ( chosen.Count > MostProbes ) chosen.RemoveAt( MostProbes );
		}

		return chosen;
	}

	//Whether a baked probe's picture is on the card as the bake has it, putting it there where it is not
	private static bool OnCard( EnvmapProbe probe, BakedEnvmapsData baked )
	{
		var id = probe.GameObject.Id;
		var pixels = baked is { Side: Side, Bands: Bands } ? baked.PixelsOf( id ) : null;

		if ( pixels is null || pixels.Length != Side * Side * Bands * 4 )
		{
			if ( held.TryGetValue( id, out var stale ) && stale.From is not null ) Forget( probe );
			return false;
		}

		if ( held.TryGetValue( id, out var one ) && ReferenceEquals( one.From, pixels ) ) return true;

		Upload( id, pixels );
		held[id].From = pixels;

		return true;
	}

	//Whether one probe goes ahead of another: higher priority, then nearer.
	private static bool Beats( EnvmapProbe probe, float distance, EnvmapProbe other, Vector3 camera )
	{
		if ( probe.Priority != other.Priority ) return probe.Priority > other.Priority;

		return distance < ( other.WorldPosition - camera ).LengthSquared;
	}

	private static readonly List<EnvmapProbe> chosen = new();

	//------------------------------------------------------- drawing it

	//Borrows a scene camera for the faces, or makes a temporary one if the scene has none
	private static CameraComponent Borrowed( Scene scene, out GameObject made, out (Transform Tx, float Fov, float Near, float Far, bool Ortho, bool Post) was )
	{
		made = null;
		was = default;

		var camera = scene.GetAllComponents<CameraComponent>()
			.FirstOrDefault( one => one.IsValid() && !one.IsSceneEditorCamera );

		if ( camera.IsValid() )
		{
			was = (camera.WorldTransform, camera.FieldOfView, camera.ZNear, camera.ZFar, camera.Orthographic,
				camera.EnablePostProcessing);

			//Post effects off for the faces so they are not baked into reflections; the curve stays on
			camera.EnablePostProcessing = false;
			camera.KeepsCurveWithoutPost = true;

			return camera;
		}

		using ( scene.Push() )
		{
			made = scene.CreateObject();
			made.Name = "Envmap Capture";
			made.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved;

			return made.Components.Create<CameraComponent>();
		}
	}

	private static void Give( CameraComponent camera, GameObject made, (Transform Tx, float Fov, float Near, float Far, bool Ortho, bool Post) was )
	{
		if ( made.IsValid() ) { made.Destroy(); return; }

		if ( !camera.IsValid() ) return;

		camera.WorldTransform = was.Tx;
		camera.FieldOfView = was.Fov;
		camera.ZNear = was.Near;
		camera.ZFar = was.Far;
		camera.Orthographic = was.Ortho;
		camera.EnablePostProcessing = was.Post;
		camera.KeepsCurveWithoutPost = false;
	}

	//The world's own axes, in the order the fold expects.
	private static Vector3 Way( int face ) => face switch
	{
		0 => new Vector3( 1, 0, 0 ),
		1 => new Vector3( -1, 0, 0 ),
		2 => new Vector3( 0, 1, 0 ),
		3 => new Vector3( 0, -1, 0 ),
		4 => new Vector3( 0, 0, 1 ),
		_ => new Vector3( 0, 0, -1 ),
	};

	//Any up will do until it is parallel with the face.
	private static Vector3 Over( int face ) => face >= 4 ? new Vector3( 0, 1, 0 ) : new Vector3( 0, 0, 1 );

	private static void Aim( CameraComponent camera, Vector3 where, EnvmapProbe probe, int face )
	{
		camera.Orthographic = false;
		camera.FieldOfView = 90.0f;
		camera.ZNear = MathF.Max( probe.ZNear, 0.01f );
		camera.ZFar = MathF.Max( probe.ZFar, camera.ZNear + 0.1f );

		camera.WorldPosition = where;
		camera.WorldRotation = Rotation.LookAt( Way( face ), Over( face ) );
	}

	//Rebuilds the face's matrix exactly as CoreSceneDrawer.Draw does, so the fold matches the draw
	private static System.Numerics.Matrix4x4 Clip( Vector3 position, EnvmapProbe probe, int face )
	{
		var forward = Way( face );
		var up = Rotation.LookAt( forward, Over( face ) ).Up;

		var view = System.Numerics.Matrix4x4.CreateLookAt(
			new System.Numerics.Vector3( position.x, position.y, position.z ),
			new System.Numerics.Vector3( position.x + forward.x, position.y + forward.y, position.z + forward.z ),
			new System.Numerics.Vector3( up.x, up.y, up.z ) );

		var near = MathF.Max( probe.ZNear, 0.01f );
		var far = MathF.Max( probe.ZFar, near + 0.1f );

		var projection = CoreSceneDrawer.ProjectionMatrix( false, 90.0f, 0.0f, 1.0f, near, far );

		return view * projection;
	}

	//-------------------------------------------------------- the fold

	//Six square faces into one octahedral picture, with blurred copies of it stacked underneath.
	private static Scratch Fold( Scratch work, Vector3 where )
	{
		var (faces, size, clips, sharp) = (work.Faces, work.Size, work.Clips, work.Sharp);

		for ( var y = 0; y < Side; y++ )
		{
			for ( var x = 0; x < Side; x++ )
			{
		//The texel's middle, so the octahedron's halves meet on the diagonal, not a texel short
				var uv = new Vector2( ( x + 0.5f ) / Side, ( y + 0.5f ) / Side );
				var direction = Unfold( uv );

				sharp[( y * Side ) + x] = Looked( faces, size, clips, direction, where );
			}
		}

		Band( work.Picture, sharp, 0 );

		for ( var band = 1; band < Bands; band++ )
		{
			Blurred( sharp, Cones[band], work.Blur );
			Band( work.Picture, work.Blur, band );
		}

		return work;
	}

	//Each band is a wider cone than the last. The angles are the usual roughness ladder: a mirror,
	//then a surface, then a wall, then something matt, then very nearly the average of the whole room.
	private static readonly float[] Cones = { 0.0f, 0.18f, 0.38f, 0.65f, 1.0f };

	//One band of the picture, encoded.
	private static void Band( byte[] picture, Vector3[] light, int band )
	{
		var start = band * Side * Side * 4;

		for ( var i = 0; i < Side * Side; i++ )
		{
			var one = light[i];
			var (r, g, b, a) = EncodeRgbm( one.x, one.y, one.z, RgbmRange );

			picture[start + ( i * 4 ) + 0] = r;
			picture[start + ( i * 4 ) + 1] = g;
			picture[start + ( i * 4 ) + 2] = b;
			picture[start + ( i * 4 ) + 3] = a;
		}
	}

	//Samples the six faces in a direction, using the largest component's face and its own matrix
	private static Vector3 Looked( byte[][] faces, int size, System.Numerics.Matrix4x4[] clips,
		Vector3 direction, Vector3 where )
	{
		var face = Whose( direction );
		var point = where + direction;

		var clip = System.Numerics.Vector4.Transform(
			new System.Numerics.Vector4( point.x, point.y, point.z, 1.0f ), clips[face] );

		if ( clip.W <= 0.0001f ) return Vector3.Zero;

		var ndc = new Vector2( clip.X / clip.W, clip.Y / clip.W );

		//The same reading as everywhere else in the engine - see SampleBounce.
		var u = ( ndc.x * 0.5f ) + 0.5f;
		var v = 0.5f - ( ndc.y * 0.5f );

		return Bilinear( faces[face], size, u, v );
	}

	//Which of the six a direction points into.
	private static int Whose( Vector3 d )
	{
		var ax = MathF.Abs( d.x );
		var ay = MathF.Abs( d.y );
		var az = MathF.Abs( d.z );

		if ( ax >= ay && ax >= az ) return d.x >= 0.0f ? 0 : 1;
		if ( ay >= az ) return d.y >= 0.0f ? 2 : 3;

		return d.z >= 0.0f ? 4 : 5;
	}

	private static Vector3 Bilinear( byte[] pixels, int size, float u, float v )
	{
		var x = Math.Clamp( ( u * size ) - 0.5f, 0.0f, size - 1.0f );
		var y = Math.Clamp( ( v * size ) - 0.5f, 0.0f, size - 1.0f );

		var x0 = (int)x;
		var y0 = (int)y;
		var x1 = Math.Min( x0 + 1, size - 1 );
		var y1 = Math.Min( y0 + 1, size - 1 );

		var fx = x - x0;
		var fy = y - y0;

		var a = At( pixels, size, x0, y0 );
		var b = At( pixels, size, x1, y0 );
		var c = At( pixels, size, x0, y1 );
		var d = At( pixels, size, x1, y1 );

		var top = Vector3.Lerp( a, b, fx );
		var bottom = Vector3.Lerp( c, d, fx );

		return Vector3.Lerp( top, bottom, fy );
	}

	//Inverts the default fitted ACES tonemap to recover captured light, clamped to RgbmRange
	private static float Untonemapped( float shown )
	{
		const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;

		var y = Math.Clamp( shown, 0.0f, 0.94f );

		var qa = ( c * y ) - a;
		var qb = ( d * y ) - b;
		var qc = e * y;

		//Nearly straight at the dark end, where the quadratic's leading term all but vanishes:
		//one divide rather than a root of a difference of two numbers that are almost equal.
		if ( MathF.Abs( qa ) < 1e-5f ) return Math.Clamp( qc / MathF.Max( -qb, 1e-6f ), 0.0f, RgbmRange );

		var under = ( qb * qb ) - ( 4.0f * qa * qc );
		if ( under < 0.0f ) return 0.0f;

		var root = MathF.Sqrt( under );

		//One root each side of nought, as qa is negative over the range and qc is not: the positive one is the light
		var first = ( -qb + root ) / ( 2.0f * qa );
		var second = ( -qb - root ) / ( 2.0f * qa );

		return Math.Clamp( MathF.Max( first, second ), 0.0f, RgbmRange );
	}

	//One pixel of a drawn face as light, not colour: the display curve off it, then the tonemap - see Untonemapped
	private static Vector3 At( byte[] pixels, int size, int x, int y )
	{
		var i = ( ( y * size ) + x ) * 4;

		return new Vector3(
			Untonemapped( MathF.Pow( pixels[i + 0] / 255.0f, 2.2f ) ),
			Untonemapped( MathF.Pow( pixels[i + 1] / 255.0f, 2.2f ) ),
			Untonemapped( MathF.Pow( pixels[i + 2] / 255.0f, 2.2f ) ) );
	}

	//Cone-averaged blur sampled by direction, so it does not smear across the octahedral seams
	private static void Blurred( Vector3[] sharp, float wideness, Vector3[] made )
	{
		var taps = wideness < 0.3f ? 16 : wideness < 0.7f ? 32 : 48;
		var spread = MathF.Max( wideness, 0.001f ) * ( MathF.PI * 0.5f );

	//The golden angle, so one texel's taps don't line up with the next's and turn the blur into a pattern
		const float Golden = 2.39996323f;

		//Per-tap cone weights are the same for every texel, so they are computed once here
		var alongCos = new float[taps];
		var acrossRight = new float[taps];
		var acrossUp = new float[taps];

		for ( var tap = 0; tap < taps; tap++ )
		{
			var along = MathF.Sqrt( ( tap + 0.5f ) / taps ) * spread;
			var around = tap * Golden;

			alongCos[tap] = MathF.Cos( along );
			acrossRight[tap] = MathF.Cos( around ) * MathF.Sin( along );
			acrossUp[tap] = MathF.Sin( around ) * MathF.Sin( along );
		}

	//A row at a time across the processors: each texel reads the sharp picture and writes only its own entry
		System.Threading.Tasks.Parallel.For( 0, Side, y =>
		{
			for ( var x = 0; x < Side; x++ )
			{
				var uv = new Vector2( ( x + 0.5f ) / Side, ( y + 0.5f ) / Side );
				var middle = Unfold( uv );

				Basis( middle, out var right, out var up );

				var total = Vector3.Zero;

				for ( var tap = 0; tap < taps; tap++ )
				{
					var offset = ( right * acrossRight[tap] ) + ( up * acrossUp[tap] );
					var direction = ( ( middle * alongCos[tap] ) + offset ).Normal;

					total += Read( sharp, Fold( direction ) );
				}

				made[( y * Side ) + x] = taps > 0 ? total / taps : Vector3.Zero;
			}
		} );
	}

	//Two directions across a third, for walking around it.
	private static void Basis( Vector3 forward, out Vector3 right, out Vector3 up )
	{
		var other = MathF.Abs( forward.z ) < 0.9f ? new Vector3( 0, 0, 1 ) : new Vector3( 1, 0, 0 );

		right = forward.Cross( other ).Normal;
		up = right.Cross( forward ).Normal;
	}

	private static Vector3 Read( Vector3[] light, Vector2 uv )
	{
		var x = Math.Clamp( (int)( uv.x * Side ), 0, Side - 1 );
		var y = Math.Clamp( (int)( uv.y * Side ), 0, Side - 1 );

		return light[( y * Side ) + x];
	}

	//--------------------------------------------- the octahedron itself

	//Octahedral mapping of a direction into the square; the shader undoes the same mapping
	internal static Vector2 Fold( Vector3 direction )
	{
		var d = direction.Normal;
		var sum = MathF.Abs( d.x ) + MathF.Abs( d.y ) + MathF.Abs( d.z );

		if ( sum < 0.00001f ) return new Vector2( 0.5f, 0.5f );

		var x = d.x / sum;
		var y = d.y / sum;

		if ( d.z < 0.0f )
		{
			var fx = ( 1.0f - MathF.Abs( y ) ) * ( x >= 0.0f ? 1.0f : -1.0f );
			var fy = ( 1.0f - MathF.Abs( x ) ) * ( y >= 0.0f ? 1.0f : -1.0f );

			x = fx;
			y = fy;
		}

		return new Vector2( ( x * 0.5f ) + 0.5f, ( y * 0.5f ) + 0.5f );
	}

	//The square back into a direction.
	internal static Vector3 Unfold( Vector2 uv )
	{
		var x = ( uv.x * 2.0f ) - 1.0f;
		var y = ( uv.y * 2.0f ) - 1.0f;
		var z = 1.0f - MathF.Abs( x ) - MathF.Abs( y );

		if ( z < 0.0f )
		{
			var fx = ( 1.0f - MathF.Abs( y ) ) * ( x >= 0.0f ? 1.0f : -1.0f );
			var fy = ( 1.0f - MathF.Abs( x ) ) * ( y >= 0.0f ? 1.0f : -1.0f );

			x = fx;
			y = fy;
		}

		return new Vector3( x, y, z ).Normal;
	}

	//--------------------------------------------------------- keeping it

	//A runtime capture onto the card, its scratch given back
	private static void Keep( Guid id, Scratch work )
	{
		Upload( id, work.Picture );
		held[id].From = null;
		Scratch.Give( work );
	}

	//A picture onto the card: into the probe's texture where it has one - the same shape every time, so a runtime
	//probe is not a new table slot at each capture - else a new one
	private static void Upload( Guid id, byte[] picture )
	{
		if ( held.TryGetValue( id, out var was ) && was.Texture != 0 &&
			CoreRenderDevice.UpdateTextureRgba( was.Texture, picture, Side, Side * Bands ) ) return;

		if ( was is not null && was.Texture != 0 )
			CoreRenderDevice.DestroyTexture( was.Texture );

		var texture = CoreRenderDevice.CreateTextureRgba( picture, Side, Side * Bands, srgb: false );

		held[id] = new Held { Texture = texture, Index = texture == 0 ? 0 : (int)CoreRender.TextureIndex( texture ) };
	}
}

using System.Runtime.InteropServices;
using Rinten.Rendering;
using static Rinten.Rendering.SpriteBatchSceneObject;

namespace Rinten;

//An effect stepped and drawn on the card: its particles are made here as ever, handed over at its next step, and moved, coloured
//and written out as sprites by particles.slang. Which effects - see ParticleEffect.OnCard. The full frame only: a page has no compute
internal sealed class ParticleGpuRun : IDisposable
{
	//One particle as the card holds it - Particle in particles.slang
	[StructLayout( LayoutKind.Sequential )]
	internal struct Packed
	{
		public System.Numerics.Vector4 Place, Motion, Size, Colour, Start, Angles, Spin, Last, Overlay, Follow;
	}

	private readonly ParticleEffect effect;
	private readonly int capacity;

	private readonly GpuBuffer<Packed> particles;
	private readonly GpuBuffer<SpriteData> sprites;
	private readonly GpuBuffer<float> table;
	private readonly GpuBuffer<Packed> spawns;
	private readonly GpuBuffer<uint> spawnSlots;

	//What the step writes for an effect drawn far to near, before SortCs orders it into sprites; null for one that is not
	private readonly GpuBuffer<SpriteData> unsorted;

	/// <summary>The most particles an effect drawn far to near may have here: one group of the sort - see SortCs.</summary>
	internal const int SortMost = 1024;
	private readonly GpuBuffer<int> indices;

	//What each slot's particle has lived and lives, and how fast its clock runs, as the card steps it - so a slot is
	//known free without reading anything back. Nought life is a free slot
	private readonly float[] age;
	private readonly float[] life;
	private readonly float[] rate;
	private readonly Stack<int> free = new();

	private readonly List<Packed> pending = new();
	private readonly List<uint> pendingSlots = new();

	//Seconds of the effect's clock the card has still to step, and the table as it was last laid
	private float owed;
	private readonly float[] laid = new float[TableFloats];
	private readonly float[] laying = new float[TableFloats];
	private bool tableWritten;

	private readonly Drawer drawer;

	/// <summary>How many of its slots hold a particle now.</summary>
	internal int Alive { get; private set; }

	//How far from the emitter the furthest of them may have got, in metres - what the effect's bounds are grown by
	private float reach;

	internal ParticleGpuRun( ParticleEffect effect )
	{
		this.effect = effect;
		capacity = Math.Max( effect.MaxParticles, 1 );

		particles = new GpuBuffer<Packed>( capacity );
		sprites = new GpuBuffer<SpriteData>( capacity );
		table = new GpuBuffer<float>( TableFloats );
		spawns = new GpuBuffer<Packed>( capacity );
		spawnSlots = new GpuBuffer<uint>( capacity );

		if ( effect.Components.Get<ParticleSpriteRenderer>() is { IsSorted: true } ) unsorted = new GpuBuffer<SpriteData>( capacity );

		indices = new GpuBuffer<int>( 6, GpuBuffer.UsageFlags.Index );
		indices.SetData( (ReadOnlySpan<int>)[0, 1, 2, 0, 2, 3] );

		//Nothing in any slot yet: a fresh buffer holds whatever was there
		particles.SetData( new Packed[capacity].AsSpan() );

		age = new float[capacity];
		life = new float[capacity];
		rate = new float[capacity];

		for ( var i = capacity - 1; i >= 0; i-- ) free.Push( i );

		drawer = new Drawer( effect.Scene, this );
	}

	/// <summary>Whether this run was made for the effect as it stands - a new cap is a new run.</summary>
	internal bool Fits( ParticleEffect of ) => ReferenceEquals( of, effect ) && capacity == Math.Max( of.MaxParticles, 1 );

	public void Dispose()
	{
		lock ( Runs ) Runs.Remove( this );

		drawer.Delete();

		particles.Dispose();
		sprites.Dispose();
		table.Dispose();
		spawns.Dispose();
		spawnSlots.Dispose();
		unsorted?.Dispose();
		indices.Dispose();
	}

	//-- the effect's side ---------------------------------------------------------------------------------------------

	/// <summary>Takes a particle the effect made, into a free slot; false where none is free and it is dropped.</summary>
	internal bool Take( Particle p, int seed )
	{
		if ( !free.TryPop( out var slot ) ) return false;

		//Nothing drew a frame to carry them over in a while: what waits is long out of date, and would only grow
		if ( pending.Count >= capacity )
		{
			pending.Clear();
			pendingSlots.Clear();
		}

		//As UpdateParticle works it out every step, which is what decides the particle's end there
		var lifetime = MathF.Max( effect.Lifetime.IsPlainConstant
			? effect.Lifetime.ConstantValue
			: effect.Lifetime.Evaluate( p.Rand( 155, 100 ), p.Rand( 145, 100 ) ), 1e-4f );
		var clock = MathF.Max( effect.PerParticleTimeScale.ConstantValue * p.TimeScale, 0.0f );

		age[slot] = MathF.Max( p.Age, 0.0f );
		life[slot] = lifetime;
		rate[slot] = clock;

		var spin = Vector3.Zero;
		var follow = System.Numerics.Vector4.Zero;

		if ( effect.Components.Get<ParticleModuleRunner>() is { Data: { } data } runner )
		{
			foreach ( var module in data.UpdateModules )
			{
				if ( module is SpinModule { Enabled: true } turning ) spin += turning.Speed.Evaluate( p, runner.Player );

				//What of its place on the shape stays the particle's all its life: its stagger, and by index its own place among the shape's
				if ( module is FollowShapeModule { Enabled: true } shaped )
				{
					var places = shaped.Sample == ParticleShapeSample.ByIndex && runner.Player?.Definition?.Shape( shaped.ShapeName ) is { } shape
						? shape.Places( runner.Emitter?.BurstCount ?? 1 )
						: 1;

					follow.X = shaped.Stagger.Evaluate( p, runner.Player );
					follow.Y = places <= 1 ? 0.0f : ( p.Index % places ) / (float)( places - 1 );
				}
			}
		}

		pending.Add( new Packed
		{
			Place = new( p.Position.x, p.Position.y, p.Position.z, age[slot] ),
			Motion = new( p.Velocity.x, p.Velocity.y, p.Velocity.z, lifetime ),
			Size = new( p.Size.x, p.Size.y, p.Size.z, clock ),
			Colour = new( p.Color.r, p.Color.g, p.Color.b, p.Color.a ),
			Start = new( p.StartAngles.pitch, p.StartAngles.yaw, p.StartAngles.roll, p.StartScale ),
			Angles = new( p.Angles.pitch, p.Angles.yaw, p.Angles.roll, BitConverter.Int32BitsToSingle( seed ) ),
			Spin = new( spin.x, spin.y, spin.z, p.Alpha ),
			Last = new( p.Velocity.x, p.Velocity.y, p.Velocity.z, p.IndexDelta ),
			Overlay = new( p.OverlayColor.r, p.OverlayColor.g, p.OverlayColor.b, p.OverlayColor.a ),
			Follow = follow,
		} );

		pendingSlots.Add( (uint)slot );
		Alive++;

		//A conservative reach for the bounds: where it starts, plus what its speed and the steady pulls could carry it in its life
		var pull = Gravity( effect ).Length + CurlStrength( effect );
		var travel = ( p.Velocity.Length * lifetime ) + ( 0.5f * pull * lifetime * lifetime );
		reach = MathF.Max( reach, Vector3.DistanceBetween( p.Position, effect.WorldPosition ) + travel + MathF.Max( p.Size.x, p.Size.y ) );

		return true;
	}

	/// <summary>A step of the effect: what its slots age by, which of them end, and the table the card reads this frame.</summary>
	internal void Step( float delta )
	{
		owed += delta;

		for ( var i = 0; i < capacity; i++ )
		{
			if ( life[i] <= 0.0f ) continue;

			var before = age[i] / life[i];
			age[i] += delta * rate[i];

			if ( before < 1.0f ) continue;

			life[i] = 0.0f;
			free.Push( i );
			Alive--;
		}

		if ( Alive <= 0 ) reach = 0.0f;

		Lay( laying );

		effect.ParticleBounds = BBox.FromPositionAndSize( effect.WorldPosition, MathF.Max( reach, 1.0f ) * 2.0f );
		drawer.Bounds = effect.ParticleBounds;

		lock ( Runs )
		{
			if ( !Runs.Contains( this ) ) Runs.Add( this );
		}
	}

	//-- the table -----------------------------------------------------------------------------------------------------

	//Where things are in the table - see particles.slang, which has the same numbers
	private const int TableGravity = 0;
	private const int TableCurl = 4;
	private const int TableCurlOffset = 8;
	private const int TableSprite = 12;
	private const int TableTint = 28;
	private const int TableApply = 32;
	private const int TableBlur = 40;
	private const int TableChannels = 44;
	private const int ChannelFloats = 8;
	private const int TableGradient = TableChannels + ( 9 * ChannelFloats );
	private const int TableOrbit = TableGradient + 12;
	private const int TableAttract = TableOrbit + 8;
	private const int TableVortex = TableAttract + 8;
	private const int TableFollow = TableVortex + 8;
	private const int TableShape = TableFollow + 16;
	private const int TableCurves = TableShape + ( ShapeSamples * 3 );

	private const int LutSamples = 64;
	private const int ShapeSamples = 256;

	//Nine channels of two curves each and a gradient's colours
	private const int TableFloats = TableCurves + ( 18 * LutSamples ) + ( 4 * LutSamples );

	//The followed shape's poses in its own frame, baked once a revision of it - see FollowShapeModule
	private readonly float[] shapePoses = new float[ShapeSamples * 3];
	private ParticleShape shapeBaked;
	private int shapeRevision = -1;

	//The gravity of every Gravity module, summed - each is the same for every particle, or the effect is not here
	private static Vector3 Gravity( ParticleEffect effect )
	{
		if ( effect.Components.Get<ParticleModuleRunner>() is not { Data: { } data } runner ) return Vector3.Zero;

		var sum = Vector3.Zero;

		foreach ( var module in data.UpdateModules )
		{
			if ( module is GravityModule { Enabled: true } gravity ) sum += gravity.Force.Evaluate( null, runner.Player );
		}

		return sum;
	}

	private static float CurlStrength( ParticleEffect effect )
	{
		if ( effect.Components.Get<ParticleModuleRunner>() is not { Data: { } data } runner ) return 0.0f;

		foreach ( var module in data.UpdateModules )
		{
			if ( module is CurlNoiseModule { Enabled: true } curl ) return MathF.Abs( curl.Strength.Evaluate( runner.Player ) );
		}

		return 0.0f;
	}

	//Everything the card reads that is the effect's rather than a particle's, as it stands now
	private void Lay( float[] into )
	{
		Array.Clear( into );

		var gravity = Gravity( effect );
		into[TableGravity] = gravity.x;
		into[TableGravity + 1] = gravity.y;
		into[TableGravity + 2] = gravity.z;

		if ( effect.Components.Get<ParticleModuleRunner>() is { Data: { } data } runner )
		{
			foreach ( var module in data.UpdateModules )
			{
				if ( module is not CurlNoiseModule { Enabled: true } curl ) continue;

				into[TableCurl] = curl.Strength.Evaluate( runner.Player );
				into[TableCurl + 1] = curl.Scale.Evaluate( runner.Player );
				into[TableCurl + 2] = curl.TimeScale.Evaluate( runner.Player );
				into[TableCurl + 3] = 1.0f;
				into[TableCurlOffset] = curl.Offset.x;
				into[TableCurlOffset + 1] = curl.Offset.y;
				into[TableCurlOffset + 2] = curl.Offset.z;
				break;
			}

			foreach ( var module in data.UpdateModules )
			{
				if ( module is not OrbitModule { Enabled: true } orbit ) continue;

				var centre = orbit.Center;
				var axis = orbit.Axis.Evaluate( null, runner.Player );

				//Local is the emitter's own frame, as the module turns it - see OrbitModule.Step
				if ( orbit.Space == ParticleSpace.Local && runner.Emitter.IsValid() )
				{
					var emitter = runner.Emitter.WorldTransform;
					centre = emitter.Position + ( emitter.Rotation * centre );
					axis = emitter.Rotation * axis;
				}

				if ( axis.IsNearlyZero() ) break;

				axis = axis.Normal;

				into[TableOrbit] = centre.x;
				into[TableOrbit + 1] = centre.y;
				into[TableOrbit + 2] = centre.z;
				into[TableOrbit + 3] = 1.0f;
				into[TableOrbit + 4] = axis.x;
				into[TableOrbit + 5] = axis.y;
				into[TableOrbit + 6] = axis.z;
				into[TableOrbit + 7] = orbit.Speed.Evaluate( runner.Player );
				break;
			}

			foreach ( var module in data.UpdateModules )
			{
				if ( module is not AttractorModule { Enabled: true } attract ) continue;

				var origin = attract.Position.Evaluate( null, runner.Player );

				if ( attract.Space == ParticleSpace.Local && runner.Emitter.IsValid() )
					origin = runner.Emitter.WorldPosition + ( runner.Emitter.WorldRotation * origin );

				into[TableAttract] = origin.x;
				into[TableAttract + 1] = origin.y;
				into[TableAttract + 2] = origin.z;
				into[TableAttract + 3] = 1.0f;
				into[TableAttract + 4] = attract.Strength.Evaluate( runner.Player );
				into[TableAttract + 5] = attract.Size;
				into[TableAttract + 6] = attract.Falloff;
				into[TableAttract + 7] = attract.Invert ? 1.0f : 0.0f;
				break;
			}

			foreach ( var module in data.UpdateModules )
			{
				if ( module is not VortexModule { Enabled: true } vortex ) continue;

				var centre = vortex.Center;
				var axis = vortex.Axis.Evaluate( null, runner.Player ).Normal;

				if ( vortex.Space == ParticleSpace.Local && runner.Emitter.IsValid() )
				{
					centre = runner.Emitter.WorldPosition + ( runner.Emitter.WorldRotation * centre );
					axis = runner.Emitter.WorldRotation * axis;
				}

				into[TableVortex] = centre.x;
				into[TableVortex + 1] = centre.y;
				into[TableVortex + 2] = centre.z;
				into[TableVortex + 3] = 1.0f;
				into[TableVortex + 4] = axis.x;
				into[TableVortex + 5] = axis.y;
				into[TableVortex + 6] = axis.z;
				into[TableVortex + 7] = vortex.Strength.Evaluate( runner.Player );
				break;
			}

			foreach ( var module in data.UpdateModules )
			{
				if ( module is not FollowShapeModule { Enabled: true } follow ) continue;
				if ( runner.Player?.Definition is not { } definition || definition.Shape( follow.ShapeName ) is not { } shape ) break;

				if ( !ReferenceEquals( shape, shapeBaked ) || shapeRevision != definition.Revision )
				{
					for ( var i = 0; i < ShapeSamples; i++ )
					{
						var pose = shape.Sample( ParticleShapeSample.ByTime, 0, 1, i / (float)( ShapeSamples - 1 ) );

						shapePoses[i * 3] = pose.Position.x;
						shapePoses[( i * 3 ) + 1] = pose.Position.y;
						shapePoses[( i * 3 ) + 2] = pose.Position.z;
					}

					shapeBaked = shape;
					shapeRevision = definition.Revision;
				}

				shapePoses.CopyTo( into, TableShape );

				var frame = shape.Frame( runner.Player );
				var along = follow.Along.IsBound ? runner.Player.GetFloat( follow.Along.ParameterName ) * follow.Along.Multiplier : 1.0f;

				into[TableFollow] = 1.0f;
				into[TableFollow + 1] = follow.Loop ? 1.0f : 0.0f;
				into[TableFollow + 2] = follow.Speed.Evaluate( runner.Player );
				into[TableFollow + 3] = follow.Weight.Evaluate( runner.Player );
				into[TableFollow + 4] = frame.Position.x;
				into[TableFollow + 5] = frame.Position.y;
				into[TableFollow + 6] = frame.Position.z;
				into[TableFollow + 7] = along;
				into[TableFollow + 8] = frame.Rotation.x;
				into[TableFollow + 9] = frame.Rotation.y;
				into[TableFollow + 10] = frame.Rotation.z;
				into[TableFollow + 11] = frame.Rotation.w;
				into[TableFollow + 12] = frame.Scale.x;
				into[TableFollow + 13] = frame.Scale.y;
				into[TableFollow + 14] = frame.Scale.z;
				into[TableFollow + 15] = TableShape;

				var alongCurves = TableCurves + ( 16 * LutSamples );
				Channel( into, 8, follow.Along.Value, 9100, ref alongCurves );
				break;
			}
		}

		if ( effect.Components.Get<ParticleSpriteRenderer>() is { } renderer )
		{
			var texture = renderer.RenderTexture ?? Texture.White;
			var sequence = texture.SequenceData;
			var aspect = texture.Size.x / MathF.Max( texture.Size.y, 1.0f );

			var flags = SpriteFlags.None;
			if ( effect.SnapToFrame ) flags |= SpriteFlags.SnapToFrame;
			if ( renderer.TextureFilter == FilterMode.Point ) flags |= SpriteFlags.Pixelated;
			if ( renderer.Additive ) flags |= SpriteFlags.Additive;

			into[TableSprite] = MathF.Abs( renderer.Scale / 2.0f * renderer.WorldScale.x );
			into[TableSprite + 1] = sequence == Vector4.Zero && aspect != 1.0f ? aspect : 1.0f;
			into[TableSprite + 2] = (uint)(SpriteRenderer.BillboardMode)renderer.Alignment;
			into[TableSprite + 3] = BitConverter.Int32BitsToSingle( texture.SpriteIndex );
			into[TableSprite + 4] = BitConverter.Int32BitsToSingle( (int)flags );
			into[TableSprite + 5] = BitConverter.UInt32BitsToSingle( SpriteData.PackFogAndAlphaCutout( renderer.FogStrength, 0.001f ) );
			into[TableSprite + 6] = renderer.Lighting ? 1.0f : 0.0f;
			into[TableSprite + 7] = renderer.DepthFeather;
			into[TableSprite + 8] = BitConverter.UInt32BitsToSingle( SpriteData.PackCameraFade( renderer.CameraFadeNear, renderer.CameraFadeFar ) );
			into[TableSprite + 9] = renderer.FaceVelocity ? renderer.RotationOffset : -1.0f;
			into[TableSprite + 10] = renderer.Pivot.x;
			into[TableSprite + 11] = renderer.Pivot.y;
			into[TableSprite + 12] = sequence.x;
			into[TableSprite + 13] = sequence.y;
			into[TableSprite + 14] = sequence.z;
			into[TableSprite + 15] = sequence.w;

			into[TableBlur] = renderer.LeadingTrail ? 2.0f : 1.0f;
			into[TableBlur + 1] = renderer.BlurAmount.Remap( 0, 1, 0, 6, false );
			into[TableBlur + 2] = renderer.BlurSpacing.Remap( 0, 1, 0, 1, false );
			into[TableBlur + 3] = renderer.BlurOpacity;

			into[TableApply + 4] = renderer.Alignment is ParticleSpriteRenderer.BillboardAlignment.LookAtCamera or ParticleSpriteRenderer.BillboardAlignment.RotateToCamera ? 1.0f : 0.0f;
		}

		var tint = effect.Tint;
		into[TableTint] = tint.r;
		into[TableTint + 1] = tint.g;
		into[TableTint + 2] = tint.b;
		into[TableTint + 3] = tint.a;

		into[TableApply] = effect.ApplyColor ? 1.0f : 0.0f;
		into[TableApply + 1] = effect.ApplyAlpha ? 1.0f : 0.0f;
		into[TableApply + 2] = effect.ApplyShape ? 1.0f : 0.0f;
		into[TableApply + 3] = effect.ApplyRotation ? 1.0f : 0.0f;

		var curves = TableCurves;

		Channel( into, 0, effect.Damping, 8234, ref curves );
		Channel( into, 1, effect.Brightness, 4626, ref curves );
		Channel( into, 2, effect.Alpha, 8525, ref curves );
		Channel( into, 3, effect.Scale, 6211, ref curves );
		Channel( into, 4, effect.Stretch, 62415, ref curves );
		Channel( into, 5, effect.Pitch, 2363, ref curves );
		Channel( into, 6, effect.Yaw, 8762, ref curves );
		Channel( into, 7, effect.Roll, 3675, ref curves );

		//Two curves past the eight are Along's, laid with the shape above
		Colours( into, effect.Gradient, 8752, curves + ( 2 * LutSamples ) );
	}

	//A ParticleFloat into its channel: type, evaluation, two constants, where its curves are baked, and its salt
	private static void Channel( float[] into, int channel, in ParticleFloat value, int salt, ref int curves )
	{
		var at = TableChannels + ( channel * ChannelFloats );

		into[at] = (float)value.Type;
		into[at + 1] = (float)value.Evaluation;
		into[at + 2] = value.ConstantA;
		into[at + 3] = value.ConstantB;
		into[at + 6] = salt;

		if ( value.Type is not ( ParticleFloat.ValueType.Curve or ParticleFloat.ValueType.CurveRange ) ) return;

		into[at + 4] = curves;
		Bake( into, curves, value.CurveA );
		curves += LutSamples;

		into[at + 5] = curves;
		Bake( into, curves, value.CurveB );
		curves += LutSamples;
	}

	private static void Bake( float[] into, int at, in Curve curve )
	{
		for ( var i = 0; i < LutSamples; i++ ) into[at + i] = curve.Evaluate( i / (float)( LutSamples - 1 ) );
	}

	//The gradient: type, evaluation, where its colours are baked, its salt, then its two constants
	private static void Colours( float[] into, in ParticleGradient value, int salt, int at )
	{
		into[TableGradient] = (float)value.Type;
		into[TableGradient + 1] = (float)value.Evaluation;
		into[TableGradient + 2] = at;
		into[TableGradient + 3] = salt;

		var a = value.ConstantA;
		var b = value.ConstantB;

		into[TableGradient + 4] = a.r;
		into[TableGradient + 5] = a.g;
		into[TableGradient + 6] = a.b;
		into[TableGradient + 7] = a.a;
		into[TableGradient + 8] = b.r;
		into[TableGradient + 9] = b.g;
		into[TableGradient + 10] = b.b;
		into[TableGradient + 11] = b.a;

		if ( value.Type != ParticleGradient.ValueType.Gradient ) return;

		var gradient = value.GradientA;

		for ( var i = 0; i < LutSamples; i++ )
		{
			var colour = gradient.Evaluate( i / (float)( LutSamples - 1 ) );

			into[at + ( i * 4 )] = colour.r;
			into[at + ( i * 4 ) + 1] = colour.g;
			into[at + ( i * 4 ) + 2] = colour.b;
			into[at + ( i * 4 ) + 3] = colour.a;
		}
	}

	//-- the card's side -----------------------------------------------------------------------------------------------

	//Every run with work for the card, stepped in the frame before the world is drawn - see Simulate
	private static readonly List<ParticleGpuRun> Runs = new();
	private static readonly List<ParticleGpuRun> stepping = new();

	private static ulong spawnPipeline;
	private static ulong stepPipeline;
	private static ulong sortPipeline;
	private static int pipelinesBuilt = -1;
	private static long simulatedAt = -1;
	private static uint frames;

	private static bool EnsurePipelines()
	{
		if ( pipelinesBuilt == CoreShaderCache.Revision ) return spawnPipeline != 0 && stepPipeline != 0;

		ForgetPipelines();
		pipelinesBuilt = CoreShaderCache.Revision;

		var spawn = CoreShaderCache.Pass( EngineShaders.Particles, "SpawnCs", CoreShaderCompiler.Stage.Compute );
		var step = CoreShaderCache.Pass( EngineShaders.Particles, "main", CoreShaderCompiler.Stage.Compute );
		var sort = CoreShaderCache.Pass( EngineShaders.Particles, "SortCs", CoreShaderCompiler.Stage.Compute );

		spawnPipeline = spawn is null ? 0 : CoreRenderDevice.CreateCompute( spawn );
		stepPipeline = step is null ? 0 : CoreRenderDevice.CreateCompute( step );
		sortPipeline = sort is null ? 0 : CoreRenderDevice.CreateCompute( sort );

		if ( spawnPipeline == 0 || stepPipeline == 0 || sortPipeline == 0 )
			Log.Warning( $"the particle passes did not compile: {CoreRenderDevice.ShaderError}" );

		return spawnPipeline != 0 && stepPipeline != 0 && sortPipeline != 0;
	}

	/// <summary>Lets go of the passes, for a device going away or shaders compiled again.</summary>
	internal static void ForgetPipelines()
	{
		if ( spawnPipeline != 0 ) CoreRenderDevice.DestroyPipeline( spawnPipeline );
		if ( stepPipeline != 0 ) CoreRenderDevice.DestroyPipeline( stepPipeline );
		if ( sortPipeline != 0 ) CoreRenderDevice.DestroyPipeline( sortPipeline );

		spawnPipeline = stepPipeline = sortPipeline = 0;
		pipelinesBuilt = -1;
	}

	/// <summary>Whether the card can step particles here at all.</summary>
	internal static bool Possible => CoreNative.Available && !CoreRenderDevice.PortableFrame;

	//Where the view that steps them stands and looks, which a sorted effect is ordered for - the first view drawn in a turn; others take its order
	private static Vector3 eye;
	private static Vector3 looking;

	/// <summary>Steps every run that has work, once a turn of the loop, into the frame being recorded - before the world that draws them.</summary>
	internal static void Simulate( Vector3 position, Vector3 forward )
	{
		if ( simulatedAt == EngineLoop.LoopFrames ) return;
		simulatedAt = EngineLoop.LoopFrames;

		eye = position;
		looking = forward;

		lock ( Runs )
		{
			stepping.Clear();
			stepping.AddRange( Runs );
			Runs.Clear();
		}

		if ( stepping.Count == 0 || !EnsurePipelines() ) return;

		CoreRenderDevice.ZoneBegin( "particles" );

		frames++;

		foreach ( var run in stepping ) run.Dispatch();

		stepping.Clear();

		CoreRenderDevice.ZoneEnd();
	}

	private readonly ulong[] bound = new ulong[6];
	private readonly byte[] push = new byte[64];

	private void Dispatch()
	{
		bound[0] = particles.Handle;
		bound[1] = sprites.Handle;
		bound[2] = table.Handle;
		bound[3] = spawns.Handle;
		bound[4] = spawnSlots.Handle;
		bound[5] = unsorted?.Handle ?? sprites.Handle;

		foreach ( var one in bound ) if ( one == 0 ) return;

		if ( !tableWritten || !laying.AsSpan().SequenceEqual( laid ) )
		{
			CoreRenderDevice.FrameWrite( table.Handle, 0, MemoryMarshal.AsBytes( laying.AsSpan() ) );
			laying.CopyTo( laid, 0 );
			tableWritten = true;
		}

		var count = Math.Min( pending.Count, capacity );

		if ( count > 0 )
		{
			CoreRenderDevice.FrameWrite( spawns.Handle, 0, MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( pending )[..count] ) );
			CoreRenderDevice.FrameWrite( spawnSlots.Handle, 0, MemoryMarshal.AsBytes( CollectionsMarshal.AsSpan( pendingSlots )[..count] ) );
		}

		BitConverter.TryWriteBytes( push.AsSpan( 0 ), (uint)capacity );
		BitConverter.TryWriteBytes( push.AsSpan( 4 ), (uint)count );
		BitConverter.TryWriteBytes( push.AsSpan( 8 ), owed );
		BitConverter.TryWriteBytes( push.AsSpan( 12 ), frames );
		BitConverter.TryWriteBytes( push.AsSpan( 16 ), (uint)Marshal.SizeOf<SpriteData>() );

		//A float3 starts on the next sixteen bytes in the push block - see Push in particles.slang
		BitConverter.TryWriteBytes( push.AsSpan( 32 ), eye.x );
		BitConverter.TryWriteBytes( push.AsSpan( 36 ), eye.y );
		BitConverter.TryWriteBytes( push.AsSpan( 40 ), eye.z );
		BitConverter.TryWriteBytes( push.AsSpan( 44 ), unsorted is not null ? 1u : 0u );
		BitConverter.TryWriteBytes( push.AsSpan( 48 ), looking.x );
		BitConverter.TryWriteBytes( push.AsSpan( 52 ), looking.y );
		BitConverter.TryWriteBytes( push.AsSpan( 56 ), looking.z );

		if ( count > 0 ) CoreRenderDevice.Dispatch( spawnPipeline, bound, ( count + 63 ) / 64, 1, 1, push );

		CoreRenderDevice.Dispatch( stepPipeline, bound, ( capacity + 63 ) / 64, 1, 1, push );

		if ( unsorted is not null ) CoreRenderDevice.Dispatch( sortPipeline, bound, 1, 1, 1, push );

		pending.Clear();
		pendingSlots.Clear();
		owed = 0.0f;
	}

	//-- the drawing ---------------------------------------------------------------------------------------------------

	//The effect's sprites drawn as the sprite batches draw theirs, out of what the card wrote - a slot with nothing in it is a sprite of no size
	private sealed class Drawer : SceneCustomObject
	{
		private readonly ParticleGpuRun run;

		private static Material Sprite => field ??= Material.FromShader( "shaders/sprite/sprite_ps.shader" );

		internal Drawer( Scene scene, ParticleGpuRun run ) : base( scene.SceneWorld )
		{
			this.run = run;
			Flags.CastShadows = false;
		}

		public override void RenderSceneObject()
		{
			base.RenderSceneObject();

			if ( run.Alive <= 0 || !run.tableWritten || Sprite is null ) return;

			var attributes = RenderAttributes.Pool.Get();

			try
			{
				attributes.SetCombo( "D_BLEND", run.laid[TableSprite + 4] != 0.0f && ( BitConverter.SingleToInt32Bits( run.laid[TableSprite + 4] ) & (int)SpriteFlags.Additive ) != 0 ? 1 : 0 );
				attributes.SetCombo( "D_OPAQUE", 0 );
				attributes.Set( "Sprites", (GpuBuffer)run.sprites );

				Graphics.DrawIndexedInstanced( (GpuBuffer)run.indices, Sprite, run.capacity, attributes );
			}
			finally
			{
				RenderAttributes.Pool.Return( attributes );
			}
		}
	}
}

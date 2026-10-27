using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vec3 = System.Numerics.Vector3;
using Vec4 = System.Numerics.Vector4;

namespace Rinten.Bake;

//Bake ray tracing in a compute shader, through hardware ray queries where the card has them; each GPU step runs on the main
//thread, falls back to CPU
internal static class BakeGpu
{
	/// <summary>Whether the bake traces on the card where it can.</summary>
	[ConVar( "bake_gpu", ConVarFlags.Saved, Help = "Trace the baked lighting's sheet on the graphics card where it can (0 = the processor)." )]
	internal static bool Enabled { get; set; } = true;

	//Whether this machine traces on the card: a full-frame device, and the setting on.
	internal static bool Available => Enabled && CoreNative.Available && !CoreRenderDevice.PortableFrame;

	//Texels a dispatch - a thousand workgroups of the kernel's sixty-four.
	private const int TexelsAtOnce = 65536;

	//Samples a texel takes a dispatch.
	private const int SamplesAtOnce = 4;

	//Dispatches recorded a loop: enough to keep the card busy, few enough that the editor keeps drawing.
	private const int DispatchesAtOnce = 4;

	//Texels across the octahedral picture of the sky the rays that miss read.
	private const int SkySide = 64;

	private const int HeaderWords = 32;
	private const int MaterialWords = 28;
	private const int LightWords = 24;
	private const int EmitterWords = 12;

	//Floats of sums a sheet texel hands back: light, insides and straight light, the sky's escapes, the bounced and the
	//straight light's first band a colour, and the shade channels' light as it arrived and unshadowed, four each - see main
	internal const int SheetFloats = 44;

	//Floats of sums a point of the volume hands back - see bake_trace.slang's ProbeCs
	internal const int ProbeFloats = 20;

	//Each kernel walking the tree, then each through hardware rays
	private static readonly ulong[] pipelines = new ulong[4];
	private static readonly int[] pipelineBuilt = { -1, -1, -1, -1 };

	/// <summary>The kernels' defines where they trace hardware rays - see bake_trace.slang.</summary>
	internal const string RaysDefines = "#define BAKE_RAYS 1\n";

	//What a sheet pass's samples do - bake_trace.slang's Mode flags
	internal const int ModeGather = 1;
	internal const int ModeSky = 2;
	internal const int ModeAllLights = 4;
	internal const int ModeNoDirect = 8;

	//Traces texels on the GPU into float4 sums; false means fall back to CPU. Blocks per step. Cache is the last pass's
	//light over pi a texel of the sheet, four floats each, for a gathering mode to read; lights, its lit triangles
	internal static bool Trace( BakeScene scene, Vec3[] position, Vec3[] normal, Vec3[] geometric, Vec3[] across, Vec3[] down,
		System.Numerics.Vector4[] reach, int[] transmitter, List<int> texels, int samples, int mode, float[] cache, CacheLights lights, string stage,
		BakeProgress progress, CancellationToken cancel, out float[] sums )
	{
		sums = null;

		if ( !Available || texels.Count == 0 ) return false;

		//The texels, five float4s each: where, which way the shading and the face look, and its step across and down -
		//how far each way it may spread in four w's (BakeSheet.reach), and in the face's its transmitter's bits
		var inputs = new float[texels.Count * 20];

		for ( var i = 0; i < texels.Count; ++i )
		{
			var at = texels[i];
			var o = i * 20;

			Put( inputs, o, position[at] );
			Put( inputs, o + 4, normal[at] );
			Put( inputs, o + 8, geometric[at] );
			Put( inputs, o + 12, across[at] );
			Put( inputs, o + 16, down[at] );

			inputs[o + 3] = reach[at].X;
			inputs[o + 7] = reach[at].Y;
			inputs[o + 11] = BitConverter.Int32BitsToSingle( transmitter[at] );
			inputs[o + 15] = reach[at].Z;
			inputs[o + 19] = reach[at].W;
		}

		return Run( scene, Kernel.Sheet, inputs, texels.Count, samples, 0, mode, cache, lights, stage, progress, cancel, out sums );
	}

	//Traces the volume's points, each direction of across x across strata once: five float4s of sums each - what
	//arrived, its red, green and blue leans (insides in red's w), the sky's escapes - see BakeVolume.TraceOnCard
	internal static bool TraceProbes( BakeScene scene, Vec3[] points, int across, BakeProgress progress,
		CancellationToken cancel, out float[] sums )
	{
		sums = null;

		if ( !Available || points.Length == 0 ) return false;

		var inputs = new float[points.Length * 4];

		for ( var i = 0; i < points.Length; ++i ) Put( inputs, i * 4, points[i] );

		return Run( scene, Kernel.Probes, inputs, points.Length, across * across, across, 0, null, null, "Tracing the volume on the card",
			progress, cancel, out sums );
	}

	private enum Kernel { Sheet, Probes }

	//The kernel's entry, and how many floats of sums an item has.
	private static (string Entry, int Floats) Of( Kernel kernel ) => kernel == Kernel.Sheet ? ("main", SheetFloats) : ("ProbeCs", ProbeFloats);

	private static bool Run( BakeScene scene, Kernel kernel, float[] inputs, int items, int samples, int across, int mode, float[] cache,
		CacheLights lights, string stage, BakeProgress progress, CancellationToken cancel, out float[] sums )
	{
		sums = null;

		uint[] world;

		try
		{
			world = Packed( scene );
		}
		catch ( Exception e )
		{
			Log.Warning( $"baked lighting: the world would not go on the card ({e.Message}) - the processor bakes it" );
			return false;
		}

		var (_, floats) = Of( kernel );

		//Where the card has rays, the world as their structure: solid triangles first, each slot's world triangle
		var (corners, order, solid) = VkRays.On ? Soup( scene ) : (null, null, 0);

		ulong worldBuffer = 0, inputBuffer = 0, sumBuffer = 0, cacheBuffer = 0, lightBuffer = 0, orderBuffer = 0;
		VkRays.Soup traced = null;
		ulong built = 0;
		var made = false;

		OnMain( () =>
		{
			if ( corners is not null && EnsurePipeline( kernel, true ) is not 0 and var rayed && CoreGpuWork.Begin( out var own ) )
			{
				try
				{
					traced = VkFrame.RaysBuildSoup( corners, solid );
				}
				finally
				{
					CoreGpuWork.End( own );
				}

				if ( traced is not null )
				{
					orderBuffer = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( order.AsSpan() ) );
					built = rayed;
				}
			}

			if ( built == 0 || orderBuffer == 0 ) built = EnsurePipeline( kernel, false );
			if ( built == 0 ) return;

			worldBuffer = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( world.AsSpan() ) );
			inputBuffer = CoreRenderDevice.CreateBuffer( MemoryMarshal.AsBytes( inputs.AsSpan() ) );
			sumBuffer = CoreRenderDevice.CreateBuffer( new byte[items * floats * 4] );
			//The probes read neither, but the rays' order is bound after them
			cacheBuffer = CoreRenderDevice.CreateBuffer( cache is { Length: > 0 } ? MemoryMarshal.AsBytes( cache.AsSpan() ) : new byte[16] );
			lightBuffer = CoreRenderDevice.CreateBuffer( lights is { Count: > 0 } ? MemoryMarshal.AsBytes( lights.Floats.AsSpan() ) : new byte[16] );

			made = worldBuffer != 0 && inputBuffer != 0 && sumBuffer != 0 && cacheBuffer != 0 && lightBuffer != 0;
		}, CancellationToken.None );

		try
		{
			if ( !made )
			{
				Log.Warning( $"baked lighting: the card would not take the bake ({CoreRenderDevice.ShaderError}) - the processor bakes it" );
				return false;
			}

			//Every lamp is a shadow ray at every bounce, so a world of many has fewer items a dispatch
			//- no dispatch should come near what a driver waits before it calls the card hung.
			var atOnce = Math.Clamp( TexelsAtOnce / ( 1 + ( ( scene.Lights.Count + ( scene.Emitters.Length > 0 ? 1 : 0 ) ) / 4 ) ), 4096, TexelsAtOnce );
			var chunks = ( items + atOnce - 1 ) / atOnce;
			var passes = ( samples + SamplesAtOnce - 1 ) / SamplesAtOnce;
			var jobs = new List<(int First, int Count, int Base, int Samples)>( chunks * passes );

			//Every item's first samples before anyone's later ones: a bake stopped half way is evenly half done
			for ( var pass = 0; pass < passes; ++pass )
			{
				var from = pass * SamplesAtOnce;
				var take = Math.Min( SamplesAtOnce, samples - from );

				for ( var chunk = 0; chunk < chunks; ++chunk )
				{
					var first = chunk * atOnce;
					jobs.Add( (first, Math.Min( atOnce, items - first ), from, take) );
				}
			}

			progress.Begin( stage, jobs.Count );

			var rays = traced is not null && orderBuffer != 0;

			var seed = (uint)System.Environment.TickCount;

			for ( var next = 0; next < jobs.Count; next += DispatchesAtOnce )
			{
				cancel.ThrowIfCancellationRequested();

				var start = next;
				var ok = false;

				OnMain( () =>
				{
					if ( !CoreGpuWork.Begin( out var own ) ) return;

					try
					{
						ok = true;

						for ( var j = start; j < Math.Min( start + DispatchesAtOnce, jobs.Count ) && ok; ++j )
						{
							var job = jobs[j];

							Span<uint> push = stackalloc uint[32];
							push.Clear();
							push[0] = (uint)job.First;
							push[1] = (uint)job.Count;
							push[2] = (uint)job.Samples;
							push[3] = (uint)job.Base;
							push[4] = seed;
							push[5] = (uint)across;
							push[6] = (uint)mode;
							push[7] = (uint)( lights?.Count ?? 0 );
							push[8] = (uint)scene.MaterialOf.Length;

							if ( rays )
							{
								push[9] = (uint)traced.Address;
								push[10] = (uint)( traced.Address >> 32 );
								push[11] = (uint)solid;
							}

							Span<ulong> buffers = stackalloc ulong[6] { worldBuffer, inputBuffer, sumBuffer, cacheBuffer, lightBuffer, orderBuffer };

							ok = CoreRenderDevice.Dispatch( built, rays ? buffers : kernel == Kernel.Sheet ? buffers[..5] : buffers[..3],
								( job.Count + 63 ) / 64, 1, 1, MemoryMarshal.AsBytes( push ) );
						}
					}
					finally
					{
						CoreGpuWork.End( own );
					}
				}, cancel );

				if ( !ok )
				{
					Log.Warning( $"baked lighting: a dispatch was refused ({CoreRenderDevice.Description}) - the processor bakes it" );
					return false;
				}

				for ( var j = start; j < Math.Min( start + DispatchesAtOnce, jobs.Count ); ++j ) progress.Step();
			}

			//And back, once the card has finished everything asked of it. The event is not disposed: a
			//bake stopped while it waits still has a read coming, which sets it on the main thread later.
			var back = new float[items * floats];
			var arrived = new ManualResetEventSlim();
			var asked = false;

			OnMain( () =>
			{
				asked = CoreReadbacks.Ask<float>( sumBuffer, 0, (long)back.Length * 4, span =>
				{
					span[..Math.Min( span.Length, back.Length )].CopyTo( back );
					arrived.Set();
				} );
			}, CancellationToken.None );

			if ( !asked || !arrived.Wait( TimeSpan.FromMinutes( 2 ), cancel ) )
			{
				Log.Warning( "baked lighting: the card's sums never came back - the processor bakes it" );
				return false;
			}

			sums = back;
			return true;
		}
		catch ( OperationCanceledException )
		{
			throw;
		}
		catch ( Exception e )
		{
			Log.Warning( $"baked lighting: the card failed part way ({e.Message}) - the processor bakes it" );
			return false;
		}
		finally
		{
			OnMain( () =>
			{
				if ( worldBuffer != 0 ) CoreRenderDevice.DestroyBuffer( worldBuffer );
				if ( inputBuffer != 0 ) CoreRenderDevice.DestroyBuffer( inputBuffer );
				if ( sumBuffer != 0 ) CoreRenderDevice.DestroyBuffer( sumBuffer );
				if ( cacheBuffer != 0 ) CoreRenderDevice.DestroyBuffer( cacheBuffer );
				if ( lightBuffer != 0 ) CoreRenderDevice.DestroyBuffer( lightBuffer );
				if ( orderBuffer != 0 ) CoreRenderDevice.DestroyBuffer( orderBuffer );

				VkRays.RetireSoup( traced );
			}, CancellationToken.None, wait: false );
		}
	}

	//Runs on the main thread and waits for it - the card is only reached from there.
	//Not waited for where wait is false: letting go of the buffers need hold nobody up.
	internal static void OnMain( Action action, CancellationToken cancel, bool wait = true )
	{
		if ( ThreadSafe.IsMainThread )
		{
			action();
			return;
		}

		//Not disposed: the main thread may set it after a caller that stopped waiting has gone
		var done = new ManualResetEventSlim();
		Exception failed = null;

		MainThread.Queue( () =>
		{
			try { action(); }
			catch ( Exception e ) { failed = e; }
			finally { done.Set(); }
		} );

		if ( !wait ) return;

		done.Wait( cancel );

		if ( failed is not null ) throw new InvalidOperationException( "the bake's work on the card failed", failed );
	}

	//A kernel's pipeline, built the first time and again when the shaders change - nought where it will not build.
	private static ulong EnsurePipeline( Kernel kernel, bool rays )
	{
		var i = ( (int)kernel * 2 ) + ( rays ? 1 : 0 );

		if ( pipelines[i] != 0 && pipelineBuilt[i] == CoreShaderCache.Revision ) return pipelines[i];

		if ( pipelines[i] != 0 ) CoreRenderDevice.DestroyPipeline( pipelines[i] );

		pipelineBuilt[i] = CoreShaderCache.Revision;

		var code = CoreShaderCache.Pass( EngineShaders.BakeTrace, Of( kernel ).Entry, CoreShaderCompiler.Stage.Compute, rays ? RaysDefines : null );
		pipelines[i] = code is null ? 0 : CoreRenderDevice.CreateCompute( code );

		return pipelines[i];
	}

	//The world's triangles for the rays' structure, three corners each: the solid ones, then those a ray may pass
	private static (float[] Corners, uint[] Order, int Solid) Soup( BakeScene scene )
	{
		var triangles = scene.MaterialOf.Length;
		var order = new uint[triangles];
		var solid = 0;

		for ( var t = 0; t < triangles; ++t )
			if ( scene.ThroughMode( t ) == 0 ) order[solid++] = (uint)t;

		for ( int t = 0, at = solid; t < triangles; ++t )
			if ( scene.ThroughMode( t ) != 0 ) order[at++] = (uint)t;

		var corners = new float[triangles * 9];

		for ( var i = 0; i < triangles; ++i )
		{
			for ( var k = 0; k < 3; ++k )
				Put( corners, ( i * 9 ) + ( k * 3 ), scene.Positions[scene.Corners[( (int)order[i] * 3 ) + k]] );
		}

		return triangles == 0 ? (null, null, 0) : (corners, order, solid);
	}

	//-- the world, in words - see bake_trace.slang's header ----------------------

	//The last world packed, for the volume after the sheet: the same scene in the same pass is the same words
	private sealed record PackedWorld( int Pass, int Side, uint[] Words );

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BakeScene, PackedWorld> packed = new();

	private static uint[] Packed( BakeScene scene )
	{
		if ( packed.TryGetValue( scene, out var held ) && held.Pass == scene.Pass && held.Side == scene.AtlasSide ) return held.Words;

		var words = Pack( scene );
		packed.AddOrUpdate( scene, new PackedWorld( scene.Pass, scene.AtlasSide, words ) );

		return words;
	}

	//Everything a ray asks of the world, in one run of words: the tree, the triangles in its order and the world's,
	//the vertices' normals, coordinates and colours, the materials and their pictures, the lamps, and the sky.
	private static uint[] Pack( BakeScene scene )
	{
		var bvh = scene.Bvh;

		var triangles = scene.MaterialOf.Length;
		var vertices = scene.Normals.Length;

		//The pictures the materials read, once each.
		var pictures = new List<BakePicture>();
		var pictureAt = new Dictionary<BakePicture, int>();
		var pictureWords = 0;

		foreach ( var material in scene.Materials )
		{
			foreach ( var picture in new[] { material.Colour, material.EmissionMap, material.RoughnessMap, material.MetalnessMap } )
			{
				if ( picture is null || pictureAt.ContainsKey( picture ) ) continue;

				pictureAt[picture] = pictureWords;
				pictures.Add( picture );
				pictureWords += picture.Width * picture.Height * 4;
			}
		}

		var nodesAt = HeaderWords;
		var treeAt = nodesAt + ( bvh.NodeCount * 8 );
		var cornersAt = treeAt + ( bvh.TriangleCount * 10 );
		var facingAt = cornersAt + ( triangles * 3 );
		var triangleAt = facingAt + ( triangles * 3 );
		var normalsAt = triangleAt + ( triangles * 4 );
		var uvsAt = normalsAt + ( vertices * 3 );
		var coloursAt = uvsAt + ( vertices * 2 );
		var materialsAt = coloursAt + ( vertices * 3 );
		var picturesAt = materialsAt + ( scene.Materials.Count * MaterialWords );
		var lightsAt = picturesAt + pictureWords;
		//A glow group's pass has no lamps; its sky is black - see BakeScene.UsePass
		var lights = scene.Pass == 0 ? scene.Lights.Count : 0;
		var grouped = Array.Exists( scene.GroupOf, g => g != 0 );

		var skyAt = lightsAt + ( lights * LightWords );
		var emittersAt = skyAt + ( SkySide * SkySide * 3 );
		var glowAt = emittersAt + ( scene.Emitters.Length * EmitterWords );
		var groupsAt = glowAt + ( scene.Emitters.Length > 0 ? triangles : 0 );
		var atlasAt = groupsAt + ( grouped ? triangles : 0 );
		var atlas = scene.AtlasSide > 0 && scene.Receivers.Count > 0;
		var mirrorsAt = atlasAt + ( atlas ? vertices * 2 : 0 );
		var mirrorOfAt = mirrorsAt + ( scene.Mirrors.Length * 8 );
		var total = mirrorOfAt + ( scene.Mirrors.Length > 0 ? triangles : 0 );

		var w = new uint[total];

		w[0] = (uint)nodesAt;
		w[1] = (uint)treeAt;
		w[2] = (uint)cornersAt;
		w[3] = (uint)facingAt;
		w[4] = (uint)triangleAt;
		w[5] = (uint)normalsAt;
		w[6] = (uint)uvsAt;
		w[7] = (uint)coloursAt;
		w[8] = (uint)materialsAt;
		w[9] = (uint)picturesAt;
		w[10] = (uint)lightsAt;
		w[11] = (uint)lights;
		w[12] = (uint)skyAt;
		w[13] = SkySide;
		w[14] = F( scene.SunCosine );
		w[15] = (uint)Math.Max( scene.Bounces, 0 );
		w[16] = F( scene.AlbedoBoost );
		w[17] = (uint)bvh.NodeCount;
		w[18] = (uint)emittersAt;
		w[19] = (uint)scene.Emitters.Length;
		w[20] = (uint)glowAt;
		w[21] = (uint)scene.Pass;
		w[22] = grouped ? (uint)groupsAt : 0u;
		w[23] = atlas ? (uint)atlasAt : 0u;
		w[24] = (uint)scene.AtlasSide;
		w[25] = (uint)mirrorsAt;
		w[26] = (uint)scene.Mirrors.Length;
		w[27] = (uint)mirrorOfAt;

		for ( var m = 0; m < scene.Mirrors.Length; ++m )
		{
			var mirror = scene.Mirrors[m];
			var at = mirrorsAt + ( m * 8 );

			Put( w, at, mirror.Normal );
			w[at + 3] = F( mirror.Offset );
			Put( w, at + 4, mirror.Reflectance );
			w[at + 7] = mirror.Dielectric ? 1u : 0u;
		}

		if ( scene.Mirrors.Length > 0 )
			for ( var t = 0; t < triangles; ++t ) w[mirrorOfAt + t] = (uint)scene.MirrorOf[t];

		//Each vertex's spot on the sheet, for a gathering ray to read the last pass's light at; off it, negative
		if ( atlas )
		{
			w.AsSpan( atlasAt, vertices * 2 ).Fill( F( -1.0f ) );

			foreach ( var receiver in scene.Receivers )
			{
				for ( var t = receiver.FirstTriangle; t < receiver.FirstTriangle + receiver.TriangleCount; ++t )
				{
					for ( var k = 0; k < 3; ++k )
					{
						var v = scene.Corners[( t * 3 ) + k];
						var uv = System.Numerics.Vector2.Clamp( scene.Uv1[v], System.Numerics.Vector2.Zero, System.Numerics.Vector2.One );

						w[atlasAt + ( v * 2 )] = F( ( ( uv.X * receiver.Side ) + receiver.X ) / scene.AtlasSide );
						w[atlasAt + ( v * 2 ) + 1] = F( ( ( uv.Y * receiver.Side ) + receiver.Y ) / scene.AtlasSide );
					}
				}
			}
		}

		if ( grouped )
			for ( var t = 0; t < triangles; ++t ) w[groupsAt + t] = (uint)scene.GroupOf[t];

		//Glowing triangles: world triangle, power summed so far, a corner and two edges; then GlowDensity per triangle
		for ( var i = 0; i < scene.Emitters.Length; ++i )
		{
			var t = scene.Emitters[i];
			var at = emittersAt + ( i * EmitterWords );
			var a = scene.Positions[scene.Corners[t * 3]];

			w[at] = (uint)t;
			w[at + 1] = F( scene.EmitterSum[i] );
			Put( w, at + 2, a );
			Put( w, at + 5, scene.Positions[scene.Corners[( t * 3 ) + 1]] - a );
			Put( w, at + 8, scene.Positions[scene.Corners[( t * 3 ) + 2]] - a );
		}

		if ( scene.Emitters.Length > 0 )
			for ( var t = 0; t < triangles; ++t ) w[glowAt + t] = F( scene.GlowDensity[t] );

		bvh.PackNodes( w.AsSpan( nodesAt, bvh.NodeCount * 8 ) );
		bvh.PackTriangles( w.AsSpan( treeAt, bvh.TriangleCount * 10 ) );

		for ( var t = 0; t < triangles; ++t )
		{
			w[cornersAt + ( t * 3 )] = (uint)scene.Corners[t * 3];
			w[cornersAt + ( t * 3 ) + 1] = (uint)scene.Corners[( t * 3 ) + 1];
			w[cornersAt + ( t * 3 ) + 2] = (uint)scene.Corners[( t * 3 ) + 2];

			Put( w, facingAt + ( t * 3 ), scene.Facing[t] );

			w[triangleAt + ( t * 4 )] = (uint)scene.MaterialOf[t] | ( (uint)scene.ThroughMode( t ) << 28 );
			Put( w, triangleAt + ( t * 4 ) + 1, scene.TintOf[t] );
		}

		for ( var v = 0; v < vertices; ++v )
		{
			Put( w, normalsAt + ( v * 3 ), scene.Normals[v] );

			var uv = v < scene.Uv0.Length ? scene.Uv0[v] : default;
			w[uvsAt + ( v * 2 )] = F( uv.X );
			w[uvsAt + ( v * 2 ) + 1] = F( uv.Y );

			Put( w, coloursAt + ( v * 3 ), v < scene.Colours.Length ? scene.Colours[v] : Vec3.One );
		}

		for ( var m = 0; m < scene.Materials.Count; ++m )
		{
			var material = scene.Materials[m];
			var at = materialsAt + ( m * MaterialWords );

			Put( w, at, material.Tint );
			w[at + 3] = F( material.Alpha );
			w[at + 4] = F( material.Metalness );
			Put( w, at + 5, material.Emission );
			w[at + 8] = F( material.Cutoff );

			var flags = 0u;
			if ( material.Masked ) flags |= 1;
			if ( material.Translucent ) flags |= 2;
			if ( material.TwoSided ) flags |= 4;
			if ( material.EmissionUsesAlbedo ) flags |= 8;
			if ( material.Colour is not null ) flags |= 16;
			if ( material.EmissionMap is not null ) flags |= 32;
			if ( material.RoughnessMap is not null ) flags |= 64;
			if ( material.MetalnessMap is not null ) flags |= 128;
			w[at + 9] = flags;
			w[at + 16] = F( material.Roughness );
			w[at + 23] = F( material.Reflectance );
			Put( w, at + 24, material.Transmits );

			if ( material.Colour is { } colour )
			{
				w[at + 10] = (uint)pictureAt[colour];
				w[at + 11] = (uint)colour.Width;
				w[at + 12] = (uint)colour.Height;
			}

			if ( material.EmissionMap is { } emission )
			{
				w[at + 13] = (uint)pictureAt[emission];
				w[at + 14] = (uint)emission.Width;
				w[at + 15] = (uint)emission.Height;
			}

			if ( material.RoughnessMap is { } rough )
			{
				w[at + 17] = (uint)pictureAt[rough];
				w[at + 18] = (uint)rough.Width;
				w[at + 19] = (uint)rough.Height;
			}

			if ( material.MetalnessMap is { } metal )
			{
				w[at + 20] = (uint)pictureAt[metal];
				w[at + 21] = (uint)metal.Width;
				w[at + 22] = (uint)metal.Height;
			}
		}

		foreach ( var picture in pictures )
		{
			var at = picturesAt + pictureAt[picture];
			var texels = picture.Texels;

			for ( var i = 0; i < texels.Length; ++i )
				Put( w, at + ( i * 4 ), texels[i] );
		}

		for ( var i = 0; i < lights; ++i )
		{
			var light = scene.Lights[i];
			var at = lightsAt + ( i * LightWords );

			w[at] = (uint)light.Kind;
			Put( w, at + 1, light.Position );
			w[at + 4] = F( light.Range );
			Put( w, at + 5, light.Colour );
			Put( w, at + 8, light.Forward );
			w[at + 11] = F( light.CosInner );
			w[at + 12] = F( light.CosOuter );
			w[at + 13] = F( light.Falloff );
			w[at + 14] = F( light.Radius );
			w[at + 15] = ( light.Shadows ? 1u : 0u ) | ( light.Whole ? 2u : 0u );
			w[at + 16] = (uint)( light.Shade + 1 );

			w[at + 17] = (uint)light.Shape;
			w[at + 18] = F( light.HalfWidth );
			w[at + 19] = F( light.HalfHeight );
			Put( w, at + 20, light.Right );
		}

		//The sky as the rays that miss see it - BakeScene.Environment, which is a function of direction alone, laid
		//over an octahedron unfolded into a square. The kernel reads it back the same way; see its Environment.
		for ( var y = 0; y < SkySide; ++y )
		{
			for ( var x = 0; x < SkySide; ++x )
			{
				var ox = ( ( ( x + 0.5f ) / SkySide ) * 2.0f ) - 1.0f;
				var oy = ( ( ( y + 0.5f ) / SkySide ) * 2.0f ) - 1.0f;

				var up = 1.0f - MathF.Abs( ox ) - MathF.Abs( oy );

				if ( up < 0.0f )
				{
					var fx = ( 1.0f - MathF.Abs( oy ) ) * ( ox >= 0.0f ? 1.0f : -1.0f );
					var fy = ( 1.0f - MathF.Abs( ox ) ) * ( oy >= 0.0f ? 1.0f : -1.0f );
					ox = fx;
					oy = fy;
				}

				var direction = Vec3.Normalize( new Vec3( ox, up, oy ) );

				Put( w, skyAt + ( ( ( y * SkySide ) + x ) * 3 ), scene.Environment( direction ) );
			}
		}

		return w;
	}

	private static uint F( float value ) => BitConverter.SingleToUInt32Bits( value );

	private static void Put( uint[] into, int at, Vec3 v )
	{
		into[at] = F( v.X );
		into[at + 1] = F( v.Y );
		into[at + 2] = F( v.Z );
	}

	private static void Put( uint[] into, int at, Vec4 v )
	{
		into[at] = F( v.X );
		into[at + 1] = F( v.Y );
		into[at + 2] = F( v.Z );
		into[at + 3] = F( v.W );
	}

	private static void Put( float[] into, int at, Vec3 v )
	{
		into[at] = v.X;
		into[at + 1] = v.Y;
		into[at + 2] = v.Z;
	}
}

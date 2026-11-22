using System;
using System.Collections.Generic;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Rinten;

namespace Benchmarks.Collider;

//The editor's gizmo walk for a selected high-poly model collider. The old path walked every triangle and built
//six gizmo vertices for it on every recorded frame; the new one keeps the line mesh on the card and adds one draw.
//`dotnet run -c Release -- gizmo` prints both, with the one-time build of the wire geometry, on a real device.
[MemoryDiagnoser]
public class ModelColliderGizmoBenchmarks
{
	[Params( 50_000, 250_000, 1_000_000 )]
	public int Triangles { get; set; }

	public static IConfig Config => ManualConfig
		.Create( DefaultConfig.Instance )
		.WithOptions( ConfigOptions.JoinSummary | ConfigOptions.DisableLogFile )
		.AddJob( Job.ShortRun );

	private Gizmo.Instance instance;
	private Rinten.PhysicsGroupDescription.BodyPart.MeshPart mesh;

	[GlobalSetup]
	public void Setup()
	{
		ThreadSafe.MarkMainThread();

		CoreRenderDevice.Initialize();
		instance = new Gizmo.Instance();
		mesh = Build( Triangles );
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		mesh?.Dispose();
		instance?.Dispose();
	}

	//What ModelCollider did before: every triangle as its three lines, recorded afresh every frame.
	[Benchmark( Baseline = true )]
	public int OldPath()
	{
		using ( instance.RecordDraws() )
		using ( instance.Push() )
		{
			Gizmo.Draw.Color = Gizmo.Colors.Green;
			Gizmo.Draw.LineTriangles( mesh.GetTriangles() );
		}

		return instance.Draws.Shapes.Count == 0 ? 0 : instance.Draws.Shapes[0].Count;
	}

	//What it does now: one wire item naming the buffers made the first time it was drawn.
	[Benchmark]
	public int NewPath()
	{
		using ( instance.RecordDraws() )
		using ( instance.Push() )
		{
			Gizmo.Draw.Color = Gizmo.Colors.Green;
			Gizmo.Draw.LineMesh( mesh );
		}

		return instance.Draws.Wires.Count;
	}

	//Runs the same pair by hand, because a BenchmarkDotNet run needs its own build and this has to say whether
	//the device-backed path actually ran rather than quietly falling back to the old one.
	public static void Run( int[] triangleCounts )
	{
		ThreadSafe.MarkMainThread();

		if ( !CoreRenderDevice.Initialize() )
		{
			Console.WriteLine( $"No graphics device here, so the cached path cannot be measured: {CoreRenderDevice.Description}" );
			return;
		}

		Console.WriteLine( $"Device: {CoreRenderDevice.Description}" );
		Console.WriteLine();
		Console.WriteLine( "  triangles   old gizmo walk   new gizmo walk   speedup   wire build" );
		Console.WriteLine( "  ---------   --------------   --------------   -------   ----------" );

		using var instance = new Gizmo.Instance();

		void Record( Action draw )
		{
			using ( instance.RecordDraws() )
			using ( instance.Push() )
			{
				Gizmo.Draw.Color = Gizmo.Colors.Green;
				draw();
			}
		}

		//The first buffer a device makes is slower than the rest - the upload ring and the memory type are settled
		//on it. A small one, thrown away, keeps that out of the per-mesh build times.
		var warm = Build( 4_000 );
		Record( () => Gizmo.Draw.LineMesh( warm ) );
		warm.Dispose();

		foreach ( var wanted in triangleCounts )
		{
			var (points, indices) = Geometry( wanted );

			//The line mesh is built the first time it is drawn and kept: three fresh parts, the median of them.
			var builds = new double[3];

			for ( var i = 0; i < builds.Length; i++ )
			{
				var fresh = new Rinten.PhysicsGroupDescription.BodyPart.MeshPart( points, indices, null, null );
				var start = Stopwatch.GetTimestamp();
				Record( () => Gizmo.Draw.LineMesh( fresh ) );
				builds[i] = Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
				fresh.Dispose();
			}

			Array.Sort( builds );
			var buildMs = builds[builds.Length / 2];

			var mesh = new Rinten.PhysicsGroupDescription.BodyPart.MeshPart( points, indices, null, null );

			Record( () => Gizmo.Draw.LineMesh( mesh ) );

			if ( instance.Draws.Wires.Count == 0 )
			{
				Console.WriteLine( $"  {wanted,9:N0}   no wire item: the mesh fell back to the old path" );
				mesh.Dispose();
				continue;
			}

			double Old( int runs ) => Median( runs, () => Record( () => Gizmo.Draw.LineTriangles( mesh.GetTriangles() ) ) );
			double New( int runs ) => Median( runs, () => Record( () => Gizmo.Draw.LineMesh( mesh ) ) );

			Old( 3 );
			New( 3 );

			var oldMs = Old( 15 );
			var newMs = New( 15 );

			Console.WriteLine( $"  {wanted,9:N0}   {oldMs,12:F3} ms   {newMs,12:F3} ms   {oldMs / newMs,6:F0}x   {buildMs,7:F1} ms" );

			mesh.Dispose();
		}

		Console.WriteLine();
		Console.WriteLine( "Old is Gizmo.Draw.LineTriangles( mesh.GetTriangles() ); new is Gizmo.Draw.LineMesh( mesh )." );
		Console.WriteLine( "Both include the per-frame gizmo record/push the view does; neither includes the GPU draw itself." );
	}

	static double Median( int runs, Action frame )
	{
		var times = new double[runs];

		for ( var i = 0; i < runs; i++ )
		{
			var start = Stopwatch.GetTimestamp();
			frame();
			times[i] = Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
		}

		Array.Sort( times );
		return times[runs / 2];
	}

	//A level-sized grid, two triangles a cell: the worst case for the old path and the ordinary one for a collider.
	internal static Rinten.PhysicsGroupDescription.BodyPart.MeshPart Build( int triangles )
	{
		var (points, indices) = Geometry( triangles );

		return new Rinten.PhysicsGroupDescription.BodyPart.MeshPart( points, indices, null, null );
	}

	//The points and triangle indices on their own, so a timing can keep the asset work out of what it measures.
	internal static (Vector3[] Points, int[] Indices) Geometry( int triangles )
	{
		var cells = Math.Max( 1, triangles / 2 );
		var side = (int)Math.Ceiling( Math.Sqrt( cells ) );
		var width = side + 1;

		var points = new Vector3[width * width];

		for ( var z = 0; z < width; z++ )
		{
			for ( var x = 0; x < width; x++ )
			{
				var height = MathF.Sin( x * 0.17f ) * MathF.Cos( z * 0.11f ) * 0.5f;
				points[( z * width ) + x] = new Vector3( x * 0.05f, height, z * 0.05f );
			}
		}

		var indices = new List<int>( side * side * 6 );

		for ( var z = 0; z < side; z++ )
		{
			for ( var x = 0; x < side; x++ )
			{
				var a = ( z * width ) + x;
				var b = a + 1;
				var c = a + width;
				var d = c + 1;

				indices.Add( a );
				indices.Add( c );
				indices.Add( b );
				indices.Add( b );
				indices.Add( c );
				indices.Add( d );
			}
		}

		return (points, indices.ToArray());
	}
}

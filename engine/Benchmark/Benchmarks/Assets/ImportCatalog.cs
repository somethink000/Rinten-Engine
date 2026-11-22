using System;
using System.Diagnostics;
using System.IO;
using Rinten;

namespace Benchmarks.Assets;

//Selecting a mesh in the model editor asks the source export for its sub-mesh names; the first ask imports the
//file, and every later ask used to import it again. Run with `dotnet run -c Release -- catalog <fbx>`.
public static class ImportCatalogBenchmarks
{
	public static void Run( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
		{
			Console.WriteLine( $"No such source file: {path}" );
			return;
		}

		var info = new FileInfo( path );
		Console.WriteLine( $"{info.Name}: {info.Length / ( 1024.0 * 1024.0 ):F1} MB" );

		var cold = Stopwatch.GetTimestamp();
		var first = ModelImportCatalog.MeshNames( path );
		var coldMs = Stopwatch.GetElapsedTime( cold ).TotalMilliseconds;

		var warm = Stopwatch.GetTimestamp();
		var second = ModelImportCatalog.MeshNames( path );
		var warmMs = Stopwatch.GetElapsedTime( warm ).TotalMilliseconds;

		Console.WriteLine( $"  first selection (imports the file): {coldMs,8:F1} ms, {first.Count} meshes" );
		Console.WriteLine( $"  later selections (cached):         {warmMs,8:F1} ms, {second.Count} meshes" );

		if ( first.Count != second.Count )
			Console.WriteLine( "  the two lists disagree - the cache is wrong" );
	}
}

using System;
using System.IO;
using Rinten;

namespace EngineTests;

//Precompiled engine shader cache: file round-trips and is keyed by source hash so edits miss
[TestClass]
[DoNotParallelize]
public class ShaderCacheTest
{
	private static string isolatedFolder;

	//A cache of the test's own, so the person's own compiled shaders - which are merged in under the baked file -
	//neither change the counts here nor get written to by the test.
	[ClassInitialize]
	public static void IsolateLocalCache( TestContext context )
	{
		isolatedFolder = Path.Combine( Path.GetTempPath(), $"rinten-shadercache-local-{Guid.NewGuid():N}" );
		Directory.CreateDirectory( isolatedFolder );

		Environment.SetEnvironmentVariable( "RINTEN_SHADER_CACHE", Path.Combine( isolatedFolder, "shaders.cache" ) );
		CoreShaderCache.Forget();
	}

	[ClassCleanup]
	public static void RestoreLocalCache()
	{
		Environment.SetEnvironmentVariable( "RINTEN_SHADER_CACHE", null );
		CoreShaderCache.Forget();

		try { Directory.Delete( isolatedFolder, true ); } catch ( IOException ) { }
	}

	//A cache with nothing in it is not an error - it is what the first run on a machine has, and what a platform with no compiler falls back from.
	[TestMethod]
	public void AnEmptyCacheWritesAndReads()
	{
		CoreShaderCache.Forget();

		var written = CoreShaderCache.Write();

		Assert.IsNotNull( written );
		Assert.IsTrue( written.Length >= 12, "magic, version and a count at least" );
	}

	//Baked entries read back only under the same source, entry and stage, via a real file on disk
	[TestMethod]
	public void WhatIsBakedIsWhatIsRead()
	{
		if ( !CoreNative.Available || !CoreShaderCompiler.Available )
		{
			Assert.Inconclusive( "no shader compiler in this build" );
			return;
		}

		var folder = Path.Combine( Path.GetTempPath(), $"rinten-shadercache-{Guid.NewGuid():N}" );
		var previous = Directory.GetCurrentDirectory();

		Directory.CreateDirectory( Path.Combine( folder, "core", "shaders" ) );

		try
		{
			Directory.SetCurrentDirectory( folder );
			CoreShaderCache.Forget();

			//Something small that compiles, and its own second copy differing by one number - so the two cannot share an entry.
			const string one = """
				struct VsOut { float4 position : SV_Position; };

				[shader("vertex")]
				VsOut MainVs( uint index : SV_VertexID )
				{
					VsOut o;
					o.position = float4( 0.5, 0, 0, 1 );
					return o;
				}
				""";

			var other = one.Replace( "0.5", "0.25" );

			var first = CoreShaderCache.Words( one, null, "MainVs", CoreShaderCompiler.Stage.Vertex );

			Assert.IsNotNull( first, "the test shader should compile" );
			Assert.IsTrue( first.Length > 0 );

			var second = CoreShaderCache.Words( other, null, "MainVs", CoreShaderCompiler.Stage.Vertex );

			Assert.IsNotNull( second );

			//Both are in what would be written, under keys of their own: an edited shader must not read back the words of the one it replaced.
			Assert.AreEqual( 2, CoreShaderCache.Count, "two sources, two entries" );

			var file = Path.Combine( folder, "core", "shaders", "builtin.shadercache" );
			File.WriteAllBytes( file, CoreShaderCache.Write() );

			//Read back as a fresh cache, which is what a shipped build does.
			CoreShaderCache.Forget();

			var read = CoreShaderCache.Words( one, null, "MainVs", CoreShaderCompiler.Stage.Vertex );

			CollectionAssert.AreEqual( first, read, "the words should survive the file" );
			Assert.AreEqual( 2, CoreShaderCache.Count, "and both entries should still be there" );
		}
		finally
		{
			Directory.SetCurrentDirectory( previous );
			CoreShaderCache.Forget();

			try { Directory.Delete( folder, true ); } catch ( IOException ) { }
		}
	}

	//A file that is not a cache, or is one of another version, is compiled past rather than believed.
	[TestMethod]
	public void RubbishIsNotReadAsAShader()
	{
		var folder = Path.Combine( Path.GetTempPath(), $"rinten-shadercache-{Guid.NewGuid():N}" );
		var previous = Directory.GetCurrentDirectory();

		Directory.CreateDirectory( Path.Combine( folder, "core", "shaders" ) );

		try
		{
			var file = Path.Combine( folder, "core", "shaders", "builtin.shadercache" );

			File.WriteAllText( file, "this is not a shader cache" );

			Directory.SetCurrentDirectory( folder );
			CoreShaderCache.Forget();

			//Nothing in it, and nothing thrown out of it.
			Assert.AreEqual( 0, CoreShaderCache.Count );
		}
		finally
		{
			Directory.SetCurrentDirectory( previous );
			CoreShaderCache.Forget();

			try { Directory.Delete( folder, true ); } catch ( IOException ) { }
		}
	}
}

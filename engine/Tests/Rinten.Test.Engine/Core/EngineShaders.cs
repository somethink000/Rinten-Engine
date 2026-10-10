using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EngineTests;

//Shipped shaders and materials exist and build; not parallel, the compiler needs the engine thread
[TestClass]
[DoNotParallelize]
public class EngineShadersTest
{
	//Every shader the engine ships, each whole in its own file.
	private static readonly string[] Shaders =
	{
		//The surfaces a material may wear, beside each other.
		"complex.shader", "foliage.shader", "glass.shader", "terrain_layer.shader",

		//And the engine's own, under its own folder
		"engine/unlit.shader", "engine/vertex_color.shader",
		"engine/sky.shader", "engine/terrain.shader",

		//Sprites, which is every sprite renderer and every particle drawn as a picture. Listed here for the reason this whole test exists:
		//the old one was deleted with the rest of the legacy vfx pipeline, nothing failed, and every sprite in the engine was invisible for weeks.
		"sprite/sprite_ps.shader",
	};

	//Legacy surface names and the shader each maps to via CoreShaderFile.Aliases
	private static readonly (string Asked, string Answers)[] Aliases =
	{
		("standard.shader", "complex.shader"),
		("simple.shader", "complex.shader"),
		("generic.shader", "complex.shader"),
		("vr_standard.shader", "complex.shader"),
		("blendable.shader", "complex.shader"),
		("sprite.shader", "engine/unlit.shader"),
		("spritecard.shader", "engine/unlit.shader"),

		//And the names the engine's own shaders had before they moved under a folder
		//of their own, so a material written against the old tree keeps finding them.
		("unlit.shader", "engine/unlit.shader"),
		("vertex_color.shader", "engine/vertex_color.shader"),
		("sky.shader", "engine/sky.shader"),
		("terrain.shader", "engine/terrain.shader"),
	};

	private static string Directory => Path.Combine( "addons", "base", "Assets", "shaders" );

	[TestMethod]
	public void EveryShaderWeShipCompiles()
	{
		//The compiler is native and wants the thread the engine was brought up on. Nothing about a shader needs a running engine beyond that.
		ThreadSafe.MarkMainThread();

		if ( !CoreNative.Available || !CoreShaderCompiler.Available )
			Assert.Inconclusive( "no slang compiler beside the engine" );

		var broken = new List<string>();

		foreach ( var name in Shaders )
		{
			var path = Path.Combine( Directory, name );

			if ( !File.Exists( path ) )
			{
				broken.Add( $"{name}: not there" );
				continue;
			}

			if ( CoreShaderFile.Compile( path ) is null )
				broken.Add( $"{name}: {CoreRenderDevice.ShaderError}" );
		}

		Assert.AreEqual( 0, broken.Count, string.Join( "\n", broken ) );
	}

	//Every standard surface stage builds, since missing optional stages fail silently
	[TestMethod]
	public void TheStandardSurfaceBuildsEveryStage()
	{
		ThreadSafe.MarkMainThread();

		if ( !CoreNative.Available || !CoreShaderCompiler.Available )
			Assert.Inconclusive( "no slang compiler beside the engine" );

		var stages = CoreShaderFile.Compile( Path.Combine( Directory, "complex.shader" ) );

		Assert.IsNotNull( stages, $"the standard surface did not compile at all: {CoreRenderDevice.ShaderError}" );

		var missing = new List<string>();

		foreach ( CoreShaderFile.Stage stage in Enum.GetValues<CoreShaderFile.Stage>() )
		{
			//Compute is the one stage a surface is not expected to have: it is built from MainCs, and a shader that draws declares no such thing.
			if ( stage == CoreShaderFile.Stage.Compute ) continue;

			if ( stages[(int)stage] is null ) missing.Add( stage.ToString() );
		}

		Assert.AreEqual( 0, missing.Count,
			$"stages that did not build: {string.Join( ", ", missing )} - {CoreRenderDevice.ShaderError}" );
	}

	//Also compiles with features on: all at once, then each alone
	[TestMethod]
	public void TheStandardSurfaceCompilesWithItsFeaturesOn()
	{
		ThreadSafe.MarkMainThread();

		if ( !CoreNative.Available || !CoreShaderCompiler.Available )
			Assert.Inconclusive( "no slang compiler beside the engine" );

		//Through the engine, so the compiler is handed the same absolute path and the same include roots it gets when something actually draws.
		var path = CoreShaderFile.Source( "shaders/complex.shader" );

		Assert.IsNotNull( path, "complex.shader is the surface every material name lands on" );

		var features = CoreShaderFeatures.Of( path );

		Assert.AreNotEqual( 0, features.Length, "the standard surface declares features" );

		var broken = new List<string>();

		try
		{
			Try( "everything on", features.ToDictionary( x => x.Name, _ => 1 ) );

			foreach ( var feature in features )
				Try( feature.Name, new Dictionary<string, int> { [feature.Name] = 1 } );
		}
		finally
		{
			//Tear down the device and caches the compile brought up, or later tests render black
			CoreResources.Clear();
			CoreRenderDevice.Shutdown();
		}

		void Try( string what, Dictionary<string, int> chosen )
		{
			var combo = CoreShaderFeatures.Combo( features, chosen );

			if ( CoreShaderFile.Compile( path, CoreShaderCompiler.Format.Spirv, combo ) is null )
				broken.Add( $"{what} ({combo}): {CoreRenderDevice.ShaderError}" );
		}

		Assert.AreEqual( 0, broken.Count, string.Join( "\n", broken ) );
	}

	//Shipped shaders fit the budgets: 32 float4 material slots and 2 per draw in the push block
	[TestMethod]
	public void WhatTheShadersDeclareFits()
	{
		var over = new List<string>();

		foreach ( var name in Shaders )
		{
			var path = Path.Combine( Directory, name );

			if ( !File.Exists( path ) ) continue;

			var declared = CoreShaderParams.Of( path );

			var material = 1;
			var draw = 0;

			foreach ( var parameter in declared )
			{
				if ( CoreShaderParams.IsCull( parameter.Name ) ) continue;
				if ( parameter.IsAttribute ) { draw += parameter.Slots; continue; }

				//The four the engine has always carried share the first slot.
				if ( parameter.Name is "g_flRoughness" or "g_flMetalness"
					or "g_flEmissionStrength" or "g_flAlphaCutoff" )
				{
					continue;
				}

				material += parameter.Slots;
			}

			if ( material > CoreRenderDevice.MaterialParamSlots )
				over.Add( $"{name}: {material} material slots of {CoreRenderDevice.MaterialParamSlots}" );

			if ( draw > CoreShaderParams.DrawSlots )
				over.Add( $"{name}: {draw} draw slots of {CoreShaderParams.DrawSlots}" );
		}

		Assert.AreEqual( 0, over.Count, string.Join( Environment.NewLine, over ) );
	}

	//No shader under our names is still a legacy one; ours have a Slang include, legacy ones never do
	[TestMethod]
	public void NoneOfOursIsStillTheirs()
	{
		var theirs = new List<string>();

		foreach ( var name in Shaders )
		{
			var path = Path.Combine( Directory, name );

			if ( !File.Exists( path ) ) continue;

			var text = File.ReadAllText( path );

			if ( text.Contains( ".slang\"", StringComparison.Ordinal ) ) continue;

			foreach ( var line in text.Split( '\n' ) )
			{
				if ( line.Trim() is "MODES" or "FEATURES" or "COMMON" )
				{
					theirs.Add( name );
					break;
				}
			}
		}

		Assert.AreEqual( 0, theirs.Count, $"still in s&box's format: {string.Join( ", ", theirs )}" );
	}

	//And the pipeline agrees that they are ours, which is what decides whether it will build them. The two rules were the
	//same words in two files, and only one of them was ever run. Asserting them together is what stops them parting again.
	[TestMethod]
	public void TheCompilerAgreesTheyAreOurs()
	{
		var refused = new List<string>();

		foreach ( var name in Shaders )
		{
			var path = Path.Combine( Directory, name );

			if ( !File.Exists( path ) ) continue;

			var text = File.ReadAllText( path );

			//The same question ShaderCompiler.IsSourceFormat asks, in the same order: a Slang include settles it before anything else is looked at.
			if ( text.Contains( ".slang\"", StringComparison.Ordinal ) ) continue;

			if ( text.Contains( "#include \"common/", StringComparison.Ordinal ) )
				refused.Add( name );
		}

		Assert.AreEqual( 0, refused.Count,
			$"the pipeline would hand these back rather than build them: {string.Join( ", ", refused )}" );
	}

	//Each surface has one file; alias names have no file of their own and map onto one that exists
	[TestMethod]
	public void EachSurfaceIsWrittenOnce()
	{
		var wrong = new List<string>();

		foreach ( var (asked, answers) in Aliases )
		{
			if ( File.Exists( Path.Combine( Directory, asked ) ) )
				wrong.Add( $"{asked}: has a file of its own - the surface is {answers}" );

			var real = CoreShaderFile.Real( $"shaders/{asked}" );

			if ( !real.EndsWith( answers, StringComparison.OrdinalIgnoreCase ) )
				wrong.Add( $"{asked}: resolves to {real}, not {answers}" );
		}

		foreach ( var name in Shaders )
		{
			var path = Path.Combine( Directory, name );

			if ( !File.Exists( path ) )
			{
				wrong.Add( $"{name}: not there" );
				continue;
			}

			//Whole in its own file: the body is here, not in something it pulls in. The engine's
			//library - the bindings, the shading, the post helpers - is shared and does not count.
			var text = File.ReadAllText( path );

			foreach ( var line in text.Split( '\n' ) )
			{
				var trimmed = line.Trim();

				if ( !trimmed.StartsWith( "#include", StringComparison.Ordinal ) ) continue;

				if ( trimmed.Contains( "frame.slang", StringComparison.Ordinal ) ||
					trimmed.Contains( "surface_shading.slang", StringComparison.Ordinal ) ||
					trimmed.Contains( "postprocess.slang", StringComparison.Ordinal ) )
				{
					continue;
				}

				wrong.Add( $"{name}: includes {trimmed} - a shader is one file" );
			}
		}

		Assert.AreEqual( 0, wrong.Count, string.Join( Environment.NewLine, wrong ) );
	}

	//No legacy HLSL shaders remain in the folder; they don't compile here
	[TestMethod]
	public void NothingHereIsHlsl()
	{
		var theirs = new List<string>();

		foreach ( var path in System.IO.Directory.GetFiles( Directory, "*", SearchOption.AllDirectories ) )
		{
			var extension = Path.GetExtension( path );

			if ( extension is ".hlsl" or ".fxc" or ".h" )
			{
				theirs.Add( Path.GetFileName( path ) );
				continue;
			}

			if ( extension is not ".shader" ) continue;

			foreach ( var line in File.ReadAllText( path ).Split( '\n' ) )
			{
				if ( line.Trim() is "MODES" or "COMMON" )
				{
					theirs.Add( Path.GetFileName( path ) );
					break;
				}
			}
		}

		Assert.AreEqual( 0, theirs.Count,
			$"s&box's, and none of it builds: {string.Join( ", ", theirs )}" );
	}

	//Every material path hardcoded in engine code exists, along with the textures it names
	[TestMethod]
	public void EveryMaterialTheEngineNamesIsThere()
	{
		string[] named =
		{
			//The one a mesh gets before anybody gives it one.
			"materials/dev/grid.mat", "materials/dev/grid.png",

			"materials/default.mat",

			//Lines, trails and beams.
			"materials/default/line.mat",

			//The envmap probe's preview ball.
			"materials/dev/metal.mat", "materials/dev/metal.png",

			//What draws when nothing else would.
			"materials/error.mat", "materials/error.png",
		};

		var missing = new List<string>();

		foreach ( var path in named )
		{
			var full = Path.Combine( "addons", "base", "Assets", path.Replace( '/', Path.DirectorySeparatorChar ) );

			if ( !File.Exists( full ) ) missing.Add( path );
		}

		Assert.AreEqual( 0, missing.Count, $"named by the engine and not shipped: {string.Join( ", ", missing )}" );
	}

	//Only listed subsystems may still name legacy .vmat files; this list should only shrink
	[TestMethod]
	public void OnlyTheKnownSubsystemsStillNameTheirs()
	{
		string[] allowed =
		{
			"materials/skybox/skybox_day_01.vmat",       //SkyBox2D, for the env probe's cubemap
			"materials/postprocess/standard_pass1.vmat", //MotionBlur
			"materials/tools/toolstrigger.vmat",         //an editor attribute's default, not a draw
		};

		var found = new List<string>();
		var seen = 0;
		var pattern = new System.Text.RegularExpressions.Regex( @"""(materials/[^""]*\.vmat)""" );

		foreach ( var file in System.IO.Directory.EnumerateFiles(
			Path.Combine( SourceRoot, "Rinten.Engine" ), "*.cs", SearchOption.AllDirectories ) )
		{
			if ( file.Contains( $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" ) ) continue;

			foreach ( System.Text.RegularExpressions.Match match in pattern.Matches( File.ReadAllText( file ) ) )
			{
				var path = match.Groups[1].Value;
				++seen;

				if ( !allowed.Contains( path ) ) found.Add( $"{Path.GetFileName( file )}: {path}" );
			}
		}

		Assert.AreEqual( 0, found.Count,
			$"the engine names s&box content that is not on the known list:{Environment.NewLine}{string.Join( Environment.NewLine, found )}" );

		//And the scan itself works. Without this the test passes just as well when SourceRoot is wrong or
		//the pattern stops matching - it finds nothing either way, and a guard that cannot fail is not one.
		Assert.AreNotEqual( 0, seen, "no .vmat found at all - the scan is not reading the engine's sources" );
	}

	//Where this repository's engine sources are, from wherever the tests run.
	private static string SourceRoot
		=> Path.GetFullPath( Path.Combine( Environment.CurrentDirectory, "..", "engine" ) );

	//The default mesh material is ours (dev grid) and has a texture
	[TestMethod]
	public void TheDefaultMeshMaterialIsOursAndHasAPicture()
	{
		var material = Path.Combine( "addons", "base", "Assets", "materials", "dev", "grid.mat" );
		var picture = Path.Combine( "addons", "base", "Assets", "materials", "dev", "grid.png" );

		Assert.IsTrue( File.Exists( material ), $"{material} should be there" );
		Assert.IsTrue( File.Exists( picture ), $"{picture} should be there - a dev material with no grid on it is a flat colour" );

		using var document = System.Text.Json.JsonDocument.Parse( File.ReadAllText( material ) );

		Assert.AreEqual( "materials/dev/grid.png",
			document.RootElement.GetProperty( "ColorMap" ).GetString(),
			"and the material should name it" );
	}
}

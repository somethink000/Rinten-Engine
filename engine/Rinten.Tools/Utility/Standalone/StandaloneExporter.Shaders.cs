using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Editor;

partial class StandaloneExporter
{
	//Runs work on the main thread - where loading a material has to happen - whichever thread the export's awaits came back on.
	private static Task OnMainThread( Action work )
	{
		if ( ThreadSafe.IsMainThread )
		{
			work();
			return Task.CompletedTask;
		}

		var done = new TaskCompletionSource();

		MainThread.Queue( () =>
		{
			try { work(); done.SetResult(); }
			catch ( Exception e ) { done.SetException( e ); }
		} );

		return done.Task;
	}

	//Precompiles every shader variant the project's materials use, since players have no Slang compiler
	private void BakeShaders()
	{
		if ( !CoreShaderCompiler.Available )
		{
			Logger.Warning( "no shader compiler here - the game ships with the engine's own cache, and a material " +
				"whose features are not its shader's defaults will not draw where there is no compiler" );
			return;
		}

		if ( Project is null || !Project.HasAssetsPath() ) return;

		//What this asks for is what the game's cache gets, beside the engine's own.
		CoreShaderCache.Record();

		var assets = Project.GetAssetsPath();
		var materials = 0;
		var shaders = 0;

		//Full frame, then the portable one: devices have no compiler, so unbaked shaders draw nothing
		BakeSurfaces( assets, portable: false, ref materials, ref shaders );

		//And the project's point effects, each as a pass of its own: what a chain of them nobody baked is drawn with on a machine with no compiler.
		CorePointChain.Bake( Directory.EnumerateFiles( assets, "*.shader", SearchOption.AllDirectories ) );

		//And the chains themselves, as the scenes put them together - then every run those turn into when an effect is switched
		//off while the game runs. The frames below are the only ones whose runs are written down; see CorePointChain.Recording.
		CorePointChain.Recording = true;

		int chains, runs, variants;

		try
		{
			chains = BakeSceneChains( assets );
			( runs, variants ) = CorePointChain.BakeVariants();

			if ( !( _exportConfig.Platform ?? ExportPlatform.Host ).IsWeb )
			{
				try
				{
					CoreShaderCache.Baking = CoreShaderCompiler.Format.SpirvPortable;

					var again = 0;
					BakeSurfaces( assets, portable: true, ref again, ref again );
					CorePointChain.BakeScreens( bakedSurfaces );
				}
				finally
				{
					CoreShaderCache.Baking = null;
				}
			}
		}
		finally
		{
			CorePointChain.Recording = false;
		}

		var into = Path.Combine( _exportConfig.TargetDir, CoreShaderCache.FileName.Replace( '/', Path.DirectorySeparatorChar ) );

		if ( CoreShaderCache.WriteForGame( into ) )
			Logger.Info( $"shaders baked for {materials} material(s), {shaders} shader(s), and {runs} run(s) of point effects " +
				$"out of {chains} scene(s) - {variants} of them, counting the runs an effect switched off makes" );
	}

	//Bakes the stages assets ask for, keyed as the renderer asks; portable skips ones that won't compile
	private void BakeSurfaces( string assets, bool portable, ref int materials, ref int shaders )
	{
		var frame = portable ? " for the portable frame" : "";

		bakedSurfaces.Clear();

		//The project's materials, and the base addon's - which every game carries and draws with: a scene mesh's grid, the error material.
		var engineDir = Environment.CurrentDirectory;
		var materialFiles = Directory.EnumerateFiles( assets, "*.mat", SearchOption.AllDirectories )
			.Select( file => (Root: assets, File: file) )
			.Concat( BaseContent( engineDir )
				.Where( file => file.EndsWith( ".mat", StringComparison.OrdinalIgnoreCase ) )
				.Select( file => (Root: BaseAssetsFolder( engineDir ), File: file) ) );

		foreach ( var (root, file) in materialFiles )
		{
			var relative = Path.GetRelativePath( root, file ).Replace( '\\', '/' );

			Material material;

			try
			{
				material = Material.Load( relative );
			}
			catch ( Exception e )
			{
				Logger.Warning( $"{relative} did not load: {e.Message}" );
				continue;
			}

			var (shader, combo) = CoreResources.ShaderAndCombo( material );

			if ( shader is null ) continue;

			bakedSurfaces.Add( (shader, combo) );

			if ( CoreShaderFile.Stages( shader, combo ) is null )
			{
				Logger.Warning( $"{relative}: {shader}{( combo is null ? "" : $" ({combo})" )} did not compile{frame} - " +
					$"{CoreRenderDevice.ShaderError}" );
				continue;
			}

			materials++;
		}

		//And what the models' own art asks of those materials: a mesh whose art says metal, or says it glows, over a material that is neither
		//is drawn with another build of that material's shader - see CoreSceneRenderer.Wearing. Only a model knows, so the models are asked.
		foreach ( var file in Directory.EnumerateFiles( assets, "*.mdl_c", SearchOption.AllDirectories ) )
		{
			foreach ( var (shader, combo) in CoreSceneRenderer.WornBuilds( file ) )
			{
				bakedSurfaces.Add( (shader, combo) );

				if ( CoreShaderFile.Stages( shader, combo ) is not null ) continue;

				Logger.Warning( $"{Path.GetRelativePath( assets, file ).Replace( '\\', '/' )}: {shader} ({combo}) did not compile{frame} - " +
					$"{CoreRenderDevice.ShaderError}" );
			}
		}

		//The project's own shaders at their defaults, whether or not a material names one yet - a post-process is drawn by code, not by a material.
		foreach ( var file in Directory.EnumerateFiles( assets, "*.shader", SearchOption.AllDirectories ) )
		{
			if ( CoreShaderFile.IsForeign( file ) ) continue;

			//Neither a surface nor a pass over the frame, which the portable frame draws between two pictures - see CorePostBlit.
			if ( portable && !CoreResources.IsSurface( file ) && !CoreShaderFile.IncludesFile( file, "postprocess.slang" ) ) continue;

			//A surface that is not one draws nothing, and nothing else would say so.
			if ( !portable && CoreShaderFile.SurfaceDoubt( file ) is { } doubt ) Logger.Warning( doubt );

			var relative = Path.GetRelativePath( assets, file ).Replace( '\\', '/' );

			if ( CoreResources.IsSurface( file ) ) bakedSurfaces.Add( (relative, null) );

			if ( CoreShaderFile.Stages( relative ) is null )
			{
				Logger.Warning( $"{relative} did not compile{frame} - {CoreRenderDevice.ShaderError}" );
				continue;
			}

			//And the entries of its own beyond MainPs and MainCs - a pass over the frame whose file holds several. See CoreShaderFile.EntryWords.
			//The portable frame has no compute to run one of those with.
			foreach ( var (entry, kind) in CoreShaderFile.EntriesOf( file ) )
			{
				if ( portable && kind == CoreShaderCompiler.Stage.Compute ) continue;

				if ( CoreShaderFile.EntryWords( file, null, entry, kind ) is null )
					Logger.Warning( $"{relative} {entry} did not compile - {CoreRenderDevice.ShaderError}" );
			}

			shaders++;
		}
	}

	//The surfaces the last BakeSurfaces found the game drawing with, and the builds of them - what
	//the portable frame's runs of point effects are compiled into. See CorePointChain.BakeScreens.
	private readonly HashSet<(string Shader, string Combo)> bakedSurfaces = new();

	//Loads, ticks and draws each scene's cameras to bake its folded point effect chains; returns count
	private int BakeSceneChains( string assets )
	{
		//Nothing here is worth an export: every run it compiles is one the game can draw a pass an effect without. So whatever
		//goes wrong - a scene that throws while it loads, a picture that will not open - is said and the build carries on.
		try
		{
			return SceneChains( assets );
		}
		catch ( Exception e )
		{
			Logger.Warning( $"the scenes' point effects were not baked ({e.Message}) - " +
				"the game draws each run a pass an effect" );

			return 0;
		}
	}

	private int SceneChains( string assets )
	{
		//Small: nothing is looked at, and the size a chain is compiled for is not part of what it is compiled from.
		var picture = Texture.CreateRenderTarget( "baking the point effects", ImageFormat.RGBA8888, new Vector2( 256, 144 ) );
		var cameras = new List<CameraComponent>();
		var drawn = 0;

		foreach ( var file in Directory.EnumerateFiles( assets, "*.scene", SearchOption.AllDirectories ) )
		{
			var relative = Path.GetRelativePath( assets, file ).Replace( '\\', '/' );

			if ( ResourceLibrary.Get<SceneFile>( relative ) is not { } resource ) continue;

			Scene scene = null;

			try
			{
				scene = new Scene();

				using ( scene.Push() )
				{
					var options = new SceneLoadOptions();
					options.SetScene( resource );

					if ( !scene.Load( options ) ) continue;

					//The effects are hung on each camera at the end of a tick - see PostProcessSystem. A scene
					//loaded here is ticked without that end reaching it, so it is asked for by hand as well.
					scene.GameTick();

					//Every effect the scene holds switched on, whatever its start left off: a game that turns one on later lays
					//a run with it, and the bake covers the runs this one leaves when effects are off - see BakeScreens
					foreach ( var holder in scene.GetAllObjects( true ) )
					{
						foreach ( var effect in holder.Components.GetAll<BasePostProcess>( FindMode.Disabled | FindMode.InSelf ) )
							effect.Enabled = true;
					}

					if ( scene.GetSystem<PostProcessSystem>() is Component.ISceneStage effects ) effects.End();

					cameras.Clear();
					scene.GetAll( cameras );

					foreach ( var camera in cameras )
					{
						if ( camera.IsValid() ) camera.RenderToTexture( picture );
					}
				}

				drawn++;
			}
			catch ( Exception e )
			{
				Logger.Warning( $"{relative}: its point effects were not baked ({e.Message}) - " +
					"a frame of it draws them a pass an effect" );
			}
			finally
			{
				scene?.Destroy();
			}
		}

		return drawn;
	}
}

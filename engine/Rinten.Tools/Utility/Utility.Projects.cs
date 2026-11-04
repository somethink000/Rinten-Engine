namespace Editor;

public static partial class EditorUtility
{
	public static partial class Projects
	{
		public static IReadOnlyList<Project> GetAll() => Project.All.AsReadOnly();

		/// <summary>Saves the project, compiles everything and loads what changed; false if the code did not build.</summary>
		public static Task<bool> Updated( Project addon ) => Updated( addon, true );

		//settle false: nothing of this project is loaded yet, so no dll watcher has anything to pick up
		internal static async Task<bool> Updated( Project addon, bool settle )
		{
			//Save changes
			addon?.Save();

			//If we're a transient project we don't need to dirty everything else. we just really want to call the save callback and return.
			if ( addon is not null && addon.IsTransient )
			{
				return true;
			}

			bool compileSuccess = await Project.CompileAsync();
			if ( !compileSuccess )
				return false;

			if ( addon is not null )
			{
				if ( addon.Compiler is not null && !addon.Compiler.BuildSuccess )
					return false;

				if ( addon.EditorCompiler is not null && !addon.EditorCompiler.BuildSuccess )
					return false;
			}

			await WaitForCompiles( settle );

			EditorEvent.Run( "localaddons.changed" );
			SceneEditorSession.Active?.UpdateEditorTitle();

			return true;
		}

		/// <summary>Wait for the local compiles to be finished</summary>
		public static Task WaitForCompiles() => WaitForCompiles( true );

		static async Task WaitForCompiles( bool settle )
		{
			//give time for any files to finish being written
			//await Task.Delay( 1000 );

			//force finding new files, running callbacks
			FileWatch.Tick();

			//wait for compiles to finish
			await Project.CompileAsync();

			//Wait for file watch to pick up written files; hacky, PackageLoader needs a manual reload trigger
			if ( settle ) await Task.Delay( 500 );

			//filesystem callbacks..
			FileWatch.Tick();

			//Tick the loader to actually load
			Rinten.GameInstanceDll.PackageLoader.Tick();

			//give time for any files to finish being written
			//await Task.Delay( 1000 );
		}

		/// <summary>Regenerates the project's solution</summary>
		public static async Task GenerateSolution()
		{
			await Project.GenerateSolution();
		}
	}
}

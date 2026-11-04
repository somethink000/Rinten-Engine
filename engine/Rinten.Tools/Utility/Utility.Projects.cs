namespace Editor;

public static partial class EditorUtility
{
	public static partial class Projects
	{
		public static IReadOnlyList<Project> GetAll() => Project.All.AsReadOnly();

		public static async Task<bool> Updated( Project addon )
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

			await WaitForCompiles();

			EditorEvent.Run( "localaddons.changed" );
			SceneEditorSession.Active?.UpdateEditorTitle();

			return true;
		}

		/// <summary>Wait for the local compiles to be finished</summary>
		public static async Task WaitForCompiles()
		{
			//give time for any files to finish being written
			//await Task.Delay( 1000 );

			//force finding new files, running callbacks
			FileWatch.Tick();

			//wait for compiles to finish
			await Project.CompileAsync();

			//Wait for file watch to pick up written files; hacky, PackageLoader needs a manual reload trigger
			await Task.Delay( 500 );

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

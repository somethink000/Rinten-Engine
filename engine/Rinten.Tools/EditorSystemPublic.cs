
using System;
using System.Threading;

namespace Rinten;

public class EditorSystemPublic : EditorSystem
{
	public override Scene Scene => SceneEditorSession.Active?.Scene;

	/// <summary>Run a process on multiple items, show progress bar</summary>
	public override async Task ForEachAsync<T>( IEnumerable<T> list, string title, Func<T, CancellationToken, Task> worker, CancellationToken cancel = default, bool modal = false )
	{
		int count = list.Count();
		if ( count <= 0 ) return;

		using ( var p = ProgressSection( modal ) )
		{
			using var linkedCt = CancellationTokenSource.CreateLinkedTokenSource( cancel, p.GetCancel() );

			p.Title = title;
			p.TotalCount = count;
			await Task.Delay( 1 );

			int current = 0;
			FastTimer ft = FastTimer.StartNew();
			foreach ( var item in list )
			{
				current++;
				await worker( item, linkedCt.Token );

				p.Current = current;
				p.Subtitle = $"Processing {current} / {count}";

				if ( ft.ElapsedMilliSeconds > 50 )
				{
					await Task.Delay( 1, linkedCt.Token );
					ft = FastTimer.StartNew();
				}

				if ( linkedCt.IsCancellationRequested ) return;
			}
		}
	}


	/// <summary>Start a progress section</summary>
	public override IProgressSection ProgressSection( bool modal = false )
	{
		return new ProgressJobSection();
	}

	public override string SaveResource( GameResource resource, string path )
	{
		if ( resource is null || string.IsNullOrWhiteSpace( path ) ) return null;

		var asset = AssetSystem.FindByPath( path ) ?? Create( path );
		if ( asset is null || !asset.SaveToDisk( resource ) ) return null;

		return asset.Path;
	}

	//A new asset at a path under the assets of the project whose folder it is in - the first active one with that folder
	private static Asset Create( string path )
	{
		var folder = System.IO.Path.GetDirectoryName( path ) ?? "";

		var root = Project.All.Where( x => x.Active && x.HasAssetsPath() ).Select( x => x.GetAssetsPath() )
			.FirstOrDefault( x => System.IO.Directory.Exists( System.IO.Path.Combine( x, folder ) ) );

		if ( root is null ) return null;

		var extension = System.IO.Path.GetExtension( path ).TrimStart( '.' );
		return AssetSystem.CreateResource( extension, System.IO.Path.Combine( root, path ) );
	}

	//Walks every component of the scene - don't call per frame, use Scene.EditorCamera instead
	public override CameraComponent Camera
	{
		get
		{
			if ( SceneEditorSession.Active?.Scene is not { } scene ) return null;

			foreach ( var camera in scene.GetAll<CameraComponent>() )
			{
				if ( camera.IsSceneEditorCamera ) return camera;
			}

			return null;
		}
	}
}

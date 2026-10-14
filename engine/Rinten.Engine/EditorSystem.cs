using System.Threading;

namespace Editor;

public abstract class EditorSystem
{
	/// <summary>The scene we're currently editing</summary>
	public abstract Scene Scene { get; }

	/// <summary>Run a process on multiple items, show progress bar</summary>
	public abstract Task ForEachAsync<T>( IEnumerable<T> list, string title, Func<T, CancellationToken, Task> worker, CancellationToken cancel = default, bool modal = false );

	/// <summary>Start a progress section</summary>
	public abstract IProgressSection ProgressSection( bool modal = false );

	/// <summary>The main editor camera</summary>
	public abstract CameraComponent Camera { get; }

	/// <summary>Writes a resource to its asset at a path under a project's assets, making the asset where there is none.
	/// Returns the resource's path, null where it could not be written.</summary>
	public abstract string SaveResource( GameResource resource, string path );
}

public interface IProgressSection : IDisposable
{
	public string Icon { get; set; }
	public string Title { get; set; }
	public string Subtitle { get; set; }
	public double Current { get; set; }
	public double TotalCount { get; set; }
	public double ProgressDelta => (TotalCount > 0 ? Current / TotalCount : 0).Clamp( 0, 1 );

	public void Cancel();
	public CancellationToken GetCancel();
}

using Rinten;
using Rinten.Diagnostics;

namespace Editor;

/// <summary>Profiler dock: CPU tree from FrameProfiler plus GPU zones, draws and memory read a frame late</summary>
[Dock( "Editor", "Profiler", "speed", DockArea.Bottom )]
public class ProfilerDock : PanelDockHost
{
	//On, the picture is already warm when the tab comes back, for a stall that only happens while looking elsewhere
	/// <summary>Whether the profiler keeps recording while no dock is looking at it.</summary>
	[ConVar( "profiler_record_always", Help = "Keep recording frame profiler spans while the Profiler dock is hidden" )]
	public static bool RecordAlways { get; set; }

	//Every profiler dock alive: a workspace or editor window can build its own, and recording follows whether any of them is seen
	private static readonly List<ProfilerDock> Open = new();

	public ProfilerDock( Widget parent ) : base( parent )
	{
		MinimumSize = new Vector2( 500, 200 );

		SetContent( new ProfilerPanel() );

		Open.Add( this );
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		Open.Remove( this );
		Record();
	}

	//Recording costs a branch per scope, so it is on only while a dock is seen. Decided once over all of them:
	//each deciding for itself let a hidden one switch it off under the one being read
	[EditorEvent.Frame]
	private static void Record()
	{
		var seen = false;

		foreach ( var dock in Open )
			seen |= dock.IsValid() && dock.Visible;

		FrameProfiler.Enabled = RecordAlways || seen;
	}
}

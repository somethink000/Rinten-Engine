using Rinten.Diagnostics;
using Rinten.UI;
using System;

namespace Editor;

//Editor cost overlay: FPS, main thread, GPU time and memory; process-wide, same on all views
[StyleSheet( "SceneStatsPanel.cs.scss" )]
internal sealed class SceneStatsPanel : EditorPanel
{
	/// <summary>Whether the stats are shown - the button on the scene view's toolbar; remembered between sessions.</summary>
	public static bool Showing
	{
		get => showing ??= EditorCookie.Get( "SceneView.ShowStats", false );
		set
		{
			showing = value;
			EditorCookie.Set( "SceneView.ShowStats", value );
		}
	}

	private static bool? showing;

	//How often the numbers are read again. Every frame they would be a blur - four times a second is as fast as a number can be read at all.
	private const float RefreshInterval = 0.25f;

	private RealTimeSince timeSinceRead;

	private readonly StatRow fps;
	private readonly StatRow cap;
	private readonly StatRow cpu;
	private readonly StatRow gpu;
	private readonly StatRow vram;
	private readonly StatRow ram;

	public SceneStatsPanel()
	{
		AddClass( "scene-stats" );

		fps = AddRow( "FPS" );
		cap = AddRow( "Cap" );
		cpu = AddRow( "CPU" );
		gpu = AddRow( "GPU" );
		vram = AddRow( "VRAM" );
		ram = AddRow( "RAM" );

		Read();
	}

	private StatRow AddRow( string label )
	{
		var row = new StatRow( label );
		AddChild( row );
		return row;
	}

	public override void Tick()
	{
		base.Tick();

		SetClass( "hidden", !Showing );

		if ( !Showing ) return;
		if ( timeSinceRead < RefreshInterval ) return;

		timeSinceRead = 0.0f;

		Read();
	}

	private void Read()
	{
		//The frame rate over the last second rather than this frame's, which jumps around too much to read
		var frameMs = PerformanceStats.LastSecond.FrameAvg;
		var perSecond = frameMs > 0.0f ? 1000.0f / frameMs : 0.0f;
		var gpuMs = PerformanceStats.GpuFrametime;

		fps.Set( $"{perSecond:0} · {frameMs:0.0} ms", FrameRateColour( frameMs ) );

		//Which limit the frame rate is actually under. The one from settings is only one of four - in the background
		//the inactive cap takes over, and a frame rate far below the picked limit reads as a bug without this.
		var limit = PerformanceStats.FrameRateCap;
		var atCap = limit > 0.0f && perSecond >= limit * 0.95f;

		cap.Set( limit > 0.0f ? $"{limit:0} · {CapSourceName( PerformanceStats.FrameRateCapSource )}" : "off",
			atCap ? Theme.Yellow : Theme.Text );

		//Main thread time as a share of the frame; 100 means the frame waits on this thread
		var cpuPct = PerformanceStats.MainThreadUsage;

		cpu.Set( $"{PerformanceStats.MainThreadTime:0.00} ms · {cpuPct:0} %", CpuColour( cpuPct ) );

		//The share of the frame the GPU spent on our work. Our work, not the whole card - what other processes are doing on it is theirs to know.
		var gpuPct = frameMs > 0.0f ? Math.Clamp( gpuMs / frameMs * 100.0f, 0.0f, 100.0f ) : 0.0f;

		//Nothing measures the GPU until our renderer has drawn a frame and its fence has proved it finished - a dash beats a confident zero
		gpu.Set( gpuMs > 0.0f ? $"{gpuMs:0.00} ms · {gpuPct:0} %" : "-", GpuColour( gpuPct ) );
		vram.Set( FormatBytes( PerformanceStats.GpuMemoryUsage ), Theme.Text );
		ram.Set( FormatBytes( PerformanceStats.ApproximateProcessMemoryUsage ), Theme.Text );
	}

	//The caps by the names a person picked them under.
	private static string CapSourceName( string source ) => source switch
	{
		"fps_max" => "limit",
		"fps_max_inactive" => "background",
		"fps_max_menu" => "menu",
		"vsync" => "vsync",
		_ => source
	};

	private static Color GpuColour( float pct ) => pct switch
	{
		<= 0.0f => Theme.Text,
		> 90.0f => Theme.Red,
		> 70.0f => Theme.Yellow,
		_ => Theme.Text
	};

	//The same thresholds the GPU row uses, so the two are read together: past ninety the frame is waiting on this thread and nothing else will help.
	private static Color CpuColour( float pct ) => GpuColour( pct );

	private static Color FrameRateColour( float frameMs ) => frameMs switch
	{
		<= 0.0f => Theme.Text,
		> 33.3f => Theme.Red,
		> 16.7f => Theme.Yellow,
		_ => Theme.Green
	};

	private static string FormatBytes( ulong bytes ) => bytes switch
	{
		>= 1024UL * 1024UL * 1024UL => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB",
		>= 1024UL * 1024UL => $"{bytes / (1024.0 * 1024.0):0} MB",
		>= 1024UL => $"{bytes / 1024.0:0} KB",
		_ => $"{bytes} B"
	};

	//One line: what it is on the left, what it says on the right.
	private sealed class StatRow : Panel
	{
		private readonly Rinten.UI.Label value;

		public StatRow( string label )
		{
			AddClass( "row" );

			var name = new Rinten.UI.Label( label );
			name.AddClass( "name" );
			AddChild( name );

			value = new Rinten.UI.Label( "-" );
			value.AddClass( "value" );
			AddChild( value );
		}

		public void Set( string text, Color colour )
		{
			value.Text = text;
			value.Style.FontColor = colour;
		}
	}
}

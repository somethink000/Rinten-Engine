using System;

namespace Editor;

//The editor's central widget: every open scene and prefab session as a tab over its view. Sessions are never
//dock widgets, so they can't be floated, split or lost by layout changes
sealed class SceneTabWidget : Widget
{
	readonly EditorTabStrip _strip;
	readonly StackedHost _content;
	readonly List<SessionTab> _tabs = new();

	SessionTab _current;

	//The tab widgets alive, for the sweep below. There is normally one.
	static readonly List<SceneTabWidget> Living = new();

	//Each frame, hide scene views covered by the current one; hidden ones would still render fully
	[EditorEvent.Frame]
	static void HideCoveredViews()
	{
		for ( var i = Living.Count - 1; i >= 0; i-- )
		{
			var one = Living[i];

			if ( !one.IsValid() ) { Living.RemoveAt( i ); continue; }

			one._content?.HideTheRest();
		}
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		Living.Remove( this );
	}

	public SceneTabWidget( Widget parent ) : base( parent )
	{
		Living.Add( this );

		Name = "SceneTabs";
		Layout = Layout.Column();

		_strip = Layout.Add( new EditorTabStrip( this ) );
		_content = Layout.Add( new StackedHost( this ), 1 );

		_strip.Trailing = new IconButton( "add", EditorScene.NewScene, _strip )
		{
			ToolTip = "New scene",
			Background = Color.Transparent,
			FixedWidth = 20,
			FixedHeight = 20
		};

		_strip.Reordered = order =>
		{
			var sorted = order.OfType<SessionTab>().ToList();

			_tabs.Clear();
			_tabs.AddRange( sorted );
		};
	}

	public void Open( SceneEditorSession session )
	{
		if ( _tabs.Any( x => x.Session == session ) ) return;

		var tab = new SessionTab( session );
		_tabs.Add( tab );

		session.SceneDock.Visible = false;
		session.SceneDock.Parent = _content;

		_strip.SetItems( _tabs );
		MakeCurrent( tab );
	}

	public void Remove( SceneEditorSession session )
	{
		var tab = _tabs.FirstOrDefault( x => x.Session == session );
		if ( tab is null ) return;

		var index = _tabs.IndexOf( tab );
		_tabs.Remove( tab );

		if ( !_strip.IsValid() )
			return;

		_strip.SetItems( _tabs );

		if ( _tabs.Count == 0 )
		{
			_current = null;
			_content.Clear();
			return;
		}

		if ( _current == tab )
		{
			_current = null;
			_tabs[Math.Min( index, _tabs.Count - 1 )].Session.MakeActive();
		}
	}

	public void MakeCurrent( SceneEditorSession session )
	{
		if ( _tabs.FirstOrDefault( x => x.Session == session ) is { } tab )
			MakeCurrent( tab );
	}

	void MakeCurrent( SessionTab tab )
	{
		if ( _current == tab ) return;

		_current = tab;
		_content.Show( tab.Session.SceneDock );
		_strip.SetCurrent( tab );
	}

	internal bool IsCurrent( SceneEditorSession session ) => _current?.Session == session;

	//A session as the strip shows it. The title carries its own star while work is only in memory - see UpdateEditorTitle
	sealed class SessionTab( SceneEditorSession session ) : IEditorTab
	{
		public SceneEditorSession Session { get; } = session;

		public string Title => Session.SceneDock?.WindowTitle ?? "Untitled";
		public string Icon => Session.IsPlaying ? "play_arrow" : Session.IsPrefabSession ? "home_repair_service" : "grid_4x4";
		public Color? Accent => Session.IsPlaying ? Theme.Green : null;
		public bool Unsaved => false;

		public void Select() => Session.MakeActive();

		//Through the dock, which writes what is unsaved on the way out
		public void Close() => Session.SceneDock?.Close();
	}
}

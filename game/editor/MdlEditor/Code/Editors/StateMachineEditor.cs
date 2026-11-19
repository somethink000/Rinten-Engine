using Editor.MdlEditor.Nodes;
using Editor.NodeEditor;
using Rinten.UI;

using UILabel = Rinten.UI.Label;

namespace Editor.MdlEditor;

//The page a state machine node opens as: nothing but the canvas, which fills whatever room the window gives it.
[NodePage( typeof( StateMachineNode ) )]
[StyleSheet( "StateMachineEditor.cs.scss" )]
internal sealed class StateMachinePage : Panel, INodePage
{
	private readonly StateMachineCanvas _canvas;

	internal StateMachineCanvas Canvas => _canvas;
	public CanvasPanel ViewCanvas => _canvas;
	public bool SupportsSecondaryView => true;
	internal object CaptureSelection() => _canvas.CaptureSelection();
	internal void RestoreSelection( object state ) => _canvas.RestoreSelection( state );
	internal void FocusSelection( object state ) => _canvas.FocusSelection( state );
	internal void NotifySelection() => _canvas.NotifySelection();
	bool INodePage.CanCutSelection => _canvas.CanCutSelection;
	bool INodePage.CanCopySelection => _canvas.CanCopySelection;
	bool INodePage.CanPasteSelection => _canvas.CanPasteSelection;
	bool INodePage.CutSelection() => _canvas.CutSelection();
	bool INodePage.CopySelection() => _canvas.CopySelection();
	bool INodePage.PasteSelection() => _canvas.PasteSelection();

	public StateMachinePage( StateMachineNode node, Window window )
	{
		AddClass( "state-machine-page" );

		_canvas = AddChild( new StateMachineCanvas( node, window ) );
	}

	public bool Rebind( Node node )
	{
		return _canvas.Rebind( node );
	}
}

//State machine canvas: states as boxes, transitions as arrows; Alt+drag links, double-click enters
internal sealed class StateMachineCanvas : CanvasPanel
{
	private const string StateClipboardIdent = "animgraph-state";

	private sealed class StateClipboardData
	{
		public StateMachineNode.State State { get; set; }
		public List<StateMachineNode.Conduit> OutgoingConduits { get; set; } = [];

		public StateClipboardData() { }
	}

	internal readonly record struct StateSelection( string Name );
	internal readonly record struct ConduitSelection( string From, string To );

	//One of the state machine's shared graphs - entry overrides or global transitions. Singletons carrying their own display info and navigation.
	internal sealed class SharedSelection
	{
		public static readonly SharedSelection GlobalTransitions =
			new( "Global Transitions", "public", "global-transitions", typeof( GlobalTransitionNode ), machine => machine.GlobalGraph );

		public static readonly SharedSelection EntryOverrides =
			new( "Entry Overrides", "login", "entry-overrides", typeof( EntryStateOverrideConditionsNode ), machine => machine.EntryGraph );

		public string Title { get; }
		public string Icon { get; }
		public string Key { get; }
		public Type DocumentationType { get; }

		private readonly Func<StateMachineNode, Data> _getGraph;

		private SharedSelection( string title, string icon, string key, Type documentationType, Func<StateMachineNode, Data> getGraph )
		{
			Title = title;
			Icon = icon;
			Key = key;
			DocumentationType = documentationType;
			_getGraph = getGraph;
		}

		public Data GetGraph( StateMachineNode machine ) => machine is null ? null : _getGraph( machine );
	}

	//One of the two always-visible shortcuts into a shared graph: a pill with the graph's icon, its name and how much is wired up in it.
	private sealed class SharedGraphButton : Panel
	{
		private readonly StateMachineCanvas _canvas;
		private readonly SharedSelection _selection;
		private readonly UILabel _label;

		public SharedGraphButton( StateMachineCanvas canvas, SharedSelection selection )
		{
			_canvas = canvas;
			_selection = selection;

			AddClass( "shared-button" );

			var icon = AddChild<UILabel>( "material-icon" );
			icon.Tokenize = false;
			icon.Text = selection.Icon;

			_label = AddChild<UILabel>( "label" );
			_label.Tokenize = false;

			Tooltip = Rinten.DisplayInfo.ForType( selection.DocumentationType ).Description;
		}

		/// <summary>How much is wired up in there, and whether it is the selection.</summary>
		public void Read( int count, bool selected )
		{
			_label.Text = count > 0 ? $"{_selection.Title} · {count}" : _selection.Title;

			SetClass( "active", count > 0 );
			SetClass( "selected", selected );
		}

		protected override void OnMouseDown( MousePanelEvent e )
		{
			base.OnMouseDown( e );

			if ( e.MouseButton != MouseButtons.Left )
				return;

			//The press is the canvas's otherwise, and a press on empty canvas starts a pan.
			_canvas.SelectShared( _selection );
			e.StopPropagation();
		}

		protected override void OnDoubleClick( MousePanelEvent e )
		{
			base.OnDoubleClick( e );

			//Left only, as on the canvas beneath it: a right double-click is a context menu, not a way into the graph.
			if ( e.MouseButton != MouseButtons.Left )
				return;

			_canvas.OpenShared( _selection );
			e.StopPropagation();
		}
	}

	private StateMachineNode _node;

	internal StateMachineNode Node => _node;
	internal static StateMachineCanvas Current { get; private set; }

	public object Selected { get; private set; }
	public event Action<object> SelectionChanged;

	private StateMachineNode.State _dragState;
	private Vector2 _dragOffset;
	private StateMachineNode.State _linkSource;
	private StateMachineNode.State _hoverState;
	private StateMachineNode.Conduit _hoverConduit;

	private readonly SharedGraphButton _entriesButton;
	private readonly SharedGraphButton _globalsButton;

	private bool[] _hasLiveStateInfo = [];
	private AnimGraphNodeDebugInfo[] _liveStateInfo = [];
	private AnimGraphSyncTrackEvent[][] _liveStateSyncTrackEvents = [];
	private string[] _liveStateSyncEventIDs = [];
	private StateMachineNode.State _liveState;
	private StateMachineNode.Conduit _liveConduit;
	private StateMachineNode.State _liveGlobalFrom;
	private StateMachineNode.State _liveGlobalTo;
	private TransitionNode _liveTransition;
	private float _liveTransitionProgress;

	private const float GridStep = 16;

	public StateMachineCanvas( StateMachineNode node, Window window ) : base( window )
	{
		_node = node;
		Current = this;

		AddClass( "state-machine-canvas" );

		var shared = AddChild<Panel>( "shared-buttons" );
		_entriesButton = shared.AddChild( new SharedGraphButton( this, SharedSelection.EntryOverrides ) );
		_globalsButton = shared.AddChild( new SharedGraphButton( this, SharedSelection.GlobalTransitions ) );

		_node.SyncStates();
		AutoLayout();
	}

	public override void OnDeleted()
	{
		if ( Current == this )
			Current = null;

		base.OnDeleted();
	}

	//States created before the canvas existed all sit at the origin — spread them in a ring.
	private void AutoLayout()
	{
		var states = _node.States;
		if ( states.Count < 2 || states.Any( s => s.Position != states[0].Position ) )
			return;

		var radius = 100 + states.Count * 30;
		for ( var i = 0; i < states.Count; i++ )
		{
			var angle = MathF.Tau * i / states.Count - MathF.PI * 0.5f;
			states[i].Position = new Vector2( MathF.Cos( angle ), MathF.Sin( angle ) ) * radius;
		}
	}

	//Whether anything is actually playing in this machine, which is the only time the readout under a state's name has anything to say.
	private bool ShowsLiveReadout => _hasLiveStateInfo.Any( live => live );

	private Rect StateRect( StateMachineNode.State state )
	{
		var width = Math.Clamp( state.Name.Length * 8f + 48, 160, 280 );

		//Reserve room for the live readout only while one is showing
		var readout = ShowsLiveReadout ? PoseDebug.Height + PoseDebug.Margin : 0.0f;

		var size = new Vector2( width, 44 + readout );

		return new Rect( state.Position - size * 0.5f, size );
	}

	private StateMachineNode.State FindState( string name ) => _node.States.FirstOrDefault( s => s.Name == name );

	//Arrow endpoints in graph space — border to border, aimed at the midpoint between
	//the two states, nudged perpendicular so opposite directions render side by side.
	private bool TransitionLine( StateMachineNode.Conduit conduit, out Vector2 start, out Vector2 end )
	{
		start = end = default;

		if ( FindState( conduit.From ) is not { } from || FindState( conduit.To ) is not { } to || from == to )
			return false;

		return TransitionLine( from, to, out start, out end );
	}

	private bool TransitionLine( StateMachineNode.State from, StateMachineNode.State to, out Vector2 start, out Vector2 end )
	{
		var ra = StateRect( from );
		var rb = StateRect( to );
		var mid = (ra.Center + rb.Center) * 0.5f;

		start = ClampToRect( ra, mid );
		end = ClampToRect( rb, mid );

		var dir = end - start;
		if ( dir.Length < 1 )
			return false;

		var offset = dir.Normal.Perpendicular * 6;

		start += offset;
		end += offset;
		return true;
	}

	private static Vector2 ClampToRect( Rect r, Vector2 p ) => new(
		Math.Clamp( p.x, r.Left, r.Right ),
		Math.Clamp( p.y, r.Top, r.Bottom ) );

	private static float DistanceToSegment( Vector2 p, Vector2 a, Vector2 b )
	{
		var ab = b - a;
		var t = Math.Clamp( Vector2.Dot( p - a, ab ) / MathF.Max( ab.LengthSquared, 0.001f ), 0, 1 );
		return p.Distance( a + ab * t );
	}

	private StateMachineNode.State HitState( Vector2 local )
	{
		var p = FromCanvas( local );

		for ( var i = _node.States.Count - 1; i >= 0; i-- )
		{
			if ( StateRect( _node.States[i] ).IsInside( p ) )
				return _node.States[i];
		}

		return null;
	}

	private StateMachineNode.Conduit HitTransition( Vector2 local )
	{
		foreach ( var conduit in _node.Conduits )
		{
			if ( !TransitionLine( conduit, out var start, out var end ) )
				continue;

			if ( DistanceToSegment( local, ToCanvas( start ), ToCanvas( end ) ) < 10 )
				return conduit;
		}

		return null;
	}

	private void Select( object item )
	{
		if ( Equals( Selected, item ) )
			return;

		Selected = item;
		NotifySelection();
		SelectionChanged?.Invoke( item );
		Repaint();
	}

	internal void SelectForTest( object item ) => Select( item );

	//Picked one of the shared graphs out of the pills over the corner.
	private void SelectShared( SharedSelection selection )
	{
		_node.SyncStates();
		Select( selection );
	}

	//Stepped into one of them, which is what double-clicking a pill does.
	private void OpenShared( SharedSelection selection )
	{
		SelectShared( selection );
		Editor.NavigateInto( selection );
	}

	internal object CaptureSelection() => Selected switch
	{
		StateMachineNode.State state => new StateSelection( state.Name ),
		StateMachineNode.Conduit conduit => new ConduitSelection( conduit.From, conduit.To ),
		SharedSelection shared => shared,
		_ => null
	};

	internal void RestoreSelection( object state )
	{
		Select( state switch
		{
			StateSelection selection => _node.States.FirstOrDefault( state => state.Name == selection.Name ),
			ConduitSelection selection => _node.Conduits.FirstOrDefault( conduit => conduit.From == selection.From && conduit.To == selection.To ),
			SharedSelection shared => shared,
			_ => null
		} );
	}

	internal void FocusSelection( object state )
	{
		RestoreSelection( state );

		var center = Selected switch
		{
			StateMachineNode.State selectedState => selectedState.Position,
			StateMachineNode.Conduit conduit when FindState( conduit.From ) is { } from && FindState( conduit.To ) is { } to
				=> (from.Position + to.Position) * 0.5f,
			_ => ViewCenter
		};

		RestoreView( center, Zoom );
		Focus();
	}

	internal void NotifySelection()
	{
		Editor.OnCanvasSelection( _node, Selected );
	}

	/// <summary>Points this canvas at the same node in a rebuilt graph, keeping the view and selection - no panel churn, no flicker.</summary>
	public bool Rebind( Node node )
	{
		if ( node is not StateMachineNode machine || machine.Identifier != _node.Identifier )
			return false;

		_node = machine;
		_node.SyncStates();
		_liveState = null;
		_liveConduit = null;
		_liveGlobalFrom = null;
		_liveGlobalTo = null;
		_liveTransition = null;
		_liveTransitionProgress = 0.0f;

		Selected = Selected switch
		{
			StateMachineNode.State state => machine.States.FirstOrDefault( s => s.Name == state.Name ),
			StateMachineNode.Conduit conduit => machine.Conduits.FirstOrDefault( c => c.From == conduit.From && c.To == conduit.To ),
			SharedSelection shared => shared,
			_ => null
		};

		NotifySelection();
		SelectionChanged?.Invoke( Selected );
		Repaint();
		return true;
	}

	private void Changed()
	{
		_node.Update();
		Editor.MarkDirty();
		Repaint();
	}

	private bool CanLink( StateMachineNode.State from, StateMachineNode.State to ) =>
		from != to && !_node.Conduits.Any( c => c.From == from.Name && c.To == to.Name );

	private void AddState( Vector2 position, StateMachineNode.StateType type = StateMachineNode.StateType.BlendTree )
	{
		var baseName = type == StateMachineNode.StateType.Off ? "Off" : "State";
		var index = _node.States.Count + 1;
		while ( _node.States.Any( s => s.Name == $"{baseName} {index}" ) )
			index++;

		Editor.PushGraphUndo( "Add State" );
		var state = new StateMachineNode.State { Name = $"{baseName} {index}", Type = type, Position = Snap( position, true ) };

		if ( type == StateMachineNode.StateType.StateMachine )
			SeedStateMachine( state );

		_node.States.Add( state );
		_node.SyncStates();
		Changed();
		Editor.PushGraphRedo();
		Select( state );
	}

	private void CopyState( StateMachineNode.State state )
	{
		var clipboard = new StateClipboardData
		{
			State = state,
			OutgoingConduits = _node.Conduits.Where( conduit => conduit.From == state.Name ).ToList(),
		};

		var json = System.Text.Json.JsonSerializer.Serialize( clipboard );
		GraphClipboard.Copy( StateClipboardIdent, json );
	}

	private void CutState( StateMachineNode.State state )
	{
		CopyState( state );
		RemoveState( state );
	}

	private bool CanPasteState() => GraphClipboard.CanPaste( StateClipboardIdent );

	internal bool CanCutSelection => Selected is StateMachineNode.State;
	internal bool CanCopySelection => Selected is StateMachineNode.State;
	internal bool CanPasteSelection => CanPasteState();

	internal bool CutSelection()
	{
		if ( Selected is not StateMachineNode.State state )
			return false;

		CutState( state );
		return true;
	}

	internal bool CopySelection()
	{
		if ( Selected is not StateMachineNode.State state )
			return false;

		CopyState( state );
		return true;
	}

	internal bool PasteSelection()
	{
		if ( !CanPasteState() )
			return false;

		PasteState( PasteAt );
		return true;
	}

	//Where a pasted state lands. Under the pointer while the pointer is over the canvas, and in the middle of the view otherwise: Ctrl+V arrives
	//from the window's own shortcuts, with the pointer anywhere at all, and the last place it happened to be over the canvas is not an answer.
	private Vector2 PasteAt => HasHovered ? FromCanvas( LocalMouse ) : ViewCenter;

	private void PasteState( Vector2 position )
	{
		if ( !GraphClipboard.TryPaste( StateClipboardIdent, out var json ) )
			return;

		StateClipboardData clipboard;
		try
		{
			clipboard = System.Text.Json.JsonSerializer.Deserialize<StateClipboardData>( json );

			if ( clipboard?.State is null )
			{
				clipboard = new StateClipboardData
				{
					State = System.Text.Json.JsonSerializer.Deserialize<StateMachineNode.State>( json ),
				};
			}
		}
		catch ( System.Text.Json.JsonException exception )
		{
			Log.Warning( $"Could not paste AnimGraph state: {exception.Message}" );
			return;
		}

		var state = clipboard?.State;
		if ( state is null )
			return;

		var outgoingConduits = (clipboard.OutgoingConduits ?? [])
			.Where( conduit => _node.States.Any( target => target.Name == conduit.To ) )
			.ToList();
		var transitionOrder = state.TransitionOrder?.ToArray() ?? [];
		var copiedTransitions = outgoingConduits
			.SelectMany( conduit => conduit.Transitions )
			.GroupBy( transition => transition.Identifier )
			.ToDictionary( group => group.Key, group => group.First() );

		state.Graph ??= Data.CreateSubGraph<PoseResultNode>();
		state.LayerGraph ??= Data.CreateSubGraph<StateLayerDataNode>( GraphKind.LayerData );
		state.Graph.RegenerateNodeIdentifiers();
		state.LayerGraph.RegenerateNodeIdentifiers();
		state.Name = UniqueStateName( state.Name );
		state.Position = Snap( position, true );

		foreach ( var conduit in outgoingConduits )
		{
			conduit.From = state.Name;
			conduit.Graph ??= Data.CreateSubGraph( GraphKind.TransitionConduit );
			conduit.Graph.RegenerateNodeIdentifiers();
		}

		var transitionIdentifiers = copiedTransitions.ToDictionary(
			entry => entry.Key,
			entry => entry.Value.Identifier );
		var globalTransitionIdentifiers = _node.GlobalTransitions
			.Select( transition => transition.Identifier )
			.ToHashSet();
		var remappedTransitionOrder = new List<string>( transitionOrder.Length );

		foreach ( var identifier in transitionOrder )
		{
			if ( transitionIdentifiers.TryGetValue( identifier, out var remappedIdentifier ) )
				remappedTransitionOrder.Add( remappedIdentifier );
			else if ( globalTransitionIdentifiers.Contains( identifier ) )
				remappedTransitionOrder.Add( identifier );
		}

		state.TransitionOrder = remappedTransitionOrder;

		Editor.PushGraphUndo( "Paste State" );
		_node.States.Add( state );
		_node.Conduits.AddRange( outgoingConduits );
		_node.SyncStates();
		Changed();
		Editor.PushGraphRedo();
		Select( state );
	}

	private string UniqueStateName( string name )
	{
		var baseName = string.IsNullOrWhiteSpace( name ) ? "State" : name;
		if ( !_node.States.Any( state => state.Name.Equals( baseName, StringComparison.OrdinalIgnoreCase ) ) )
			return baseName;

		var index = 2;
		while ( _node.States.Any( state => state.Name.Equals( $"{baseName} {index}", StringComparison.OrdinalIgnoreCase ) ) )
			++index;

		return $"{baseName} {index}";
	}

	//A state machine state's graph is a state machine wired to the output
	private static void SeedStateMachine( StateMachineNode.State state )
	{
		if ( state.Graph?.Nodes.OfType<PoseResultNode>().FirstOrDefault() is not { } output )
			return;

		var machine = new StateMachineNode { Position = output.Position - new Vector2( 300, 0 ) };
		state.Graph.AddNode( machine );
		output.Pose = new NodeInput { Identifier = machine.Identifier, Output = nameof( StateMachineNode.Result ) };
	}

	private void RemoveState( StateMachineNode.State state )
	{
		Editor.PushGraphUndo( "Remove State" );
		_node.Conduits.RemoveAll( c => c.From == state.Name || c.To == state.Name );
		_node.States.Remove( state );

		if ( _node.DefaultState == state.Name )
			_node.DefaultState = "";

		_node.SyncStates();
		Changed();
		Editor.PushGraphRedo();
		Select( null );
	}

	private void AddConduit( StateMachineNode.State from, StateMachineNode.State to )
	{
		Editor.PushGraphUndo( "Add Transition" );
		var conduit = new StateMachineNode.Conduit { From = from.Name, To = to.Name };
		conduit.Graph.AddNode( new TransitionNode { Position = new Vector2( 400, 0 ) } );
		_node.Conduits.Add( conduit );
		Changed();
		Editor.PushGraphRedo();
		Select( conduit );
	}

	private void RemoveConduit( StateMachineNode.Conduit conduit )
	{
		Editor.PushGraphUndo( "Remove Transition" );
		_node.Conduits.Remove( conduit );
		Changed();
		Editor.PushGraphRedo();
		Select( null );
	}

	private void ReverseConduit( StateMachineNode.Conduit conduit )
	{
		Editor.PushGraphUndo( "Reverse Transition" );
		(conduit.From, conduit.To) = (conduit.To, conduit.From);
		Changed();
		Editor.PushGraphRedo();
	}

	//Only convertible when the graph is exactly one state machine wired to the output
	internal static bool CanConvertToStateMachineState( StateMachineNode.State state ) =>
		state.Graph?.Nodes.Count() == 2
		&& state.Graph.Nodes.OfType<PoseResultNode>().FirstOrDefault() is { } output
		&& output.GetConnectedNode( output.Pose ) is StateMachineNode;

	internal void ConvertStateType( StateMachineNode.State state, StateMachineNode.StateType type )
	{
		Editor.PushGraphUndo( "Convert State" );
		state.Type = type;
		Changed();
		Editor.PushGraphRedo();
	}

	private void SetDefaultState( StateMachineNode.State state )
	{
		Editor.PushGraphUndo( "Set Default State" );
		_node.DefaultState = state.Name;
		Changed();
		Editor.PushGraphRedo();
	}

	protected override void Fit()
	{
		var min = new Vector2( float.MaxValue );
		var max = new Vector2( float.MinValue );

		foreach ( var state in _node.States )
		{
			var rect = StateRect( state );
			min = Vector2.Min( min, rect.TopLeft );
			max = Vector2.Max( max, rect.BottomRight );
		}

		if ( _node.States.Count == 0 )
		{
			min = new Vector2( -200 );
			max = new Vector2( 200 );
		}

		var span = Vector2.Max( max - min, new Vector2( 100 ) ) + 120;
		FrameView( (min + max) * 0.5f, MathF.Min( MathF.Min( Width / span.x, Height / span.y ), 1.25f ) );
	}

	private Vector2 Snap( Vector2 p, bool snap ) => snap
		? new Vector2( MathF.Round( p.x / GridStep ) * GridStep, MathF.Round( p.y / GridStep ) * GridStep )
		: p;

	protected override void OnMouseDown( MousePanelEvent e )
	{
		//Takes the keyboard and starts a middle-button pan.
		base.OnMouseDown( e );

		var local = LocalMouse;

		if ( e.MouseButton == MouseButtons.Left )
		{
			if ( HitState( local ) is { } state )
			{
				Select( state );

				if ( Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Alt ) )
				{
					_linkSource = state;
				}
				else
				{
					_dragState = state;
					_dragOffset = state.Position - FromCanvas( local );
					Editor.PushGraphUndo( "Move State", GraphChangeScope.EditorOnly );
				}
			}
			else
			{
				Select( HitTransition( local ) );

				if ( Selected is null )
					StartPan( local, MouseButtons.Left );
			}

			e.StopPropagation();
			return;
		}

		if ( e.MouseButton == MouseButtons.Right )
		{
			OpenContextMenu( local );
			e.StopPropagation();
		}
	}

	protected override void OnDoubleClick( MousePanelEvent e )
	{
		if ( e.MouseButton != MouseButtons.Left )
			return;

		var local = LocalMouse;

		if ( HitState( local ) is { } state )
		{
			EditState( state );
			e.StopPropagation();
			return;
		}

		if ( HitTransition( local ) is { } conduit )
		{
			EditConduit( conduit );
			e.StopPropagation();
			return;
		}

		AddState( FromCanvas( local ) );
		e.StopPropagation();
	}

	private void EditState( StateMachineNode.State state )
	{
		if ( state.Type == StateMachineNode.StateType.Off )
			return;

		Select( state );
		Editor.NavigateInto( state );
	}

	private void EditConduit( StateMachineNode.Conduit conduit )
	{
		Select( conduit );
		Editor.NavigateInto( conduit.Graph );
	}

	/// <summary>Once a frame: whatever is being dragged, what is playing, and how much is wired into the shared graphs.</summary>
	public override void Tick()
	{
		base.Tick();

		FollowDrag();
		UpdateLiveState();
		ReadSharedButtons();
	}

	//A state being moved, and a link being drawn out of one, are both followed from where the pointer actually
	//is: a panel has no implicit grab, and either drag routinely runs off the edge of the canvas and back.
	private void FollowDrag()
	{
		if ( _dragState is null && _linkSource is null )
			return;

		if ( _dragState is not null )
		{
			//Ctrl here means the opposite of what it means on a blend canvas: a state snaps to the grid unless you say not to.
			var free = Application.KeyboardModifiers.HasFlag( KeyboardModifiers.Ctrl );
			var position = Snap( FromCanvas( LocalMouse ) + _dragOffset, !free );

			if ( _dragState.Position != position )
			{
				_dragState.Position = position;
				Changed();
			}
		}
		else
		{
			//The line follows the pointer wherever it has got to, which is how a transition is aimed at a state near the edge of the view.
			Repaint();
		}

		if ( Application.MouseButtons.HasFlag( MouseButtons.Left ) )
			return;

		EndStateDrag();
		EndLink();
	}

	private void EndStateDrag()
	{
		if ( _dragState is null )
			return;

		_dragState = null;
		Editor.PushGraphRedo();
	}

	//Lets go of an alt-drag, making the transition if it landed on one.
	private void EndLink()
	{
		if ( _linkSource is null )
			return;

		var source = _linkSource;
		_linkSource = null;

		if ( HitState( LocalMouse ) is { } target && CanLink( source, target ) )
			AddConduit( source, target );

		Repaint();
	}

	protected override void OnMouseMove( MousePanelEvent e )
	{
		base.OnMouseMove( e );

		if ( _dragState is not null || _linkSource is not null || IsPanning )
			return;

		//The pills over the corner are panels and answer for themselves; what is under them is not hovered.
		var overButton = _entriesButton.HasHovered || _globalsButton.HasHovered;

		var local = LocalMouse;
		var hoverState = overButton ? null : HitState( local );
		var hoverConduit = !overButton && hoverState is null ? HitTransition( local ) : null;

		if ( hoverState == _hoverState && hoverConduit == _hoverConduit )
			return;

		_hoverState = hoverState;
		_hoverConduit = hoverConduit;

		Tooltip = (hoverState, hoverConduit) switch
		{
			(not null, _) => Rinten.DisplayInfo.ForType( typeof( StateMachineNode.State ) ).Description,
			(_, not null) => Rinten.DisplayInfo.ForType( typeof( StateMachineNode.Conduit ) ).Description,
			_ => null
		};

		SetClass( "over-item", hoverState is not null || hoverConduit is not null );
		Repaint();
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );

		EndStateDrag();
		EndLink();
	}

	public override void OnButtonTyped( ButtonEvent e )
	{
		if ( e.Pressed && e.Button == "delete" )
		{
			switch ( Selected )
			{
				case StateMachineNode.State deletedState:
					RemoveState( deletedState );
					e.StopPropagation = true;
					return;
				case StateMachineNode.Conduit conduit:
					RemoveConduit( conduit );
					e.StopPropagation = true;
					return;
			}
		}

		base.OnButtonTyped( e );
	}

	private void OpenContextMenu( Vector2 local )
	{
		var menu = new Menu();

		if ( HitState( local ) is { } state )
		{
			Select( state );

			var editState = menu.AddOption( "Edit State", "animation", () => EditState( state ) );
			editState.Enabled = state.Type != StateMachineNode.StateType.Off;

			var isDefault = _node.States.IndexOf( state ) == _node.DefaultStateIndex;
			var setDefault = menu.AddOption( "Set as Default State", "star", () => SetDefaultState( state ) );
			setDefault.Enabled = !isDefault;

			if ( state.Type == StateMachineNode.StateType.BlendTree && CanConvertToStateMachineState( state ) )
				menu.AddOption( "Convert to State Machine State", "hub", () => ConvertStateType( state, StateMachineNode.StateType.StateMachine ) );

			if ( state.Type == StateMachineNode.StateType.StateMachine )
				menu.AddOption( "Convert to Blend Tree State", "account_tree", () => ConvertStateType( state, StateMachineNode.StateType.BlendTree ) );

			menu.AddOption( "Cut State", "content_cut", () => CutState( state ), "Ctrl+X" );
			menu.AddOption( "Copy State", "content_copy", () => CopyState( state ), "Ctrl+C" );

			var transitions = menu.AddMenu( "Add Transition To", "arrow_forward" );
			foreach ( var to in _node.States.Where( s => s != state ) )
			{
				var option = transitions.AddOption( to.Name, null, () => AddConduit( state, to ) );
				option.Enabled = CanLink( state, to );
			}
			transitions.Enabled = _node.States.Count > 1;

			menu.AddSeparator();
			menu.AddOption( "Delete State", "delete", () => RemoveState( state ) );
		}
		else if ( HitTransition( local ) is { } conduit )
		{
			Select( conduit );

			menu.AddOption( "Edit Transitions", "route", () => EditConduit( conduit ) );

			var reverse = menu.AddOption( "Reverse Direction", "swap_horiz", () => ReverseConduit( conduit ) );
			reverse.Enabled = FindState( conduit.To ) is { } from && FindState( conduit.From ) is { } to && CanLink( from, to );

			menu.AddSeparator();
			menu.AddOption( "Delete Transition", "delete", () => RemoveConduit( conduit ) );
		}
		else
		{
			var position = Snap( FromCanvas( local ), true );
			menu.AddOption( "Add State", "animation", () => AddState( position ) );
			menu.AddOption( "Add State Machine State", "hub", () => AddState( position, StateMachineNode.StateType.StateMachine ) );
			menu.AddOption( "Add Off State", "power_settings_new", () => AddState( position, StateMachineNode.StateType.Off ) );
			var paste = menu.AddOption( "Paste State", "content_paste", () => PasteState( position ), "Ctrl+V" );
			paste.Enabled = CanPasteState();
			menu.AddSeparator();
			menu.AddOption( "Entry Overrides", "login", () => SelectShared( SharedSelection.EntryOverrides ) );
			menu.AddOption( "Global Transitions", "public", () => SelectShared( SharedSelection.GlobalTransitions ) );
			menu.AddSeparator();
			menu.AddOption( "Fit View", "fit_screen", Fit );
		}

		menu.OpenAtCursor( this );
	}

	//What the pills say. Counted rather than remembered because both graphs are edited on their own pages, and neither tells this one when it changes.
	private void ReadSharedButtons()
	{
		var entries = _node.States.Count( s => _node.EntryConditions?.ConditionFor( s.Name ).IsValid ?? false );
		var globals = _node.GlobalTransitions.Count( g => g.Condition.IsValid );

		_entriesButton.Read( entries, ReferenceEquals( Selected, SharedSelection.EntryOverrides ) );
		_globalsButton.Read( globals, ReferenceEquals( Selected, SharedSelection.GlobalTransitions ) );
	}

	private void UpdateLiveState()
	{
		var changed = false;
		if ( _liveStateInfo.Length != _node.States.Count )
		{
			_hasLiveStateInfo = new bool[_node.States.Count];
			_liveStateInfo = new AnimGraphNodeDebugInfo[_node.States.Count];
			_liveStateSyncTrackEvents = new AnimGraphSyncTrackEvent[_node.States.Count][];
			_liveStateSyncEventIDs = new string[_node.States.Count];
			changed = true;
		}

		for ( var i = 0; i < _node.States.Count; i++ )
		{
			var hasInfo = Editor.TryGetLiveStatePoseDebugInfo(
				_node,
				_node.States[i],
				out var info,
				out var syncTrackEvents,
				out var syncEventID );

			if ( _hasLiveStateInfo[i] == hasInfo
				&& _liveStateInfo[i] == info
				&& (_liveStateSyncTrackEvents[i] ?? []).SequenceEqual( syncTrackEvents )
				&& _liveStateSyncEventIDs[i] == syncEventID )
				continue;

			_hasLiveStateInfo[i] = hasInfo;
			_liveStateInfo[i] = info;
			_liveStateSyncTrackEvents[i] = syncTrackEvents;
			_liveStateSyncEventIDs[i] = syncEventID;
			changed = true;
		}

		Editor.TryGetLiveStateMachineState( _node, out var liveState, out var liveFrom, out var liveTransition, out var liveTransitionProgress );
		var liveConduit = liveFrom is not null
			? _node.Conduits.FirstOrDefault( conduit => conduit.From == liveFrom.Name && conduit.To == liveState.Name )
			: null;
		var liveGlobalFrom = liveFrom is not null && liveConduit is null ? liveFrom : null;
		var liveGlobalTo = liveGlobalFrom is null ? null : liveState;

		if ( !ReferenceEquals( _liveState, liveState )
			|| !ReferenceEquals( _liveConduit, liveConduit )
			|| !ReferenceEquals( _liveGlobalFrom, liveGlobalFrom )
			|| !ReferenceEquals( _liveGlobalTo, liveGlobalTo )
			|| !ReferenceEquals( _liveTransition, liveTransition )
			|| _liveTransitionProgress != liveTransitionProgress )
		{
			_liveState = liveState;
			_liveConduit = liveConduit;
			_liveGlobalFrom = liveGlobalFrom;
			_liveGlobalTo = liveGlobalTo;
			_liveTransition = liveTransition;
			_liveTransitionProgress = liveTransitionProgress;
			changed = true;
		}

		if ( changed )
			Repaint();
	}

	protected override void PaintCanvas()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.WindowBackground );
		Paint.DrawRect( Paint.LocalRect );

		PaintGrid();

		foreach ( var conduit in _node.Conduits )
			PaintTransition( conduit );

		PaintLiveGlobalTransition();
		PaintLinkPreview();

		for ( var i = 0; i < _node.States.Count; i++ )
			PaintState(
				_node.States[i],
				i == _node.DefaultStateIndex,
				ReferenceEquals( _liveState, _node.States[i] ),
				_hasLiveStateInfo.ElementAtOrDefault( i ),
				_liveStateInfo.ElementAtOrDefault( i ),
				_liveStateSyncTrackEvents.ElementAtOrDefault( i ) ?? [],
				_liveStateSyncEventIDs.ElementAtOrDefault( i ) ?? "" );

		PaintLiveTransitionOverlay();
	}

	private void PaintGrid()
	{
		var step = GridStep * 4 * Zoom;
		while ( step < 24 ) step *= 2;

		var origin = ToCanvas( Vector2.Zero );

		Paint.ClearBrush();
		Paint.SetPen( Theme.WindowBackground.Lighten( 0.3f ) );

		for ( var x = origin.x % step; x < Width; x += step )
			Paint.DrawLine( new Vector2( x, 0 ), new Vector2( x, Height ) );

		for ( var y = origin.y % step; y < Height; y += step )
			Paint.DrawLine( new Vector2( 0, y ), new Vector2( Width, y ) );
	}

	private void PaintArrowLine( Vector2 a, Vector2 b, Color color, float width, bool blocked = false )
	{
		Paint.ClearBrush();
		Paint.SetPen( color, width );
		Paint.DrawLine( a, b );

		var dir = (b - a).Normal;
		var mid = (a + b) * 0.5f;
		var size = MathF.Max( 13 * Zoom, 9 );

		if ( blocked )
		{
			//X at the midpoint — this conduit has no transitions, so it blocks
			var d1 = (dir + dir.Perpendicular).Normal * size * 0.5f;
			var d2 = (dir - dir.Perpendicular).Normal * size * 0.5f;

			Paint.SetPen( color, MathF.Max( width, 2.5f ) );
			Paint.DrawLine( mid - d1, mid + d1 );
			Paint.DrawLine( mid - d2, mid + d2 );
			return;
		}

		//Direction chevron at the midpoint, Mecanim style
		Paint.ClearPen();
		Paint.SetBrush( color );
		Paint.DrawArrow( mid - dir * size * 0.5f, mid + dir * size * 0.5f, size );
	}

	private static readonly Color TransitionColor = Color.Parse( "#c0c0c0" ) ?? default;

	private void PaintTransition( StateMachineNode.Conduit conduit )
	{
		if ( !TransitionLine( conduit, out var start, out var end ) )
			return;

		var a = ToCanvas( start );
		var b = ToCanvas( end );

		var selected = ReferenceEquals( Selected, conduit );
		var hovered = conduit == _hoverConduit;
		var live = conduit == _liveConduit;
		var hasTransitions = conduit.HasTransitions;

		var color = hovered ? SelectionColor
			: selected ? SelectionColor.Darken( 0.2f )
			: live ? Color.Cyan
			: hasTransitions ? TransitionColor
			: Theme.Red;
		var width = (live ? 4 : hovered ? 4.5f : 3) * Zoom;
		PaintArrowLine( a, b, color, width, blocked: !hasTransitions );
		PaintTransitionEndpoints( a, b, color, width );
	}

	private void PaintLiveGlobalTransition()
	{
		if ( _liveGlobalFrom is null
			|| _liveGlobalTo is null
			|| !TransitionLine( _liveGlobalFrom, _liveGlobalTo, out var start, out var end ) )
			return;

		var a = ToCanvas( start );
		var b = ToCanvas( end );
		PaintArrowLine( a, b, Color.Cyan, 4 * Zoom );
		PaintTransitionEndpoints( a, b, Color.Cyan, 4 * Zoom );
	}

	private static void PaintTransitionEndpoints( Vector2 a, Vector2 b, Color color, float width )
	{
		var radius = MathF.Max( width * 2.2f, 6.5f );

		Paint.ClearPen();
		Paint.SetBrush( color );
		Paint.DrawCircle( a, radius );
		Paint.DrawCircle( b, radius );
	}

	private void PaintLiveTransitionOverlay()
	{
		Vector2 start;
		Vector2 end;

		if ( _liveConduit is not null )
		{
			if ( !TransitionLine( _liveConduit, out start, out end ) )
				return;
		}
		else if ( _liveGlobalFrom is not null && _liveGlobalTo is not null )
		{
			if ( !TransitionLine( _liveGlobalFrom, _liveGlobalTo, out start, out end ) )
				return;
		}
		else
		{
			return;
		}

		PaintLiveTransitionFeedback( ToCanvas( start ), ToCanvas( end ) );
	}

	private void PaintLiveTransitionFeedback( Vector2 a, Vector2 b )
	{
		var progress = _liveTransitionProgress.Clamp( 0.0f, 1.0f );
		var current = a + (b - a) * progress;
		var direction = (b - a).Normal;
		var tracerSize = MathF.Max( 15 * Zoom, 10 );

		Paint.ClearPen();
		Paint.SetBrush( Color.Cyan.WithAlpha( 0.18f ) );
		Paint.DrawCircle( current, tracerSize );
		Paint.SetBrush( Color.Cyan.WithAlpha( 0.35f ) );
		Paint.DrawCircle( current, tracerSize * 0.7f );
		Paint.SetBrush( Color.Cyan );
		Paint.DrawArrow( current - direction * tracerSize * 0.65f, current + direction * tracerSize * 0.35f, tracerSize );
		Paint.SetBrush( Color.White );
		Paint.DrawArrow( current - direction * tracerSize * 0.5f, current + direction * tracerSize * 0.25f, tracerSize * 0.65f );

		if ( Zoom < 0.45f )
			return;

		var name = string.IsNullOrWhiteSpace( _liveTransition?.TransitionName )
			? "Transition"
			: _liveTransition.TransitionName;
		var global = _liveTransition is GlobalTransitionNode ? "Global · " : "";
		var label = $"{global}{name} · {progress * 100:0}%";

		Paint.SetDefaultFont( 8 * Zoom, 500 );
		var labelSize = new Vector2( Paint.MeasureText( label ).x + 16 * Zoom, 22 * Zoom );
		var center = (a + b) * 0.5f + direction.Perpendicular * 18 * Zoom;
		var rect = new Rect( center - labelSize * 0.5f, labelSize );

		Paint.ClearPen();
		Paint.SetBrush( Theme.WindowBackground.WithAlpha( 0.95f ) );
		Paint.DrawRect( rect, 11 * Zoom );
		Paint.ClearBrush();
		Paint.SetPen( Color.Cyan.WithAlpha( 0.85f ), Zoom );
		Paint.DrawRect( rect, 11 * Zoom );
		Paint.SetPen( Theme.Text );
		Paint.DrawText( rect, label, TextFlag.Center );
	}

	private void PaintLinkPreview()
	{
		if ( _linkSource is null )
			return;

		var mouse = LocalMouse;
		var target = HitState( mouse );
		var valid = target is not null && CanLink( _linkSource, target );

		var color = target is null ? Color.White : valid ? Theme.Green : Theme.Red;
		var a = ToCanvas( ClampToRect( StateRect( _linkSource ), FromCanvas( mouse ) ) );
		var b = target is not null && valid ? ToCanvas( ClampToRect( StateRect( target ), _linkSource.Position ) ) : mouse;

		if ( a.Distance( b ) < 16 )
			return;

		PaintArrowLine( a, b, color, 2 );
	}

	private void PaintState(
		StateMachineNode.State state,
		bool isDefault,
		bool isLiveState,
		bool hasLiveInfo,
		AnimGraphNodeDebugInfo liveInfo,
		IReadOnlyList<AnimGraphSyncTrackEvent> syncTrackEvents,
		string syncEventID )
	{
		var rect = ToCanvas( StateRect( state ) );
		var titleRect = new Rect( rect.Left, rect.Top, rect.Width, 44 * Zoom );
		var selected = ReferenceEquals( Selected, state );
		var hovered = state == _hoverState || state == _linkSource;
		var isOff = state.Type == StateMachineNode.StateType.Off;
		var primary = isOff ? Color.Gray : isDefault ? Theme.Green : Theme.Blue;
		var radius = 5 * Zoom;

		if ( hasLiveInfo && liveInfo.IsInitialized && !liveInfo.IsValid )
		{
			Paint.ClearBrush();
			Paint.SetPen( Color.Red, 2.0f * Zoom );
			Paint.DrawRect( rect.Grow( 4 * Zoom ), radius + 4 * Zoom );
		}

		Paint.ClearPen();
		Paint.SetBrush( Color.Black.WithAlpha( 0.3f ) );
		Paint.DrawRect( rect + new Vector2( 0, 3 * Zoom ), radius );

		Paint.SetBrush( primary.Darken( hovered || selected ? 0.5f : 0.6f ) );
		Paint.DrawRect( rect, radius );

		Paint.ClearBrush();
		var edgeColor = selected ? SelectionColor
			: isLiveState ? Color.Cyan
			: primary.WithAlpha( hovered ? 0.9f : 0.5f );
		var edgeWidth = isLiveState ? 5 : selected ? 2 : 1.2f;
		Paint.SetPen( edgeColor, edgeWidth * Zoom );
		Paint.DrawRect( rect, radius );

		var inner = titleRect.Shrink( 9 * Zoom, 0 );

		//A state fed from the graph the machine sits on says so, because its own page is empty and looking at an empty page is the wrong conclusion.
		var icon = isOff ? "power_settings_new"
			: state.HasExternalPose ? "input"
			: state.Type == StateMachineNode.StateType.StateMachine ? "hub"
			: isDefault ? "play_arrow" : "animation";

		Paint.ClearBrush();
		Paint.SetPen( primary.Lighten( 0.7f ).WithAlpha( 0.8f ) );
		Paint.DrawIcon( inner, icon, 15 * Zoom, TextFlag.LeftCenter );

		Paint.SetDefaultFont( 8 * Zoom, 500 );
		Paint.SetPen( Theme.Text.WithAlpha( hovered || selected ? 1f : 0.9f ) );
		Paint.DrawText( titleRect, state.Name );

		if ( _node.EntryConditions?.ConditionFor( state.Name ).IsValid ?? false )
		{
			Paint.SetPen( Theme.Yellow.WithAlpha( 0.9f ) );
			Paint.DrawIcon( inner, "login", 13 * Zoom, TextFlag.RightCenter );
		}

		if ( !ShowsLiveReadout ) return;

		var debugRect = new Rect(
			rect.Left + PoseDebug.Margin * Zoom,
			titleRect.Bottom,
			rect.Width - PoseDebug.Margin * 2.0f * Zoom,
			PoseDebug.Height * Zoom );

		//No placeholders: a state that is not the one playing leaves its strip empty rather than filling it with "N/A" beside the one that is.
		PoseDebug.PaintDebugInfo(
			debugRect,
			hasLiveInfo && liveInfo.IsActive && liveInfo.ValueType == AnimGraphNodeValueType.Pose,
			liveInfo,
			syncTrackEvents,
			syncEventID,
			Zoom,
			placeholders: false );
	}
}

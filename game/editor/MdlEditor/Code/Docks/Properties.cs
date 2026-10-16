using Rinten.UI;

using UILabel = Rinten.UI.Label;

namespace Editor.MdlEditor;

/// <summary>Properties dock: the selection's editable properties, or the graph's settings when none</summary>
public class Properties : PanelDockHost
{
	private readonly PropertiesPanel _panel;

	//No parent: the dock manager is what puts a dock somewhere, and a parent handed over here is replaced the moment the dock is opened.
	public Properties( Window window ) : base( null )
	{
		Name = "Properties";
		WindowTitle = "Properties";
		SetWindowIcon( "edit" );

		//No minimum width: the dock manager ignores it on layout restore, and the sheet reflows

		_panel = new PropertiesPanel( window );

		SetContent( _panel );
	}

	/// <summary>What the dock is showing.</summary>
	public object Target
	{
		get => _panel.Target;
		set => _panel.Target = value;
	}

	/// <summary>Told whenever something in the sheet is edited.</summary>
	public Action<SerializedProperty> PropertyUpdated
	{
		get => _panel.PropertyUpdated;
		set => _panel.PropertyUpdated = value;
	}

	//Build the sheet again, for when the values behind it changed.
	internal void Refresh() => _panel.Refresh();

	internal void NavigationContextChanged() => _panel.NavigationContextChanged();

	public void UpdateClipInfo() => _panel.UpdateClipInfo();

	public void PlayAnimation( string name ) => _panel.PlayAnimation( name );

	public void ShowPose( Nodes.AnimationPoseNode pose ) => _panel.ShowPose( pose );
}

//The header, the clip preview, and the sheet under them.
[StyleSheet( "AnimGraphProperties.scss" )]
internal sealed class PropertiesPanel : EditorPanel
{
	private const string DocumentationExpandedCookie = "AnimGraph.DocumentationExpanded";
	private const string DebugExpandedCookie = "AnimGraph.DebugExpanded";

	private const float PreviewHeight = 250;

	private readonly Window _window;
	private readonly PropertiesHeader _header;
	private readonly SceneRenderingPanel _clipPreview;
	private readonly Panel _scroll;

	//What sits above the properties, and what sits below them.
	private readonly Panel _above;
	private readonly Panel _below;

	//The properties themselves. One sheet, told what to show.
	private readonly Inspectors.ControlSheet _sheet;

	private object _target;

	//The scene behind the clip preview - ours, and nothing to do with the window's own preview.
	private Scene _miniScene;
	private CameraComponent _miniCamera;
	private GameObject _miniModelObject;
	private SkinnedModelRenderer _miniRenderer;
	private readonly ClipPreview _clip = new();
	private Model _miniModel;
	private string _currentAnim;
	private Nodes.AnimationPoseNode _poseNode;

	public Action<SerializedProperty> PropertyUpdated { get; set; }

	public object Target
	{
		get => _target;
		set
		{
			if ( value == _target )
				return;

			_target = value;
			Rebuild();
		}
	}

	public PropertiesPanel( Window window )
	{
		_window = window;

		AddClass( "properties" );

		EnsureMiniScene();

		_clipPreview = AddChild( new SceneRenderingPanel() );
		_clipPreview.AddClass( "clip-preview" );
		_clipPreview.Scene = _miniScene;
		_clipPreview.Style.Height = PreviewHeight;
		_clipPreview.OnPreFrame += OnMiniPreFrame;
		_clipPreview.SetClass( "hidden", true );

		//The same shape the main editor's inspector has: one scroll, its children packed a hairline apart, and nothing between them.
		_scroll = AddChild<Panel>( "scroll" );

		//Said here as well as in the stylesheet: flex defaults to a row, and without this the header, the sheet and the extras sit side by side.
		_scroll.Style.FlexDirection = FlexDirection.Column;
		_scroll.Style.AlignItems = Align.Stretch;

		_header = _scroll.AddChild( new PropertiesHeader() );

		//What goes above the properties - a variation banner and the data of the variation being edited, which read as being about the sheet below them.
		_above = _scroll.AddChild<Panel>( "extras above" );

		//One sheet, kept. Setting its target is what rebuilds it, and it empties itself first; a fresh one per selection left the last
		//one in place, and the properties of the next object started under it - further down every time something new was opened.
		_sheet = _scroll.AddChild( new Inspectors.ControlSheet() );

		//Cards, buttons and the sections that fold, after the properties.
		_below = _scroll.AddChild<Panel>( "extras below" );
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		_clip.Dispose();
		CleanupMiniScene();
	}

	private void Rebuild()
	{
		var wantsPreview = _target is AnimationClipInfo or Nodes.AnimationClipNode or Nodes.AnimationPoseNode;

		UpdateHeader();

		_clipPreview?.SetClass( "hidden", !wantsPreview );

		SyncPreview();
		RebuildSheet();
	}

	//Build the sheet again, for when the values behind it changed - a variation swap, say.
	internal void Refresh() => Rebuild();

	internal void NavigationContextChanged()
	{
		if ( _target is null )
			return;

		UpdateHeader();
		RebuildSheet();
	}

	/// <summary>Play a specific animation in the clip preview.</summary>
	public void PlayAnimation( string name )
	{
		_currentAnim = name;
		_poseNode = null;

		if ( _miniRenderer is null ) return;

		_clip.Play( _miniRenderer, name );
	}

	/// <summary>Show an animation held at a pose node's fixed time.</summary>
	public void ShowPose( Nodes.AnimationPoseNode pose )
	{
		PlayAnimation( pose.AnimationName );
		_poseNode = pose;
	}

	//Keep the preview on whatever the inspector is showing - the clip of the variation being
	//edited, which changes when the target, its data or the selected variation changes.
	private void SyncPreview()
	{
		switch ( _target )
		{
			case AnimationClipInfo clip when clip.AnimationName != _currentAnim:
				PlayAnimation( clip.AnimationName );
				break;
			case Nodes.AnimationClipNode clip when clip.AnimationName != _currentAnim:
				PlayAnimation( clip.AnimationName );
				break;
			case Nodes.AnimationPoseNode pose when pose.AnimationName != _currentAnim || _poseNode != pose:
				ShowPose( pose );
				break;
		}
	}

	/// <summary>Update the header without rebuilding the sheet under it.</summary>
	public void UpdateClipInfo() => UpdateHeader();

	//---- the sheet -------------------------------------------------------

	private void RebuildSheet()
	{
		if ( _sheet is null || _target is null )
			return;

		_above.DeleteChildren();
		_below.DeleteChildren();

		var so = _target.GetSerialized();

		so.OnPropertyChanged += property =>
		{
			UpdateHeader();
			SyncPreview();
			PropertyUpdated?.Invoke( property );
		};

		so.OnPropertyFinishEdit += p => _window.CommitChange( p is null ? "Edit Properties" : $"Edit {p.Name}" );

		var hideAnimName = _target is AnimationClipInfo;

		//Variation data is edited in the inspector, above the properties every variation shares
		var variationNode = _target as VariationDataNode;
		var variationID = _window.SelectedVariationID;
		var inherited = variationNode is not null
			&& variationID != Variation.DefaultVariationID
			&& !variationNode.HasVariationOverride( variationID );

		if ( variationNode is not null )
		{
			if ( variationID != Variation.DefaultVariationID )
				AddVariationBanner( variationNode, variationID, inherited );

			AddVariationData( so, inherited );
		}

		//Told what to show, not rebuilt: the sheet reads its own target every tick and empties
		//itself when it changes. Same as the component sheets in the main editor's inspector.
		_sheet.PropertyFilter = p => FilterProperty( p, hideAnimName )
			&& !(variationNode is not null && p.Name == VariationDataNode.SelectedDataProperty);
		_sheet.Target = so;
		_sheet.Rebuild();

		if ( _target is Nodes.StateMachineNode.Conduit conduit )
			AddConduitCard( conduit );

		if ( _target is Nodes.StateMachineNode.State selectedState && _window.SelectedStateMachine is { } machine )
			AddTransitionPriority( machine, selectedState );

		switch ( _target )
		{
			case Nodes.StateMachineNode.State { Type: not Nodes.StateMachineNode.StateType.Off } state:
				AddConvertButton( state );
				break;
		}

		if ( _target is Node debugNode )
			AddDebugSection( debugNode );

		AddDocumentation( _target );
	}

	private void AddDocumentation( object target )
	{
		var type = target switch
		{
			Node node => node.GetType(),
			Nodes.StateMachineNode.State => typeof( Nodes.StateMachineNode.State ),
			Nodes.StateMachineNode.Conduit => typeof( Nodes.StateMachineNode.Conduit ),
			StateMachineCanvas.SharedSelection shared => shared.DocumentationType,
			_ => null
		};

		if ( type is null )
			return;

		var description = DisplayInfo.ForType( type ).Description;
		if ( string.IsNullOrWhiteSpace( description ) )
			return;

		AddSection( "Documentation", "help", "blue", DocumentationExpandedCookie, body =>
		{
			var label = body.AddChild<UILabel>( "documentation" );
			label.Text = description;
		} );
	}

	private void AddDebugSection( Node node )
	{
		if ( !node.HasDebugContent )
			return;

		AddSection( "Debug", "bug_report", "green", DebugExpandedCookie, node.BuildDebugWidgets );
	}

	//A section that folds away, remembering whether it was open under cookie. What goes in it is up to the caller.
	private void AddSection( string title, string icon, string accent, string cookie, Action<Panel> buildBody )
	{
		var section = _below.AddChild<Panel>( "section" );
		section.AddClass( accent );

		var expanded = ProjectCookie.Get( cookie, false );

		var header = section.AddChild( new PanelFoldHeader { Title = title, Icon = icon, Expanded = expanded } );

		//The band skin, so a section here reads as a section of the main editor's inspector rather than as something this dock invented.
		header.AddClass( "bar" );
		var body = section.AddChild<Panel>( "section-body" );

		void Show( bool open )
		{
			header.SyncContentVisible( open );
			body.SetClass( "hidden", !open );
		}

		header.OnExpandedChanged = open =>
		{
			Show( open );
			ProjectCookie.Set( cookie, open );
		};

		Show( expanded );

		buildBody( body );
	}

	private void AddConduitCard( Nodes.StateMachineNode.Conduit conduit )
		=> _below.AddChild( new ConduitCard( conduit ) );

	private void AddTransitionPriority( Nodes.StateMachineNode machine, Nodes.StateMachineNode.State state )
		=> _below.AddChild( new TransitionPriorityCard( machine, state, MoveTransition ) );

	private void AddConvertButton( Nodes.StateMachineNode.State state )
	{
		if ( _window.StateMachineCanvas is not { } canvas )
			return;

		switch ( state.Type )
		{
			case Nodes.StateMachineNode.StateType.BlendTree when StateMachineCanvas.CanConvertToStateMachineState( state ):
				AddButtonRow( "Convert to State Machine State", "hub", () =>
				{
					canvas.ConvertStateType( state, Nodes.StateMachineNode.StateType.StateMachine );
					RebuildSheet();
				} );
				break;
			case Nodes.StateMachineNode.StateType.StateMachine:
				AddButtonRow( "Convert to Blend Tree State", "account_tree", () =>
				{
					canvas.ConvertStateType( state, Nodes.StateMachineNode.StateType.BlendTree );
					RebuildSheet();
				} );
				break;
		}
	}

	private void AddButtonRow( string text, string icon, Action clicked )
	{
		var row = _below.AddChild<Panel>( "button-row" );

		//The editor's own button rather than one made here: what a button looks like is written once, in design.scss under .btn.
		var button = row.AddChild<TextButtonPanel>();
		button.Text = text;
		button.Icon = icon;
		button.Pressed = clicked;
	}

	//Explains where the data of a variation data node comes from, and lets you override or revert it without leaving the inspector.
	private void AddVariationBanner( VariationDataNode node, string variationID, bool inherited )
	{
		var source = _window.GetVariationDataSource( node, variationID );

		var banner = _above.AddChild<Panel>( "variation-banner" );
		banner.SetClass( "inherited", inherited );

		var mark = banner.AddChild<UILabel>( "material-icon" );
		mark.Text = inherited ? "link" : "edit_note";

		var label = banner.AddChild<UILabel>( "label" );
		label.Text = inherited ? $"Inherits from '{source}'" : $"Overrides for '{variationID}'";

		var button = banner.AddChild<UILabel>( "material-icon action" );
		button.Text = inherited ? "edit_note" : "undo";
		button.Tooltip = inherited
			? $"Give '{variationID}' its own copy of this data so it can be edited"
			: $"Remove this override so '{variationID}' inherits its data again";
		button.AddEventListener( "onclick", () => _window.SetVariationOverride( node, variationID, inherited ) );
	}

	//The data of the variation being edited, always in the same place so overriding doesn't move
	//anything around. Inherited data is read only - only the variation owning it can edit it.
	private void AddVariationData( SerializedObject so, bool inherited )
	{
		if ( !so.TryGetProperty( VariationDataNode.SelectedDataProperty, out var property ) )
			return;

		var host = _above.AddChild<Panel>( "variation-data" );
		host.SetClass( "is-disabled", inherited );

		var sheet = host.AddChild( new Inspectors.ControlSheet() );
		sheet.AddPropertyRow( property );
	}

	private static bool FilterProperty( SerializedProperty property, bool hideAnimName )
	{
		if ( property.HasAttribute<HideAttribute>() ) return false;
		if ( !property.HasAttribute<PropertyAttribute>() ) return false;
		if ( hideAnimName && property.Name == "AnimationName" ) return false;
		return true;
	}

	private bool MoveTransition( Nodes.StateMachineNode machine, Nodes.StateMachineNode.State state, Nodes.TransitionNode transition, int newIndex )
	{
		if ( !machine.MoveTransition( state, transition, newIndex ) )
			return false;

		_window.PushGraphUndo( "Reorder Transitions" );
		machine.Update();
		_window.PushGraphRedo();
		return true;
	}

	internal static string ConditionSummary( Nodes.TransitionNode transition )
		=> DescribeCondition( transition.GetConnectedNode( transition.Condition ), 0 );

	private static string DescribeCondition( Node node, int depth )
	{
		if ( node is null )
			return "No condition";

		if ( depth >= 3 )
			return node.DisplayInfo.Name ?? node.GetType().Name;

		return node switch
		{
			Nodes.BoolParameterNode parameter => string.IsNullOrWhiteSpace( parameter.ParameterName ) ? "Bool Parameter" : parameter.ParameterName,
			Nodes.ConstBoolNode constant => constant.Value ? "Always" : "Never",
			Nodes.NotNode not => $"not {DescribeInput( not, not.Value, depth )}",
			Nodes.AndNode and => and.Conditions.Count == 0
				? "Always"
				: string.Join( " and ", and.Conditions.Select( x => DescribeInput( and, x.Input, depth ) ) ),
			Nodes.OrNode or => $"{DescribeInput( or, or.A, depth )} or {DescribeInput( or, or.B, depth )}",
			Nodes.StateCompletedConditionNode completed => completed.AtClipEnd ? "At clip end" : "State completed",
			Nodes.TimeConditionNode time => TimeConditionSummary( time ),
			Nodes.FloatComparisonNode comparison => FloatComparisonSummary( comparison ),
			_ => node.DisplayInfo.Name ?? node.GetType().Name,
		};
	}

	private static string DescribeInput( Node owner, NodeInput input, int depth )
	{
		var source = owner.GetConnectedNode( input );
		var summary = DescribeCondition( source, depth + 1 );
		return source is Nodes.AndNode or Nodes.OrNode ? $"({summary})" : summary;
	}

	private static string FloatComparisonSummary( Nodes.FloatComparisonNode comparison )
	{
		var value = comparison.GetConnectedNode( comparison.Value )?.DisplayInfo.Name ?? "Value";
		var comparand = comparison.GetConnectedNode( comparison.Comparand )?.DisplayInfo.Name ?? comparison.ComparisonValue.ToString( "0.##" );
		var symbol = comparison.Comparison switch
		{
			AnimGraphBuilder.FloatComparison.GreaterThanEqual => "≥",
			AnimGraphBuilder.FloatComparison.LessThanEqual => "≤",
			AnimGraphBuilder.FloatComparison.NearEqual => "≈",
			AnimGraphBuilder.FloatComparison.GreaterThan => ">",
			AnimGraphBuilder.FloatComparison.LessThan => "<",
			_ => "?",
		};

		return $"{value} {symbol} {comparand}";
	}

	private static string TimeConditionSummary( Nodes.TimeConditionNode time )
	{
		var value = time.GetConnectedNode( time.ValueInput )?.DisplayInfo.Name ?? time.Value.ToString( "0.##" );
		return $"{TimeConditionName( time.Type )} {ComparisonSymbol( time.Operator )} {value}";
	}

	private static string TimeConditionName( AnimGraphBuilder.StateMachineBuilder.TimeComparisonType type ) => type switch
	{
		AnimGraphBuilder.StateMachineBuilder.TimeComparisonType.PercentageThroughState => "State progress",
		AnimGraphBuilder.StateMachineBuilder.TimeComparisonType.PercentageThroughSyncEvent => "Sync event progress",
		_ => "Elapsed time",
	};

	private static string ComparisonSymbol( AnimGraphBuilder.StateMachineBuilder.TimeComparison comparison ) => comparison switch
	{
		AnimGraphBuilder.StateMachineBuilder.TimeComparison.LessThan => "<",
		AnimGraphBuilder.StateMachineBuilder.TimeComparison.LessThanEqual => "≤",
		AnimGraphBuilder.StateMachineBuilder.TimeComparison.GreaterThan => ">",
		_ => "≥",
	};

	//---- the header ------------------------------------------------------

	private void UpdateHeader()
	{
		_header.Accent = PropertiesHeader.DefaultIconColor;

		switch ( _target )
		{
			case AnimationClipInfo clip:
				SetHeader( "Animation Clip", clip.AnimationName );
				_header.Icon = "movie";
				_header.Accent = Theme.Green;
				break;
			case Node node:
				var type = DisplayInfo.ForType( node.GetType() );
				SetHeader( type.Name ?? node.GetType().Name, node.DisplayInfo.Name );
				_header.Icon = node.DisplayInfo.Icon ?? type.Icon ?? "account_tree";
				_header.Accent = node.PrimaryColor;
				break;
			case Parameter parameter:
				SetHeader( $"{AnimParameterUI.TypeName( parameter.Type )} Parameter", parameter.Name );
				_header.Icon = AnimParameterUI.Icon( parameter.Type );
				_header.Accent = AnimParameterUI.Color( parameter.Type );
				break;
			case VirtualParameter parameter:
				SetHeader( $"Virtual {AnimParameterUI.TypeName( parameter.Type )} Parameter", parameter.Name );
				_header.Icon = AnimParameterUI.Icon( parameter.Type );
				_header.Accent = AnimParameterUI.Color( parameter.Type );
				break;
			case Nodes.StateMachineNode.State state:
				SetHeader( "State", state.Name );
				_header.Icon = "animation";
				_header.Accent = Theme.Red;
				break;
			case Nodes.StateMachineNode.Conduit conduit:
				SetHeader( "Conduit", $"{conduit.From} → {conduit.To}" );
				_header.Icon = "route";
				_header.Accent = Theme.Pink;
				break;
			case StateMachineCanvas.SharedSelection shared:
				SetHeader( shared.Title );
				_header.Icon = shared.Icon;
				break;
			case Nodes.Blend2DNode.BlendPoint point:
				SetHeader( "Blend Point", point.Name );
				_header.Icon = "grain";
				_header.Accent = Theme.Primary;
				break;
			case Nodes.Blend1DNode.BlendPoint point:
				SetHeader( "Blend Point", point.Name );
				_header.Icon = "linear_scale";
				_header.Accent = Theme.Primary;
				break;
			default:
				SetHeader( "Graph Settings" );
				_header.Icon = "settings";
				break;
		}

		var navigation = _window.GetNavigationAction( _target );
		_header.SetOpenAction( navigation?.Text, "open_in_full", navigation?.Action );
	}

	//Set the header to the selection's type name plus optional instance detail.
	private void SetHeader( string type, string detail = null )
	{
		_header.Text = type;
		_header.Detail = string.IsNullOrWhiteSpace( detail ) || detail == type ? null : detail;
	}

	//---- the preview scene ----------------------------------------------

	private void EnsureMiniScene()
	{
		if ( _miniScene is not null ) return;

		_miniScene = Scene.CreateEditorScene();
		_miniScene.Name = "Properties Preview";

		using ( _miniScene.Push() )
		{
			var camGo = new GameObject( true, "camera" );
			_miniCamera = camGo.AddComponent<CameraComponent>();
			_miniCamera.BackgroundColor = Theme.ControlBackground;
			_miniCamera.FieldOfView = 30f;
			_miniCamera.ZFar = 15000f;
			_miniCamera.ZNear = 0.1f;

			//Lit from the view the camera is put at - see FrameMiniModel
			PreviewLighting.HideSky( _miniCamera );
			PreviewLighting.Light( _miniScene, new Angles( 20, 225, 0 ) );
		}
	}

	private void RebuildMiniModel()
	{
		if ( _miniScene is null ) return;

		var model = _window.Graph?.TargetModel;
		_miniModel = model;

		using ( _miniScene.Push() )
		{
			_miniModelObject?.Destroy();
			_miniModelObject = null;
			_miniRenderer = null;
			_clip.Clear();

			if ( model is null )
				return;

			var go = new GameObject( true, "model" );
			go.WorldTransform = global::Transform.Zero;
			_miniModelObject = go;

			_miniRenderer = go.AddComponent<SkinnedModelRenderer>();
			_miniRenderer.Model = model;
			_miniRenderer.PlayAnimationsInEditorScene = true;

			//Without a clip this stays in its reference pose rather than running the model's legacy animgraph
			_miniRenderer.UseAnimGraph = false;

			if ( !string.IsNullOrWhiteSpace( _currentAnim ) )
			{
				_clip.Play( _miniRenderer, _currentAnim );
			}

			FrameMiniModel();
		}
	}

	//Frame the model to best fill the viewport. FieldOfView is horizontal, so for wide viewports the vertical axis constrains and the fov is converted.
	private void FrameMiniModel()
	{
		if ( _miniCamera is null || _miniModel is null )
			return;

		var bounds = _miniModel.Bounds;
		var radius = MathF.Max( bounds.Size.Length * 0.5f, 1f );

		var size = _clipPreview is { IsValid: true } ? _clipPreview.ScreenSize : default;

		var fov = _miniCamera.FieldOfView;
		var aspect = size.y > 0 ? size.x / size.y : 1f;
		if ( aspect > 1f )
			fov = MathF.Atan( MathF.Tan( fov.DegreeToRadian() * 0.5f ) / aspect ).RadianToDegree() * 2f;

		var distance = MathX.SphereCameraDistance( radius, fov ) * 1.05f;

		_miniCamera.WorldRotation = new Angles( 20, 225, 0 );
		_miniCamera.WorldPosition = bounds.Center - _miniCamera.WorldRotation.Forward * distance;
	}

	private float _miniTime;
	private Vector2 _lastPreviewSize;

	private void OnMiniPreFrame()
	{
		if ( _miniScene is null ) return;

		if ( _miniModel != _window.Graph?.TargetModel )
			RebuildMiniModel();

		var size = _clipPreview is { IsValid: true } ? _clipPreview.ScreenSize : default;

		if ( _lastPreviewSize != size )
		{
			_lastPreviewSize = size;
			FrameMiniModel();
		}

		using ( _miniScene.Push() )
		{
			if ( _poseNode is not null )
			{
				if ( _poseNode.UseFramesAsInput )
					_clip.Time = _poseNode.FixedTime / _clip.FrameRate;
				else
					_clip.TimeNormalized = Math.Clamp( _poseNode.FixedTime, 0f, 1f );
			}
			else
			{
				_clip.Advance( RealTime.Delta );
			}

			_miniTime += RealTime.Delta;
			_miniScene.EditorTick( _miniTime, RealTime.Delta );
		}
	}

	private void CleanupMiniScene()
	{
		_miniScene?.Destroy();
		_miniScene = null;
		_miniCamera = null;
		_miniModelObject = null;
		_miniRenderer = null;
		_miniModel = null;
	}
}

//What is selected, said in one line: an icon, its type, and its name.
internal sealed class PropertiesHeader : Panel
{
	public static readonly Color DefaultIconColor = global::Color.Parse( "#8E9199" ) ?? Theme.TextControl;

	public string Text
	{
		get;
		set
		{
			field = value;
			if ( _text.IsValid() ) _text.Text = value;
		}
	} = "Properties";

	public string Detail
	{
		get;
		set
		{
			field = value;

			var has = !string.IsNullOrWhiteSpace( value );

			_dot?.SetClass( "hidden", !has );
			_detail?.SetClass( "hidden", !has );

			if ( _detail.IsValid() ) _detail.Text = value ?? "";
		}
	}

	public string Icon
	{
		get;
		set
		{
			field = value;
			if ( _icon.IsValid() ) _icon.Text = value ?? "";
			_icon?.SetClass( "hidden", string.IsNullOrEmpty( value ) );
		}
	} = "settings";

	/// <summary>The colour of the selection - a node's own, a parameter's type colour. Written into the style because it is per-selection, which is the one thing a stylesheet cannot say.</summary>
	public Color Accent
	{
		get;
		set
		{
			field = value;

			if ( _icon.IsValid() ) _icon.Style.FontColor = value;
			if ( _text.IsValid() ) _text.Style.FontColor = value == DefaultIconColor ? Theme.Text : value;
		}
	} = DefaultIconColor;

	private readonly UILabel _icon;
	private readonly UILabel _text;
	private readonly UILabel _dot;
	private readonly UILabel _detail;
	private readonly UILabel _open;

	private Action _openAction;

	public PropertiesHeader()
	{
		AddClass( "properties-header" );

		_icon = AddChild<UILabel>( "material-icon icon" );
		_text = AddChild<UILabel>( "title" );

		_dot = AddChild<UILabel>( "dot hidden" );
		_dot.Text = "·";

		_detail = AddChild<UILabel>( "detail hidden" );

		_open = AddChild<UILabel>( "material-icon open hidden" );
		_open.AddEventListener( "onclick", () => _openAction?.Invoke() );
	}

	/// <summary>Show a right-anchored action - Open Full Graph, say - or hide it.</summary>
	public void SetOpenAction( string tooltip, string icon, Action action )
	{
		_openAction = action;

		_open.SetClass( "hidden", action is null );
		_open.Tooltip = tooltip;
		_open.Text = icon;
	}
}

//A conduit says how many rules decide whether it may be taken.
internal sealed class ConduitCard : Panel
{
	public ConduitCard( Nodes.StateMachineNode.Conduit conduit )
	{
		AddClass( "card" );

		var count = conduit.Transitions.Count();
		var none = count == 0;

		SetClass( "empty", none );

		var heading = AddChild<Panel>( "heading" );

		var title = heading.AddChild<UILabel>( "title" );
		title.Text = "Transition Rules";

		var status = heading.AddChild<UILabel>( "status" );
		status.Text = count == 1 ? "1 rule" : $"{count} rules";

		var details = AddChild<UILabel>( "details" );
		details.Text = none
			? $"No rules are defined, so {conduit.From} cannot transition to {conduit.To}."
			: $"Controls when {conduit.From} can transition to {conduit.To}.";
	}
}

//The order the transitions out of a state are tried in, and a way to change it.
internal sealed class TransitionPriorityCard : Panel
{
	private readonly Nodes.StateMachineNode _machine;
	private readonly Nodes.StateMachineNode.State _state;
	private readonly List<TransitionPriorityRow> _rows = new();

	public TransitionPriorityCard(
		Nodes.StateMachineNode machine,
		Nodes.StateMachineNode.State state,
		Func<Nodes.StateMachineNode, Nodes.StateMachineNode.State, Nodes.TransitionNode, int, bool> move )
	{
		_machine = machine;
		_state = state;

		AddClass( "card" );

		var routes = machine.GetOrderedTransitions( state );

		var heading = AddChild<Panel>( "heading" );

		var title = heading.AddChild<UILabel>( "title" );
		title.Text = "Transition Priority";

		var count = heading.AddChild<UILabel>( "status" );
		count.Text = routes.Count == 1 ? "1 transition" : $"{routes.Count} transitions";

		var details = AddChild<UILabel>( "details" );
		details.Text = routes.Count == 0
			? "This state has no outgoing transitions."
			: "The first transition with a true condition wins.";

		var list = AddChild<Panel>( "rows" );

		for ( var i = 0; i < routes.Count; i++ )
		{
			var index = i;

			var row = list.AddChild( new TransitionPriorityRow( state, routes[index], index, ( transition, newIndex ) =>
			{
				if ( move( _machine, _state, transition, newIndex ) )
					Reread();
			} ) );

			_rows.Add( row );
		}
	}

	//The order changed, so every row is showing a different transition.
	private void Reread()
	{
		var ordered = _machine.GetOrderedTransitions( _state );

		for ( var i = 0; i < _rows.Count && i < ordered.Count; i++ )
			_rows[i].Bind( ordered[i], i );
	}
}

//What is carried while a transition is being dragged up or down the order.
internal sealed record TransitionPriorityDrag(
	Nodes.StateMachineNode.State State,
	Nodes.TransitionNode Transition,
	int Index );

//One transition in the priority order, draggable to change it.
internal sealed class TransitionPriorityRow : Panel, IEditorDropTarget
{
	private readonly Nodes.StateMachineNode.State _state;
	private readonly Action<Nodes.TransitionNode, int> _moved;

	private Nodes.StateMachineNode.TransitionRoute _route;
	private int _index;

	private readonly UILabel _handle;
	private readonly UILabel _priority;
	private readonly UILabel _name;
	private readonly UILabel _summary;
	private readonly UILabel _target;
	private readonly UILabel _global;

	public TransitionPriorityRow(
		Nodes.StateMachineNode.State state,
		Nodes.StateMachineNode.TransitionRoute route,
		int index,
		Action<Nodes.TransitionNode, int> moved )
	{
		_state = state;
		_moved = moved;

		AddClass( "transition-row" );

		_handle = AddChild<UILabel>( "material-icon handle" );
		_handle.Text = "drag_indicator";

		_priority = AddChild<UILabel>( "priority" );

		var middle = AddChild<Panel>( "middle" );
		_name = middle.AddChild<UILabel>( "name" );
		_summary = middle.AddChild<UILabel>( "summary" );

		_global = AddChild<UILabel>( "badge hidden" );
		_global.Text = "global";

		_target = AddChild<UILabel>( "target" );

		Bind( route, index );
	}

	public void Bind( Nodes.StateMachineNode.TransitionRoute route, int index )
	{
		_route = route;
		_index = index;

		_priority.Text = (index + 1).ToString();
		_name.Text = string.IsNullOrWhiteSpace( route.Transition.TransitionName ) ? "Transition" : route.Transition.TransitionName;
		_summary.Text = PropertiesPanel.ConditionSummary( route.Transition );
		_target.Text = $"→ {route.TargetState}";

		_global.SetClass( "hidden", !route.IsGlobal );

		Tooltip = $"Drag to change priority.\nCondition: {_summary.Text}";
	}

	private bool _pressed;
	private bool _carrying;
	private Vector2 _pressAt;

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );

		if ( e.MouseButton != MouseButtons.Left ) return;

		_pressed = true;
		_carrying = false;
		_pressAt = Application.CursorPosition;
	}

	/// <summary>A press that has moved is a drag. Watched here rather than answered from a drag-start event, because that is how every other panel in this editor picks something up - see the clip list.</summary>
	public override void Tick()
	{
		base.Tick();

		if ( !_pressed || _carrying ) return;

		if ( ( Application.MouseButtons & MouseButtons.Left ) == 0 )
		{
			_pressed = false;
			return;
		}

		if ( Application.CursorPosition.Distance( _pressAt ) <= 4.0f ) return;

		_carrying = true;

		EditorDrag.Begin( this, new DragData
		{
			Text = _route.Transition.TransitionName,
			Object = new TransitionPriorityDrag( _state, _route.Transition, _index )
		}, null );
	}

	public bool AcceptEditorDrops => true;

	public DropAction OnEditorDragHover( Widget.DragEvent ev )
	{
		if ( ev.Data.Object is not TransitionPriorityDrag drag
			|| !ReferenceEquals( drag.State, _state )
			|| ReferenceEquals( drag.Transition, _route.Transition ) )
		{
			SetDropIndicator( false, false );
			return DropAction.Ignore;
		}

		SetDropIndicator( drag.Index > _index, drag.Index < _index );
		return DropAction.Move;
	}

	public void OnEditorDragDrop( Widget.DragEvent ev )
	{
		SetDropIndicator( false, false );

		if ( ev.Data.Object is TransitionPriorityDrag drag && ReferenceEquals( drag.State, _state ) )
			_moved( drag.Transition, _index );
	}

	public void OnEditorDragLeave() => SetDropIndicator( false, false );

	private void SetDropIndicator( bool above, bool below )
	{
		SetClass( "drop-above", above );
		SetClass( "drop-below", below );
	}
}

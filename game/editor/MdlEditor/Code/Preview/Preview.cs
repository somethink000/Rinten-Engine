using Editor.Inspectors;
using Rinten.UI;

using UILabel = Rinten.UI.Label;
using UIOption = Rinten.UI.Option;

namespace Editor.MdlEditor;

//Everything around the picture - the strip of tools over it, the rounded groups its buttons stand in, the transport under it - is a box in a box, so it is markup and a stylesheet rather than a handful of paint overrides.
/// <summary>3D preview dock that renders the graph's target model with animation.</summary>
[StyleSheet( "Preview.cs.scss" )]
public partial class Preview : PreviewScenePanel
{
	private readonly IPreviewHost _host;
	private AnimGraph _animationGraph;
	private bool _isViewModel;
	private Data _boundGraph;
	private string _boundVariationID;
	private SliderControl _speedSlider;
	private UILabel _speedLabel;
	private readonly RecorderToolbar _recorder;
	private FocusBoneSettings _focusSettings;
	private PreviewGizmoOptions _gizmoOptions;
	private Panel _focusHost;
	private readonly List<PreviewToolButton> _toolButtons = [];
	private bool _ragdollActive;
	private bool _paused;
	private bool _reviewingRecording;
	private Transform _preReviewTransform;
	private int _recordedTreadmillUpdate = -1;
	private bool _showRootMotion;
	private bool _showActiveAnimations;
	private Vector3 _rootVelocity;
	private bool _showBones;
	private bool _boneNamesAlways;
	private bool _boneAxes;
	private float _boneScale = 1f;
	private bool _showAttachments;
	private bool _showHulls;
	private bool _showJoints;

	//The shading options in the display menu, so picking one clears the rest.
	//Qualified: Editor.Checkbox is the widget of the same name.
	private readonly List<(Rinten.UI.Checkbox Box, SceneCameraDebugMode Mode)> _shadingOptions = [];
	private readonly List<string> _hoveredBones = [];
	private string _highlightedBone;
	private ModelDocItem _modelSelection;
	private bool _modelMode;
	private SceneCameraDebugMode _debugMode;
	private bool _wireframe;
	private Panel _graphPlayback;

	//The hairline after the playback tools - gone with them, or it stands alone.
	private Panel _graphSeparator;
	private int _simBodies;
	private bool _showGround;
	private float _timeScale = 1f;
	private float _fov;
	private float _fovViewModel;
	private ModelPhysics _ragdoll;
	private GameObject _ragdollFloor;
	private PreviewToolButton _simulateToggle;

	//Simulating a model with no bodies but jiggle bones: the model is dragged about and they swing
	private bool _shaking;
	private Transform _preShakeTransform;

	private bool IsSimulating => _ragdollActive || _shaking;
	private Panel _modelPreviewHost;
	private bool _fillingModelPreview;
	private string _modelPreviewKey;
	private string _previewMaterialGroup = "";
	private readonly Dictionary<string, string> _modelBodyChoices = new();
	private int _modelPreviewHash;
	private readonly float[] _samples = new float[120];
	private int _sampleCount;
	private int _sampleHead;
	private readonly List<(string Name, float Cycle, float Duration)> _activeAnimations = [];

	//One clip played straight on the model, for the model half.
	private readonly ClipPreview _clipPreview = new();
	private string _clipPath;

	//Plays one clip on the preview model; kept as a path so it survives the renderer being remade
	internal void PlayClip( string clipPath )
	{
		_clipPath = string.IsNullOrWhiteSpace( clipPath ) ? null : clipPath;

		if ( _clipPath is null )
		{
			_clipPreview.Clear();
			return;
		}

		_clipPreview.Play( Renderer, _clipPath );
	}

	//Stop it, leaving the model in its reference pose.
	internal void StopClip()
	{
		_clipPath = null;
		_clipPreview.Clear();
	}

	internal readonly struct PreviewReloadState
	{
		public bool Simulating { get; init; }
		public string ClipPath { get; init; }
		public string ClipName { get; init; }
		public bool Looping { get; init; }
		public Vector2 Angles { get; init; }
		public Vector3 Origin { get; init; }
		public Vector3 Center { get; init; }
		public float Distance { get; init; }
	}

	internal PreviewReloadState CaptureForReload() => new()
	{
		Simulating = IsSimulating,
		ClipPath = _clipPath,
		ClipName = PickedClipName,
		Looping = _clipPreview.Looping,
		Angles = _angles,
		Origin = _origin,
		Center = _center,
		Distance = _distance,
	};

	internal void RestoreAfterReload( PreviewReloadState state )
	{
		_angles = state.Angles;
		_origin = state.Origin;
		_center = state.Center;
		_distance = state.Distance;
		UpdateCamera();

		FillClipList( _host.ModelDefinition );

		if ( ClipStillPresent( state.ClipName ) && !string.IsNullOrWhiteSpace( state.ClipPath ) )
		{
			PlayClip( state.ClipPath );
			_clipPreview.Looping = state.Looping;
			if ( _loopButton is not null )
				_loopButton.IsActive = state.Looping;

			PickedClipName = state.ClipName;
		}

		if ( state.Simulating )
			SetSimulating( true );
	}

	bool ClipStillPresent( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) || name == "(none)" )
			return false;

		var clips = _host.ModelDefinition?.AllClips();
		if ( clips is null )
			return false;

		foreach ( var clip in clips )
		{
			if ( clip is not null && string.Equals( clip.SequenceName, name, StringComparison.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}

	public SkinnedModelRenderer ModelRenderer => Renderer;
	public Scene PreviewScene => Scene;
	public bool IsPaused => _paused;
	public bool IsCompiling
	{
		set => SetCompiling( value );
	}

	public Preview( IPreviewHost host ) : base( "AnimGraph Preview" )
	{
		_host = host;

		AddClass( "preview" );

		_paused = Cookie( "paused", false );
		_showRootMotion = Cookie( "rootmotion", false );
		_showActiveAnimations = Cookie( "activeanimations", false );
		_showBones = Cookie( "bones", false );
		_boneNamesAlways = Cookie( "bones.names", false );
		_boneAxes = Cookie( "bones.axes", false );
		_boneScale = Cookie( "bones.scale", 1f );
		_showAttachments = Cookie( "attachments", false );
		_showHulls = Cookie( "hulls", false );
		_showJoints = Cookie( "joints", false );
		_debugMode = (SceneCameraDebugMode)Cookie( "debugmode", 0 );
		_wireframe = Cookie( "wireframe", false );
		_showGround = Cookie( "ground", true );
		_fov = Cookie( "fov", 60f );
		_fovViewModel = Cookie( "fov.viewmodel", 30f );

		BuildToolbar();
		BuildTimeline();

		//Under the picture, where the recording controls have always been.
		_recorder = AddChild( new RecorderToolbar( host ) );

		SetTimeScale( Cookie( "speed", 1f ) );
		SetPaused( _paused );

		RebuildModel();
	}

	//Graph playback tools - hidden while the model half is up.
	internal void SetGraphToolsVisible( bool visible )
	{
		if ( _recorder.IsValid() )
			_recorder.SetClass( "hidden", !visible );

		if ( _graphPlayback.IsValid() )
			_graphPlayback.SetClass( "hidden", !visible );

		//The hairline divides the playback tools from the rest; with nothing on its left it is a line before the first button.
		if ( _graphSeparator.IsValid() )
			_graphSeparator.SetClass( "hidden", !visible );
	}

	internal void SetModelMode( bool modelMode )
	{
		_modelMode = modelMode;
		if ( modelMode )
		{
			_showBones = Cookie( "bones", true );
			_showAttachments = Cookie( "attachments", true );
		}
		else
		{
			SetSimulating( false );
		}

		//The transport is not on the picture in either half: the model editor stands it in its Timeline dock, over the clip's timeline.
		SetGraphToolsVisible( !modelMode );
		FillClipList( _host.ModelDefinition );
		EnsureModelPreviewTools();
		UpdateSimulateButton();
		ApplyViewMode();
	}

	internal void SetHighlightedBone( string bone ) => _highlightedBone = bone;

	//The model page selection; overlays draw it and picked body group choices show here
	internal void SetModelSelection( ModelDocItem item )
	{
		_modelSelection = item;

		if ( item?.Kind == ModelDocKind.BodyGroupChoice ) FollowBodyChoice( item );
	}

	//Show the choice somebody just picked in the tree.
	void FollowBodyChoice( ModelDocItem item )
	{
		var groups = _host.ModelDefinition?.BodyGroups;
		if ( groups is null || item.ParentIndex < 0 || item.ParentIndex >= groups.Count ) return;

		var group = groups[item.ParentIndex];
		if ( group?.Choices is not { } choices ) return;
		if ( item.Index < 0 || item.Index >= choices.Count ) return;

		var name = group.Name ?? "";
		if ( string.IsNullOrWhiteSpace( name ) ) return;

		var choice = choices[item.Index]?.Name ?? "";

		SyncModelPreviewKey();

		if ( _modelBodyChoices.TryGetValue( name, out var showing ) && showing == choice ) return;

		_modelBodyChoices[name] = choice;
		SetCookie( $"model.{_modelPreviewKey}.body.{name}", choice );

		//The combo over the viewport is filled from the same table, so it is rebuilt rather than left saying what was showing a moment ago.
		RebuildModelPreviewTools();
	}


	private static T Cookie<T>( string key, T fallback ) => EditorCookie.Get( $"animgraph.preview.{key}", fallback );
	private static void SetCookie<T>( string key, T value ) => EditorCookie.Set( $"animgraph.preview.{key}", value );

	private void BuildToolbar()
	{
		//Playback - pause, single step, speed
		_graphPlayback = Toolbar.AddChild<Panel>( "row" );

		var playback = AddGroup( _graphPlayback );
		AddToggle( playback, "pause", "Pause", () => _paused, SetPaused );
		AddButton( playback, "skip_next", "Step one frame", () => { SetPaused( true ); StepFrame(); } );
		AddButton( playback, "replay", "Restart the graph from its initial state", RestartGraph );

		var speed = AddGroup( _graphPlayback );
		AddButton( speed, "speed", "Playback speed - click to reset", () => SetTimeScale( 1f ) );

		_speedSlider = speed.AddChild( new SliderControl( 0f, 2f, 0.05f ) { ShowBackground = false } );
		_speedSlider.AddClass( "speed toolbar-slider" );
		_speedSlider.Value = 1f;
		_speedSlider.Tooltip = "Playback speed";
		_speedSlider.OnValueChanged = SetTimeScale;

		_speedLabel = speed.AddChild<UILabel>( "reading" );

		_graphSeparator = Toolbar.AddChild<Panel>( "separator" );

		var options = AddGroup( Toolbar );
		PreviewToolButton displayOptions = null;
		displayOptions = AddButton( options, "visibility", "Preview display options", () => OpenDisplayOptions( displayOptions ) );

		PreviewToolButton gizmoOptions = null;
		gizmoOptions = AddButton( options, "grid_on", "Grid and gizmo settings", () => OpenGizmoSettings( gizmoOptions ) );

		//Filled by RebuildModelPreviewTools, and gone while the model has no skins or body groups to pick between.
		_modelPreviewHost = Toolbar.AddChild<Panel>( "group model-preview hidden" );

		Toolbar.AddChild<Panel>( "spacer" );

		var camRow = Toolbar.AddChild<Panel>( "row" );

		var simulation = AddGroup( camRow );
		_simulateToggle = AddToggle( simulation, "sports_basketball", "Simulate physics", () => IsSimulating, SetSimulating );

		_focusHost = camRow.AddChild<Panel>( "row" );

		_focusSettings = new FocusBoneSettings { Bone = FocusBone };
		var focusSo = _focusSettings.GetSerialized();
		focusSo.OnPropertyChanged += _ => SetFocusBone( _focusSettings.Bone );

		var focusPicker = _focusHost.AddChild( new FocusBonePicker
		{
			Preview = this,
			Property = focusSo.GetProperty( nameof( FocusBoneSettings.Bone ) ),
		} );

		focusPicker.AddClass( "focus-picker" );

		//After the property, which puts the property's own description here.
		focusPicker.Tooltip = "Focus the camera on a bone";

		var view = AddGroup( _focusHost );
		AddButton( view, "center_focus_strong", "Frame camera on model", FrameCamera );

		var cam = AddGroup( camRow );
		PreviewToolButton camButton = null;
		camButton = AddButton( cam, "video_settings", "Camera settings", () => OpenCameraSettings( camButton ) );
	}

	private void OpenDisplayOptions( Panel button )
	{
		var popup = OpenToolbarPopup( button );

		_shadingOptions.Clear();

		AddDisplayOption( popup, "Root Motion Velocity", "north_east", _showRootMotion, value => SetCookie( "rootmotion", _showRootMotion = value ) );
		AddDisplayOption( popup, "Active Animation Names", "movie", _showActiveAnimations, value => SetCookie( "activeanimations", _showActiveAnimations = value ) );
		AddDisplayOption( popup, "Bones", "device_hub", _showBones, value => SetCookie( "bones", _showBones = value ) );
		AddDisplayOption( popup, "Bone Names", "label", _boneNamesAlways, value => SetCookie( "bones.names", _boneNamesAlways = value ) );
		AddDisplayOption( popup, "Bone Axes", "control_camera", _boneAxes, value => SetCookie( "bones.axes", _boneAxes = value ) );

		AddPopupHeading( popup, "Bone Scale" );

		var boneScale = popup.AddChild( new SliderControl( 0.25f, 4f, 0.05f ) );
		boneScale.Value = _boneScale;
		boneScale.Tooltip = "Bone handle size";
		boneScale.OnValueChanged = value => SetCookie( "bones.scale", _boneScale = value );

		AddDisplayOption( popup, "Attachments", "push_pin", _showAttachments,
			value => SetCookie( "attachments", _showAttachments = value ) );
		AddDisplayOption( popup, "Physics Hulls", "hexagon", _showHulls, value => SetCookie( "hulls", _showHulls = value ) );
		AddDisplayOption( popup, "Physics Joints", "link", _showJoints, value => SetCookie( "joints", _showJoints = value ) );
		AddDisplayOption( popup, "Ground", "grid_4x4", _showGround, SetGroundVisible );
		AddDisplayOption( popup, "Wireframe", "grid_on", _wireframe, value => { SetCookie( "wireframe", _wireframe = value ); ApplyViewMode(); } );

		AddPopupHeading( popup, "Shading" );

		AddShadingOption( popup, "Lit", SceneCameraDebugMode.Normal );
		AddShadingOption( popup, "Full Bright", SceneCameraDebugMode.FullBright );
		AddShadingOption( popup, "Albedo", SceneCameraDebugMode.Albedo );
		AddShadingOption( popup, "World-Space Normals", SceneCameraDebugMode.NormalMap );
		AddShadingOption( popup, "UV Maps", SceneCameraDebugMode.ShowUV );
		AddShadingOption( popup, "Roughness", SceneCameraDebugMode.Roughness );

		popup.SyncAnchor();
	}

	//One shading mode. They are boxes rather than a list of one-of because that is what they have always looked like - so picking one puts the others out.
	void AddShadingOption( Panel popup, string title, SceneCameraDebugMode mode )
	{
		var checkbox = AddOptionBox( popup, title, "tune", _debugMode == mode );

		_shadingOptions.Add( (checkbox, mode) );

		checkbox.ValueChanged = _ =>
		{
			_debugMode = mode;
			SetCookie( "debugmode", (int)mode );
			ApplyViewMode();

			foreach ( var (box, boxMode) in _shadingOptions )
			{
				if ( box.IsValid() ) box.Value = boxMode == mode;
			}
		};
	}

	void ApplyViewMode()
	{
		if ( Camera is null ) return;
		Camera.DebugMode = _debugMode;
		Camera.WireframeMode = _wireframe;
	}

	private static void AddDisplayOption( Panel popup, string title, string icon, bool value, Action<bool> setValue )
	{
		var checkbox = AddOptionBox( popup, title, icon, value );

		checkbox.ValueChanged = setValue;
	}

	//A box, the glyph that says what it is about, and its name.
	//Qualified: Editor.Checkbox is the widget of the same name.
	private static Rinten.UI.Checkbox AddOptionBox( Panel popup, string title, string icon, bool value )
	{
		var checkbox = popup.AddChild<Rinten.UI.Checkbox>( "option" );

		//Before the text: the label is added when LabelText is written, and it goes on the end of whatever is already there.
		checkbox.AddChild<UILabel>( "material-icon" ).Text = icon;

		checkbox.LabelText = title;
		checkbox.Value = value;

		return checkbox;
	}

	private static void AddPopupHeading( Panel popup, string title )
		=> popup.AddChild<UILabel>( "heading" ).Text = title;

	private Panel AddGroup( Panel row ) => row.AddChild<Panel>( "group" );

	private PreviewToolButton AddToggle( Panel parent, string icon, string tooltip, Func<bool> getValue, Action<bool> setValue )
	{
		var button = AddButton( parent, icon, tooltip, () => setValue( !getValue() ) );
		button.IsActive = getValue;
		return button;
	}

	private PreviewToolButton AddButton( Panel parent, string icon, string tooltip, Action onClick )
	{
		var button = parent.AddChild( new PreviewToolButton
		{
			Icon = icon,
			Tooltip = tooltip,
			Action = onClick,
		} );

		_toolButtons.Add( button );
		return button;
	}

	//Gizmo settings popup: snapping, grid and handle size; setters shared with the shortcuts
	private void OpenGizmoSettings( Panel button )
	{
		var popup = OpenToolbarPopup( button );
		popup.AddClass( "wide" );

		//Held rather than made here, so the sheet's rows are reading and writing the same object next time this is opened.
		_gizmoOptions ??= new PreviewGizmoOptions();

		var so = _gizmoOptions.GetSerialized();

		//The floating move/rotate/scale bar draws the world-space button, and world space is one of the rows in here.
		so.OnPropertyChanged += _ => PlacementToolChanged?.Invoke();

		//By its full name: Preview lives in Editor.MdlEditor with `using Editor`, where a bare ControlSheet is
		//Editor.ControlSheet - the widget one, built on a GridLayout, which a popup made of panels has nowhere to put.
		popup.AddChild( new Editor.Inspectors.ControlSheet
		{
			Target = so,
			ShowPropertyNameInTooltip = true,
		} );

		AddPopupHeading( popup, "Hold Ctrl while dragging to invert whichever snapping is set." );

		popup.SyncAnchor();
	}

	private void OpenCameraSettings( Panel button )
	{
		var viewModel = _isViewModel;
		var popup = OpenToolbarPopup( button );

		AddPopupHeading( popup, "Field Of View" );

		var slider = popup.AddChild( new SliderControl( 10f, 90f, 1f ) );
		slider.Value = viewModel ? _fovViewModel : _fov;
		slider.OnValueChanged = value =>
		{
			if ( viewModel )
				SetCookie( "fov.viewmodel", _fovViewModel = value );
			else
				SetCookie( "fov", _fov = value );
		};

		popup.SyncAnchor();
	}

	//A menu hanging off a toolbar button. It goes on the window's overlay rather than under the button, which is what keeps a long
	//list from being clipped by the strip it opened from and what puts it back inside the window when there is no room below.
	static PreviewPopup OpenToolbarPopup( Panel button )
	{
		Popup.CloseAll();

		var popup = new PreviewPopup();

		popup.SetPositioning( button, Popup.PositionMode.BelowLeft, 2.0f );
		popup.CloseWhenParentIsHidden = true;

		return popup;
	}

	private void RestartGraph()
	{
		if ( !Renderer.IsValid() )
			return;

		using ( Scene.Push() )
		{
			Renderer.AnimGraph = null;
			Renderer.AnimGraph = _animationGraph;
		}
	}

	private void SetPaused( bool paused )
	{
		_paused = paused;
		ApplyTimeScale();
		SetCookie( "paused", paused );
	}

	private void SetTimeScale( float scale )
	{
		_timeScale = scale;
		ApplyTimeScale();

		_speedSlider.Value = scale;
		_speedLabel.Text = $"{scale:0.00}x";
		SetCookie( "speed", scale );
	}

	//Pausing is for the graph's own playback - a ragdoll being tested isn't animation, and zeroing Scene.TimeScale
	//for it stops physics right along with it, which reads as the ragdoll toggle doing nothing while paused.
	private void ApplyTimeScale()
	{
		Scene.TimeScale = _paused && !_ragdollActive ? 0f : _timeScale;
	}

	private void SetGroundVisible( bool visible )
	{
		_showGround = visible;
		ShowGrid = visible && !_isViewModel;
		SetCookie( "ground", visible );
	}

	//Follow the graph's target model, so the preview rebuilds when it changes. This used to be a model field under the viewport as well.
	//A graph lives beside the model it animates now, so what it targets is not something to be picked here - only something to be watched.
	private void RebindGraph()
	{
		var variationID = _host.Graph?.SelectedVariationID;
		if ( _host.Graph == _boundGraph && variationID == _boundVariationID )
			return;

		_boundGraph = _host.Graph;
		_boundVariationID = variationID;

		if ( _boundGraph is null )
			return;

		var so = _boundGraph.GetSerialized();
		so.OnPropertyChanged += p =>
		{
			if ( p.Name == nameof( Data.TargetModel ) )
				RebuildModel();
		};
		so.OnPropertyFinishEdit += p => _host.CommitChange( $"Edit {p?.Name ?? "Graph"}" );
	}

	private void SetSimulating( bool enabled )
	{
		//The preview's scene goes when the preview does, and everything the ragdoll had was in it. Asked after that - the window hands the preview
		//between its two halves as it tears down, and handing it back turns model mode off - there is nothing to push and nothing left to undo.
		if ( !Scene.IsValid() )
		{
			_ragdoll = null;
			_ragdollFloor = null;
			_ragdollActive = false;
			_shaking = false;
			return;
		}

		using ( Scene.Push() )
		{
			_ragdoll?.Destroy();
			_ragdoll = null;
			_ragdollFloor?.Destroy();
			_ragdollFloor = null;
			TreadmillEnabled = true;
			_ragdollActive = false;
			ApplyTimeScale();

			//Back where it stood before it was dragged about
			if ( _shaking && ModelObject.IsValid() )
				ModelObject.WorldTransform = _preShakeTransform;

			_shaking = false;
			ModelDraggable = false;

			if ( !enabled || !Renderer.IsValid() )
			{
				//Hand the pose back to the graph - without this the renderer keeps whatever UseAnimGraph was left
				//at, which is off, and the model freezes in its ragdolled pose instead of resuming playback.
				if ( Renderer.IsValid() )
					BindAnimationGraph();

				return;
			}

			if ( Model?.Physics is not { Parts.Count: > 0 } )
			{
				//No bodies to drop: what swings on its own is the jiggle bones, and moving the model is what swings them
				if ( JiggleBoneCount() > 0 && ModelObject.IsValid() )
				{
					_preShakeTransform = ModelObject.WorldTransform;
					_shaking = true;
					ModelDraggable = true;
				}

				return;
			}

			_ragdollFloor = new GameObject( true, "floor" );
			var floor = _ragdollFloor.AddComponent<PlaneCollider>();
			floor.Static = true;
			floor.Scale = new Vector2( 8192, 8192 );

			//The treadmill cancels root motion - hand its velocity to the bodies
			var rootVelocity = Renderer.WorldRotation * Renderer.RootMotion.Position / RealTime.Delta.Clamp( 0.001f, 1f );

			//The graph would otherwise keep driving bones every frame, fighting the physics bodies
			//for them - same as PlayerController.CreateRagdoll does for the in-game version of this.
			Renderer.UseAnimGraph = false;

			_ragdoll = ModelObject.AddComponent<ModelPhysics>();

			//Skeletons ignore root (the treadmill places them); boneless bodies need root to move at all
			_ragdoll.IgnoreRoot = Model.BoneCount > 0;

			_ragdoll.Renderer = Renderer;
			_ragdoll.Model = Model;
			_ragdoll.CopyBonesFrom( Renderer, false );

			foreach ( var body in ModelObject.GetComponentsInChildren<Rigidbody>() )
				body.Velocity += rootVelocity;

			TreadmillEnabled = false;
			_ragdollActive = true;
			ApplyTimeScale();
			_simBodies = ModelObject.GetComponentsInChildren<Rigidbody>().Count();
		}
	}

	//Jiggle bones the definition switches on, which the preview swings whether or not anything else simulates
	int JiggleBoneCount()
		=> _host.ModelDefinition?.JiggleBones?.Count( j => j is { Enabled: true } && !string.IsNullOrWhiteSpace( j.Bone ) ) ?? 0;

	bool IsModelPreview => _modelMode || _host.IsModelPreview;

	//What the button would run on this model, shown on it; dimmed with what to add when there is nothing
	void UpdateSimulateButton()
	{
		if ( _simulateToggle is null )
			return;

		var physics = Model?.Physics;
		var jiggles = JiggleBoneCount();

		var tooltip = physics is { Parts.Count: > 0 }
			? physics.Joints.Count > 0 ? "Simulate physics - ragdoll the model; drag a limb to pull it" : "Simulate physics - drop the model's bodies; drag one to throw it"
			: jiggles > 0 ? "Simulate - drag the model about to swing its jiggle bones"
			: "Nothing to simulate - give the model colliders or jiggle bones";

		if ( _simulateToggle.Tooltip != tooltip )
			_simulateToggle.Tooltip = tooltip;

		_simulateToggle.SetClass( "unavailable", physics is not { Parts.Count: > 0 } && jiggles == 0 );
	}

	void SyncModelPreviewKey()
	{
		var key = Model?.Name ?? _host.ModelAsset?.Path ?? "";
		if ( key == _modelPreviewKey )
			return;

		_modelPreviewKey = key;
		_previewMaterialGroup = Cookie( $"model.{key}.skin", "" );
		_modelBodyChoices.Clear();
	}

	void EnsureModelPreviewTools()
	{
		if ( !_modelPreviewHost.IsValid() )
			return;

		var show = IsModelPreview && HasModelPreviewChoices();
		var shown = !_modelPreviewHost.HasClass( "hidden" );
		var hash = HashModelPreviewLayout();
		if ( shown == show && hash == _modelPreviewHash )
			return;

		_modelPreviewHash = hash;
		RebuildModelPreviewTools();
	}

	int HashModelPreviewLayout()
	{
		var hash = new HashCode();
		hash.Add( IsModelPreview );
		hash.Add( Model?.Name );
		foreach ( var name in CollectPreviewMaterialGroups() )
			hash.Add( name );
		foreach ( var (name, choices) in CollectPreviewBodyGroups() )
		{
			hash.Add( name );
			foreach ( var choice in choices )
				hash.Add( choice );
		}

		return hash.ToHashCode();
	}

	bool HasModelPreviewChoices()
		=> CollectPreviewMaterialGroups().Count > 0 || CollectPreviewBodyGroups().Count > 0;

	List<string> CollectPreviewMaterialGroups()
	{
		var names = new List<string>();
		var groups = _host.ModelDefinition?.MaterialGroups;
		if ( groups is { Count: > 0 } )
		{
			foreach ( var set in groups )
			{
				if ( set is null || string.IsNullOrWhiteSpace( set.Name ) )
					continue;
				names.Add( set.Name );
			}

			return names;
		}

		if ( Model is { MaterialGroupCount: > 0 } )
		{
			for ( var i = 0; i < Model.MaterialGroupCount; i++ )
				names.Add( Model.GetMaterialGroupName( i ) );
		}

		return names;
	}

	List<(string Name, List<string> Choices)> CollectPreviewBodyGroups()
	{
		var list = new List<(string, List<string>)>();
		var groups = _host.ModelDefinition?.BodyGroups;
		if ( groups is { Count: > 0 } )
		{
			foreach ( var group in groups )
			{
				if ( group is null || !group.Enabled || group.Choices is not { Count: > 0 } )
					continue;

				var choices = new List<string>();
				foreach ( var choice in group.Choices )
					choices.Add( choice?.Name ?? "" );
				list.Add( (group.Name ?? "", choices) );
			}

			return list;
		}

		if ( Model is not null )
		{
			foreach ( var part in Model.Parts.All )
			{
				if ( part.Choices.Count == 0 )
					continue;

				var choices = new List<string>( part.Choices.Count );
				foreach ( var choice in part.Choices )
					choices.Add( choice.Name );
				list.Add( (part.Name, choices) );
			}
		}

		return list;
	}

	void RebuildModelPreviewTools()
	{
		if ( !_modelPreviewHost.IsValid() )
			return;

		SyncModelPreviewKey();
		_fillingModelPreview = true;
		_modelPreviewHost.DeleteChildren( true );

		var skins = CollectPreviewMaterialGroups();
		var bodies = CollectPreviewBodyGroups();
		if ( skins.Count > 0 )
		{
			//"Default" is what no material group at all is called, and it is valued as the empty string so the two never have to be told apart.
			var options = new List<UIOption> { new( "Default", "" ) };
			foreach ( var name in skins )
				options.Add( new UIOption( name, name ) );

			//Wider than a body group picker: a material group is named by whoever authored the model and those names run long.
			var combo = AddModelPreviewCombo( "Material Group", options, _previewMaterialGroup );
			combo.AddClass( "skin" );
			combo.ValueChanged = OnPreviewMaterialGroupPicked;
		}

		foreach ( var (name, choices) in bodies )
		{
			if ( !_modelBodyChoices.TryGetValue( name, out var selected ) )
			{
				selected = Cookie( $"model.{_modelPreviewKey}.body.{name}", choices[0] );
				_modelBodyChoices[name] = selected;
			}

			var options = new List<UIOption>();
			foreach ( var choice in choices )
				options.Add( new UIOption( string.IsNullOrEmpty( choice ) ? "(none)" : choice, choice ?? "" ) );

			var groupName = name;
			var combo = AddModelPreviewCombo( string.IsNullOrWhiteSpace( name ) ? "Body Group" : name, options, selected );
			combo.ValueChanged = choice => OnPreviewBodyGroupPicked( groupName, choice );
		}

		_fillingModelPreview = false;
		_modelPreviewHost.SetClass( "hidden", !IsModelPreview || (skins.Count == 0 && bodies.Count == 0) );
	}

	//One picker over the viewport, showing whichever option is on.
	ControlDropDown AddModelPreviewCombo( string tooltip, List<UIOption> options, string value )
	{
		var combo = _modelPreviewHost.AddChild<ControlDropDown>( "picker" );

		combo.Tooltip = tooltip;
		combo.Options = options;

		//Falls back to the first option, which is how a choice the model no longer has reads: whatever it does have.
		var known = options.Any( o => (string)o.Value == (value ?? "") );
		combo.Value = known ? value ?? "" : (string)options[0].Value;

		return combo;
	}

	void OnPreviewMaterialGroupPicked( string group )
	{
		if ( _fillingModelPreview )
			return;

		_previewMaterialGroup = group ?? "";
		SetCookie( $"model.{_modelPreviewKey}.skin", _previewMaterialGroup );
		ApplyModelPreviewSettings();
	}

	void OnPreviewBodyGroupPicked( string group, string choice )
	{
		if ( _fillingModelPreview )
			return;

		_modelBodyChoices[group] = choice ?? "";
		SetCookie( $"model.{_modelPreviewKey}.body.{group}", choice ?? "" );
		ApplyModelPreviewSettings();
	}

	void ApplyModelPreviewSettings()
	{
		if ( !Renderer.IsValid() )
			return;

		ClearPreviewAttachments();
		SyncModelPreviewKey();

		Renderer.MaterialGroup = string.IsNullOrEmpty( _previewMaterialGroup ) ? null : _previewMaterialGroup;

		if ( Model is null )
			return;

		Renderer.BodyGroups = BodyMask.Unset;
		foreach ( var (name, choice) in _modelBodyChoices )
			Renderer.SetBodyGroup( name, choice );

		ApplyPreviewMorphs();
	}

	//Pushes each blend shape as far as the page says, so a shape can be tried here rather than by putting the model in a scene to find out.
	//Every frame, because this is what a slider being dragged looks like from here - the page writes the number and the next frame shows it.
	private void ApplyPreviewMorphs()
	{
		var listed = _host.ModelDefinition?.Morphs;
		if ( listed is not { Count: > 0 } ) return;

		foreach ( var morph in listed )
		{
			if ( morph is null || string.IsNullOrWhiteSpace( morph.Name ) ) continue;

			//A shape that is switched off is not in the compiled model, so there is nothing to push and asking would only name it.
			Renderer.Morphs.Set( morph.Name, morph.Enabled ? morph.Preview : 0.0f );
		}
	}

	private void SetFocusBone( string bone )
	{
		FocusBone = bone;
		_focusSettings.Bone = bone;

		if ( _host.Graph is { } graph && graph.PreviewFocusBone != bone )
		{
			graph.PreviewFocusBone = bone;
			_host.MarkDirty( false );
		}
	}

	public void RebuildModel( bool force = false )
	{
		RebindGraph();

		var model = _host.OpenModel ?? _host.Graph?.TargetModel;
		if ( Model != model )
			_recorder.Clear();

		var modelChanged = SetModel( model, force );

		if ( _modelMode )
			FillClipList( _host.ModelDefinition );

		ApplyViewMode();

		UpdateSimulateButton();
		if ( _modelMode )
			EnsureModelPreviewTools();

		//Saved focus bone that exists on this model, or nothing
		var focus = _host.Graph?.PreviewFocusBone;
		FocusBone = focus is not null && Model?.Bones.GetBone( focus ) is not null ? focus : null;
		_focusSettings.Bone = FocusBone;

		//Same model - keep the live object, recreating it would pop the preview
		if ( !modelChanged )
		{
			if ( Renderer.IsValid() && Renderer.AnimGraph != _animationGraph )
				BindAnimationGraph();

			return;
		}

		_isViewModel = model is not null && System.IO.Path.GetFileName( model.Name ).StartsWith( "v_" );
		ShowGrid = _showGround && !_isViewModel;

		//Camera is locked to the viewmodel's camera bone - focus controls are meaningless
		_focusHost.SetClass( "hidden", _isViewModel );

		if ( !Renderer.IsValid() )
			return;

		using ( Scene.Push() )
		{
			BindAnimationGraph();

			if ( _isViewModel )
				{
					var armsGo = new GameObject( true, "arms" );
					armsGo.Parent = ModelObject;

					var arms = armsGo.AddComponent<SkinnedModelRenderer>();
					arms.Model = Model.Load( "models/first_person/first_person_arms_preview.vmdl" );
					arms.BoneMergeTarget = Renderer;
					arms.UseAnimGraph = false;
				}
			}
	}

	/// <summary>Play a compiled animation graph on the preview model. A variation is its own graph resource, so switching variation means playing a different graph. The model is rebuilt with it, so a variation never plays on another one's model.</summary>
	public void SetAnimationGraph( AnimGraph graph )
	{
		if ( _animationGraph == graph )
			return;

		_recorder.Clear();
		_animationGraph = graph;

		RebuildModel();
	}

	private void BindAnimationGraph()
	{
		if ( !Renderer.IsValid() )
			return;

		//A clip picked out of the model's own list owns the renderer while it is playing. Rebinding under it would take the
		//pose away and leave the clip thinking it still had one, which is a model that stops moving and never starts again.
		if ( _clipPath is not null )
			return;

		//The model half shows the model as it is - the graph open in the other half is not this model's, and playing it here would be a pose from nowhere
		var graph = _host.IsModelPreview ? null : _animationGraph;

		Renderer.Sequence.Name = "";
		Renderer.UseAnimGraph = graph is not null;
		Renderer.AnimGraph = graph;
	}

	protected override void OnScenePrepareFrame()
	{
		RebuildModel();

		if ( _ragdoll is not null && !_ragdoll.IsValid() )
		{
			_ragdoll = null;
			SetSimulating( false );
		}

		if ( IsModelPreview )
			ApplyModelPreviewSettings();
		else
			ApplyPreviewSettings( _host.Graph );

		if ( _clipPath is not null )
			_clipPreview.Play( Renderer, _clipPath );

		if ( !_reviewingRecording )
			_host.ApplyPreviewParameters();
	}

	protected override void OnScenePreFrame()
	{
		PlacementDragging = false;
		ModelDocPhysics.PosedBone = SimulatedBone;

		if ( !_reviewingRecording )
			_host.ConsumeAutoResetPreviews();

		if ( _clipPath is not null && !_paused )
			_clipPreview.Advance( RealTime.Delta * Scene.TimeScale );

		if ( _showBones || !string.IsNullOrEmpty( _highlightedBone ) )
			DrawBones();
		else
			_hoveredBones.Clear();

		if ( _showAttachments || SelectedAttachment() >= 0 )
			DrawAttachments();

		DrawSelection();
		DrawSimOverlay();
		UpdateSimulateButton();
		EnsureModelPreviewTools();

		DrawPlacement();

		if ( _showRootMotion )
			DrawRootMotion();

		foreach ( var button in _toolButtons )
			button.UpdateState();

		DrawStats();
		DrawActiveAnimations();
	}

	/// <summary>The transport follows the clip whether or not the scene was drawn this frame, so it is read here rather than off the back of the render.</summary>
	public override void Tick()
	{
		base.Tick();

		TickTimeline();
	}

	private void DrawRootMotion()
	{
		if ( !Renderer.IsValid() )
			return;

		//Root motion scales with the ticked delta - divide it back out so the display is timescale-independent
		var dt = RealTime.Delta * Scene.TimeScale;
		if ( dt > 0f )
			UpdateRootVelocity( Renderer.WorldRotation, dt );

		var speed = _rootVelocity.Length;
		var origin = Renderer.WorldPosition + Vector3.Up * 0.5f;

		Gizmo.Draw.LineStrip( Circle( origin, 0.1524f ), Theme.Green, overlay: true );

		if ( speed < 5f )
			return;

		var forward = _rootVelocity.Normal;
		var left = Vector3.Cross( Vector3.Up, forward ).Normal;
		var length = 0.3048f + (speed * 0.2f).Clamp( 6f, 60f ) * 0.0254f;
		var start = origin + forward * (0.2286f);

		//Flat arrow silhouette on the ground plane
		Vector3 P( float along, float side ) => start + forward * along + left * side;

		Gizmo.Draw.LineStrip( new[]
		{
			P( 0f, 0.04572f ),
			P( length - 0.2032f, 0.04572f ),
			P( length - 0.2032f, 0.1143f ),
			P( length, 0f ),
			P( length - 0.2032f, -0.1143f ),
			P( length - 0.2032f, -0.04572f ),
			P( 0f, -0.04572f ),
			P( 0f, 0.04572f ),
		}, Theme.Green, overlay: true );

		var pixel = ToViewportPixels( P( length, 0f ) + Vector3.Up * (0.0508f), out var isBehind );
		if ( isBehind )
			return;

		Gizmo.Draw.ScreenText( $"{speed:0} u/s", pixel - new Vector2( 0f, 16f ), size: 13, flags: TextFlag.Center, color: Theme.Green );
	}

	private void UpdateRootVelocity( Rotation rotation, float deltaTime )
	{
		_rootVelocity = rotation * Renderer.RootMotion.Position / deltaTime;
	}

	//A ring lying on the ground, for the root motion marker to sit in. On the ground, which is the ZX plane - it used to be
	//swept in x and y with nothing in z, which was the ground when z was the height and is a wall standing across the model now.
	private static IEnumerable<Vector3> Circle( Vector3 center, float radius )
	{
		for ( var i = 0; i <= 32; i++ )
		{
			var rad = i / 32f * MathF.Tau;
			yield return center + Vector3.Right * ( MathF.Cos( rad ) * radius )
				+ Vector3.Backward * ( MathF.Sin( rad ) * radius );
		}
	}

	//Where a bone is in the pose being played, for a handle to hang off.
	internal Transform? BoneNow( string bone )
	{
		if ( !Renderer.IsValid() || string.IsNullOrWhiteSpace( bone ) ) return null;

		return Renderer.TryGetBoneTransform( bone, out var tx ) ? tx : null;
	}

	//Draws every attachment where the renderer says it is this frame, as the game would see it
	private void DrawAttachments()
	{
		if ( !Renderer.IsValid() ) return;

		var model = Renderer.Model;
		var definition = _host.ModelDefinition;

		if ( definition?.Attachments is not { Count: > 0 } attachments ) return;

		var selected = SelectedAttachment();
		var size = ModelDocPhysics.DefaultRadius( model );

		Gizmo.Draw.IgnoreDepth = true;

		for ( var i = 0; i < attachments.Count; i++ )
		{
			var attachment = attachments[i];
			if ( attachment is null || !attachment.Enabled ) continue;

			var at = Renderer.GetAttachment( attachment.Name )
				?? AttachmentDoc.Placed( model, definition, attachment, BoneNow );

			var lit = i == selected;

			AttachmentDoc.Draw( at, attachment.Name, attachment.FacesOneWay, size, lit,
				label: lit || _boneNamesAlways );

			if ( lit )
			{
				var influence = _modelSelection?.Kind == ModelDocKind.AttachmentInfluence
					? _modelSelection.Index
					: -1;

				AttachmentDoc.DrawInfluences( model, definition, attachment, at, size, influence, BoneNow );
			}
		}
	}

	//Which attachment the tree has picked, so the viewport lights the same one.
	private int SelectedAttachment() => _modelSelection is null || _modelSelection.IsFolder
		? -1
		: _modelSelection.Kind switch
		{
			ModelDocKind.Attachment => _modelSelection.Index,
			ModelDocKind.AttachmentInfluence => _modelSelection.ParentIndex,
			_ => -1,
		};

	private void DrawBones()
	{
		if ( !Renderer.IsValid() || Renderer.Model is null )
			return;

		var ray = Gizmo.CurrentRay;
		var picked = _hoveredBones.FirstOrDefault();
		var hits = new List<(string Name, float Score)>();

		foreach ( var bone in Renderer.Model.Bones.AllBones )
		{
			if ( !Renderer.TryGetBoneTransform( bone, out var tx ) )
				continue;

			if ( bone.Parent is null || !Renderer.TryGetBoneTransform( bone.Parent, out var parentTx ) )
				parentTx = Renderer.WorldTransform;

			//Strip the bone's scale, which isn't 1 here, or the handle grows with it
			using var scope = Gizmo.Scope( $"BoneHandle{bone.Index}", tx.WithScale( 1f ) );

			//How far the pointer reaches for a bone, the same size on the screen at any distance - what the pick, the axes and the label are measured in.
			var radius = Math.Min( 1f, tx.Position.Distance( Gizmo.CameraTransform.Position ) / 64f ) * _boneScale;
			var hovered = bone.Name == picked || bone.Name == _highlightedBone;

			//Cursor distance in units of that reach - a screen-space hit test where overlapping bones all register instead of the front one winning
			var toBone = tx.Position - ray.Position;
			var score = toBone.Cross( ray.Forward ).Length / radius;

			if ( ViewIsHovered && score < 1.5f && toBone.Dot( ray.Forward ) > 0f )
				hits.Add( (bone.Name, score) );

			//Drawn the way a skeleton on the scene is drawn, so a bone looks the same here as on the model placed
			//in a level - see Gizmo.DrawBone. The root has no bone to go back to, so it is a joint and no line.
			var toParent = bone.Parent is null ? Vector3.Zero : tx.PointToLocal( parentTx.Position );
			Gizmo.DrawBone( toParent, bone.Name == _highlightedBone, bone.Name == picked, Gizmo.BoneJointSize * _boneScale );

			if ( _boneAxes )
			{
				//X red, Y green, Z blue, each down the axis it is named after - the same letters the transform handles wear. Blue was drawn down Vector3.Forward,
				//which was +X when this was written and is -Z now, so the bone's blue arm pointed the opposite way to the blue arrow of the handle sitting on it.
				Gizmo.Draw.Color = Color.Red;
				Gizmo.Draw.Line( 0f, Vector3.Right * radius * 2f );
				Gizmo.Draw.Color = Color.Green;
				Gizmo.Draw.Line( 0f, Vector3.Up * radius * 2f );
				Gizmo.Draw.Color = Color.Blue;
				Gizmo.Draw.Line( 0f, Vector3.Backward * radius * 2f );
			}

			var showName = hovered || _boneNamesAlways || bone.Name == _highlightedBone;
			if ( !showName )
				continue;

			//Best match's label first, any other bones under the cursor stacked dimly below - drawn on the camera overlay, above everything in the scene
			var pixel = ToViewportPixels( tx.Position + Gizmo.CameraTransform.Right * radius * 2f, out var isBehind );
			if ( isBehind )
				continue;

			if ( _boneNamesAlways || bone.Name == _highlightedBone )
			{
				var label = new TextRendering.Scope
				{
					Text = bone.Name,
					TextColor = Color.White.WithAlpha( hovered ? 1f : 0.7f ),
					FontSize = 12,
					FontName = "Roboto Mono",
					FontWeight = 400,
					LineHeight = 1,
					Outline = new TextRendering.Outline { Color = Color.Black, Enabled = true, Size = 3 },
				};
				Camera.Overlay.DrawText( label, pixel, TextFlag.LeftCenter );
				continue;
			}

			for ( var i = 0; i < _hoveredBones.Count; i++ )
			{
				var label = new TextRendering.Scope
				{
					Text = _hoveredBones[i],
					TextColor = Color.White.WithAlpha( i == 0 ? 1f : 0.7f ),
					FontSize = 12,
					FontName = "Roboto Mono",
					FontWeight = 400,
					LineHeight = 1,
					Outline = new TextRendering.Outline { Color = Color.Black, Enabled = true, Size = 3 },
				};

				Camera.Overlay.DrawText( label, pixel + new Vector2( 0f, i * 16f ), TextFlag.LeftCenter );
			}
		}

		//Hover trails the draw by a frame, imperceptibly. Frozen while dragging the camera so a right click still opens the menu for what was hovered
		if ( CameraDragging )
			return;

		_hoveredBones.Clear();

		if ( hits.Count == 0 )
			return;

		//The animating model jiggles the scores every frame - keep the current pick on top unless another bone
		//is clearly closer, and stack the rest in skeleton order, so the labels never flicker or reshuffle
		var best = hits.MinBy( h => h.Score );
		var current = hits.FirstOrDefault( h => h.Name == picked );
		var top = current.Name is not null && current.Score < best.Score + 0.5f ? current.Name : best.Name;

		_hoveredBones.Add( top );
		_hoveredBones.AddRange( hits.Where( h => h.Name != top ).Select( h => h.Name ) );
	}

	public override void OnDeleted()
	{
		_clipPreview.Dispose();

		base.OnDeleted();
	}

	//A click on the picture picks the bone under it, where the bones are drawn, and
	//a click on none of them lets the picked one go - see IPreviewHost.PickBone.
	protected override void OnViewLeftClick()
	{
		if ( _isViewModel )
			return;

		if ( !_showBones && string.IsNullOrEmpty( _highlightedBone ) )
			return;

		_host.PickBone( _hoveredBones.FirstOrDefault() );
	}

	protected override void OnViewRightClick()
	{
		if ( _isViewModel )
			return;

		var menu = new Menu();

		foreach ( var bone in _hoveredBones.Where( b => b != FocusBone ) )
			menu.AddOption( $"Focus Camera on {bone}", "center_focus_strong", () => SetFocusBone( bone ) );

		if ( FocusBone is not null )
			menu.AddOption( "Clear Camera Focus", "center_focus_weak", () => SetFocusBone( null ) );

		menu.AddOption( "Frame Camera", "fit_screen", FrameCamera );

		menu.OpenAtCursor();
	}

	protected override void UpdateCamera()
	{
		if ( _isViewModel )
		{
			if ( Renderer.IsValid() && Renderer.TryGetBoneTransform( "camera", out var tx ) )
			{
				Camera.WorldPosition = tx.Position;
				Camera.WorldRotation = tx.Rotation;
				Camera.FieldOfView = _fovViewModel;
			}

			return;
		}

		Camera.FieldOfView = _fov;
		ApplyViewMode();
		base.UpdateCamera();
	}

	void DrawSimOverlay()
	{
		string text = null;
		if ( _ragdollActive && _simBodies > 0 )
			text = $"Simulating physics with {_simBodies} bodies.";
		else if ( _shaking )
			text = $"Drag the model to swing its {JiggleBoneCount()} jiggle bones.";

		if ( text is null )
			return;

		var scope = new TextRendering.Scope
		{
			Text = text,
			TextColor = Color.White.WithAlpha( 0.9f ),
			FontSize = 12,
			FontName = "Roboto Mono",
			FontWeight = 400,
			LineHeight = 1,
			Outline = new TextRendering.Outline { Color = Color.Black, Enabled = true, Size = 3 },
		};

		Gizmo.Draw.ScreenText( scope, new Vector2( 6, 6 ), TextFlag.LeftTop );
	}

	//Where the simulation has put a bone, for gizmos that would otherwise sit on the rest pose.
	Transform? SimulatedBone( string bone )
	{
		if ( !IsSimulating || !Renderer.IsValid() )
			return null;

		if ( string.IsNullOrEmpty( bone ) )
			return Renderer.WorldTransform.WithScale( 1f );

		return Renderer.TryGetBoneTransform( bone, out var tx ) ? tx.WithScale( 1f ) : null;
	}

	void DrawSelection()
	{
		if ( !Renderer.IsValid() )
			return;

		if ( _modelSelection is null && !_showHulls && !_showJoints )
			return;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = Theme.Primary.WithAlpha( 0.85f );
		Gizmo.Draw.LineThickness = 2f;

		Action changed = () =>
		{
			PlacementDragging = true;
			_host.NotifyModelDocChanged();
		};
		var kind = _modelSelection?.Kind;
		var resize = PlacementTool == PlacementTool.Scale
			&& kind is ModelDocKind.Hull or ModelDocKind.Hulls;

		//Picking a hull or a joint brings the rest of its kind up around it; the display
		//options keep those same gizmos up when nothing of the kind is picked at all.
		var item = _modelSelection ?? new ModelDocItem { Kind = ModelDocKind.Hulls, IsFolder = true };
		var physics = kind is ModelDocKind.Hull or ModelDocKind.Hulls or ModelDocKind.Joint or ModelDocKind.Joints;

		if ( _showHulls || physics || ( _modelMode && _modelSelection is { IsFolder: false } ) )
			ModelDoc.Of( ModelDocKind.Hulls )?.DrawOverlay( Model, _host.ModelDefinition, item, changed, resize );

		if ( _showJoints || kind is ModelDocKind.Joint or ModelDocKind.Joints )
			ModelDoc.Of( ModelDocKind.Joint )?.DrawOverlay( Model, _host.ModelDefinition, item, changed, resize );

		if ( _modelSelection is null || physics )
			return;

		ModelDoc.Of( _modelSelection.Kind )?.DrawOverlay( Model, _host.ModelDefinition, _modelSelection, changed, resize );
	}

	private void DrawStats()
	{
		var playing = Renderer.IsValid() && Renderer.UseAnimGraph && _animationGraph is not null;
		var ms = playing ? Renderer.SceneModel?.AnimGraph2UpdateTime ?? 0.0f : 0.0f;

		if ( !playing || ms <= 0.0f )
		{
			_sampleCount = 0;
			_sampleHead = 0;
			return;
		}

		_samples[_sampleHead] = ms;
		_sampleHead = (_sampleHead + 1) % _samples.Length;
		_sampleCount = Math.Min( _sampleCount + 1, _samples.Length );

		var total = 0.0f;
		var peak = 0.0f;

		for ( var i = 0; i < _sampleCount; i++ )
		{
			total += _samples[i];
			peak = Math.Max( peak, _samples[i] );
		}

		var scope = new TextRendering.Scope
		{
			Text = $"graph {ms:0.00} ms   avg {total / _sampleCount:0.00}   peak {peak:0.00}",
			TextColor = Color.White.WithAlpha( 0.9f ),
			FontSize = 12,
			FontName = "Roboto Mono",
			FontWeight = 400,
			LineHeight = 1,
			Outline = new TextRendering.Outline { Color = Color.Black, Enabled = true, Size = 3 },
		};

		Gizmo.Draw.ScreenText( scope, new Vector2( 6, 6 ), TextFlag.LeftTop );
	}

	private void DrawActiveAnimations()
	{
		if ( !_showActiveAnimations || !Renderer.IsValid() )
			return;

		_host.CollectActiveAnimations( Renderer.SceneModel, _activeAnimations );
		if ( _activeAnimations.Count == 0 )
			return;

		var nameWidth = Math.Max( "animation".Length, _activeAnimations.Max( animation => animation.Name.Length ) );
		var lines = _activeAnimations.Select( animation =>
			$"{animation.Name.PadRight( nameWidth )}   {animation.Cycle,5:0.00}   {animation.Cycle * animation.Duration,5:0.00}/{animation.Duration:0.00}s" );
		var header = "animation".PadRight( nameWidth ) + "   cycle          time";

		var scope = new TextRendering.Scope
		{
			Text = $"{header}\n{string.Join( '\n', lines )}",
			TextColor = Color.White.WithAlpha( 0.9f ),
			FontSize = 12,
			FontName = "Roboto Mono",
			FontWeight = 400,
			LineHeight = 1,
			Outline = new TextRendering.Outline { Color = Color.Black, Enabled = true, Size = 3 },
		};

		Gizmo.Draw.ScreenText( scope, new Vector2( 6, 24 ), TextFlag.LeftTop );
	}

	internal void ClearRecording() => _recorder.Clear();

	internal void SetRecordingReviewMode( bool reviewing, bool paused )
	{
		if ( reviewing && !_reviewingRecording && ModelObject.IsValid() )
			_preReviewTransform = ModelObject.WorldTransform;
		else if ( !reviewing && _reviewingRecording && ModelObject.IsValid() )
			ModelObject.WorldTransform = _preReviewTransform;

		if ( reviewing != _reviewingRecording )
		{
			TreadmillEnabled = !reviewing && !_ragdollActive;
			_recordedTreadmillUpdate = -1;
			_rootVelocity = default;
			Ground.Reset();
		}

		_reviewingRecording = reviewing;
		SetPaused( paused );
	}

	internal bool ApplyRecordedUpdate( int updateIndex )
	{
		var model = Renderer?.SceneModel;
		if ( !model.IsValid() )
			return false;

		var startIndex = updateIndex == _recordedTreadmillUpdate + 1 ? updateIndex : 0;
		if ( startIndex == 0 )
			Ground.Reset();

		for ( var i = startIndex; i <= updateIndex; i++ )
		{
			if ( !model.GoToAnimGraph2RecordedUpdate( i ) )
				return false;

			if ( model.TryGetAnimGraph2RecordedUpdateInfo( out var info )
				&& info.Type == AnimGraphRecordedUpdateType.TimeStep )
			{
				Ground.Update( Renderer );
			}
		}

		_recordedTreadmillUpdate = updateIndex;
		var characterTransform = model.GetAnimGraph2RecordedCharacterTransform();
		if ( model.TryGetAnimGraph2RecordedUpdateInfo( out var currentInfo )
			&& currentInfo.Type == AnimGraphRecordedUpdateType.TimeStep
			&& currentInfo.DeltaTime > 0f )
		{
			UpdateRootVelocity( characterTransform.Rotation, currentInfo.DeltaTime );
		}
		else
		{
			_rootVelocity = default;
		}

		model.Update( 0f );

		if ( ModelObject.IsValid() )
			ModelObject.WorldTransform = characterTransform;

		return true;
	}

	public void Cleanup() => DestroyScene();
}

class FocusBoneSettings
{
	public string Bone { get; set; }
}

//Picks the preview model's camera focus bone.
class FocusBonePicker : BonePickerControl
{
	public Preview Preview { get; init; }

	//The preview's own model, which the graph's target model isn't while a variation is previewed
	protected override Model TargetModel => Preview?.ModelRenderer?.Model;

	//The toolbar control is narrow, the bone names in it aren't
	protected override float PopupMinWidth => 380f;
}

//Viewport-style icon button, lit while its predicate is true; re-checked every frame
sealed class PreviewToolButton : Panel
{
	private readonly UILabel _glyph;

	public string Icon
	{
		get => _glyph.Text;
		set => _glyph.Text = value;
	}

	public Action Action { get; set; }
	public Func<bool> IsActive { get; set; }

	public PreviewToolButton()
	{
		AddClass( "preview-tool" );

		_glyph = AddChild<UILabel>( "material-icon" );

		AddEventListener( "onclick", () => Action?.Invoke() );
	}

	public void UpdateState() => SetClass( "on", IsActive?.Invoke() ?? false );
}

//A menu hanging off one of the preview's toolbar buttons. Its own type only so the preview's sheet reaches it: a popup is put
//on the window's overlay rather than under the button that opened it, and styles fall down the tree the panel is actually in.
[StyleSheet( "Preview.cs.scss" )]
sealed class PreviewPopup : Popup
{
	public PreviewPopup()
	{
		AddClass( "editor-popup" );
		AddClass( "preview-popup" );
	}
}

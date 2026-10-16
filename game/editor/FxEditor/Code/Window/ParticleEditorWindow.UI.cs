namespace Editor.FxEditor;

/// <summary>The window's furniture: the docks around the preview. Nothing sits above them - opening, saving, undo and redo are shortcuts, and adding is done from the docks' own bars.</summary>
public sealed partial class ParticleEditorWindow
{
	void BuildUI()
	{
		BuildWorkspace();

		//Each with the edge it belongs on: a registered dock is made at once and put on its area closed.
		DockManager.RegisterDock( new() { Title = "Effect", Icon = "account_tree", Area = DockArea.Left, CreateAction = () => _tree } );
		DockManager.RegisterDock( new() { Title = "Properties", Icon = "tune", Area = DockArea.Right, CreateAction = () => _properties } );
		DockManager.RegisterDock( new() { Title = "Parameters", Icon = "sliders", Area = DockArea.Left, CreateAction = () => _parameters } );
	}

	//The tree, the properties and the preview, made together and bound to each other. Made again on hotload, which is
	//why it is one method: a rebuilt panel is a different widget, and the docks showing the old ones are re-seated.
	void BuildWorkspace()
	{
		var oldTree = _tree;
		var oldProperties = _properties;
		var oldPreview = _preview;
		var oldParameters = _parameters;

		_tree = new EffectTree( this );
		_properties = new Properties( this );
		_preview = new ParticlePreview( this );
		_parameters = new ParameterList( this );

		_tree.SetDefinition( _definition );
		_preview.SetDefinition( _definition );
		_parameters.SetDefinition( _definition );
		_properties.Show( _selection ?? _definition );

		DockManager.SetCentralWidget( _preview );

		Reseat( "Effect", _tree );
		Reseat( "Properties", _properties );
		Reseat( "Parameters", _parameters );

		if ( oldTree.IsValid() && oldTree != _tree ) oldTree.Destroy();
		if ( oldProperties.IsValid() && oldProperties != _properties ) oldProperties.Destroy();
		if ( oldPreview.IsValid() && oldPreview != _preview ) oldPreview.Destroy();
		if ( oldParameters.IsValid() && oldParameters != _parameters ) oldParameters.Destroy();
	}

	void Reseat( string title, Widget widget )
	{
		if ( DockManager.FindDockWidget( title ) is not { } dock ) return;

		dock.Widget = widget;
		widget.Parent = dock;
	}

	[EditorEvent.Hotload]
	public void OnHotload()
	{
		BuildWorkspace();
		ApplyLayout();
	}

	//Where the docks sit when nobody has moved them.
	protected override void BuildDefaultLayout()
	{
		var effect = DockManager.OpenDock( "Effect", DockArea.Left );
		DockManager.OpenDock( "Properties", DockArea.Right );

		//Under the tree, in the same column: the parameters are part of the effect's shape the way the
		//emitters are, and the values are what somebody tweaks while watching the preview beside them.
		DockManager.OpenDock( "Parameters", DockArea.Bottom, effect );

		ApplyLayout();
	}

	//Narrow tree, narrow properties, and the effect itself gets the rest.
	internal void ApplyLayout()
	{
		Widths( "Effect", 0.22f );
		Widths( "Properties", 0.76f );

		DockManager.RaiseDock( "Effect" );
		DockManager.RaiseDock( "Properties" );
		DockManager.RaiseDock( "Parameters" );
	}

	void Widths( string title, float first )
	{
		if ( DockManager.FindDockWidget( title ) is { } dock )
			DockManager.SetSplitterProportions( dock, first, 1.0f - first );
	}

	//Reveal the open file in the OS file browser.
	void ShowInExplorer()
	{
		if ( _asset?.GetSourceFile( true ) is not { Length: > 0 } path )
			return;

		EditorUtility.OpenFileFolder( path );
	}
}

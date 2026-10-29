using Editor.TreeBrowser;

namespace Editor;

[EditorTool( "tools.eye-dropper-tool", Hidden = true )]
public class EyeDropperTool : EditorTool
{
	static string LastTool;
	internal static SerializedProperty TargetProperty = null;
	internal static Action OnBackToLastTool;

	//A menu of the picked object's matching components is up: the tool waits on it
	static bool MenuOpen;

	string LastSelection;

	public override void OnEnabled()
	{
		base.OnEnabled();

		SubscribeEvents();

		AllowGameObjectSelection = true;
		LastSelection = Manager.CurrentSession.SerializeSelection();

		SetCrosshairCursor( true );
	}

	public override void OnDisabled()
	{
		base.OnDisabled();

		UnsubscribeEvents();
		SetCrosshairCursor( false );
	}

	void SubscribeEvents()
	{
		Selection.OnItemAdded += OnItemAdded;
		TreeAssetBrowser.AssetHighlighted += OnItemAdded;
		Application.OnWidgetClicked += OnWidgetPressed;
	}

	void UnsubscribeEvents()
	{
		Selection.OnItemAdded -= OnItemAdded;
		TreeAssetBrowser.AssetHighlighted -= OnItemAdded;
		Application.OnWidgetClicked -= OnWidgetPressed;
	}

	void OnItemAdded( object obj )
	{
		Select( obj );
		UnsubscribeEvents();
		Manager.CurrentSession.DeserializeSelection( LastSelection );
	}

	void OnWidgetPressed( Widget widget, MouseEvent mouseEvent )
	{
	}

	internal static void Select( object obj )
	{
		if ( obj is GameObject gameObject )
		{
			if ( ProcessObject( gameObject ) ) return;
		}
		else if ( obj is Component component )
		{
			ProcessComponent( component );
		}
		else if ( obj is Asset asset )
		{
			ProcessAsset( asset );
		}
		BackToLastTool();
	}

	public override void OnUpdate()
	{
		base.OnUpdate();

		if ( MenuOpen ) return;

		if ( TargetProperty is null || !TargetProperty.Parent.Contains( TargetProperty ) )
		{
			BackToLastTool();
			return;
		}

		if ( Gizmo.WasLeftMouseReleased )
		{
			var tr = MeshTrace.Run();
			GameObject hitObject = null;
			if ( tr.Hit )
				hitObject = tr.GameObject;

			ProcessAfterSelection( hitObject );
		}
	}

	//True where the choice went to a menu of several matching components, which puts the tool away itself
	static bool ProcessObject( GameObject obj )
	{
		if ( TargetProperty is null ) return false;
		if ( !obj.IsValid() ) return false;

		if ( TargetProperty.PropertyType == typeof( GameObject ) || TargetProperty.PropertyType.IsInstanceOfType( obj ) )
		{
			TargetProperty.SetValue( obj );
			return false;
		}

		var found = obj.Components.GetAll( TargetProperty.PropertyType, FindMode.EnabledInSelfAndDescendants ).ToList();
		if ( found.Count == 0 ) found = obj.Components.GetAll( TargetProperty.PropertyType, FindMode.DisabledInSelfAndDescendants ).ToList();

		if ( found.Count > 1 )
		{
			OpenComponentMenu( obj, found );
			return true;
		}

		ProcessComponent( found.FirstOrDefault() );
		return false;
	}

	//Every matching component on the picked object; the one clicked is taken. The menu does not block, so it
	//writes to the property it was opened for and puts the tool away when it closes
	static void OpenComponentMenu( GameObject obj, List<Component> components )
	{
		MenuOpen = true;
		var target = TargetProperty;

		var menu = new Menu();
		menu.AddHeading( obj.Name );

		foreach ( var component in components )
		{
			var type = EditorTypeLibrary.GetType( component.GetType() );
			var name = type?.Title ?? component.GetType().Name;
			if ( component.GameObject != obj ) name = $"{name} ({component.GameObject?.Name})";

			menu.AddOption( name, type?.Icon, () =>
			{
				if ( component.IsValid() && target.PropertyType.IsInstanceOfType( component ) ) target.SetValue( component );
			} );
		}

		menu.AboutToHide += () =>
		{
			MenuOpen = false;
			BackToLastTool();
		};

		menu.OpenAtCursor();
	}

	static void ProcessComponent( Component comp )
	{
		if ( TargetProperty is null ) return;
		if ( !comp.IsValid() ) return;
		if ( !TargetProperty.PropertyType.IsInstanceOfType( comp ) ) return;
		TargetProperty.SetValue( comp );
	}

	static void ProcessAsset( Asset asset )
	{
		if ( TargetProperty is null || asset is null ) return;

		if ( TargetProperty.PropertyType == typeof( GameObject ) )
		{
			if ( asset.TryLoadResource( out PrefabFile prefabFile ) )
				ProcessObject( SceneUtility.GetPrefabScene( prefabFile ) );
			return;
		}

		if ( !TargetProperty.PropertyType.IsInterface ) return;
		if ( !asset.AssetType.IsGameResource ) return;
		if ( !asset.AssetType.ResourceType.IsAssignableTo( TargetProperty.PropertyType ) ) return;

		var resource = asset.LoadResource<GameResource>();
		if ( resource is null || !resource.IsValid() ) return;
		TargetProperty.SetValue( resource );
	}

	async void ProcessAfterSelection( GameObject obj )
	{
		await Task.Delay( 100 );

		if ( obj.IsValid() && ProcessObject( obj ) ) return;

		BackToLastTool();
	}

	public static void SetTargetProperty( SerializedProperty property )
	{
		if ( EditorToolManager.CurrentModeName == nameof( EyeDropperTool ) )
		{
			BackToLastTool();
			return;
		}

		LastTool = EditorToolManager.CurrentModeName;
		EditorToolManager.SetTool( nameof( EyeDropperTool ) );
		TargetProperty = property;
	}

	internal static void BackToLastTool()
	{
		SetCrosshairCursor( false );

		if ( string.IsNullOrEmpty( LastTool ) )
			return;

		EditorToolManager.SetTool( LastTool );
		LastTool = null;
		TargetProperty = null;
		MenuOpen = false;

		OnBackToLastTool?.Invoke();
	}

	static void SetCrosshairCursor( bool active )
	{
		var shape = active ? CursorShape.Cross : CursorShape.Arrow;

		if ( SceneDock.Active is { IsValid: true } dock )
		{
			dock.Cursor = shape;
			dock.PixmapCursor = null;
		}

		if ( SceneTreeWidget.Current is { IsValid: true } tree )
			tree.Cursor = active ? CursorShape.Cross : CursorShape.None;
	}
}

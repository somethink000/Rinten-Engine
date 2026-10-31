namespace Editor.Inspectors;

public enum ComponentViewMode
{
	Default,
	Events,
}

public class ComponentSheetPanel : Panel, IEditorDropTarget
{
	public Guid GameObjectId { get; set; }
	public SerializedObject Target { get; set; }
	public string HeaderTitle { get; set; }

	public ComponentSheetHeaderPanel Header { get; private set; }

	SerializedObject targetObject;
	Guid gameObjectId;
	Panel content;
	ControlSheet propertySheet;
	ComponentPanel customPanel;

	internal ComponentViewMode ViewMode = ComponentViewMode.Default;
	List<Func<bool>> hideShowConditions = new();
	int hotloadEpoch;

	//The same care as ShowAdvanced below: this is read while folding, which can happen before there is a target to name.
	string ExpandedCookieString => $"expand.{gameObjectId}.{targetObject?.TypeName}";

	bool ExpandedCookie
	{
		get => ProjectCookie.Get( ExpandedCookieString, true );
		set
		{
			if ( value ) ProjectCookie.Remove( ExpandedCookieString );
			else ProjectCookie.Set( ExpandedCookieString, value );
		}
	}

	internal bool Expanded { get; private set; } = true;

	//Whether any target shows advanced properties; null-safe since BuildHashCode reads it early
	internal bool ShowAdvanced =>
		targetObject?.Targets.OfType<Component>()
			.Any( x => x.IsValid() && x.Flags.Contains( ComponentFlags.ShowAdvancedProperties ) ) ?? false;

	internal ComponentListPanel ListPanel { get; set; }

	internal void SetShowAdvanced( bool value )
	{
		foreach ( var component in targetObject.Targets.OfType<Component>() )
			if ( component.IsValid() )
				component.Flags = component.Flags.WithFlag( ComponentFlags.ShowAdvancedProperties, value );
		RebuildContent();
	}

	internal void SetViewMode( ComponentViewMode mode )
	{
		if ( ViewMode == mode ) return;
		ViewMode = mode;
		RebuildContent();
	}

	internal void SetExpanded( bool expanded )
	{
		if ( Expanded == expanded ) return;
		Expanded = expanded;
		ExpandedCookie = expanded;
		Header.Expanded = expanded;
		if ( expanded )
		{
			if ( !content.HasChildren )
				RebuildContent();
			else
				content.SetClass( "hidden", false );
		}
		else
			content.SetClass( "hidden", true );
	}

	internal void Bind( Guid ownerId, SerializedObject target )
	{
		GameObjectId = ownerId;
		Target = target;
		ApplyTarget();
	}

	protected override void OnParametersSet() => ApplyTarget();

	void ApplyTarget()
	{
		if ( Target is null ) return;

		if ( targetObject is not null && SameTargets( targetObject, Target ) && Header is not null )
		{
			if ( ReferenceEquals( targetObject, Target ) )
				return;

			targetObject = Target;
			RebuildContent();
			return;
		}

		targetObject = Target;
		gameObjectId = GameObjectId;
		DeleteChildren();
		Initialize();
	}

	public override void OnHotloaded()
	{
		base.OnHotloaded();

		//A rebuild can change what the sheet should draw without changing anything the hash below can see, so the count stands in for "the code moved".
		hotloadEpoch++;
	}

	void Initialize()
	{
		AddClass( "component-sheet-panel inspector-card" );
		Expanded = ExpandedCookie;
		Header = AddChild( new ComponentSheetHeaderPanel( targetObject, this ) );
		if ( !string.IsNullOrEmpty( HeaderTitle ) )
			Header.Title = HeaderTitle;
		content = AddChild<Panel>( "content" );
		RebuildContent();
	}

	//The panel used to compare this itself, every frame, in Tick. The engine asks every
	//panel the same question now, so the answer lives here and the reaction next to it.
	protected override int BuildHash() => !IsValid || ListPanel is not null ? 0 : BuildHashCode();

	protected override void OnBuildHashChanged() => RebuildContent();

	int BuildHashCode()
	{
		HashCode hc = new();
		if ( targetObject is not null )
		{
			hc.Add( TargetsHash() );
		}
		else
		{
			hc.Add( ShowAdvanced );
		}

		hc.Add( hotloadEpoch );
		hc.Add( ViewMode );
		foreach ( var c in hideShowConditions ) hc.Add( c() );
		return hc.ToHashCode();
	}

	const float TargetsHashSeconds = 0.25f;
	int targetsHash;
	SerializedObject targetsHashOf;
	RealTimeSince sinceTargetsHash;

	//Walks every target: at a rate over several, which only delays a change made elsewhere
	int TargetsHash()
	{
		if ( ReferenceEquals( targetsHashOf, targetObject ) && targetObject.IsMultipleTargets && sinceTargetsHash < TargetsHashSeconds )
			return targetsHash;

		targetsHashOf = targetObject;
		sinceTargetsHash = 0;

		HashCode hc = new();
		hc.Add( targetObject.TypeName );
		hc.Add( GetPropertySchemaHash( targetObject ) );
		hc.Add( ShowAdvanced );

		foreach ( var target in targetObject.Targets )
			hc.Add( target );

		return targetsHash = hc.ToHashCode();
	}

	internal static int GetPropertySchemaHash( SerializedObject so )
	{
		if ( so is null ) return 0;

		HashCode hc = new();
		Type last = null;

		//A type once per run of it: a big selection is thousands of the same one
		foreach ( var target in so.Targets )
		{
			var type = target?.GetType();
			if ( type == last ) continue;

			last = type;
			hc.Add( GetTargetSchemaHash( target ) );
		}

		return hc.ToHashCode();
	}

	internal static int GetComponentSchemaHash( Component component ) => GetTargetSchemaHash( component );
	//Schema hash cached per TypeDescription, since a type's shape only changes on recompile
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Rinten.TypeDescription, object> schemaHashes = new();

	static int GetTargetSchemaHash( object target )
	{
		if ( target is null ) return 0;

		var type = EditorTypeLibrary.GetType( target.GetType() );
		if ( type is null ) return 0;

		if ( schemaHashes.TryGetValue( type, out var cached ) )
			return (int)cached;

		var hash = ComputeSchemaHash( type );
		schemaHashes.AddOrUpdate( type, hash );
		return hash;
	}

	static int ComputeSchemaHash( Rinten.TypeDescription type )
	{
		HashCode hc = new();
		hc.Add( type.FullName );

		foreach ( var prop in type.Properties )
		{
			hc.Add( prop.Name );
			hc.Add( prop.PropertyType?.FullName );
		}

		foreach ( var field in type.Fields )
		{
			hc.Add( field.Name );
			hc.Add( field.FieldType?.FullName );
		}

		foreach ( var method in type.Methods )
		{
			if ( method.IsStatic ) continue;
			hc.Add( method.Name );
		}

		return hc.ToHashCode();
	}

	internal static bool SameTargets( SerializedObject a, SerializedObject b )
	{
		if ( a == b ) return true;
		if ( a is null || b is null ) return false;
		if ( a.TypeName != b.TypeName ) return false;

		var aTargets = a.Targets.ToArray();
		var bTargets = b.Targets.ToArray();
		if ( aTargets.Length != bTargets.Length ) return false;

		for ( var i = 0; i < aTargets.Length; i++ )
			if ( aTargets[i] != bTargets[i] )
				return false;

		return true;
	}

	public void RebuildContent()
	{
		if ( !IsValid || content is null ) return;
		RefreshSerializedTarget();
		propertySheet = null;
		customPanel = null;
		content.DeleteChildren();
		hideShowConditions.Clear();
		if ( Expanded )
		{
			BuildInstanceContent();
			content.SetClass( "hidden", false );
		}
		else
			content.SetClass( "hidden", true );
		MarkBuildHashDrawn();
	}

	void RefreshSerializedTarget()
	{
		if ( ListPanel is not null || targetObject is null )
			return;

		var targets = targetObject.Targets.ToArray();
		if ( targets.Length == 0 )
			return;

		if ( targets.Length == 1 )
		{
			targetObject = targets[0].GetSerialized();
			Target = targetObject;
			return;
		}

		var mso = new MultiSerializedObject();
		foreach ( var target in targets )
			mso.Add( target.GetSerialized() );
		mso.Rebuild();
		targetObject = mso;
		Target = mso;
	}

	internal bool IsGameTransformSheet =>
		targetObject?.Targets.FirstOrDefault() is GameTransform
		|| targetObject?.TypeName == nameof( GameTransform );

	internal Menu BuildCustomHeaderMenu()
	{
		var panel = customPanel ?? ComponentPanel.Create( targetObject );
		if ( panel is null ) return null;

		var menu = new Menu();
		if ( !panel.FillHeaderContextMenu( menu ) ) return null;
		if ( !menu.HasOptions ) return null;

		return menu;
	}

	void BuildInstanceContent()
	{
		customPanel = ComponentPanel.Create( targetObject );
		if ( customPanel is not null )
		{
			content.AddChild( customPanel );
			return;
		}

		var sheet = new ControlSheet();
		sheet.PropertyFilter = o => FilterProperties( o, ShowAdvanced );
		propertySheet = sheet;
		sheet.Target = targetObject;
		content.AddChild( sheet );
	}

	//Another component's header carried over this sheet goes above or below it, by which half of the sheet the pointer is on. The Transform sheet is
	//not in the list and takes nothing. A component from another object is the hierarchy's to take - dropped on that object's row it is moved or copied.

	bool IEditorDropTarget.AcceptEditorDrops => ListPanel is not null;

	DropAction IEditorDropTarget.OnEditorDragHover( Widget.DragEvent ev )
	{
		if ( !TryGetReorder( ev, out _, out _, out var below ) )
		{
			SetDropEdge( false, false );
			return DropAction.Ignore;
		}

		SetDropEdge( !below, below );
		return DropAction.Move;
	}

	void IEditorDropTarget.OnEditorDragDrop( Widget.DragEvent ev )
	{
		SetDropEdge( false, false );

		if ( !TryGetReorder( ev, out var component, out var delta, out _ ) )
			return;

		var session = SceneEditorSession.Resolve( component.GameObject );
		using var scene = session.Scene.Push();

		using ( session.UndoScope( "Change Component Order" ).WithGameObjectChanges( component.GameObject, GameObjectUndoFlags.Components ).Push() )
			component.Components.Move( component, delta );

		ev.Action = DropAction.Move;
		ListPanel?.ReorderSheets();
	}

	void IEditorDropTarget.OnEditorDragLeave() => SetDropEdge( false, false );

	void SetDropEdge( bool top, bool bottom )
	{
		SetClass( "drop-top", top );
		SetClass( "drop-bottom", bottom );
	}

	//The component being carried and how far it moves to land on the side of this sheet the pointer is on. Nothing where it would not move.
	bool TryGetReorder( Widget.DragEvent ev, out Component component, out int delta, out bool below )
	{
		delta = 0;
		below = false;
		component = ev.Data?.OfType<Component>().FirstOrDefault();

		if ( targetObject is null || targetObject.IsMultipleTargets ) return false;

		var mine = targetObject.Targets.OfType<Component>().FirstOrDefault();
		if ( !component.IsValid() || !mine.IsValid() || mine == component ) return false;
		if ( component.GameObject != mine.GameObject ) return false;

		var components = mine.Components.GetAll().ToList();
		var from = components.IndexOf( component );
		var at = components.IndexOf( mine );
		if ( from < 0 || at < 0 ) return false;

		below = ev.LocalPosition.y > Box.Rect.Height * 0.5f;

		var to = below ? at + 1 : at;
		if ( from < to ) to--;

		delta = to - from;
		return delta != 0;
	}

	bool FilterProperties( SerializedProperty o, bool showAdvanced )
	{
		if ( o.PropertyType is null ) return false;
		var hideInEventTab = o.PropertyType.IsAssignableTo( typeof( Delegate ) ) && o.Name.StartsWith( "OnComponent" );
		if ( ViewMode == ComponentViewMode.Events && !hideInEventTab ) return false;
		if ( ViewMode != ComponentViewMode.Events && hideInEventTab ) return false;
		if ( o.HasAttribute<AdvancedAttribute>() && !showAdvanced ) return false;
		if ( o.IsMethod ) return true;
		return o.HasAttribute<PropertyAttribute>();
	}
}

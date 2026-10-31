namespace Editor.Inspectors;

public class ComponentListPanel : Panel
{
	public SerializedObject Target
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			ApplyTarget();
		}
	}

	SerializedObject serializedObject;
	Guid gameObjectId;
	Panel body;
	Dictionary<Component, ComponentSheetPanel> current = new();
	int hotloadCount;
	int contentHash;

	protected override void OnParametersSet() => ApplyTarget();

	void ApplyTarget()
	{
		if ( Target is null ) return;
		if ( Target == serializedObject && body is not null ) return;

		serializedObject = Target;
		gameObjectId = Target.IsMultipleTargets ? Guid.Empty : Target.Targets.OfType<GameObject>().Select( x => x.Id ).Single();

		if ( body is null )
		{
			body = AddChild<Panel>( "body" );
		}

		contentHash = 0;
		Tick();
	}

	[EditorEvent.Hotload]
	void Hotload() => hotloadCount++;

	public override void OnHotloaded()
	{
		base.OnHotloaded();
		contentHash = 0;
	}

	void Rebuild()
	{
		var gobs = serializedObject.Targets.OfType<GameObject>().ToArray();
		if ( gobs.Length == 0 ) return;

		var old = current;
		current = new();
		var sheets = new List<ComponentSheetPanel>();
		var used = new HashSet<Component>();

		foreach ( var component in gobs[0].Components.GetAll() )
		{
			if ( !component.IsValid() || component.Flags.HasFlag( ComponentFlags.Hidden ) ) continue;
			var baseType = EditorTypeLibrary.GetType( component.GetType() )?.BaseType?.TargetType;
			if ( baseType != typeof( Component ) && !( baseType?.IsSubclassOf( typeof( Component ) ) ?? false ) ) continue;

			var allGobs = gobs.Select( x => x.Components.GetAll()
				.Where( y => !used.Contains( y ) )
				.Where( y => y?.GetType() == component?.GetType() )
				.FirstOrDefault() ).ToArray();

			if ( allGobs.Length != gobs.Length || allGobs.Any( x => !x.IsValid() ) ) continue;

			var mso = new MultiSerializedObject();
			foreach ( var entry in allGobs ) mso.Add( entry.GetSerialized() );
			mso.Rebuild();
			WireUndo( mso );

			if ( old.TryGetValue( component, out var existing ) && existing.IsValid() )
			{
				existing.ListPanel = this;
				existing.Bind( gameObjectId, mso );
				sheets.Add( existing );
				current[component] = existing;
				old.Remove( component );
				used.Add( component );
				continue;
			}

			var sheet = new ComponentSheetPanel();
			sheet.ListPanel = this;
			sheet.Bind( gameObjectId, mso );
			sheets.Add( sheet );
			current[component] = sheet;
			used.Add( component );
		}

		foreach ( var leftover in old.Values )
			leftover?.Delete( true );

		var keep = new HashSet<ComponentSheetPanel>( sheets );
		foreach ( var sheet in sheets )
		{
			if ( sheet.Parent == body )
				sheet.Parent = null;
		}

		foreach ( var child in body.Children.ToArray() )
		{
			if ( child is ComponentSheetPanel sheet && keep.Contains( sheet ) )
				continue;

			child.Delete( true );
		}

		foreach ( var sheet in sheets )
			body.AddChild( sheet );
	}

	IDisposable undoScope;

	void WireUndo( MultiSerializedObject mso )
	{
		mso.OnPropertyStartEdit += p => PropertyStartEdit( p, mso.Targets.OfType<Component>() );
		mso.OnPropertyFinishEdit += p => PropertyFinishEdit( p, mso.Targets.OfType<Component>() );
		mso.OnPropertyChanged += p => PropertyChanged( p, mso.Targets.OfType<Component>() );
	}

	void PropertyStartEdit( SerializedProperty property, IEnumerable<Component> components )
	{
		var session = SceneEditorSession.Resolve( components.FirstOrDefault() );
		using var scene = session.Scene.Push();
		undoScope = session.UndoScope( $"Edit {property.Name}" ).WithComponentChanges( components ).Push();
		property.DispatchPreEdited();
	}

	void PropertyChanged( SerializedProperty property, IEnumerable<Component> components )
	{
		using var scene = components.FirstOrDefault()?.Scene.Push();
		property.DispatchEdited();
	}

	void PropertyFinishEdit( SerializedProperty property, IEnumerable<Component> components )
	{
		using var scene = components.FirstOrDefault()?.Scene.Push();
		property.DispatchEdited();
		undoScope?.Dispose();
		undoScope = null;
	}

	public override void Tick()
	{
		base.Tick();
		if ( !IsValid ) return;

		//Walks every target's components: at a rate over several, which only delays a change made elsewhere
		if ( serializedObject?.IsMultipleTargets == true && sinceComponentHash < ComponentHashSeconds ) return;
		sinceComponentHash = 0;

		var hash = BuildComponentHash();
		if ( hash != contentHash ) { contentHash = hash; Rebuild(); }
	}

	const float ComponentHashSeconds = 0.25f;
	RealTimeSince sinceComponentHash;

	int BuildComponentHash()
	{
		if ( serializedObject is null ) return 0;

		HashCode hc = default;
		hc.Add( hotloadCount );
		foreach ( var go in serializedObject.Targets.OfType<GameObject>() )
		{
			var order = 0;
			foreach ( var c in go.Components.GetAll().Where( c => c.IsValid() && !c.Flags.HasFlag( ComponentFlags.Hidden ) ) )
			{
				hc.Add( c );
				hc.Add( order++ );
				hc.Add( c.GetType() );
				hc.Add( ComponentSheetPanel.GetComponentSchemaHash( c ) );
			}
		}
		return hc.ToHashCode();
	}

	internal int SheetCount => current.Count;

	internal void CollapseOthers( Component except )
	{
		foreach ( var (entry, sheet) in current )
			sheet.SetExpanded( entry == except );
	}

	internal void CollapseAll()
	{
		foreach ( var sheet in current.Values )
			sheet.SetExpanded( false );
	}

	internal void ExpandAll()
	{
		foreach ( var sheet in current.Values )
			sheet.SetExpanded( true );
	}

	internal void ReorderSheets()
	{
		var gobs = serializedObject?.Targets.OfType<GameObject>().ToArray();
		if ( gobs is null || gobs.Length == 0 || body is null ) return;

		var sheets = new List<ComponentSheetPanel>();
		foreach ( var component in gobs[0].Components.GetAll() )
		{
			if ( current.TryGetValue( component, out var sheet ) )
				sheets.Add( sheet );
		}

		//Moved, not deleted and added back: a deleted sheet is dead, and the next tick would have built every one of them again.
		for ( var i = 0; i < sheets.Count; i++ )
		{
			if ( sheets[i].Parent == body )
				body.SetChildIndex( sheets[i], i );
		}

		contentHash = BuildComponentHash();
	}
}

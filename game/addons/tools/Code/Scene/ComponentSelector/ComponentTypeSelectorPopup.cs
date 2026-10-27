using System.IO;
using Rinten.UI;

namespace Editor;

/// <summary>Panel popup to pick a component type (Add Component).</summary>
[StyleSheet( "ComponentTypeSelectorPopup.cs.scss" )]
public sealed class ComponentTypeSelectorPopup : Popup
{
	const string CategorySeparator = "/";
	const string NoCategoryName = "Uncategorized";

	public Action<TypeDescription> OnSelect { get; set; }

	readonly AdvancedDropdownItem rootItem = new();
	readonly List<AdvancedDropdownItem> categoryStack = new();

	Panel headRow;
	TextEntry searchEntry;
	ToolbarButton filterButton;
	Panel backRow;
	Rinten.UI.Label backLabel;
	Panel listPanel;

	Panel filterDrop;
	bool needsFocus;
	bool newComponentMode;
	string newComponentName = "MyComponent";

	bool hideBaseComponents = EditorCookie.Get<bool>( "ComponentSelector.HideBase", false );
	bool flatView = EditorCookie.Get<bool>( "ComponentSelector.FlatView", false );

	public static ComponentTypeSelectorPopup Open( Panel anchor, Action<TypeDescription> onSelect )
	{
		if ( anchor is null ) return null;

		var popup = new ComponentTypeSelectorPopup( anchor );
		popup.OnSelect = onSelect;
		return popup;
	}

	ComponentTypeSelectorPopup( Panel anchor )
	{
		SetPositioning( anchor, PositionMode.BelowStretch, 4 );
		AnchorPanel = anchor;
		CloseWhenParentIsHidden = true;
		AddClass( "editor-popup" );
		AddClass( "component-type-selector-popup" );

		headRow = AddChild<Panel>( "head" );
		searchEntry = headRow.AddChild<TextEntry>( "search" );
		searchEntry.Placeholder = "Search Components";
		searchEntry.OnTextEdited = OnSearchEdited;

		filterButton = headRow.AddChild<ToolbarButton>( "filter" );
		filterButton.Icon = "sort";
		filterButton.ToolTip = "Filter Settings";
		filterButton.OnPressed = OpenFilterMenu;

		backRow = AddChild<Panel>( "back" );
		backRow.AddChild<IconPanel>( "icon" ).Text = "arrow_back";
		backLabel = backRow.AddChild<Rinten.UI.Label>( "caption" );
		backRow.AddEventListener( "onmousedown", _ => GoBack() );
		backRow.SetClass( "hidden", true );

		listPanel = AddChild<Panel>( "list" );

		RebuildTree();
		RebuildList();
		needsFocus = true;
		SyncAnchor();
	}

	public override void Tick()
	{
		base.Tick();

		if ( needsFocus && searchEntry.IsValid() )
		{
			searchEntry.Focus();
			needsFocus = false;
		}

		if ( filterDrop is not null && !filterDrop.IsValid() )
		{
			filterDrop = null;
			if ( filterButton is not null )
				filterButton.Active = false;
		}

		if ( filterDrop is { IsValid: true } )
			PositionFilterDrop();
	}

	void OnSearchEdited( string text )
	{
		categoryStack.Clear();
		newComponentMode = false;
		RebuildList();
	}

	void OpenFilterMenu()
	{
		if ( filterDrop is { IsValid: true } )
		{
			filterDrop.Delete();
			filterDrop = null;
			filterButton.Active = false;
			return;
		}

		filterDrop = AddChild<Panel>( "filter-drop" );

		AddFilterRow( "Flat View", () => flatView, v =>
		{
			flatView = v;
			EditorCookie.Set( "ComponentSelector.FlatView", flatView );
			ResetBrowse();
		} );

		AddFilterRow( "Hide Base Components", () => hideBaseComponents, v =>
		{
			hideBaseComponents = v;
			EditorCookie.Set( "ComponentSelector.HideBase", hideBaseComponents );
			ResetBrowse();
		} );

		filterButton.Active = true;
		PositionFilterDrop();
	}

	void PositionFilterDrop()
	{
		if ( filterDrop is null || !filterDrop.IsValid() || filterButton is null || !filterButton.IsValid() )
			return;

		const float width = 240f;
		var buttonRect = filterButton.GetScreenStyleRect();
		var hostRect = GetScreenStyleRect();

		filterDrop.Style.Width = Length.Pixels( width );
		filterDrop.Style.Left = Length.Pixels( buttonRect.Right - hostRect.Left - width );
		filterDrop.Style.Top = Length.Pixels( buttonRect.Bottom - hostRect.Top + 4 );
	}

	void AddFilterRow( string label, Func<bool> get, Action<bool> set )
	{
		var row = filterDrop.AddChild<Panel>( "filter-row" );

		var check = row.AddChild<IconPanel>( "check" );
		check.Text = "check";
		check.SetClass( "hidden", !get() );

		row.AddChild<Rinten.UI.Label>( "caption" ).Text = label;

		row.AddEventListener( "onmousedown", _ =>
		{
			set( !get() );
			check.SetClass( "hidden", !get() );
		} );
	}

	void ResetBrowse()
	{
		categoryStack.Clear();
		newComponentMode = false;
		RebuildTree();
		RebuildList();
	}

	void RebuildTree()
	{
		rootItem.Clear();
		BuildComponentTree( rootItem );
	}

	void BuildComponentTree( AdvancedDropdownItem root )
	{
		var types = EditorTypeLibrary.GetTypes<Component>()
			.Where( x => !x.IsAbstract && !x.HasAttribute<HideAttribute>() && !x.HasAttribute<ObsoleteAttribute>() );

		if ( hideBaseComponents )
			types = types.Where( x => !x.FullName.StartsWith( "Rinten." ) );

		root.Add( "New Component", "note_add" );

		if ( flatView )
		{
			foreach ( var type in types.OrderBy( x => x.Title.AutoLocalize() ) )
				root.Add( CreateComponentItem( type ) );
			return;
		}

		var categories = types
			.Select( x => string.IsNullOrWhiteSpace( x.Group ) ? NoCategoryName : x.Group )
			.Distinct()
			.OrderBy( x => x.AutoLocalize() )
			.ToArray();

		if ( categories.Length <= 1 )
		{
			foreach ( var type in types.OrderBy( x => x.Title.AutoLocalize() ) )
				root.Add( CreateComponentItem( type ) );
			return;
		}

		var categoryNodes = new Dictionary<string, AdvancedDropdownItem>();

		foreach ( var category in categories )
		{
			var topLevel = category.Split( CategorySeparator ).FirstOrDefault() ?? NoCategoryName;

			if ( !categoryNodes.TryGetValue( topLevel, out var categoryNode ) )
			{
				categoryNode = new AdvancedDropdownItem( topLevel.AutoLocalize() );
				categoryNodes[topLevel] = categoryNode;
				root.Add( categoryNode );
			}

			var parts = category.Split( CategorySeparator );
			if ( parts.Length > 1 )
			{
				var current = categoryNode;
				for ( int i = 1; i < parts.Length; i++ )
				{
					var subName = parts[i].AutoLocalize();
					var existing = current.Children.FirstOrDefault( x => x.Title == subName && x.HasChildren );
					if ( existing is null )
					{
						existing = new AdvancedDropdownItem( subName );
						current.Add( existing );
					}
					current = existing;
				}

				foreach ( var type in types.Where( x => x.Group == category ).OrderBy( x => x.Title.AutoLocalize() ) )
					current.Add( CreateComponentItem( type ) );
			}
			else
			{
				var categoryTypes = types.Where( x =>
					(category == NoCategoryName) ? (x.Group == null) : (x.Group == category) )
					.OrderBy( x => x.Title.AutoLocalize() );

				foreach ( var type in categoryTypes )
					categoryNode.Add( CreateComponentItem( type ) );
			}
		}
	}

	static AdvancedDropdownItem CreateComponentItem( TypeDescription type )
	{
		return new AdvancedDropdownItem
		{
			Title = type.Title.AutoLocalize(),
			Icon = type.Icon,
			Description = type.Description,
			Tooltip = $"<b>{type.FullName}</b><br/>{type.Description}",
			Value = type
		};
	}

	void RebuildList()
	{
		listPanel.DeleteChildren();

		var search = searchEntry?.Text?.Trim() ?? "";
		var searching = !string.IsNullOrWhiteSpace( search );

		var showBack = newComponentMode || (!searching && categoryStack.Count > 0);
		backRow.SetClass( "hidden", !showBack );

		if ( showBack )
			backLabel.Text = newComponentMode ? "Create a new component" : categoryStack[^1].Title;

		if ( newComponentMode )
			BuildNewComponentList();
		else if ( searching )
			BuildSearchList( search );
		else
			BuildBrowseList();
	}

	void BuildBrowseList()
	{
		var items = categoryStack.Count == 0
			? rootItem.Children
			: categoryStack[^1].Children;

		foreach ( var item in items )
			AddItemRow( item );
	}

	void BuildSearchList( string search )
	{
		var parts = search.Split( ' ', StringSplitOptions.RemoveEmptyEntries );

		listPanel.AddChild( new ComponentSelectorRowPanel(
			$"New Component '{search}'",
			"note_add",
			() => EnterNewComponentMode( search ),
			false ) );

		var scored = new List<(AdvancedDropdownItem Item, int Score)>();

		foreach ( var leaf in CollectLeaves( rootItem ) )
		{
			if ( leaf.Value is not TypeDescription ) continue;
			var score = ScoreType( leaf, parts );
			if ( score > 0 ) scored.Add( (leaf, score) );
		}

		foreach ( var (item, _) in scored.OrderByDescending( x => x.Score ).ThenBy( x => x.Item.Title ) )
			AddItemRow( item, selectLeaf: true );
	}

	void BuildNewComponentList()
	{
		listPanel.AddChild( new ComponentSelectorRowPanel( "Name", null, null, false, isHeading: true ) );

		var nameEntry = listPanel.AddChild<TextEntry>( "name-entry" );
		nameEntry.Text = newComponentName;
		nameEntry.OnTextEdited = t => newComponentName = t;

		listPanel.AddChild( new ComponentSelectorRowPanel( "Create Script from Template", null, null, false, isHeading: true ) );

		foreach ( var template in ComponentTemplate.GetAllTypes() )
		{
			listPanel.AddChild( new ComponentSelectorRowPanel(
				$"New {template.Title}..",
				template.Icon,
				() => _ = CreateNewComponent( template, newComponentName ),
				false ) );
		}
	}

	void AddItemRow( AdvancedDropdownItem item, bool selectLeaf = false )
	{
		if ( item.Title == "New Component" && !item.HasChildren && item.Value is null )
		{
			listPanel.AddChild( new ComponentSelectorRowPanel(
				"New Component",
				"note_add",
				() => EnterNewComponentMode(),
				false ) );
			return;
		}

		if ( item.HasChildren && !selectLeaf )
		{
			listPanel.AddChild( new ComponentSelectorRowPanel(
				item.Title,
				item.Icon,
				() => PushCategory( item ),
				true ) );
			return;
		}

		if ( item.Value is TypeDescription type )
		{
			listPanel.AddChild( new ComponentSelectorRowPanel(
				item.Title,
				item.Icon,
				() => SelectType( type ),
				false ) );
		}
	}

	void EnterNewComponentMode( string name = "MyComponent" )
	{
		newComponentMode = true;
		newComponentName = name;
		RebuildList();
	}

	void PushCategory( AdvancedDropdownItem item )
	{
		categoryStack.Add( item );
		RebuildList();
	}

	void GoBack()
	{
		if ( newComponentMode )
		{
			newComponentMode = false;
			RebuildList();
			return;
		}

		PopCategory();
	}

	void PopCategory()
	{
		if ( categoryStack.Count == 0 ) return;
		categoryStack.RemoveAt( categoryStack.Count - 1 );
		RebuildList();
	}

	void SelectType( TypeDescription type )
	{
		OnSelect?.Invoke( type );
		Delete();
	}

	static IEnumerable<AdvancedDropdownItem> CollectLeaves( AdvancedDropdownItem node )
	{
		if ( !node.HasChildren )
		{
			yield return node;
			yield break;
		}

		foreach ( var child in node.Children )
		{
			foreach ( var leaf in CollectLeaves( child ) )
				yield return leaf;
		}
	}

	static int ScoreType( AdvancedDropdownItem item, string[] parts )
	{
		if ( item.Value is not TypeDescription type ) return 0;

		var score = 0;
		var t = type.Title.AutoLocalize().Replace( " ", "" );
		var c = type.ClassName.Replace( " ", "" );
		var d = type.Description.Replace( " ", "" );

		foreach ( var w in parts )
		{
			if ( t.Contains( w, StringComparison.OrdinalIgnoreCase ) ) score += 10;
			if ( c.Contains( w, StringComparison.OrdinalIgnoreCase ) ) score += 5;
			if ( d.Contains( w, StringComparison.OrdinalIgnoreCase ) ) score += 1;
		}

		return score;
	}

	async Task CreateNewComponent( TypeDescription desc, string componentName )
	{
		var template = EditorTypeLibrary.Create<ComponentTemplate>( desc.Name );
		if ( template is null ) return;

		var codePath = template.DefaultDirectory;
		if ( !Directory.Exists( codePath ) )
			Directory.CreateDirectory( codePath );

		var fd = new FileDialog( EditorWindow );
		fd.Title = "Create new component..";
		fd.Directory = codePath;
		fd.DefaultSuffix = template.Suffix;
		fd.SelectFile( $"{componentName}{template.Suffix}" );
		fd.SetFindFile();
		fd.SetModeSave();
		fd.SetNameFilter( template.NameFilter );

		if ( !fd.Execute() ) return;

		componentName = Path.GetFileNameWithoutExtension( fd.SelectedFile );
		componentName = componentName.ToTitleCase().Replace( " ", "" );

		if ( !File.Exists( fd.SelectedFile ) )
			template.Create( componentName, fd.SelectedFile );

		await Task.Delay( 500 );

		CodeEditor.OpenFile( fd.SelectedFile );
		await EditorUtility.Projects.WaitForCompiles();

		var componentType = FindComponentType( componentName, fd.SelectedFile );
		if ( componentType is null )
			Log.Warning( $"Couldn't find target component type {componentName}" );
		else
			SelectType( componentType );
	}

	static TypeDescription FindComponentType( string name, string filePath )
	{
		if ( EditorTypeLibrary.GetType<Component>( name ) is { } match )
			return match;

		var assetsPath = Project.Current.GetAssetsPath().Replace( "\\", "/" );
		filePath = filePath.Replace( "\\", "/" );

		if ( filePath.StartsWith( $"{assetsPath}/" ) )
		{
			var assetPath = filePath.Substring( assetsPath.Length + 1 );
			return EditorTypeLibrary.GetTypes<Component>()
				.FirstOrDefault( x => string.Equals( assetPath, x.SourceFile, StringComparison.OrdinalIgnoreCase ) );
		}

		return null;
	}
}

internal sealed class ComponentSelectorRowPanel : Panel
{
	public ComponentSelectorRowPanel( string title, string icon, Action onClick, bool isCategory, bool isHeading = false )
	{
		AddClass( "component-selector-row" );
		if ( isHeading ) AddClass( "heading" );
		if ( isCategory ) AddClass( "category" );

		if ( !string.IsNullOrEmpty( icon ) )
			AddChild<IconPanel>( "icon" ).Text = icon;

		AddChild<Rinten.UI.Label>( "caption" ).Text = title;

		if ( isCategory )
			AddChild<IconPanel>( "chevron" ).Text = "chevron_right";

		if ( onClick is not null )
			AddEventListener( "onmousedown", _ => onClick() );
	}
}

using Editor.MdlEditor.Nodes;
using Rinten.UI;
using System.Collections;

using UILabel = Rinten.UI.Label;

namespace Editor.MdlEditor;

//One kind of thing the search can turn up, and how it is drawn.
internal sealed record SearchType( string Key, string Title, string Group, string Icon, Color Color );

//One thing the search found, and what going to it does.
internal sealed record SearchResult( string Title, string Context, string Icon, Color Color, SearchType Type, string SearchText, Action Activate );

//Find in Graph popup: nodes, states, transitions and parameters by name; Escape handled by the field
[StyleSheet( "SearchPopup.cs.scss" )]
internal sealed class SearchPopup : Popup
{
	private const string ActiveTypesCookie = "animgraph.search.active-types";
	private const string ExcludedTypesCookie = "animgraph.search.excluded-types";

	//The gap between the popup and whatever is opened beside it.
	internal const float Gap = 4.0f;

	private readonly Window _window;
	private readonly TextField _search;
	private readonly CornerIconButton _typeFilter;
	private readonly Panel _filterTags;
	private readonly SearchResultList _results;
	private readonly UILabel _summary;
	private readonly List<SearchResult> _index;
	private readonly HashSet<string> _activeTypes;
	private readonly HashSet<string> _excludedTypes;

	private SearchTypeFilterPopup _typeFilterPopup;

	//The kinds the results are narrowed to. Empty means all of them.
	internal HashSet<string> ActiveTypes => _activeTypes;

	//The kinds kept out of the results whatever else is on.
	internal HashSet<string> ExcludedTypes => _excludedTypes;

	/// <summary>Opens the search under the breadcrumb strip, replacing any popup already open</summary>
	public static SearchPopup Open( Panel anchor, Window window, HashSet<string> activeTypes, HashSet<string> excludedTypes )
	{
		if ( anchor is not { IsValid: true } )
			return null;

		CloseAll();

		return new SearchPopup( anchor, window, activeTypes, excludedTypes );
	}

	private SearchPopup( Panel anchor, Window window, HashSet<string> activeTypes, HashSet<string> excludedTypes )
	{
		_window = window;
		_activeTypes = activeTypes;
		_excludedTypes = excludedTypes;

		AddClass( "editor-popup" );
		AddClass( "search-popup" );

		SetPositioning( anchor, Popup.PositionMode.BelowRight, Gap );
		CloseWhenParentIsHidden = true;

		//The strip says where the popup goes and nothing else. Left as the anchor it would also be spared by the click-away rule, so Back,
		//Home, a crumb or the Fit View icon would navigate the graph and leave the search hanging over it with an index of where it used to be.
		AnchorPanel = null;

		var searchRow = AddChild<Panel>( "search-row" );

		_search = searchRow.AddChild<TextField>( "field" );
		_search.Placeholder = "Search nodes, properties, states and parameters...";
		_search.ValueChanged = _ => Refresh();
		_search.ButtonTyped = HandleSearchKey;

		_filterTags = searchRow.AddChild<Panel>( "tags" );

		_typeFilter = searchRow.AddChild( new CornerIconButton( "filter_list", OpenTypeFilter ) );
		_typeFilter.CornerColor = Theme.TextControl.WithAlpha( 0.5f );

		_results = AddChild( new SearchResultList() );
		_results.Activated = Activate;

		_summary = AddChild<UILabel>( "summary" );
		_summary.Tokenize = false;

		_index = BuildIndex();
		RestoreTypeFilter();
		UpdateTypeFilter();

		//The field has it from the moment the popup is up, and keeps it: every key this popup answers arrives through it.
		_search.Focus();

		SyncAnchor();
	}

	private List<SearchResult> BuildIndex()
	{
		var results = new List<SearchResult>();

		foreach ( var node in _window.Graph.AllNodes )
		{
			var title = SearchTitle( node );
			var path = _window.GetNodePath( node );
			var searchText = SearchableText( node, title, path, node.DisplayInfo.Description );
			var display = Rinten.DisplayInfo.ForType( node.GetType() );
			var nodeType = new SearchType( $"node:{node.GetType().FullName}", display.Name,
				string.IsNullOrWhiteSpace( display.Group ) ? "Nodes" : display.Group,
				display.Icon ?? node.DisplayInfo.Icon ?? "account_tree", node.PrimaryColor );
			results.Add( new( title, path, nodeType.Icon, nodeType.Color, nodeType, searchText,
				() => _window.NavigateToNode( node.Identifier ) ) );

			if ( node is not StateMachineNode machine )
				continue;

			foreach ( var state in machine.States )
			{
				var context = $"{path} › {machine.DisplayInfo.Name} · State";
				var stateType = new SearchType( "state", "State", "State Machine", "animation", Theme.Red );
				results.Add( new( state.Name, context, stateType.Icon, stateType.Color, stateType,
					SearchableText( state, state.Name, context ),
					() => _window.NavigateToStateMachineItem( machine, new StateMachineCanvas.StateSelection( state.Name ) ) ) );
			}

			foreach ( var conduit in machine.Conduits )
			{
				var conduitTitle = $"{conduit.From} → {conduit.To}";
				var context = $"{path} › {machine.DisplayInfo.Name} · Transition";
				var transitionType = new SearchType( "transition", "Transition", "State Machine", "route", Theme.Pink );
				results.Add( new( conduitTitle, context, transitionType.Icon, transitionType.Color, transitionType,
					SearchableText( conduit, conduitTitle, context ),
					() => _window.NavigateToStateMachineItem( machine, new StateMachineCanvas.ConduitSelection( conduit.From, conduit.To ) ) ) );
			}
		}

		foreach ( var parameter in _window.Graph.AllParameters )
		{
			var kind = parameter.IsVirtual ? "Virtual Parameter" : "Control Parameter";
			var context = string.IsNullOrWhiteSpace( parameter.Group ) ? kind : $"{kind} · {parameter.Group}";
			var parameterType = new SearchType( parameter.IsVirtual ? "virtual-parameter" : "control-parameter", kind, "Parameters",
				AnimParameterUI.Icon( parameter.Type ), AnimParameterUI.Color( parameter.Type ) );
			results.Add( new( parameter.Name, context, parameterType.Icon, parameterType.Color, parameterType,
				SearchableText( parameter, parameter.Name, context, parameter.Type.ToString() ),
				() => _window.NavigateToParameter( parameter ) ) );
		}

		return results;
	}

	//The kinds of thing to answer with, in a popup beside this one. A second press closes it again. The click-away rule spares a popup whose anchor was
	//pressed, and this popup's anchor is that very button - so without the first half of this the button would only ever open another one on top of the last.
	private void OpenTypeFilter()
	{
		if ( _typeFilterPopup.IsValid() )
		{
			//Now rather than at the end of the frame: the next press on the button has to find it gone.
			_typeFilterPopup.Delete( true );
			_typeFilterPopup = null;

			return;
		}

		_typeFilterPopup = SearchTypeFilterPopup.Open( this, _typeFilter );
	}

	//How many of the results are of this kind.
	internal int CountOf( SearchType type ) => _index.Count( result => result.Type.Key == type.Key );

	//Every kind the graph has in it, dealt into two columns of about the same height so neither runs on while the other stands empty.
	internal List<IGrouping<string, SearchType>>[] TypeColumns()
	{
		var groups = _index.Select( result => result.Type )
			.DistinctBy( type => type.Key )
			.GroupBy( type => type.Group )
			.OrderByDescending( group => group.Count() )
			.ToArray();

		var columns = new[] { new List<IGrouping<string, SearchType>>(), new List<IGrouping<string, SearchType>>() };
		var sizes = new int[2];

		foreach ( var group in groups )
		{
			var column = sizes[0] <= sizes[1] ? 0 : 1;
			columns[column].Add( group );
			sizes[column] += group.Count() + 1;
		}

		return columns;
	}

	//The filter changed: the chips beside the field, the cookie that remembers it, and the results. Internal because the type filter is
	//a popup of its own rather than a widget inside this one, and this is the single thing it has to say when something in it moves.
	internal void UpdateTypeFilter()
	{
		var count = _activeTypes.Count + _excludedTypes.Count;

		_typeFilter.IsActive = count > 0;
		_typeFilter.Tooltip = count > 0 ? $"Filter result types ({count} active)" : "Filter result types";

		//Cleared and refilled in the same breath, so the old chips have to go now rather than at the end of the frame.
		_filterTags.DeleteChildren( true );

		foreach ( var (key, excluded) in _activeTypes.Select( key => (key, false) ).Concat( _excludedTypes.Select( key => (key, true) ) ) )
		{
			var type = _index.Select( result => result.Type ).FirstOrDefault( type => type.Key == key );

			if ( type is null )
				continue;

			var chip = _filterTags.AddChild( new TagChipEntry( new TagChip( type.Key, type.Title, type.Icon, type.Color ), excluded ) );

			chip.Removed = () =>
			{
				_activeTypes.Remove( key );
				_excludedTypes.Remove( key );

				//The picker, where it happens to be open beside us: it is showing the same two sets and one of them just lost a key.
				if ( _typeFilterPopup.IsValid() )
					_typeFilterPopup.ReadTypes();

				UpdateTypeFilter();
			};
		}

		ProjectCookie.Set( ActiveTypesCookie, string.Join( ';', _activeTypes.Order() ) );
		ProjectCookie.Set( ExcludedTypesCookie, string.Join( ';', _excludedTypes.Order() ) );

		//Everything this popup answers to the keyboard arrives through the field, and a chip is a click somewhere
		//that is not the field. Put back here rather than at each of the half-dozen places a filter can change.
		_search.Focus();

		Refresh();
	}

	private void RestoreTypeFilter()
	{
		var available = _index.Select( result => result.Type.Key ).ToHashSet();
		_activeTypes.RemoveWhere( key => !available.Contains( key ) );
		_excludedTypes.RemoveWhere( key => !available.Contains( key ) );

		if ( _activeTypes.Count > 0 || _excludedTypes.Count > 0 )
			return;

		if ( ProjectCookie.TryGet( ActiveTypesCookie, out string active ) )
			_activeTypes.UnionWith( active.Split( ';', StringSplitOptions.RemoveEmptyEntries ).Where( available.Contains ) );

		if ( ProjectCookie.TryGet( ExcludedTypesCookie, out string excluded ) )
			_excludedTypes.UnionWith( excluded.Split( ';', StringSplitOptions.RemoveEmptyEntries ).Where( available.Contains ) );
	}

	private void Refresh()
	{
		var terms = _search.Value.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		var matches = _index
			.Select( result => (Result: result, Score: Score( result, terms )) )
			.Where( match => match.Score >= 0 && MatchesTypeFilter( match.Result ) )
			.OrderByDescending( match => match.Score )
			.ThenBy( match => match.Result.Title, StringComparer.OrdinalIgnoreCase )
			.Take( 100 )
			.Select( match => match.Result )
			.ToArray();

		//The best of them is picked as the results arrive, so return means something the moment there is anything to return to.
		_results.SetItems( matches );

		_summary.Text = matches.Length switch
		{
			0 => "No results",
			1 => "1 result",
			100 => "Showing the best 100 results",
			var count => $"{count} results"
		};
	}

	private bool MatchesTypeFilter( SearchResult result )
	{
		if ( _excludedTypes.Contains( result.Type.Key ) )
			return false;

		return _activeTypes.Count == 0 || _activeTypes.Contains( result.Type.Key );
	}

	private static int Score( SearchResult result, string[] terms )
	{
		if ( terms.Length == 0 )
			return 0;

		var score = 0;
		foreach ( var term in terms )
		{
			if ( !result.SearchText.Contains( term, StringComparison.OrdinalIgnoreCase ) )
				return -1;

			if ( result.Title.Equals( term, StringComparison.OrdinalIgnoreCase ) )
				score += 1000;
			else if ( result.Title.StartsWith( term, StringComparison.OrdinalIgnoreCase ) )
				score += 500;
			else if ( result.Title.Contains( term, StringComparison.OrdinalIgnoreCase ) )
				score += 250;
			else if ( result.Context.Contains( term, StringComparison.OrdinalIgnoreCase ) )
				score += 50;
			else
				score += 10;
		}

		return score;
	}

	//Gets keys before the text field swallows them, so arrows and Escape still work
	private void HandleSearchKey( ButtonEvent e )
	{
		if ( !e.Pressed )
			return;

		switch ( e.Button )
		{
			case "down":
				_results.Move( 1 );
				break;

			case "up":
				_results.Move( -1 );
				break;

			case "enter":
			case "return":
				Activate( _results.Selected );
				break;

			case "escape":
				//The picker in front of the search before the search itself, so one press never takes away two things at once.
				if ( _typeFilterPopup.IsValid() )
				{
					_typeFilterPopup.Delete( true );
					_typeFilterPopup = null;
				}
				else
				{
					Delete();
				}

				break;

			default:
				return;
		}

		e.StopPropagation = true;
	}

	private void Activate( SearchResult result )
	{
		if ( result is null )
			return;

		Delete();
		result.Activate();
	}

	private static string SearchTitle( Node node )
	{
		if ( node is CommentNode comment && !string.IsNullOrWhiteSpace( comment.Title ) )
			return comment.Title;

		var typeName = node.DisplayInfo.Name ?? node.GetType().Name;
		var key = GetSearchProperties( node.GetType() )
			.Where( property => property.PropertyType == typeof( string )
				&& PropertyNamePriority( property.Name ) < 10 )
			.OrderBy( property => PropertyNamePriority( property.Name ) )
			.Select( property => property.GetValue( node ) as string )
			.FirstOrDefault( value => !string.IsNullOrWhiteSpace( value ) );

		return key is null ? typeName : $"{typeName} · {key}";
	}

	private static int PropertyNamePriority( string name ) => name switch
	{
		"Name" => 0,
		"AnimationName" => 1,
		"ParameterName" => 2,
		"StateName" => 3,
		"Id" or "ID" => 4,
		_ => 10
	};

	private static string SearchableText( object target, params string[] text )
	{
		var values = new List<string>( text.Where( x => !string.IsNullOrWhiteSpace( x ) ) );
		AppendPropertyValues( target, values, 0 );
		return string.Join( '\n', values );
	}

	private static void AppendPropertyValues( object target, List<string> values, int depth )
	{
		if ( target is null || depth > 1 )
			return;

		if ( target is string text )
		{
			if ( !string.IsNullOrWhiteSpace( text ) )
				values.Add( text );

			return;
		}

		if ( EditorTypeLibrary.GetType( target.GetType() )?.IsValueType == true )
		{
			values.Add( target.ToString() );
			return;
		}

		foreach ( var property in GetSearchProperties( target.GetType() ) )
		{
			var value = property.GetValue( target );
			if ( value is string propertyText )
			{
				if ( !string.IsNullOrWhiteSpace( propertyText ) )
					values.Add( propertyText );
			}
			else if ( value is IEnumerable collection )
			{
				foreach ( var item in collection )
					AppendPropertyValues( item, values, depth + 1 );
			}
			else if ( value is not null && EditorTypeLibrary.GetType( value.GetType() )?.IsValueType == true )
			{
				values.Add( value.ToString() );
			}
		}
	}

	private static IEnumerable<PropertyDescription> GetSearchProperties( Type type )
	{
		return EditorTypeLibrary.GetType( type )?.Properties.Where( property =>
			property.IsPublic
			&& property.CanRead
			&& !property.IsIndexer
			&& property.Attributes.Any( attribute => attribute is PropertyAttribute )
			&& property.Attributes.All( attribute => attribute is not HideAttribute ) ) ?? [];
	}
}

//What the search found, one line each. Which row is picked belongs here rather than to the pointer: the field above keeps the focus for the
//popup's whole life, so the arrows and return are the only things that move it, and a pooled list has no notion of a current row of its own.
internal sealed class SearchResultList : PooledRowList<SearchResultRow>
{
	private IReadOnlyList<SearchResult> _items = [];
	private int _selected = -1;

	/// <summary>Called with whatever was picked, by a click or by return.</summary>
	public Action<SearchResult> Activated { get; set; }

	public SearchResultList()
	{
		AddClass( "results" );
	}

	//Tall enough for a name with where it lives under it - what the widget drew.
	protected override float RowHeight => 42.0f;

	protected override int CellCount => _items.Count;

	/// <summary>The row the arrows are on, which return goes to.</summary>
	public SearchResult Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

	/// <summary>A fresh set of results, with the best of them already picked.</summary>
	public void SetItems( IReadOnlyList<SearchResult> items )
	{
		_items = items ?? [];
		_selected = _items.Count > 0 ? 0 : -1;

		//Back to the top: how far down the old results somebody had scrolled says nothing about the new ones.
		ScrollOffset = new Vector2( ScrollOffset.x, 0.0f );

		Invalidate();
	}

	/// <summary>Moves the pick down the list, or up it, and keeps it in view.</summary>
	public void Move( int rows )
	{
		if ( _items.Count == 0 )
			return;

		Select( ( _selected + rows ).Clamp( 0, _items.Count - 1 ) );
	}

	private void Select( int index )
	{
		if ( index == _selected )
			return;

		_selected = index;

		//Which row is picked is a class on it, so the rows are asked to read themselves again rather than rebuilt.
		Invalidate();
		ScrollToCell( index );
	}

	protected override SearchResultRow CreateCell() => new();

	protected override void BindCell( SearchResultRow cell, int index )
	{
		cell.Index = index;
		cell.Clicked = row => Activate( row.Index );

		cell.Set( _items[index], index == _selected );
	}

	private void Activate( int index )
	{
		if ( index < 0 || index >= _items.Count )
			return;

		Select( index );

		Activated?.Invoke( _items[index] );
	}
}

//One search result; writes nothing for copy, so a stray right-click leaves the clipboard alone
internal sealed class SearchResultRow : PanelPooledRow
{
	private readonly UILabel _icon;
	private readonly UILabel _title;
	private readonly UILabel _context;

	public SearchResultRow()
	{
		//design.scss's .list-row, and never its band: the hover and the pick are the editor's own, and this is not a list you read across.
		AddClass( "search-result list-row" );

		_icon = AddLabel( this, "material-icon icon" );

		var text = AddChild<Panel>( "text" );

		_title = AddLabel( text, "title" );
		_context = AddLabel( text, "context" );
	}

	//A label of this row, told not to read its text as tokens.
	private static UILabel AddLabel( Panel parent, string classes )
	{
		var label = parent.AddChild<UILabel>( classes );

		label.Tokenize = false;

		return label;
	}

	/// <summary>What this row is over, and whether it is the one picked.</summary>
	public void Set( SearchResult result, bool selected )
	{
		SetClass( "selected", selected );

		PanelText.Set( _icon, result.Icon );
		PanelText.Set( _title, result.Title );
		PanelText.Set( _context, result.Context );

		//The kind's own colour, which is the one thing the sheet cannot know.
		_icon.Style.FontColor = result.Color;
	}
}

//Popup choosing which kinds of thing the search returns; own type so its sheet applies on the overlay
[StyleSheet( "SearchPopup.cs.scss" )]
internal sealed class SearchTypeFilterPopup : Popup
{
	//How big it is. The same numbers as the sheet, because where it goes depends on whether it fits.
	private const float Width = 460.0f;
	private const float Height = 300.0f;

	private readonly SearchPopup _owner;
	private readonly PanelToolbarButton _clear;
	private readonly List<SearchTypeGroup> _groups = new();

	public static SearchTypeFilterPopup Open( SearchPopup owner, Panel button )
	{
		if ( owner is not { IsValid: true } || button is not { IsValid: true } )
			return null;

		return new SearchTypeFilterPopup( owner, button );
	}

	private SearchTypeFilterPopup( SearchPopup owner, Panel button )
	{
		_owner = owner;

		AddClass( "editor-popup" );
		AddClass( "search-type-filter" );

		SetPositioning( owner, Side( owner ), SearchPopup.Gap );

		//Placed against the search but anchored on the button: what a press is measured against is what opened this, and that is the button.
		AnchorPanel = button;
		CloseWhenParentIsHidden = true;

		var header = AddChild( new PanelToolbar() );

		var heading = header.AddChild<UILabel>( "heading" );
		heading.Text = "Filter Result Types";

		_clear = header.AddIcon( "filter_alt_off", "Clear type filters", ClearAll );

		var columns = AddChild<Panel>( "type-columns" );

		foreach ( var groups in owner.TypeColumns() )
		{
			var column = columns.AddChild<Panel>( "type-column" );

			foreach ( var group in groups )
			{
				var types = group.OrderBy( type => type.Title ).ToArray();

				_groups.Add( column.AddChild( new SearchTypeGroup( owner, group.Key, types, Changed ) ) );
			}
		}

		ReadClear();
		SyncAnchor();
	}

	//Places the filter beside, below or opposite the search, wherever the overlay has room
	private static Popup.PositionMode Side( Panel owner )
	{
		var overlay = EditorOverlay.For( owner );

		if ( overlay is null )
			return Popup.PositionMode.Right;

		var scale = owner.ScaleFromScreen;
		var rect = owner.Box.Rect;
		var size = new Vector2( Width, Height );

		if ( Fits( overlay, new Vector2( rect.Right * scale + SearchPopup.Gap, rect.Top * scale ), size ) )
			return Popup.PositionMode.Right;

		if ( Fits( overlay, new Vector2( rect.Right * scale - Width, rect.Bottom * scale + SearchPopup.Gap ), size ) )
			return Popup.PositionMode.BelowRight;

		return Popup.PositionMode.Left;
	}

	//Whether something this big can be where it wants to be without being pushed.
	private static bool Fits( EditorOverlay overlay, Vector2 wanted, Vector2 size )
		=> overlay.Place( wanted, size ) == wanted;

	//Reads the two sets again, on every group and on the clear button.
	internal void ReadTypes()
	{
		foreach ( var group in _groups )
		{
			if ( group.IsValid() ) group.Read();
		}

		ReadClear();
	}

	//Something in here moved: the clear button, and then the search itself.
	private void Changed()
	{
		ReadClear();

		_owner.UpdateTypeFilter();
	}

	private void ReadClear()
		=> _clear.Enabled = _owner.ActiveTypes.Count + _owner.ExcludedTypes.Count > 0;

	private void ClearAll()
	{
		_owner.ActiveTypes.Clear();
		_owner.ExcludedTypes.Clear();

		foreach ( var group in _groups )
			group.Read();

		Changed();
	}
}

//One group of kinds: a tri-state box over its chips, guarded so updates don't feed back
internal sealed class SearchTypeGroup : Panel
{
	private readonly SearchPopup _owner;
	private readonly SearchType[] _types;
	private readonly Action _changed;
	private readonly TriStateCheckbox _toggle;
	private readonly List<TagChipOption> _chips = new();

	private bool _reading;

	public SearchTypeGroup( SearchPopup owner, string title, SearchType[] types, Action changed )
	{
		_owner = owner;
		_types = types;
		_changed = changed;

		AddClass( "type-group" );

		_toggle = AddChild( new TriStateCheckbox( title ) );
		_toggle.StateChanged = OnGroupState;

		foreach ( var type in types )
			_chips.Add( AddChild( Chip( type ) ) );

		Read();
	}

	private TagChipOption Chip( SearchType type )
	{
		var chip = new TagChipOption( new TagChip( type.Key, type.Title, type.Icon, type.Color ), _owner.CountOf( type ) );

		chip.Included = () => Toggle( _owner.ActiveTypes, _owner.ExcludedTypes, type.Key );
		chip.Excluded = () => Toggle( _owner.ExcludedTypes, _owner.ActiveTypes, type.Key );

		return chip;
	}

	//Says what the filter has to say about this group, on the box and on every chip.
	internal void Read()
	{
		_reading = true;

		var active = _types.Count( type => _owner.ActiveTypes.Contains( type.Key ) );
		var excluded = _types.Count( type => _owner.ExcludedTypes.Contains( type.Key ) );

		_toggle.State = active == _types.Length
			? CheckState.On
			: active > 0 || excluded > 0 ? CheckState.Partial : CheckState.Off;

		foreach ( var chip in _chips )
		{
			chip.IsSelected = _owner.ActiveTypes.Contains( chip.Chip.Key );
			chip.IsExcluded = _owner.ExcludedTypes.Contains( chip.Chip.Key );
		}

		_reading = false;
	}

	//The box was clicked: the whole group follows it.
	private void OnGroupState( CheckState state )
	{
		if ( _reading )
			return;

		foreach ( var type in _types )
		{
			_owner.ExcludedTypes.Remove( type.Key );

			if ( state == CheckState.Off )
				_owner.ActiveTypes.Remove( type.Key );
			else
				_owner.ActiveTypes.Add( type.Key );
		}

		Read();
		_changed();
	}

	//One kind on, or off again - and never in both sets at once.
	private void Toggle( HashSet<string> target, HashSet<string> other, string key )
	{
		other.Remove( key );

		if ( !target.Add( key ) )
			target.Remove( key );

		Read();
		_changed();
	}
}

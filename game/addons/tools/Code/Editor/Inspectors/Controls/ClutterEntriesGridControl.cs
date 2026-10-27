namespace Editor.Inspectors;

using Editor.TreeBrowser;
using Rinten.Clutter;
using Rinten.UI;

/// <summary>A clutter definition's entries as a grid of tiles rather than a column of rows.</summary>
[StyleSheet( "ClutterEntriesGridControl.cs.scss" )]
[CustomEditor( typeof( List<ClutterEntry> ), WithAllAttributes = [typeof( ClutterEntriesGridAttribute )] )]
public sealed class ClutterEntriesGridControl : BaseControl, IEditorDropTarget
{
	readonly Panel _grid;
	readonly Rinten.UI.Label _empty;

	ClutterEntry _selected;
	int _built = -1;

	public override bool SupportsMultiEdit => false;

	/// <summary>Told which entry the grid is on, for whatever is showing it.</summary>
	public Action<ClutterEntry> OnEntrySelected { get; set; }

	public ClutterEntriesGridControl()
	{
		MultipleRows = true;
		ShowBackground = false;
		AddClass( "clutter-grid" );

		_grid = AddChild<Panel>( "grid" );

		_empty = _grid.AddChild<Rinten.UI.Label>( "empty" );
		_empty.Text = "Drag & drop Prefabs or Models here, or click Add Entry";

		var footer = AddChild<Panel>( "footer" );
		var add = footer.AddChild( new Rinten.UI.Button( "Add Entry", "add" ) );
		add.AddClass( "add" );
		add.AddEventListener( "onclick", OpenPicker );
	}

	public override void Rebuild()
	{
		_built = -1;
		BuildTiles();
	}

	public override void Tick()
	{
		base.Tick();

		if ( !EditorDrag.Active && HasClass( "drop-target" ) )
			SetClass( "drop-target", false );

		BuildTiles();
	}

	//The list as it stands, or an empty one to add the first entry to.
	List<ClutterEntry> Entries() => Property?.GetValue<List<ClutterEntry>>() ?? new List<ClutterEntry>();

	void Store( List<ClutterEntry> entries )
	{
		if ( Property is null ) return;

		Property.SetValue( entries );
		Property.Parent?.NoteChanged( Property );

		_built = -1;
		BuildTiles();
	}

	//Rebuilt when the list changes shape; a tile keeps itself up to date otherwise.
	void BuildTiles()
	{
		var entries = Entries();
		var hash = Hash( entries );

		if ( hash == _built ) return;

		_built = hash;

		foreach ( var tile in _grid.ChildrenOfType<ClutterEntryTile>().ToArray() )
			tile.Delete( true );

		_empty.SetClass( "hidden", entries.Count > 0 );

		if ( _selected is not null && !entries.Contains( _selected ) )
			Select( null );

		for ( var i = 0; i < entries.Count; i++ )
			_grid.AddChild( new ClutterEntryTile( this, entries[i], i ) );
	}

	static int Hash( List<ClutterEntry> entries )
	{
		var hash = new HashCode();
		hash.Add( entries.Count );

		foreach ( var entry in entries )
		{
			hash.Add( entry?.Model?.ResourcePath );
			hash.Add( (entry?.Prefab as PrefabScene)?.Source?.ResourcePath );
			hash.Add( entry?.Weight );
		}

		return hash.ToHashCode();
	}

	internal void Select( ClutterEntry entry )
	{
		_selected = entry;

		foreach ( var tile in _grid.ChildrenOfType<ClutterEntryTile>() )
			tile.SetClass( "selected", tile.Entry == entry );

		OnEntrySelected?.Invoke( entry );
	}

	internal void Move( int from, int to )
	{
		var entries = Entries();

		if ( from < 0 || to < 0 || from == to ) return;
		if ( from >= entries.Count || to >= entries.Count ) return;

		var entry = entries[from];
		entries.RemoveAt( from );
		entries.Insert( to, entry );

		Store( entries );
	}

	//Whether the pointer is still over this control, for a tile deciding if it was thrown away.
	internal bool ContainsCursor() => GetScreenStyleRect().IsInside( Application.CursorPosition );

	internal void Remove( ClutterEntry entry )
	{
		var entries = Entries();

		if ( !entries.Remove( entry ) ) return;

		Store( entries );
	}

	//Writes a weight somebody dragged, without rebuilding the tile under their pointer.
	internal void NoteWeightChanged()
	{
		if ( Property is null ) return;

		Property.Parent?.NoteChanged( Property );
		_built = -1;
	}

	internal void ShowInAssetBrowser( ClutterEntry entry )
	{
		var asset = AssetOf( entry );

		if ( asset is null ) return;

		TreeAssetBrowser.OpenTo( asset );
	}

	//The asset a tile shows, whichever kind of entry it is.
	internal static Asset AssetOf( ClutterEntry entry )
	{
		if ( entry?.Prefab is PrefabScene prefab && prefab.Source is not null )
			return AssetSystem.FindByPath( prefab.Source.ResourcePath );

		if ( entry?.Model is not null )
			return AssetSystem.FindByPath( entry.Model.ResourcePath );

		return null;
	}

	void OpenPicker()
	{
		if ( IsReadOnly ) return;

		var picker = AssetPicker.Create( Rinten.Internal.GlobalToolsNamespace.EditorWindow, null );

		picker.OnAssetPicked = assets =>
		{
			var entries = Entries();

			foreach ( var asset in assets ?? [] )
			{
				if ( Make( asset ) is { } entry )
					entries.Add( entry );
			}

			Store( entries );
		};

		picker.Show();
	}

	//An entry for an asset, where the asset is something clutter can place.
	static ClutterEntry Make( Asset asset )
	{
		if ( asset is null ) return null;

		var entry = new ClutterEntry();

		if ( asset.AssetType?.FileExtension == "mdl" && asset.TryLoadResource<Model>( out var model ) )
			entry.Model = model;
		else if ( asset.AssetType?.FileExtension == "prefab" && asset.TryLoadResource<PrefabFile>( out var prefabFile ) )
			entry.Prefab = SceneUtility.GetPrefabScene( prefabFile );

		return entry.HasAsset ? entry : null;
	}

	static bool IsClutterPath( string path ) =>
		!string.IsNullOrEmpty( path )
		&& (path.EndsWith( ".mdl" ) || path.EndsWith( ".mdl_c" ) || path.EndsWith( ".prefab" ));

	//Whether clutter can place this asset at all.
	static bool IsClutter( Asset asset ) => asset?.AssetType?.FileExtension is "mdl" or "prefab";

	//Assets in a drag; goes through DragData.Files, which strips the file:/// scheme
	static List<Asset> DraggedAssets( DragData data )
	{
		var assets = new List<Asset>();

		void Add( Asset asset )
		{
			if ( asset is null || asset.IsDeleted || assets.Contains( asset ) ) return;

			assets.Add( asset );
		}

		foreach ( var carried in data.OfType<Asset>() )
			Add( carried );

		if ( assets.Count > 0 ) return assets;

		if ( !data.HasFileOrFolder ) return assets;

		foreach ( var path in data.Files ?? [] )
			Add( AssetSystem.FindByPath( path ) );

		return assets;
	}

	public override bool AcceptEditorDrops => !IsReadOnly;

	public override DropAction OnEditorDragHover( Widget.DragEvent ev )
	{
		var accept = DraggedAssets( ev.Data ).Any( IsClutter )
			|| (ev.Data.Assets ?? []).Any( x => IsClutterPath( x.AssetPath ) );

		SetClass( "drop-target", accept );

		return accept ? DropAction.Copy : DropAction.Ignore;
	}

	public override void OnEditorDragLeave() => SetClass( "drop-target", false );

	public override void OnEditorDragDrop( Widget.DragEvent ev )
	{
		SetClass( "drop-target", false );

		if ( IsReadOnly ) return;

		var entries = Entries();
		var added = false;

		foreach ( var asset in DraggedAssets( ev.Data ) )
		{
			if ( Make( asset ) is not { } entry ) continue;

			entries.Add( entry );
			added = true;
		}

		if ( added )
		{
			Store( entries );
			ev.Action = DropAction.Copy;
			return;
		}

		//Nothing resolved here and now: a cloud asset has to be fetched first.
		if ( ev.Data.Assets is null ) return;

		_ = AddDropped( ev.Data.Assets.ToArray() );

		ev.Action = DropAction.Copy;
	}

	//A dropped asset that is not on disk yet is fetched first, so this finishes later.
	async Task AddDropped( DragAssetData[] dropped )
	{
		var entries = Entries();
		var added = false;

		foreach ( var dragged in dropped )
		{
			var asset = await dragged.GetAssetAsync();

			if ( Make( asset ) is not { } entry ) continue;

			entries.Add( entry );
			added = true;
		}

		if ( !added || !this.IsValid() ) return;

		Store( entries );
	}
}

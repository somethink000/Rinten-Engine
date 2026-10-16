using System;

namespace Editor;

//An editor's open documents, each a complete editor instance of its own, shown one at a time with the shared tab strip
//over the editor's central view - as the scene tabs stand over the scene. The workspace strip says which editor; this, which document
internal sealed class DocumentTabWidget : StackedHost
{
	/// <summary>Fired when the last document closes - the workspace has nothing left to show.</summary>
	public Action OnEmpty;

	readonly string _blankTitle;
	readonly List<Document> _documents = new();

	Document _current;
	bool _closingAll;

	internal sealed class Document : IEditorTab
	{
		public DocumentTabWidget Owner;

		//The file's full path in lower case, or null while nothing is open in it
		public string Key;
		public string Name;

		//The editor instance - a hidden window husk once its parts are embedded
		public Widget Editor;

		//What fills the workspace: the editor's parts, with this document's strip over its central view
		public Widget Content;
		public EditorTabStrip Strip;

		public string Title => Name ?? Owner._blankTitle;
		public string Icon => null;
		public Color? Accent => null;
		public bool Unsaved => Editor is IAssetEditor { HasUnsavedChanges: true };

		public void Select() => Owner.MakeCurrent( this );
		public void Close() => Owner.Close( this );
	}

	public DocumentTabWidget( Widget parent, string blankTitle ) : base( parent )
	{
		Name = "Documents";
		Background = Theme.TabBackground;

		_blankTitle = blankTitle;
	}

	/// <summary>How many files are open - the number on the tool strip's square.</summary>
	internal int Count => _documents.Count( x => x.Key is not null );

	/// <summary>The editor of the document in front.</summary>
	internal Widget CurrentEditor => _current?.Editor;

	/// <summary>Brings up the document for this file, making it if it is not open: a blank document in front takes it, else a new tab
	/// does. A null path opens a blank document. The editor is made by <paramref name="create"/>, then handed to <paramref name="load"/>.</summary>
	public Widget Open( string path, string name, Func<Widget> create, Action<Widget> load )
	{
		var key = path is null ? null : System.IO.Path.GetFullPath( path ).ToLowerInvariant();

		if ( key is not null && _documents.FirstOrDefault( x => x.Key == key ) is { } open )
		{
			MakeCurrent( open );
			return open.Editor;
		}

		var document = key is not null && _current is { Key: null } blank ? blank : Add( create );

		if ( document is null )
			return null;

		if ( key is not null )
		{
			document.Key = key;
			document.Name = name;

			//After the embed, so anything it builds lands in the right parents. Editors written
			//as windows call Show() in there - the husk is put away again before it ever paints
			load?.Invoke( document.Editor );

			if ( document.Editor is Window window )
				window.Visible = false;
		}

		MakeCurrent( document );
		Changed();

		return document.Editor;
	}

	Document Add( Func<Widget> create )
	{
		var editor = create();

		if ( !editor.IsValid() )
			return null;

		var content = WorkspaceArea.Embed( editor );

		if ( !content.IsValid() )
		{
			editor.Destroy();
			return null;
		}

		var document = new Document { Owner = this, Editor = editor };

		document.Strip = new EditorTabStrip( null );
		document.Strip.Reordered = Reorder;
		document.Content = WithStrip( editor, content, document.Strip );

		document.Content.Parent = this;
		document.Content.Visible = false;

		_documents.Add( document );

		return document;
	}

	//The strip goes over the editor's central view, where the scene tabs stand over the scene: in the central dock's header, which stays
	//whatever the editor puts in the dock. An editor with no central dock has it over everything
	static Widget WithStrip( Widget editor, Widget content, EditorTabStrip strip )
	{
		if ( editor is DockWindow window && window.DockManager.FindDockWidget( "CentralWidget" ) is { } central )
		{
			central.Header = strip;
			return content;
		}

		var frame = new Widget( null ) { Name = "DocumentView" };
		frame.Layout = Layout.Column();
		frame.Layout.Add( strip );
		frame.Layout.Add( content, 1 );

		return frame;
	}

	public void Close( Document document )
	{
		if ( !_documents.Contains( document ) ) return;

		//The editor's own close, which writes what is unsaved; one that asks first closes itself later, and Prune sees it go
		if ( document.Editor.IsValid() && !document.Editor.AskClose() )
			return;

		Remove( document );
	}

	/// <summary>Closes every document; false where one asked to stay open for now.</summary>
	public bool CloseAll()
	{
		_closingAll = true;

		try
		{
			foreach ( var document in _documents.ToList() )
				Close( document );
		}
		finally
		{
			_closingAll = false;
		}

		return _documents.Count == 0;
	}

	void Remove( Document document )
	{
		var index = _documents.IndexOf( document );
		if ( index < 0 ) return;

		_documents.Remove( document );

		document.Content?.Destroy();
		document.Editor?.Destroy();

		if ( _documents.Count == 0 )
		{
			_current = null;
			Changed();

			if ( !_closingAll )
				OnEmpty?.Invoke();

			return;
		}

		if ( _current == document )
		{
			_current = null;
			MakeCurrent( _documents[Math.Min( index, _documents.Count - 1 )] );
		}

		Changed();
	}

	//An editor that asked before closing, and then closed itself
	[EditorEvent.Frame]
	void Prune()
	{
		foreach ( var document in _documents.Where( x => !x.Editor.IsValid() ).ToList() )
			Remove( document );
	}

	void MakeCurrent( Document document )
	{
		if ( _current == document ) return;

		var outgoing = _current;
		_current = document;

		//Every document of an editor wears the layout the user left the last one in
		if ( outgoing?.Editor is DockWindow from && document.Editor is DockWindow to && from.DockManager.IsValid() && to.DockManager.IsValid() )
		{
			var state = from.DockManager.State;

			if ( state != to.DockManager.State )
				to.DockManager.State = state;
		}

		Show( document.Content );

		foreach ( var each in _documents )
			each.Strip.SetCurrent( document );
	}

	void Reorder( IReadOnlyList<IEditorTab> order )
	{
		var sorted = order.OfType<Document>().ToList();

		_documents.Clear();
		_documents.AddRange( sorted );

		Changed();
	}

	//Every document's strip shows them all, and the tool strip its count
	void Changed()
	{
		foreach ( var each in _documents )
		{
			each.Strip.SetItems( _documents );
			each.Strip.SetCurrent( _current );
		}

		WorkspaceArea.Current?.TabBar.UpdateTabs();
	}

	public override void OnDestroyed()
	{
		base.OnDestroyed();

		//The husk windows aren't our children, so destroying the widget tree wouldn't reach them. One that
		//throws on its way out is its own problem: it must not keep the tabs after it from being torn down too
		foreach ( var document in _documents )
		{
			try
			{
				document.Editor?.Destroy();
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"'{document.Title}' threw on the way out" );
			}
		}

		_documents.Clear();
	}
}

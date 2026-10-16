using System;
using System.Collections.Generic;
using System.Linq;

namespace Editor.MdlEditor;

//Which of the two editors a linked editor is.
internal enum LinkedKind
{
	Model,
	Graph,
}

//An editor that can be kept on the same model as its counterpart: the model editor and the animation graph.
internal interface ILinkedEditor : IValid
{
	LinkedKind Kind { get; }

	//The asset this editor has open, or null while it is empty.
	Asset CurrentAsset { get; }

	//The asset the other kind of editor would show for what this one has open - a model's graph, a graph's model - or null where there is none on disk.
	Asset CounterpartAsset { get; }

	//Whether this editor's layout is the one the person is looking at.
	bool IsOnScreen { get; }

	//Take up the counterpart's asset where there is nothing of this kind to open for it: a graph editor shown a model
	//with no graph starts an empty graph on that model. Given the counterpart's own asset, not one of this kind.
	void AdoptCounterpart( Asset counterpart );
}

//Keeps the model editor and anim graph on the same model when switching workspaces without asking
internal static class EditorLink
{
	private const string FollowCookie = "mdleditor.link.follow";

	private static readonly List<ILinkedEditor> _editors = new();

	//The editor class each kind is opened in, for telling a workspace's type apart.
	private static readonly Dictionary<Type, LinkedKind> _kinds = new();

	private static bool _subscribed;
	private static bool? _follow;

	/// <summary>Whether coming to an editor's workspace shows the other editor's model. On unless somebody turned it off.</summary>
	public static bool Follow
	{
		get => _follow ??= EditorCookie.Get( FollowCookie, true );
		set
		{
			_follow = value;
			EditorCookie.Set( FollowCookie, value );
		}
	}

	public static void Register( ILinkedEditor editor )
	{
		if ( !_subscribed )
		{
			_subscribed = true;
			EditorWorkspaces.Entered += OnWorkspaceEntered;
		}

		_kinds[editor.GetType()] = editor.Kind;
		_editors.Remove( editor );
		_editors.Add( editor );
	}

	public static void Unregister( ILinkedEditor editor ) => _editors.Remove( editor );

	/// <summary>The editors of one kind, the one on screen first and the newest after it.</summary>
	public static IEnumerable<ILinkedEditor> Of( LinkedKind kind )
	{
		_editors.RemoveAll( x => !x.IsValid );

		return _editors.Where( x => x.Kind == kind )
			.OrderByDescending( x => x.IsOnScreen )
			.ThenByDescending( _editors.IndexOf );
	}

	/// <summary>The other kind.</summary>
	public static LinkedKind Other( LinkedKind kind ) => kind == LinkedKind.Model ? LinkedKind.Graph : LinkedKind.Model;

	/// <summary>The editor of the other kind whose document a newcomer of this kind should pair with: the one on screen, or failing that the last one anything was opened in. Null where the other editor is not open at all.</summary>
	public static ILinkedEditor CounterpartOf( LinkedKind kind )
		=> Of( Other( kind ) ).FirstOrDefault( x => x.CurrentAsset is not null );

	//A workspace has come up. Where it is one of ours and following is on, put it on the counterpart of what the other editor is showing.
	private static void OnWorkspaceEntered( Type editorType )
	{
		if ( !Follow ) return;
		if ( !_kinds.TryGetValue( editorType, out var kind ) ) return;

		var source = CounterpartOf( kind );
		if ( source is null ) return;

		//Already on it: the workspace was left on this model, or the person opened it themselves a moment ago.
		var wanted = source.CounterpartAsset;

		if ( wanted is not null )
		{
			if ( Of( kind ).Any( x => x.IsOnScreen && ModelGraphResolver.SamePath( x.CurrentAsset?.Path, wanted.Path ) ) )
				return;

			//Through the asset system rather than into any editor by hand, so the workspace's own document tabs raise or
			//make the document - the same road a double click takes, minus the switch to a workspace we are already in.
			IAssetEditor.OpenInEditor( wanted, out _ );
			return;
		}

		//The other side has a document with no counterpart on disk - a model with no graph yet. The editor on screen takes it up as best it can.
		if ( source.CurrentAsset is { } counterpart && Of( kind ).FirstOrDefault( x => x.IsOnScreen ) is { } shown )
			shown.AdoptCounterpart( counterpart );
	}

	/// <summary>Open the counterpart of this editor's document in the other editor, on screen.</summary>
	public static void OpenCounterpart( ILinkedEditor from )
	{
		if ( from?.CounterpartAsset is { } asset )
		{
			IAssetEditor.OpenInEditor( asset, out _ );
			return;
		}

		if ( from?.CurrentAsset is not { } current ) return;

		//Nothing on disk to open: bring the other editor up and hand it what we have.
		var other = Other( from.Kind );

		EditorAppAttribute.For( _kinds.FirstOrDefault( x => x.Value == other ).Key )?.Open();

		Of( other ).FirstOrDefault( x => x.IsOnScreen )?.AdoptCounterpart( current );
	}
}

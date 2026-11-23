using Rinten.UI;
using System;
using System.Collections.Generic;

using UILabel = Rinten.UI.Label;

namespace Editor.MdlEditor;

//The "+ Add" popup: grouped, filterable list of things to add to the model, under the button
[StyleSheet( "ModelAddPopup.cs.scss" )]
sealed class ModelAddPopup : Popup
{
	//One add: its row, and what it is filtered on.
	readonly record struct Entry( Panel Row, string Title, ModelDocAdd Add );

	//One group: its heading, and its adds.
	readonly record struct Section( Panel Heading, List<Entry> Entries );

	readonly TextField _search;
	readonly List<Section> _sections = new();
	readonly Action<ModelDocAdd> _run;

	/// <summary>Opens the list under the button. Whatever else was open goes first - here rather than in the constructor, where this popup is already on the list CloseAll walks and would close itself.</summary>
	public static ModelAddPopup Open( Panel anchor, List<(string Group, string Icon, List<ModelDocAdd> Entries)> groups, Action<ModelDocAdd> run )
	{
		CloseAll();

		return new ModelAddPopup( anchor, groups, run );
	}

	ModelAddPopup( Panel anchor, List<(string Group, string Icon, List<ModelDocAdd> Entries)> groups, Action<ModelDocAdd> run )
	{
		_run = run;

		//Not BelowStretch: the anchor is a toolbar square
		SetPositioning( anchor, PositionMode.BelowLeft, 2.0f );
		CloseWhenParentIsHidden = true;

		AddClass( "editor-popup" );
		AddClass( "model-add-popup" );

		_search = AddChild<TextField>( "search" );
		_search.Placeholder = "Search...";
		_search.ValueChanged = _ => Filter();
		_search.ButtonTyped = OnSearchKey;

		var rows = AddChild<Panel>( "rows" );

		var first = true;
		foreach ( var (group, icon, entries) in groups )
		{
			var heading = rows.AddChild<Panel>( "heading" );
			heading.SetClass( "first", first );
			first = false;

			Glyph( heading, icon );
			heading.AddChild<UILabel>( "text" ).Text = group;

			var section = new Section( heading, new List<Entry>() );

			foreach ( var entry in entries )
			{
				var add = entry;
				var option = rows.AddChild<Panel>( "option" );

				Glyph( option, add.Icon );
				option.AddChild<UILabel>( "text" ).Text = add.Title;

				option.AddEventListener( "onclick", () => Pick( add ) );

				section.Entries.Add( new Entry( option, add.Title ?? "", add ) );
			}

			_sections.Add( section );
		}

		//The field has the keys from the moment the popup is up: typing is what it is for, and return and escape arrive through it.
		_search.Focus();

		SyncAnchor();
	}

	//The icon before the words. Its place stays where there is none, so the words line up.
	static void Glyph( Panel row, string icon )
	{
		var glyph = row.AddChild<UILabel>( "material-icon" );
		glyph.Text = icon ?? "";
	}

	void Pick( ModelDocAdd add )
	{
		Delete();
		_run( add );
	}

	//Shows the adds the field's text is in, and only the headings with something under them.
	void Filter()
	{
		var text = ( _search.Value ?? "" ).Trim();

		foreach ( var section in _sections )
		{
			var any = false;

			foreach ( var entry in section.Entries )
			{
				var show = text.Length == 0
					|| entry.Title.Contains( text, StringComparison.OrdinalIgnoreCase );

				entry.Row.SetClass( "hidden", !show );
				any |= show;
			}

			section.Heading.SetClass( "hidden", !any );
		}
	}

	//The first add still showing, or null where the text matched nothing.
	ModelDocAdd? FirstShowing()
	{
		foreach ( var section in _sections )
		{
			foreach ( var entry in section.Entries )
			{
				if ( !entry.Row.HasClass( "hidden" ) )
					return entry.Add;
			}
		}

		return null;
	}

	void OnSearchKey( ButtonEvent e )
	{
		if ( !e.Pressed )
			return;

		switch ( e.Button )
		{
			case "enter":
			case "return":
				if ( FirstShowing() is { } add )
					Pick( add );
				break;

			case "escape":
				Delete();
				break;
		}
	}
}

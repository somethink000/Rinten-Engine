namespace Editor;

using Rinten.UI;

[StyleSheet( "TagSetPickerPopup.cs.scss" )]
public sealed class TagSetPickerPopup : Popup
{
	public static TagSetPickerPopup Open( Panel anchor, SerializedProperty property, Action onChanged )
	{
		if ( anchor is null || property is null ) return null;

		return new TagSetPickerPopup( anchor, property, onChanged );
	}

	readonly SerializedProperty property;
	readonly Action onChanged;
	Panel entryWrap;
	TextEntry entry;
	Rinten.UI.Label placeholderLabel;
	Panel suggestions;
	bool needsFocus;

	TagSetPickerPopup( Panel anchor, SerializedProperty property, Action onChanged )
	{
		this.property = property;
		this.onChanged = onChanged;

		SetPositioning( anchor, PositionMode.BelowRight, 4 );
		AnchorPanel = anchor;
		CloseWhenParentIsHidden = true;
		AddClass( "editor-popup" );
		AddClass( "tagset-picker-popup" );

		entryWrap = AddChild<Panel>( "entry-wrap" );
		entry = entryWrap.AddChild<TextEntry>( "entry" );
		entry.OnTextEdited = _ => UpdatePlaceholder();
		entry.AddEventListener( "onsubmit", _ => TryAddCustomTag() );

		placeholderLabel = entryWrap.AddChild<Rinten.UI.Label>( "placeholder" );
		placeholderLabel.Text = "New tag..";
		UpdatePlaceholder();

		suggestions = AddChild<Panel>( "suggestions" );

		RebuildSuggestions();
		needsFocus = true;
		UpdateVerticalPosition();
		SyncAnchor();
	}

	public override void Tick()
	{
		UpdateVerticalPosition();
		base.Tick();

		if ( needsFocus && entry.IsValid() )
		{
			entry.Focus();
			needsFocus = false;
		}

		UpdatePlaceholder();
	}

	void UpdatePlaceholder()
	{
		if ( placeholderLabel is null || !placeholderLabel.IsValid() || entry is null ) return;

		placeholderLabel.SetClass( "hidden", !string.IsNullOrEmpty( entry.Text ) );
	}

	void UpdateVerticalPosition()
	{
		if ( PopupSource is null || !PopupSource.IsValid() ) return;

		var root = FindRootPanel();
		if ( root is null ) return;

		var bounds = root.PanelBounds;
		var source = PopupSource.GetScreenPixelRect();
		var height = Box.Rect.Height > 0 ? Box.Rect.Height : 150;
		var gap = PopupSourceOffset;
		var padding = 10;

		var fitsBelow = source.Bottom + gap + height <= bounds.Bottom - padding;
		var fitsAbove = source.Top - gap - height >= bounds.Top + padding;

		if ( fitsBelow || !fitsAbove )
			Position = PositionMode.BelowRight;
		else
			Position = PositionMode.AboveRight;
	}

	void TryAddCustomTag()
	{
		var text = entry?.Text?.Trim();
		if ( string.IsNullOrEmpty( text ) ) return;

		ToggleTag( text );
		entry.Text = "";
		UpdatePlaceholder();
	}

	void ToggleTag( string tag )
	{
		var tags = property.GetValue<ITagSet>();
		if ( tags is null ) return;

		if ( tags.Has( tag ) ) tags.Remove( tag );
		else tags.Add( tag );

		property.Parent?.NoteChanged( property );
		onChanged?.Invoke();
		RebuildSuggestions();
	}

	void RebuildSuggestions()
	{
		if ( suggestions is null ) return;

		suggestions.DeleteChildren();

		var tags = property.GetValue<ITagSet>();
		if ( tags is null ) return;

		var scene = property.Parent?.Targets.OfType<GameObject>().FirstOrDefault()?.Scene;
		var suggested = tags.GetSuggested().Distinct( StringComparer.OrdinalIgnoreCase ).Take( 24 );

		foreach ( var tag in suggested )
		{
			var count = scene is null ? 0 : CountTagUsage( scene, tag );
			suggestions.AddChild( new TagSuggestionPanel( tag, count, tags.Has( tag ), () => ToggleTag( tag ) ) );
		}

		suggestions.SetClass( "hidden", !suggestions.HasChildren );
	}

	static int CountTagUsage( Scene scene, string tag )
	{
		var n = 0;

		foreach ( var go in scene.GetAllObjects( true ) )
		{
			if ( !go.IsValid() ) continue;
			if ( go.Tags.Has( tag, false ) ) n++;
		}

		return n;
	}
}

internal sealed class TagSuggestionPanel : Panel
{
	public TagSuggestionPanel( string tag, int count, bool on, Action onClick )
	{
		AddClass( "tag-suggestion" );
		SetClass( "on", on );

		AddChild<Rinten.UI.Label>( "name" ).Text = tag;

		if ( count > 0 )
			AddChild<Rinten.UI.Label>( "count" ).Text = count.ToString();

		AddEventListener( "onmousedown", _ => onClick() );
	}
}

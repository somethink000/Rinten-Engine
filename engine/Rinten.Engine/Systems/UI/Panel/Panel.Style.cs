
namespace Rinten.UI;

public partial class Panel
{
	/// <summary>This is the style that we computed last. If you're looking to see which styles are set on this panel then this is what you're looking for.</summary>
	[Hide]
	public Styles ComputedStyle { get; internal set; }

	/// <summary>A importance sorted list of style blocks that are active on this panel</summary>
	[Hide]
	public IEnumerable<IStyleBlock> ActiveStyleBlocks => Style?.LastActiveRules?.Select( x => x.Block ) ?? Enumerable.Empty<IStyleBlock>();

	/// <summary>Allows you to set styles specifically on this panel. Setting the style will only affect this panel and no others and will override any other styles.</summary>
	[Hide]
	public PanelStyle Style { get; private set; }

	/// <summary>Try to find <c>@keyframes</c> CSS rule with given name in <see cref="AllStyleSheets"/>.</summary>
	public bool TryFindKeyframe( string name, out KeyFrames keyframes )
	{
		keyframes = null;

		//AllStyleSheets' walk to the nearest boundary without its two iterators: asked every animation frame
		for ( var panel = this; panel is not null; panel = panel.Parent )
		{
			if ( panel.StyleSheet.List is { } sheets )
			{
				foreach ( var sheet in sheets )
				{
					if ( sheet.KeyFrames.TryGetValue( name, out var keyframe ) )
					{
						keyframes = keyframe;
						return true;
					}
				}
			}

			if ( panel.StyleBoundary ) break;
		}

		return false;
	}

	internal void MarkStylesRebuilt()
	{
		inRebuildStyleRulesList = false;
		inRebuildStyleRulesList_Ancestors = false;
		inRebuildStyleRulesList_Children = false;
	}

	bool inRebuildStyleRulesList;
	bool inRebuildStyleRulesList_Ancestors;
	bool inRebuildStyleRulesList_Children;

	//Should be called when something happens that means that this panel's stylesheets need to be re-evaluated. Like becoming hovered or classes
	//changed. You don't call this when changing styles directly on the panel, just on anything that will change which stylesheets should get selected.
	protected internal void StyleSelectorsChanged( bool ancestors, bool descendants, RootPanel root = null )
	{
		root ??= FindRootPanel();
		if ( root == null )
			return;

		if ( ancestors && !inRebuildStyleRulesList_Ancestors )
		{
			inRebuildStyleRulesList_Ancestors = true;
			Parent?.StyleSelectorsChanged( true, false, root );
		}

		if ( descendants && !inRebuildStyleRulesList_Children && HasChildren )
		{
			inRebuildStyleRulesList_Children = true;

			foreach ( var child in Children )
			{
				child.StyleSelectorsChanged( false, true, root );
			}
		}

		if ( !inRebuildStyleRulesList )
		{
			inRebuildStyleRulesList = true;
			root.AddToBuildStyleRulesList( this );
		}
	}

	//A pseudo-class came or went: this panel and its ancestors re-evaluate, and below it only what reads an ancestor's pseudo-class.
	void PseudoClassChanged( PseudoClass changed )
	{
		var root = FindRootPanel();
		if ( root == null )
			return;

		if ( !inRebuildStyleRulesList_Ancestors )
		{
			inRebuildStyleRulesList_Ancestors = true;
			Parent?.StyleSelectorsChanged( true, false, root );
		}

		if ( _children is not null )
		{
			for ( int i = 0; i < _children.Count; i++ )
			{
				_children[i]?.AncestorPseudoClassChanged( changed, root );
			}
		}

		if ( !inRebuildStyleRulesList )
		{
			inRebuildStyleRulesList = true;
			root.AddToBuildStyleRulesList( this );
		}
	}

	//Queues this panel if any rule it could wear reads these pseudo-classes off an ancestor, then asks its children.
	void AncestorPseudoClassChanged( PseudoClass changed, RootPanel root )
	{
		if ( !inRebuildStyleRulesList && ( Style?.ReadsContext( changed ) ?? false ) )
		{
			inRebuildStyleRulesList = true;
			root.AddToBuildStyleRulesList( this );
		}

		if ( _children is null )
			return;

		for ( int i = 0; i < _children.Count; i++ )
		{
			_children[i]?.AncestorPseudoClassChanged( changed, root );
		}
	}

}

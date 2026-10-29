namespace Rinten.UI;

public sealed class PanelStyle : Styles
{
	Panel panel;

	internal Styles Cached = new Styles();

	//Styles given as presentation attributes, lowest in the cascade. Only an svg element has any.
	internal Styles Presentation;

	//Sets a presentation attribute - an svg element's fill="..." and the rest - under every rule.
	internal bool SetPresentation( string property, string value )
	{
		Presentation ??= new Styles();

		if ( !Presentation.Set( property, value ) ) return false;

		rulesLayerBuilt = false;
		Dirty();
		return true;
	}
	internal Styles Final = new Styles();

	//This could be a local variable if we wanted to create a new class every time
	List<StyleSelector> activeRules;

	//Store the last active rules so we can compare them when they change and trigger sounds etc on new styles
	internal List<StyleSelector> LastActiveRules;

	//Cache of the active rules that are applied, that way we can trigger stuff only if they actually changed
	int ActiveRulesGuid;

	private bool isDirty = true;
	internal bool skipTransitions = true;

	bool rulesChanged = true;

	//A dirty style is turned into a layout by the panel's tick, so the tick is asked
	//for, and the layout is scheduled so an inline style change is not skipped by it.
	public override void Dirty()
	{
		isDirty = true;
		panel.RequestTick();
		panel?.SetNeedsPreLayout();
	}

	internal bool IsDirty => isDirty;

	internal PanelStyle( Panel panel )
	{
		this.panel = panel;
	}

	//Should be called when a stylesheet in our bundle has changed. This can happen as a result of editing it in the style editor.
	internal void UnderlyingStyleHasChanged()
	{
		ActiveRulesGuid = -1;
	}

	//Candidate style blocks from our and ancestors' sheets, filtered by class/element/id; null at first
	List<StyleBlock> StyleBlocks;

	//Scratch set for de-duplicating candidate blocks. Rules build on worker threads, so one per thread. Emptied around every gather: a block
	//held here would keep its stylesheet's textures alive, and a gather that threw would leave stale entries for the next one.
	[ThreadStatic]
	static HashSet<StyleBlock> seenBlocks;

	//A hash of the things that are checked in the broadphase.
	int broadPhaseHash = 0;

	bool _hasBeforeElement;
	bool _hasAfterElement;

	/// <summary>This style has a ::before element available. This is signalling to the panel system that if we apply this style, we should also create a ::before element.</summary>
	public bool HasBeforeElement => _hasBeforeElement;

	/// <summary>This style has a ::after element available. This is signalling to the panel system that if we apply this style, we should also create a ::after element.</summary>
	public bool HasAfterElement => _hasAfterElement;

	//Called when a stylesheet has been added or removed from ourselves or one of our
	//ancestor panels - because under that condition we need to rebuild our broadphase.
	internal void InvalidateBroadphase()
	{
		//Always walk the whole subtree and rebuild rules, so a stylesheet swap reaches every panel
		StyleBlocks = null;
		panel.StyleSelectorsChanged( false, false );

		foreach ( var child in panel.Children )
		{
			child.Style?.InvalidateBroadphase();
		}
	}

	void BuildApplicableBlocks()
	{
		var list = StyleBlocks ??= new List<StyleBlock>();
		list.Clear();

		//Gather only the rules indexed for our classes (plus the unindexed element/id/* rules) rather
		//than scanning every rule. ::before/::after broadphase against their parent's classes.
		var bp = ((IStyleTarget)panel).IsBeforeOrAfter ? panel.Parent : panel;

		if ( bp != null )
		{
			var seen = seenBlocks ??= new HashSet<StyleBlock>();
			seen.Clear();

			try
			{
				//Panel.AllStyleSheets, walked by hand: the iterator allocated on every gather.
				for ( var p = panel; p != null; p = p.Parent )
				{
					var sheets = p.StyleSheet.List;

					if ( sheets != null )
					{
						foreach ( var sheet in sheets )
							sheet.GatherCandidates( bp._class, panel, seen, list );
					}

					if ( p.StyleBoundary ) break;
				}
			}
			finally
			{
				seen.Clear();
			}
		}

		var flags = PseudoClass.None;

		foreach ( var block in list )
			flags |= block.ContextFlags;

		contextFlags = flags;
	}

	//The pseudo-classes any rule this panel could wear reads off a panel other than this one. See StyleSelector.ContextFlags.
	PseudoClass contextFlags;

	//Whether an ancestor's pseudo-class coming or going could change which rules apply here; yes until the candidates are gathered.
	internal bool ReadsContext( PseudoClass changed )
	{
		return StyleBlocks == null || ( contextFlags & changed ) != 0;
	}



	//Called from the root panel in a thread. We replace activeRules with all of the rules that we want applied and return true if the rules changed.
	internal bool BuildRulesInThread()
	{
		activeRules?.Clear();

		//The class set's own hash rather than the Classes string, which is built (and allocated) again after every class change just to be hashed here.
		var hash = HashCode.Combine( panel.Id, panel.ElementName, panel.ClassHash );
		if ( StyleBlocks == null || hash != broadPhaseHash )
		{
			BuildApplicableBlocks();
		}
		broadPhaseHash = hash;

		//Kept to compare: a ::before that comes with :hover flips these while the panel's own winning rules stay the same.
		var hadBefore = _hasBeforeElement;
		var hadAfter = _hasAfterElement;

		_hasBeforeElement = false;
		_hasAfterElement = false;

		bool isBeforeOrAfter = (panel as IStyleTarget).IsBeforeOrAfter;

		foreach ( var c in StyleBlocks )
		{
			//If we're not a ::before or ::after element, see if we have any styles with ::before or ::after elements.
			if ( !isBeforeOrAfter )
			{
				//Only probe blocks that actually have a ::before / ::after selector - the rest can never produce a pseudo-element, so testing them is wasted work.
				if ( !_hasBeforeElement && c.HasBefore )
					_hasBeforeElement = c.Test( panel, PseudoClass.Before ) != null;

				if ( !_hasAfterElement && c.HasAfter )
					_hasAfterElement = c.Test( panel, PseudoClass.After ) != null;
			}

			var winningSelector = c.Test( panel );
			if ( winningSelector == null ) continue;

			activeRules ??= new();
			activeRules.Add( winningSelector );
		}

		int ruleguid = 0;
		if ( activeRules != null )
		{
			activeRules.Sort( StyleOrderer.Instance );

			foreach ( var entry in activeRules )
			{
				ruleguid = HashCode.Combine( ruleguid, entry );
			}
		}

		rulesChanged = rulesChanged || ruleguid != ActiveRulesGuid;
		ActiveRulesGuid = ruleguid;

		//A pseudo-element coming or going is a change the tick has to act on, even with the same rules.
		return rulesChanged || hadBefore != _hasBeforeElement || hadAfter != _hasAfterElement;
	}

	//The styles under the inline layer, as BuildCached last merged them - see there.
	private readonly Styles rulesLayer = new();
	private bool rulesLayerBuilt;
	private int rulesLayerGuid;
	private uint rulesLayerEdits;

	private void BuildRulesLayer()
	{
		rulesLayer.From( Styles.Default );

		//A vector shape's presentation attributes - fill="red" in the markup - sit
		//under every rule, as SVG has them: a stylesheet that names the property wins.
		if ( Presentation is not null )
			rulesLayer.Add( Presentation );

		if ( activeRules != null )
		{
			foreach ( var entry in activeRules )
			{
				rulesLayer.Add( entry.Block.Styles );
			}
		}

		rulesLayerBuilt = true;
		rulesLayerGuid = ActiveRulesGuid;
		rulesLayerEdits = Styles.RuleEdits;
	}

	internal bool BuildCached( ref LayoutCascade cascade )
	{
		if ( !isDirty && !cascade.SelectorChanged && !rulesChanged )
			return false;

		isDirty = false;

		//Everything under the panel's own inline style, kept while none of it moved: the defaults, the presentation attributes and every winning rule, merged
		//a block at a time. A HUD whose bars carry style="height: ..." changed only the inline layer, and merged every rule again for each bar each time.
		if ( !rulesLayerBuilt || rulesChanged || cascade.SelectorChanged
			|| rulesLayerGuid != ActiveRulesGuid || rulesLayerEdits != Styles.RuleEdits )
		{
			BuildRulesLayer();
		}

		Cached.From( rulesLayer );

		//Rules changed
		if ( rulesChanged )
		{
			rulesChanged = false;
			cascade.SelectorChanged = true;

			LastActiveRules ??= new();
			activeRules ??= new();

			//Rules are ordered by cascade priority. Only the winning sound rule can trigger.
			var soundIn = activeRules.LastOrDefault( x => x.Block.Styles.SoundIn != null );
			if ( soundIn != null && !LastActiveRules.Contains( soundIn ) )
				OnRuleAdded( soundIn );

			var soundOut = LastActiveRules.LastOrDefault( x => x.Block.Styles.SoundOut != null );
			if ( soundOut != null && !activeRules.Contains( soundOut ) )
				OnRuleRemoved( soundOut );

			LastActiveRules.Clear();
			LastActiveRules.AddRange( activeRules );
		}

		Cached.Add( this );

		Cached.ApplyScale( cascade.Scale );
		return true;
	}

	//Whether Final has been through the passes below at least once.
	private bool finalBuilt;

	internal Styles BuildFinal( ref LayoutCascade cascade, out bool changed )
	{
		var time = panel.TimeNow;
		var timeDelta = panel.TimeDelta;

		cascade.SkipTransitions = skipTransitions || cascade.SkipTransitions;
		changed = BuildCached( ref cascade );

		//Nothing moved, so Final is what the passes below would produce. The animation to check is Final's, not the inline style's.
		if ( finalBuilt
			&& !changed
			&& !cascade.SkipTransitions
			&& !cascade.ParentChanged
			&& !HasTransitions
			&& !Final.HasAnimation
			&& !panel.HasActiveTransitions )
		{
			return Final;
		}

		if ( !cascade.SkipTransitions && !HasTransitions && !HasAnimation )
		{
			Final.CopyShadows( Cached );
		}

		if ( cascade.SkipTransitions )
		{
			Final.CopyShadows( Cached );
			panel.Transitions.Kill();
			skipTransitions = false;
		}
		else if ( changed && Final != null )
		{
			panel.Transitions.Kill( Final );
			panel.Transitions.Add( Final, Cached, time - timeDelta );
		}

		Final.From( Cached );
		Final.ResolveCssWide( cascade.ParentStyles );
		cascade.ApplyCascading( Final );
		var needsDefaultFontSize = !Final.FontSize.HasValue;
		Final.FillDefaults();

		//Defaults are filled after scaling. Scale the fallback font too, but leave inherited
		//and explicitly styled fonts alone - they're already in screen pixels.
		if ( needsDefaultFontSize )
			Length.Scale( ref Final._fontsize, cascade.Scale, true );

		if ( Final.HasCurrentColor )
			Final.ResolveCurrentColor( cascade.ParentStyles );

		if ( panel.Transitions.Run( Final, time ) )
		{
			changed = true;
		}

		finalBuilt = true;

		if ( Final.ApplyAnimation( panel ) )
		{
			changed = true;
		}

		return Final;
	}

	public override bool Set( string property, string value )
	{
		Dirty();

		return base.Set( property, value );
	}

	internal void OnRuleAdded( StyleSelector selector )
	{
		if ( selector.Block.Styles.SoundIn != null )
		{
			panel.PlaySound( selector.Block.Styles.SoundIn );
		}
	}

	internal void OnRuleRemoved( StyleSelector selector )
	{
		if ( selector.Block.Styles.SoundOut != null )
		{
			panel.PlaySound( selector.Block.Styles.SoundOut );
		}
	}

	//Helpers


	public void SetBackgroundImage( Texture texture )
	{
		BackgroundImage = texture;
	}

	public void SetBackgroundImage( string image )
	{
		SetBackgroundImage( Texture.Load( image ) );
	}

	public async Task SetBackgroundImageAsync( string image )
	{
		SetBackgroundImage( await Texture.LoadAsync( image ) );
	}

	public void SetRect( Rect rect )
	{
		Left = rect.Left;
		Top = rect.Top;
		Width = rect.Width;
		Height = rect.Height;

		Dirty();
	}

	//Returns true if we have the style
	internal bool ContainsStyle( Styles style )
	{
		if ( LastActiveRules == null ) return false;

		foreach ( var rule in LastActiveRules )
		{
			if ( rule.Block.Styles == style )
				return true;
		}

		return false;
	}
}

internal class StyleOrderer : IComparer<StyleSelector>
{
	internal static StyleOrderer Instance = new StyleOrderer();

	public int Compare( StyleSelector x, StyleSelector y )
	{
		return x.Score.CompareTo( y.Score );
	}
}

using Rinten.Engine;
using Rinten.Rendering;

namespace Rinten.UI;

/// <summary>A root panel. Serves as a container for other panels, handles things such as rendering.</summary>
public partial class RootPanel : Panel
{
	/// <summary>Bounds of the panel, i.e. its size and position on the screen.</summary>
	public Rect PanelBounds { get; set; } = new Rect( 0, 0, 512, 512 );

	//If any of our panels are visible and want mouse input (pointer-events != none) then this will be set to true.
	internal bool ChildrenWantMouseInput { get; set; }

	/// <summary>The scale of this panel and its children.</summary>
	public float Scale { get; protected set; } = 1.0f;

	/// <summary>If set to true this panel won't be rendered to the screen like a normal panel. This is true when the panel is drawn via other means (like as a world panel).</summary>
	public bool RenderedManually { get; set; }

	//Root driven by its own window (editor windows); registered here but the game UI must not tick it
	internal virtual bool ExternallyDriven => false;

	/// <summary>This tree is the editor's own interface: a window of docks, inspectors and menus.</summary>
	public bool IsEditorUi { get; internal set; }

	/// <summary>Smoothed per-frame UI cost of this root's window (layout, input, draw); zero if unmeasured</summary>
	public UiFrameCost FrameCost { get; internal set; }

	/// <summary>True if this is a world panel, so should be skipped when determining cursor visibility etc</summary>
	public virtual bool IsWorldPanel { get; set; }

	/// <summary>If this panel belongs to a VR overlay</summary>
	public bool IsVR { get; internal set; }

	/// <summary>If this panel should be rendered with ~4K resolution.</summary>
	public bool IsHighQualityVR { get; internal set; }

	//Current global mouse position, projected onto plane for world panels.
	internal Vector2 MousePos;

	//The frame the flat render walk last made of this tree, kept by CoreUiRenderer
	//and handed over again while nothing in the tree changed. Editor windows only.
	internal CoreUiRenderer.FrameGeometry Frame;

	//Single flat command list used by the flat rendering path. All panels record into this one list instead of per-panel lists.
	internal readonly CommandList PanelCommandList;

	public RootPanel()
	{
		Style.Width = Length.Percent( 100 );
		Style.Height = Length.Percent( 100 );

		PanelCommandList = new CommandList( $"UI Root: {GetType().Name}" );

		GlobalContext.Current.UISystem.AddRoot( this );
		AddToLists();

		//The interface's own defaults, which live in the base library's content. Quietly where there is
		//none mounted: an editor window is a root panel too, and it is open long before any game is.
		StyleSheet.Load( "/styles/rootpanel.scss", failSilently: true );
	}

	public override void Delete( bool immediate = true )
	{
		base.Delete( immediate );
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		GlobalContext.Current.UISystem.RemoveRoot( this );

		//The kept frame's lists go back to the walk's pool rather than to the collector.
		if ( Frame is not null )
		{
			CoreUiRenderer.Recycle( Frame.Batches );
			Frame = null;
		}
	}

	internal override void AddToLists()
	{
		base.AddToLists();

		Rinten.Internal.IPanel.InspectablePanels.Add( this );
	}

	internal override void RemoveFromLists()
	{
		base.RemoveFromLists();

		Rinten.Internal.IPanel.InspectablePanels.Remove( this );
	}

	//This is called from tests to emulate the regular root panel simulate loop
	internal void Layout()
	{
		TickInternal();
		PreLayout();
		CalculateLayout();
		PostLayout();
	}

	//Panel layout only — no tick. Used while editor widgets settle flex geometry.
	internal void SettleLayout()
	{
		PreLayout();
		CalculateLayout();
		PostLayout();
	}

	int layoutHash;

	//Called before layout to lock the bounds of this root panel to the screen size
	//(which is passed). Internally this sets PanelBounds to rect and calls UpdateScale.
	protected virtual void UpdateBounds( Rect rect )
	{
		PanelBounds = rect;

		if ( IsVR && IsHighQualityVR )
		{
			PanelBounds = new Rect( 0, 0, 3840, 2400 );
			return;
		}
	}

	//Work out scaling here. Default is to scale relative to the screen being 1920 wide. ie - scale = screensize.Width / 1920.0f;
	protected virtual void UpdateScale( Rect screenSize )
	{
		Scale = screenSize.Height / 1080.0f;

		if ( IsVR && IsHighQualityVR )
		{
			Scale = 2.33f;
		}
	}

	internal void TickInputInternal()
	{
		ChildrenWantMouseInput = WantsMouseInput();
	}

	internal void PreLayout( Rect screenSize )
	{
		UpdateBounds( screenSize );
		UpdateScale( PanelBounds );

		Scale = MathX.Clamp( Scale, 0.1f, 10.0f );

		PreLayout();
	}

	internal void PreLayout()
	{
		var cascade = new LayoutCascade
		{
			Scale = Scale,
			Root = this,
		};

		Style.Left = 0.0f;
		Style.Top = 0.0f;
		Style.Width = PanelBounds.Width * (1 / Scale);
		Style.Height = PanelBounds.Height * (1 / Scale);

		var hash = HashCode.Combine( PanelBounds.Width, PanelBounds.Height, Scale );
		if ( hash != layoutHash )
		{
			layoutHash = hash;
			StyleSelectorsChanged( true, true );
			SkipAllTransitions();

			cascade.SelectorChanged = true;
		}

		BuildStyleRules();

		PushRootValues();

		//A face came since: on a page, one still downloading when text was first set in a stand-in - every label sets its text again
		if ( fontGeneration != FontManager.Generation )
		{
			fontGeneration = FontManager.Generation;

			foreach ( var panel in Descendants )
			{
				if ( panel is Label ) panel.SetNeedsPreLayout();
			}
		}

		PreLayout( cascade );
	}

	//The faces this tree's labels were last set in - see FontManager.Generation.
	int fontGeneration = -1;

	internal void CalculateLayout()
	{
		if ( LayoutTree == null )
			return;

		using var perfScope = Performance.Scope( "CalculateLayout" );

		//Under -uitrace, every node the layout's cache could not answer for is counted, as the
		//calls of an empty zone: how many boxes a pass really visits, beside what it costs.
		if ( TracingTick ) global::Rinten.Layout.FlexLayout.Trace ??= static ( _, _, _, _, _, _, _ ) => Performance.Scope( "layout visit" ).Dispose();

		PushRootValues();
		LayoutTree.CalculateLayout();
	}

	internal void PostLayout()
	{
		PushRootValues();
		FinalLayout( Vector2.Zero );

		//Once the layout is final, and only then: the render walk uses these to drop a subtree that cannot reach the clip it would be drawn into.
		UpdateSubtreeBounds();
	}

	internal void PushRootValues()
	{
		Length.RootSize = new Vector2( PanelBounds.Width, PanelBounds.Height );
		Length.RootFontSize = ComputedStyle?.FontSize ?? Length.Pixels( 13 ).Value;
		Length.RootScale = ScaleToScreen;
	}

	/// <summary>Tab and Shift+Tab that nothing below us handled move focus through the tree. Not in the editor's own windows, whose keyboard focus belongs to the editor.</summary>
	public override void OnButtonTyped( ButtonEvent e )
	{
		if ( e.Button == "tab" && e.Pressed && !ExternallyDriven )
		{
			var from = InputFocus.Next ?? InputFocus.Current;
			if ( Rinten.Engine.GlobalContext.Current.UISystem?.MoveFocus( from, e.HasShift ) ?? false )
				return;
		}

		base.OnButtonTyped( e );
	}

	public override void OnLayout( ref Rect layoutRect )
	{
		layoutRect = PanelBounds;
	}

	internal void Render( float opacity = 1.0f )
	{
		PanelCommandList.ExecuteOnRenderThread();
	}

	//Build descriptors for this panel and all children. Called during the tick phase, before gathering.
	internal void BuildDescriptors( float opacity = 1.0f )
	{
		var renderer = GlobalContext.Current.UISystem.Renderer;
		renderer.BuildDescriptors( this, opacity );
	}

	internal void BuildCommandList( float opacity = 1.0f )
	{
		var renderer = GlobalContext.Current.UISystem.Renderer;
		renderer.BuildCommandList( this, opacity );
	}

	/// <summary>Render this panel manually. This gives more flexibility to where UI is rendered, to texture for example. <see cref="RenderedManually"/> must be set to true.</summary>
	public void RenderManual( float opacity = 1.0f )
	{
		Graphics.AssertRenderBlock();

		if ( !RenderedManually && !IsWorldPanel )
			throw new Exception( $"{nameof( RenderedManually )} must be set to true to render this panel manually." );

		BuildCommandList( opacity );
		Render( opacity );
	}

	[Event( "ui.skiptransitions" )]
	internal void SkipAllTransitions()
	{
		SkipTransitions();
	}

	//A list of panels that are waiting to have their styles re-evaluated
	readonly HashSet<Panel> styleRuleUpdates = new();

	//The queue laid out as a list, for the job workers to share out by index.
	readonly List<Panel> styleRuleList = new();

	//Throttles the BuildStyleRules readout under tracing.
	static RealTimeSince sinceSaidRules;

	//How many queued panels it takes before their rules are built on the pool rather than here.
	const int ParallelRulesFrom = 64;

	//Add this panel to a list to have their styles re-evaluated. This should be done any time the panel changes in a way that
	//could affect its style selector.. like if its child index changed, or classes added or removed, or became hovered etc.
	internal void AddToBuildStyleRulesList( Panel panel )
	{
		styleRuleUpdates.Add( panel );
	}

	//Run through all panels that are pending a re-check on their style rules. Only properly invalidate them if their rules actually change.
	internal void BuildStyleRules()
	{
		if ( styleRuleUpdates.Count == 0 )
			return;

		BuildQueuedStyleRules();
	}

	//The work of BuildStyleRules, apart from its early out. Its local function captures the lock and the count and goes to Parallel.ForEach as a delegate,
	//so the method builds a closure on entry - and with the early out in the same method, that was every layout of every window with nothing queued.
	private void BuildQueuedStyleRules()
	{
		var timer = FastTimer.StartNew();
		int count = styleRuleUpdates.Count;
		int locks = 0;

		var l = new object();

		//Anything in BuildRules should be thread safe.
		void Build( Panel panel )
		{
			try
			{
				if ( !panel.IsValid() || panel.Style is null )
					return;

				if ( panel.Style.BuildRulesInThread() )
				{
					lock ( l )
					{
						locks++;
						panel.SetNeedsPreLayout();

						//New rules may bring a ::before or ::after, which the tick makes.
						panel.RequestTick();
					}
				}

				panel.MarkStylesRebuilt();
			}
			catch ( Exception e )
			{
				Log.Warning( e, e.Message );
			}
		}

		//A handful is done here: at rest the queue is a panel or two, and the pool costs more than the rules.
		if ( count < ParallelRulesFrom || Rinten.Utility.Parallel.InOrder )
		{
			foreach ( var panel in styleRuleUpdates )
				Build( panel );
		}
		else
		{
			styleRuleList.Clear();
			styleRuleList.AddRange( styleRuleUpdates );

			Rinten.Jobs.JobSystem.For( styleRuleList.Count, (styleRuleList, (Action<Panel>)Build), static ( s, i ) => s.Item2( s.styleRuleList[i] ) );
		}

		styleRuleUpdates.Clear();

		//Under tracing twice a second regardless; otherwise only when it was slow.
		if ( TracingTick && sinceSaidRules > 0.5f )
		{
			sinceSaidRules = 0;
			Log.Trace( $"BuildStyleRules queued {count:n0}, changed {locks}, took {timer.ElapsedMilliSeconds}ms" );
		}
		else if ( timer.ElapsedMilliSeconds > 0.5 )
		{
			Log.Trace( $"BuildStyleRules {count:n0} ({locks}) took {timer.ElapsedMilliSeconds}ms" );
		}
	}
}

using Rinten;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Editor;

/// <summary>Docking: four edges around a middle, and tabs where more than one dock shares an edge.</summary>
[FramesOnDemand]
public partial class DockManager
{
	//A layout node: tabbed docks or a split of two nodes; the five edges are only the starting leaves
	internal sealed class Side
	{
		/// <summary>The edge this started as, or Hidden for one somebody made.</summary>
		public DockArea Area { get; init; }

		/// <summary>Made by a drop rather than by the default layout, so it can go again.</summary>
		public bool Made { get; init; }

		public List<DockWidget> Docks { get; } = new();

		/// <summary>Which dock is showing, where several share the group.</summary>
		public int Current;

		/// <summary>How much of this the first half takes, where it is split.</summary>
		public float Share = 0.2f;

		/// <summary>The two halves, where this is a split rather than a group.</summary>
		public Side First;
		public Side Second;

		/// <summary>Whether the halves are beside each other rather than stacked.</summary>
		public bool SideBySide;

		/// <summary>Where it ended up, so the pointer can be tested against it.</summary>
		public Rect Rect;

		public bool IsSplit => First is not null;

		/// <summary>The docks here that are open, and here rather than in a window of their own.</summary>
		public IEnumerable<DockWidget> Open => Docks.Where( x => !x.IsClosed && !x.Floating );

		/// <summary>Whether there is anything to give room to.</summary>
		public bool Any => IsSplit ? First.Any || Second.Any : Open.Any();

		/// <summary>Whether anything here wears a name. The central area doesn't: the scene is the middle of the window rather than a panel you picked, and a strip saying so - with a close box, on the one dock that can't be closed - is a row of pixels spent on nothing.</summary>
		public bool AnyTabbed => Open.Any( x => !x.NoTab );
	}

	private Side root;

	//The whole layout, built the first time anything asks for it.
	private Side Root => root ??= DefaultLayout();

	//The five edges, nested the way the old fixed arrangement nested them: the left and the right run
	//the height of the window, and the top, the middle and the bottom share the column between them.
	private static Side DefaultLayout()
	{
		var column = Split(
			new Side { Area = DockArea.Top },
			Split(
				new Side { Area = DockArea.Center },
				new Side { Area = DockArea.Bottom },
				false, 0.75f ),
			false, 0.2f );

		return Split(
			new Side { Area = DockArea.Left },
			Split( column, new Side { Area = DockArea.Right }, true, 0.75f ),
			true, 0.2f );
	}

	//Two sides, one against the other.
	private static Side Split( Side first, Side second, bool sideBySide, float share )
	{
		return new Side { First = first, Second = second, SideBySide = sideBySide, Share = share };
	}

	//Every group in the layout, in the order they are laid out.
	private IEnumerable<Side> Leaves( Side from = null )
	{
		var node = from ?? Root;

		if ( !node.IsSplit )
		{
			yield return node;
			yield break;
		}

		foreach ( var leaf in Leaves( node.First ) ) yield return leaf;
		foreach ( var leaf in Leaves( node.Second ) ) yield return leaf;
	}

	//The split holding this one, or null for the root.
	private Side ParentOf( Side child, Side from = null )
	{
		var node = from ?? Root;

		if ( !node.IsSplit ) return null;
		if ( node.First == child || node.Second == child ) return node;

		return ParentOf( child, node.First ) ?? ParentOf( child, node.Second );
	}

	//The first group inside this, which is itself where it is one.
	private static Side LeafOf( Side node )
	{
		while ( node.IsSplit ) node = node.First;

		return node;
	}

	//Auto-hide edges share the same leaf as their docked edge.
	private static DockArea LeafArea( DockArea area ) => area switch
	{
		DockArea.AutoHideBottom => DockArea.Bottom,
		_ => area,
	};

	//The group a named edge points at, made against the window if it has gone.
	private Side LeafFor( DockArea area )
	{
		area = LeafArea( area );

		foreach ( var leaf in Leaves() )
		{
			if ( leaf.Area == area ) return leaf;
		}

		return MakeEdge( area == DockArea.Hidden ? DockArea.Center : area );
	}

	//A new group against one edge of the whole window.
	private Side MakeEdge( DockArea area )
	{
		if ( area == DockArea.Center ) return LeafOf( Root );

		var made = new Side { Area = area };

		//The root keeps its identity - everything reaches the tree through it - so what moves is a copy of what was in it.
		var was = new Side();

		Adopt( was, Root );

		var first = area is DockArea.Left or DockArea.Top;

		root.Docks.Clear();
		root.Current = 0;
		root.SideBySide = area is DockArea.Left or DockArea.Right;
		root.Share = first ? 0.2f : 0.8f;
		root.First = first ? made : was;
		root.Second = first ? was : made;

		return made;
	}

	//Makes one side into a copy of another, keeping the object itself.
	private static void Adopt( Side node, Side from )
	{
		node.Docks.Clear();
		node.Docks.AddRange( from.Docks );
		node.Current = from.Current;
		node.First = from.First;
		node.Second = from.Second;
		node.SideBySide = from.SideBySide;
		node.Share = from.Share;
	}

	//Takes a dock out of whatever group it is in.
	private void Detach( DockWidget dock )
	{
		foreach ( var leaf in Leaves().ToList() ) leaf.Docks.Remove( dock );

		Prune( Root );
	}

	//A split with an empty half becomes the half that is left. Only for a group somebody made by dropping: the five the layout starts with
	//keep their place while they are empty, so a panel closed and opened again comes back to the edge it belongs to rather than to the middle.
	private void Prune( Side node )
	{
		if ( node is null || !node.IsSplit ) return;

		Prune( node.First );
		Prune( node.Second );

		var gone = Spent( node.First ) ? node.Second : Spent( node.Second ) ? node.First : null;

		if ( gone is null ) return;

		Adopt( node, gone );

		//What it became can be collapsible in turn, and it shrinks every time, so asking again ends.
		Prune( node );
	}

	//Whether this group was made by a drop and has nothing left in it.
	private static bool Spent( Side node ) => node is { IsSplit: false, Made: true, Docks.Count: 0 };

	//How tall the strip of tab names is.
	private const float TabHeight = 24.0f;

	//How wide the gap between two edges is: the theme's one separator width.
	private static float Gap => Theme.SeparatorWidth;

	//The split being moved, while one is.
	private Side dragging;

	//How wide the close box at the end of a tab is.
	private const float CloseSize = 16.0f;

	//The size of the icon at the front of a tab.
	private const float TabIconSize = 14.0f;

	//How far a press has to travel before it is carrying a dock.
	private const float DragStarts = 6.0f;

	//The tab the pointer is over, which is what says a strip can be picked up.
	private DockWidget hovered;

	//The tab the pointer went down on, until it is a click or a drag.
	private DockWidget pressed;
	private Vector2 pressedAt;

	//The dock being carried to another edge, if one is.
	private DockWidget carrying;

	//A floating window of this layout being carried by its caption, which can be put down on this layout - see CoreWindow.Carry
	private FloatingDock arriving;

	//Where it would land if it were let go now.
	private DockArea landing = DockArea.Hidden;

	//The group the pointer is over, which is what the compass sits on.
	private Side over;

	//Which gap of that group's tab strip it would go into; -1 is the end of it.
	private int landingSlot = -1;

	//Where a dock let go now would land among the tabs.
	internal int LandingSlot => landingSlot;

	//Whether the square under the pointer is one of the window's own.
	private bool landingOuter;

	//Where the middle of the compass is, in this widget's own pixels.
	private Vector2 compass;

	//How wide a tab is: its name, its close box, and room around both.
	private static float TabWidth( DockWidget dock )
		=> EditorText.Measure( dock.Title ).x + 24.0f + CloseSize + IconRoom( dock );

	//The room a tab's icon takes from the front of it: the glyph and the gap after.
	private static float IconRoom( DockWidget dock )
		=> string.IsNullOrEmpty( dock.Icon ) ? 0.0f : TabIconSize + 6.0f;

	//The close box at the end of a tab.
	private static Rect CloseOf( Rect tab )
		=> new( tab.Right - CloseSize - 6.0f, tab.Top + ( ( tab.Height - CloseSize ) * 0.5f ), CloseSize, CloseSize );

	//Where each of a group's tabs sits, left to right, with its place in the open ones.
	private IEnumerable<(DockWidget Dock, Rect Tab, int Index)> TabsOf( Side side )
	{
		var strip = StripOf( side );

		if ( strip.Width <= 0.0f ) yield break;

		var open = side.Open.ToList();
		var at = strip.Left;

		for ( var i = 0; i < open.Count; i++ )
		{
			var dock = open[i];

			//The ones that asked for no tab take no room on the strip.
			if ( dock.NoTab ) continue;

			var width = TabWidth( dock );

			yield return (dock, new Rect( at, strip.Top, width, strip.Height ), i);

			at += width;
		}
	}

	//The tab under a point, where there is one.
	private DockWidget TabAt( Vector2 local )
	{
		foreach ( var side in Leaves() )
		{
			var strip = StripOf( side );

			if ( strip.Width <= 0.0f || !strip.IsInside( local ) ) continue;

			foreach ( var (dock, tab, _) in TabsOf( side ) )
			{
				if ( tab.IsInside( local ) ) return dock;
			}

			return null;
		}

		return null;
	}

	//Which gap between a group's tabs a point is at: 0 in front of the first, one past the last for the end. By
	//the middle of each tab, so a tab carried a little way along the strip changes places with its neighbour.
	private int TabSlotAt( Side side, Vector2 local )
	{
		var slot = 0;

		foreach ( var (_, tab, _) in TabsOf( side ) )
		{
			if ( local.x < tab.Center.x ) break;

			slot++;
		}

		return slot;
	}

	//How wide the mark between two tabs is.
	private const float CaretWidth = 3.0f;

	//The mark saying which gap of a strip a carried dock would go into; -1 for the end of it.
	internal Rect CaretOf( Side side, int slot )
	{
		var strip = StripOf( side );

		if ( strip.Width <= 0.0f ) return default;

		var at = strip.Left;
		var seen = 0;

		foreach ( var (_, tab, _) in TabsOf( side ) )
		{
			if ( seen == slot ) { at = tab.Left; break; }

			at = tab.Right;
			seen++;
		}

		//A strip narrower than the tabs on it still has its two ends.
		var least = strip.Left + ( CaretWidth * 0.5f );
		var most = MathF.Max( strip.Right - ( CaretWidth * 0.5f ), least );

		at = Math.Clamp( at, least, most );

		return new Rect( at - ( CaretWidth * 0.5f ), strip.Top + 2.0f, CaretWidth, MathF.Max( strip.Height - 4.0f, 0.0f ) );
	}

	//How far each tab has faded into being the open one.
	private readonly Dictionary<DockWidget, Theme.Lit> lighting = new();

	//Steps a tab's hover fade and repaints while moving; old entries are pruned to not hold closed docks
	private float Lighting( DockWidget dock, bool on )
	{
		if ( lighting.Count > 64 )
		{
			foreach ( var gone in lighting.Keys.Where( x => !x.IsValid() ).ToList() )
				lighting.Remove( gone );
		}

		lighting.TryGetValue( dock, out var fade );

		var amount = fade.Step( on );

		lighting[dock] = fade;

		if ( fade.Moving ) Update();

		return amount;
	}

	//Drop targets drawn over the hovered group: as a tab, or one of its four halves
	private static readonly DockArea[] Compass =
	{
		DockArea.Center, DockArea.Left, DockArea.Right, DockArea.Top, DockArea.Bottom
	};

	//How big one square of the compass is.
	private const float TargetSize = 46.0f;

	//The room between two of them.
	private const float TargetGap = 8.0f;

	//The four edges of the window itself, drawn against them.
	private static readonly DockArea[] Edges =
	{
		DockArea.Left, DockArea.Right, DockArea.Top, DockArea.Bottom
	};

	//How far in from the edge of the window its own squares sit.
	private const float OuterInset = 12.0f;

	//The group under a point, or null where the layout is empty.
	private Side LeafAt( Vector2 local )
	{
		foreach ( var leaf in Leaves() )
		{
			if ( !leaf.Any ) continue;

			if ( leaf.Rect.IsInside( local ) ) return leaf;
		}

		return null;
	}

	//Where one square of the group's compass sits.
	internal Rect TargetOf( DockArea area )
	{
		var middle = compass;
		var step = TargetSize + TargetGap;

		var offset = area switch
		{
			DockArea.Left => new Vector2( -step, 0.0f ),
			DockArea.Right => new Vector2( step, 0.0f ),
			DockArea.Top => new Vector2( 0.0f, -step ),
			DockArea.Bottom => new Vector2( 0.0f, step ),
			_ => Vector2.Zero,
		};

		return new Rect(
			middle.x + offset.x - ( TargetSize * 0.5f ),
			middle.y + offset.y - ( TargetSize * 0.5f ),
			TargetSize, TargetSize );
	}

	//Square for docking against a whole-window edge, pinned to the window rather than the group
	private Rect OuterOf( DockArea area )
	{
		var rect = LocalRect;
		var half = TargetSize * 0.5f;

		return area switch
		{
			DockArea.Left => new Rect( rect.Left + OuterInset, rect.Center.y - half, TargetSize, TargetSize ),
			DockArea.Right => new Rect( rect.Right - OuterInset - TargetSize, rect.Center.y - half, TargetSize, TargetSize ),
			DockArea.Top => new Rect( rect.Center.x - half, rect.Top + OuterInset, TargetSize, TargetSize ),
			DockArea.Bottom => new Rect( rect.Center.x - half, rect.Bottom - OuterInset - TargetSize, TargetSize, TargetSize ),
			_ => default,
		};
	}

	//Points the compass at somewhere in the window, as carrying a dock over that point does, and answers what letting go there would do.
	internal DockArea AimAt( Vector2 local, out bool outer )
	{
		over = LeafAt( local );
		compass = over is not null ? over.Rect.Center : LocalRect.Center;

		Aim( local, out var area, out outer );

		return area;
	}

	//What the pointer is over: one of the window's four, or the group's five.
	private void Aim( Vector2 local, out DockArea area, out bool outer )
	{
		landingSlot = -1;

		foreach ( var edge in Edges )
		{
			if ( !OuterOf( edge ).IsInside( local ) ) continue;

			area = edge;
			outer = true;
			return;
		}

		outer = false;
		area = DockArea.Hidden;

		//The group's own five are only drawn when there is a group under the pointer, and a square nobody can see is not one to be caught by.
		if ( over is null ) return;

		foreach ( var square in Compass )
		{
			if ( !TargetOf( square ).IsInside( local ) ) continue;

			area = square;
			return;
		}

		//The strip of names is where tabs live, so letting go on it is asking for another one rather than
		//for the group to be split along its top - and which gap of it says where in the order that tab goes.
		if ( StripOf( over ).IsInside( local ) )
		{
			area = DockArea.Center;
			landingSlot = TabSlotAt( over, local );
			return;
		}

		//And anywhere else in the group: whichever part of it the pointer is in. Pointing at a panel is already saying which panel, and having to find
		//the arrow afterwards to say it again is a step nobody wants twice - the squares are what the answer looks like, not the only way to give it.
		area = PartOf( over.Rect, local );
	}

	//How much of a group's middle is "as another tab in it".
	private const float Middle = 0.5f;

	//Which part of a group a point is in: the middle, or one of the sides.
	private static DockArea PartOf( Rect rect, Vector2 point )
	{
		if ( rect.Width <= 0.0f || rect.Height <= 0.0f ) return DockArea.Center;

		var across = ( point.x - rect.Center.x ) / ( rect.Width * 0.5f );
		var down = ( point.y - rect.Center.y ) / ( rect.Height * 0.5f );

		if ( MathF.Abs( across ) < Middle && MathF.Abs( down ) < Middle ) return DockArea.Center;

		if ( MathF.Abs( across ) > MathF.Abs( down ) ) return across < 0.0f ? DockArea.Left : DockArea.Right;

		return down < 0.0f ? DockArea.Top : DockArea.Bottom;
	}

	//The arrow drawn on one square.
	private static string TargetIcon( DockArea area ) => area switch
	{
		DockArea.Left => "arrow_back",
		DockArea.Right => "arrow_forward",
		DockArea.Top => "arrow_upward",
		DockArea.Bottom => "arrow_downward",
		_ => "crop_square",
	};

	//The room a dock would take, which is what the highlight covers. The room itself, not the edge of the screen it is nearest:
	//dropped on the side of a group it takes half of that group, and dropped on the middle of one it takes all of it as another tab.
	internal Rect ZoneRect( DockArea area, bool outer )
	{
		if ( !outer && over is not null ) return HalfOf( over.Rect, area );

		var rect = LocalRect;

		//Against the window, where the edge already has a group: as much as that group has, because that is where it is going.
		foreach ( var leaf in Leaves() )
		{
			if ( leaf.Area == area && leaf.Any ) return leaf.Rect;
		}

		return HalfOf( rect, area );
	}

	//The half of a rectangle on one side of it, or all of it for the middle.
	private static Rect HalfOf( Rect rect, DockArea area ) => area switch
	{
		DockArea.Left => new Rect( rect.Left, rect.Top, rect.Width * 0.5f, rect.Height ),
		DockArea.Right => new Rect( rect.Left + ( rect.Width * 0.5f ), rect.Top, rect.Width * 0.5f, rect.Height ),
		DockArea.Top => new Rect( rect.Left, rect.Top, rect.Width, rect.Height * 0.5f ),
		DockArea.Bottom => new Rect( rect.Left, rect.Top + ( rect.Height * 0.5f ), rect.Width, rect.Height * 0.5f ),
		_ => rect,
	};

	internal Side SideOf( DockWidget dock )
	{
		return Leaves().FirstOrDefault( x => x.Docks.Contains( dock ) );
	}

	//Puts a dock on an edge, and shows it.
	internal void PlaceManaged( DockWidget dock, DockArea area )
	{
		if ( dock is null ) return;

		Detach( dock );

		Join( dock, LeafFor( area ), -1 );

		if ( area == DockArea.Hidden ) dock.ToggleView( false );
	}

	//Puts a dock in the group another one is in, as a tab beside it.
	internal void PlaceManaged( DockWidget dock, DockWidget beside )
	{
		if ( dock is null ) return;

		var leaf = SideOf( beside );

		if ( leaf is null )
		{
			PlaceManaged( dock, DockArea.Center );
			return;
		}

		Detach( dock );

		Join( dock, leaf, leaf.Docks.IndexOf( beside ) + 1 );
	}

	//Puts a dock into a group at a place among its tabs; -1 is the end of them. The gap is turned into
	//a tab to sit in front of before anything moves: taking the dock out shifts every tab after it.
	internal void PlaceManaged( DockWidget dock, Side leaf, int slot )
	{
		if ( dock is null ) return;

		if ( leaf is null || leaf.IsSplit )
		{
			PlaceManaged( dock, DockArea.Center );
			return;
		}

		//The only one on this strip, dropped back on it: it is already where the mark was, and taking it out would collapse the group it is going into.
		if ( leaf.Docks.Contains( dock ) && leaf.Open.Count() == 1 )
		{
			dock.SetAsCurrentTab();
			return;
		}

		var tabs = TabsOf( leaf ).Select( x => x.Dock ).ToList();
		var before = slot >= 0 && slot < tabs.Count ? tabs[slot] : null;

		//Let go on itself.
		if ( before == dock )
		{
			dock.SetAsCurrentTab();
			return;
		}

		Detach( dock );

		Join( dock, leaf, before is null ? -1 : leaf.Docks.IndexOf( before ) );
	}

	//Puts a dock against one side of a group, which splits that group in two. The group keeps its own identity and becomes the split: what was in it
	//moves into one half and the dock arrives in the other. Everything that points at a group - the edge names, the handles - goes on pointing at it.
	internal void SplitManaged( DockWidget dock, Side leaf, DockArea area )
	{
		if ( dock is null || leaf is null || leaf.IsSplit ) return;

		if ( area is DockArea.Center or DockArea.Hidden )
		{
			PlaceManaged( dock, leaf.Docks.FirstOrDefault() ?? dock );
			return;
		}

		Detach( dock );

		var kept = new Side { Area = leaf.Area, Made = true, Current = leaf.Current };
		kept.Docks.AddRange( leaf.Docks );

		var made = new Side { Area = DockArea.Hidden, Made = true };
		made.Docks.Add( dock );

		var first = area is DockArea.Left or DockArea.Top;

		leaf.Docks.Clear();
		leaf.Current = 0;
		leaf.SideBySide = area is DockArea.Left or DockArea.Right;
		leaf.Share = 0.5f;
		leaf.First = first ? made : kept;
		leaf.Second = first ? kept : made;

		Settle( dock );
	}

	//Adds a dock to a group, at a place in its tabs or at the end.
	private void Join( DockWidget dock, Side leaf, int at )
	{
		leaf.Docks.Insert( at < 0 || at > leaf.Docks.Count ? leaf.Docks.Count : at, dock );

		//Current counts the open ones, which is not the same list as the docks in the group: one that is closed or out in a window of its own is still in it.
		leaf.Current = Math.Max( 0, leaf.Open.ToList().IndexOf( dock ) );

		Settle( dock );
	}

	//A dock that has just been put somewhere is in the window again.
	private void Settle( DockWidget dock )
	{
		dock.Floating = false;
		dock.SetPanelParent( this );

		MarkNeedsArrange();
		Update();
	}

	private protected override void OnArrange()
	{
		base.OnArrange();

		if ( !OnPanels ) return;

		spare.Clear();
		spare.AddRange( handles.Keys );

		Arrange( Root, LocalRect );

		//A split that has gone since the last time still has its handle.
		foreach ( var gone in spare )
		{
			if ( handles.Remove( gone, out var handle ) ) handle?.Destroy();
		}

		SyncTabStrips();
	}

	//The splits that had a handle last time, so the dead ones can go.
	private readonly List<Side> spare = new();

	//One interactive strip per tab group.
	private readonly List<TabStrip> tabStrips = new();

	/// <summary>Keeps the tab strips in step with the manager whose tabs they are.</summary>
	[EditorEvent.Frame]
	public void FollowVisibility()
	{
		if ( !OnPanels || tabStrips.Count == 0 ) return;

		var showing = Visible;

		foreach ( var strip in tabStrips )
		{
			if ( strip.IsValid() ) strip.Visible = showing;
		}
	}

	//Lays one part of the layout into its rectangle, and its halves into theirs. A half with nothing in it takes no room and the other
	//one gets all of it, which is what keeps an edge with every panel closed from leaving a band of nothing down the side of the window.
	private void Arrange( Side node, Rect rect )
	{
		node.Rect = rect;

		if ( !node.IsSplit )
		{
			Place( node, rect );
			return;
		}

		if ( !node.First.Any )
		{
			PlaceHandle( node, default );
			Arrange( node.First, default );
			Arrange( node.Second, rect );
			return;
		}

		if ( !node.Second.Any )
		{
			PlaceHandle( node, default );
			Arrange( node.First, rect );
			Arrange( node.Second, default );
			return;
		}

		var share = Math.Clamp( node.Share, 0.05f, 0.95f );

		if ( node.SideBySide )
		{
			var width = rect.Width * share;

			Arrange( node.First, new Rect( rect.Left, rect.Top, width, rect.Height ) );
			Arrange( node.Second, new Rect( rect.Left + width + Gap, rect.Top, rect.Width - width - Gap, rect.Height ) );

			PlaceHandle( node, new Rect( rect.Left + width + ( Gap * 0.5f ) - ( Grab * 0.5f ), rect.Top, Grab, rect.Height ) );

			return;
		}

		var height = rect.Height * share;

		Arrange( node.First, new Rect( rect.Left, rect.Top, rect.Width, height ) );
		Arrange( node.Second, new Rect( rect.Left, rect.Top + height + Gap, rect.Width, rect.Height - height - Gap ) );

		PlaceHandle( node, new Rect( rect.Left, rect.Top + height + ( Gap * 0.5f ) - ( Grab * 0.5f ), rect.Width, Grab ) );
	}

	//How wide the strip you drag to move an edge is. Wider than the gap it sits over. The gap is four pixels of nothing, and four pixels
	//is not something anybody can be asked to hit - which is why the edges read as immovable even though they always could be moved.
	private const float Grab = 10.0f;

	private readonly Dictionary<Side, DockHandle> handles = new();

	//Puts the handle for a split over the gap it made, or hides it.
	private void PlaceHandle( Side node, Rect gap )
	{
		spare.Remove( node );

		if ( !handles.TryGetValue( node, out var handle ) || !handle.IsValid() )
		{
			if ( gap.Width <= 0.0f && gap.Height <= 0.0f ) return;

			handle = new DockHandle( this, node );
			handles[node] = handle;
		}

		var wanted = gap.Width > 0.0f && gap.Height > 0.0f;

		handle.Visible = wanted;

		if ( !wanted ) return;

		handle.Position = gap.Position;
		handle.Size = gap.Size;
	}

	//A split is being moved, from the handle over its gap.
	internal void BeginEdgeDrag( Side node )
	{
		dragging = node;

		//Followed on the frames it asks for, for as long as the button is held.
		RequestFrame();
	}

	//Moves a split to wherever the pointer is.
	private void DragEdge()
	{
		var node = dragging;

		if ( node is null || !node.IsSplit ) return;

		var local = Application.CursorPosition - ScreenPosition;
		var rect = node.Rect;

		var share = node.SideBySide
			? ( local.x - rect.Left ) / MathF.Max( rect.Width, 1 )
			: ( local.y - rect.Top ) / MathF.Max( rect.Height, 1 );

		var wanted = Math.Clamp( share, 0.05f, 0.95f );

		if ( MathF.Abs( wanted - node.Share ) < 0.0005f ) return;

		node.Share = wanted;

		MarkNeedsArrange();
		Update();
	}

	//Lays the docks of one group into its rectangle: the one on top fills it, the rest are only names on the strip above it.
	private static void Place( Side side, Rect rect )
	{
		var open = side.Open.ToList();

		if ( open.Count == 0 ) return;

		side.Current = Math.Clamp( side.Current, 0, open.Count - 1 );

		var inside = rect;

		//A dock wears its name, alone or not: the strip is what says which panel this is, and what a dock
		//is picked up and closed by. The ones that asked for no tab get the whole rectangle instead.
		if ( side.AnyTabbed ) inside.Top += TabHeight;

		for ( var i = 0; i < open.Count; i++ )
		{
			var dock = open[i];
			var showing = i == side.Current;

			dock.Position = inside.Position;
			dock.Size = inside.Size;
			dock.Visible = showing;
		}
	}

	//Where each group's tab strip is, for drawing and for clicks.
	private Rect StripOf( Side side )
	{
		var open = side.Open.ToList();

		if ( open.Count == 0 || !side.AnyTabbed ) return default;

		var first = open[0];

		return new Rect( first.Position.x, first.Position.y - TabHeight, first.Size.x, TabHeight );
	}

	//Tab strips live on the overlay so they paint and receive clicks above the docks.
	private void SyncTabStrips()
	{
		if ( !OnPanels )
		{
			foreach ( var strip in tabStrips )
				strip.Destroy();

			tabStrips.Clear();
			return;
		}

		var layer = EditorOverlay.For( Surface );

		if ( layer is null ) return;

		var sides = Leaves().ToList();

		for ( var i = tabStrips.Count - 1; i >= 0; i-- )
		{
			if ( sides.Contains( tabStrips[i].Side ) ) continue;

			tabStrips[i].Destroy();
			tabStrips.RemoveAt( i );
		}

		var at = ScreenPosition;

		foreach ( var side in sides )
		{
			var rect = StripOf( side );
			var strip = tabStrips.FirstOrDefault( x => x.Side == side );

			//Nothing open to name - every panel in the group closed or floating - is no strip. One left standing keeps where it last was,
			//over whatever is there now: made before the group was placed, that is the top left of the window, over the menus
			if ( rect.Width <= 0 )
			{
				if ( strip is not null )
				{
					strip.Destroy();
					tabStrips.Remove( strip );
				}

				continue;
			}

			if ( !strip.IsValid() )
			{
				strip = new TabStrip( this, side );
				tabStrips.Add( strip );
				layer.AddChild( strip.Surface );
			}

			strip.Position = at + rect.Position;
			strip.Size = rect.Size;
			strip.Visible = Visible;
			strip.Update();
		}
	}

	//Draws one group's tab strip in strip-local coordinates.
	internal void PaintTabs( Side side )
	{
		if ( !OnPanels ) return;

		var open = side.Open.ToList();

		if ( open.Count == 0 || !side.AnyTabbed ) return;

		Paint.SetDefaultFont();
		Paint.Antialiasing = true;

		var width = open[0].Size.x;
		var strip = new Rect( 0, 0, width, TabHeight );

		if ( Theme.TabBarBackground.a > 0.0f )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.TabBarBackground );
			Paint.DrawRect( strip );
		}

		var at = strip.Left;
		Rect? openTab = null;

		for ( var i = 0; i < open.Count; i++ )
		{
			var dock = open[i];

			if ( dock.NoTab ) continue;

			var tabWidth = TabWidth( dock );
			var tab = new Rect( at, strip.Top, tabWidth, strip.Height );

			var showing = i == side.Current;

			if ( showing )
			{
				var round = Theme.TabRadius;

				//The open tab is the panel's grey joined to the panel, and that is all: no line of the accent along its top, the way nothing that is on in this
				//editor wears a bar of colour. Edged by the same hairline the cards under it wear, and then opened along the bottom again, where it joins its panel.
				Paint.SetPen( Theme.Border, 1.0f );
				Paint.SetBrush( Theme.TabBackground );
				Paint.DrawRect( tab.Shrink( 0.5f, 0.5f, 0.5f, -1.0f ), round, round, 0.0f, 0.0f );

				Paint.ClearPen();
				Paint.DrawRect( new Rect( tab.Left + 1.0f, tab.Bottom - 1.0f, tab.Width - 2.0f, 2.0f ) );

				openTab = tab;
			}

			Paint.SetPen( showing ? Theme.Ink : Theme.InkDim );

			var room = IconRoom( dock );
			var words = new Rect( tab.Left + room, tab.Top, tab.Width - CloseSize - 6.0f - room, tab.Height );

			Paint.DrawText( words, dock.Title, TextFlag.Center | TextFlag.SingleLine );

			if ( room > 0.0f )
			{
				//Sits just ahead of the centred name, so the pair reads as one label.
				var nameWidth = EditorText.Measure( dock.Title ).x;
				var left = words.Center.x - ( nameWidth * 0.5f ) - room;
				var glyph = new Rect( left, tab.Top, TabIconSize, tab.Height );

				Paint.SetPen( showing ? Theme.Ink : Theme.InkDim );
				Paint.DrawIcon( glyph, dock.Icon, TabIconSize, TextFlag.Center );
			}

			Paint.SetPen( showing ? Theme.InkDim : Theme.InkOff );
			Paint.DrawIcon( CloseOf( tab ), "close", 12.0f );

			at += tabWidth;
		}

		//The hairline the open tab stands on, either side of it: the top edge of the bar under the strip, which the tab opens into.
		var baseline = strip.Bottom - 0.5f;
		Paint.SetPen( Theme.Border, 1.0f );

		if ( openTab is { } t )
		{
			if ( t.Left > strip.Left ) Paint.DrawLine( new Vector2( strip.Left, baseline ), new Vector2( t.Left + 0.5f, baseline ) );
			Paint.DrawLine( new Vector2( t.Right - 0.5f, baseline ), new Vector2( strip.Right, baseline ) );
		}
		else
		{
			Paint.DrawLine( new Vector2( strip.Left, baseline ), new Vector2( strip.Right, baseline ) );
		}
	}

	//A tab strip on the window overlay: painted here and clicked here.
	private sealed class TabStrip : Widget
	{
		private readonly DockManager docks;
		internal Side Side { get; }

		public TabStrip( DockManager docks, Side side ) : base( null )
		{
			this.docks = docks;
			Side = side;
			Name = "TabStrip";

			MakeSurface( null );
			Surface.Style.PointerEvents = Rinten.UI.PointerEvents.All;
		}

		public override bool UsesCanvasDraw => true;

		protected override void OnPaint() => docks.PaintTabs( Side );

		protected override void OnMousePress( MouseEvent e )
		{
			docks.HandleTabPress( Side, e.LocalPosition );
			e.Accepted = true;
		}
	}

	//Tab click, close, and the start of a drag.
	internal void HandleTabPress( Side side, Vector2 stripLocal )
	{
		var open = side.Open.ToList();
		var at = 0.0f;

		for ( var i = 0; i < open.Count; i++ )
		{
			var dock = open[i];

			if ( dock.NoTab ) continue;

			var width = TabWidth( dock );
			var tab = new Rect( at, 0, width, TabHeight );

			if ( !tab.IsInside( stripLocal ) )
			{
				at += width;
				continue;
			}

			if ( CloseOf( tab ).IsInside( stripLocal ) )
			{
				dock.ToggleViewManaged( false );

				MarkNeedsArrange();
				Update();
				return;
			}

			side.Current = i;

			pressed = dock;
			pressedAt = StripOf( side ).Position + stripLocal;

			//Watched for becoming a carry on the frames it asks for.
			RequestFrame();

			MarkNeedsArrange();
			Update();
			return;
		}
	}

	public override void Update()
	{
		base.Update();
		SyncTabStrips();
	}

	public override void OnDestroyed()
	{
		foreach ( var strip in tabStrips )
			strip.Destroy();

		tabStrips.Clear();

		base.OnDestroyed();
	}

	//What is being carried and where it would go, drawn on the layer above the window. The coordinates are this widget's own, because the layer is put
	//exactly over it - which is what makes every rect worked out here (the zones, the compass, the tab under the pointer) mean the same thing on both.
	internal void PaintCarry()
	{
		if ( carrying.IsValid() )
		{
			//The room it would take, where the pointer is on one of the squares.
			if ( landing != DockArea.Hidden )
			{
				var zone = ZoneRect( landing, landingOuter );

				Paint.Antialiasing = true;

				Paint.ClearPen();
				Paint.SetBrush( Theme.Good.WithAlpha( 0.18f ) );
				Paint.DrawRect( zone, Theme.ControlRadius );

				Paint.ClearBrush();
				Paint.SetPen( Theme.Good, 1.5f );
				Paint.DrawRect( zone, Theme.ControlRadius );
			}

			//The zone says which group takes it; this says which of its tabs it becomes, which is the whole of putting a group's panels in an order.
			if ( landing == DockArea.Center && !landingOuter && over is not null )
			{
				var caret = CaretOf( over, landingSlot );

				if ( caret.Width > 0.0f && caret.Height > 0.0f )
				{
					Paint.ClearPen();
					Paint.SetBrush( Theme.Good );
					Paint.DrawRect( caret, CaretWidth * 0.5f );
				}
			}

			//The compass itself: five squares in the middle of the group under the pointer,
			//saying what would happen to that group, with the one under the pointer lit.
			Paint.Antialiasing = true;

			if ( over is not null )
			{
				foreach ( var area in Compass )
				{
					PaintTarget( TargetOf( area ), area, !landingOuter && area == landing );
				}
			}

			//And four against the window, for putting it down the side of everything rather than of one group.
			foreach ( var area in Edges )
			{
				PaintTarget( OuterOf( area ), area, landingOuter && area == landing );
			}

			//A window carried in is its own picture of what is moving
			if ( arriving is not null ) return;

			//The tab itself, under the pointer: what is being moved has to be visible while it is
			//in the air, or the highlight is an answer to a question nobody can see being asked.
			var at = Application.CursorPosition - ScreenPosition;
			var width = TabWidth( carrying );

			var held = new Rect( at.x - ( width * 0.5f ), at.y - ( TabHeight * 0.5f ), width, TabHeight );

			Paint.ClearPen();
			Paint.SetBrush( Theme.TabBackground.WithAlpha( 0.9f ) );
			Paint.DrawRect( held, 2.0f );

			Paint.ClearBrush();
			Paint.SetPen( Theme.EdgeLit );
			Paint.DrawRect( held, 2.0f );

			Paint.SetDefaultFont();
			Paint.SetPen( Theme.Text );
			Paint.DrawText( held, carrying.Title, TextFlag.Center | TextFlag.SingleLine );
		}
	}

	//One square of a compass: a plate, an edge and the arrow on it.
	private static void PaintTarget( Rect target, DockArea area, bool lit )
	{
		if ( target.Width <= 0.0f ) return;

		Paint.ClearPen();
		Paint.SetBrush( lit ? Theme.Good : Theme.GlassDeep );
		Paint.DrawRect( target, 6.0f );

		Paint.ClearBrush();
		Paint.SetPen( lit ? Theme.Good : Theme.Edge, 1.0f );
		Paint.DrawRect( target, 6.0f );

		Paint.SetPen( lit ? Theme.Ink : Theme.InkFaint );
		Paint.DrawIcon( target, TargetIcon( area ), 20.0f, TextFlag.Center );
	}

	//A widget the size of the dock area, sat on the window's overlay so that what it draws is over the docks rather than under them.
	private sealed class CarryLayer : Widget
	{
		private readonly DockManager docks;

		public CarryLayer( DockManager docks ) : base( null )
		{
			this.docks = docks;

			//It is a picture of what is going on, not something to click on.
			Surface.Style.PointerEvents = Rinten.UI.PointerEvents.None;
		}

		protected override void OnPaint() => docks.PaintCarry();
	}

	private CarryLayer carryLayer;

	//Puts the layer over this widget, or takes it away. Called as a carry starts and ends, and again whenever what it draws has changed.
	private void ShowCarryLayer( bool carry )
	{
		if ( !OnPanels ) return;

		if ( !carry )
		{
			carryLayer?.Destroy();
			carryLayer = null;
			return;
		}

		var layer = EditorOverlay.For( Surface );

		if ( layer is null ) return;

		if ( !carryLayer.IsValid() )
		{
			carryLayer = new CarryLayer( this );
			layer.AddChild( carryLayer.Surface );
		}

		//Place the layer in window-root space (as CursorPosition is), or the compass is offset by the caption
		carryLayer.Position = ScreenPosition;
		carryLayer.Size = Size;
		carryLayer.Update();
	}

	protected override void OnMousePress( MouseEvent e )
	{
		if ( !OnPanels ) { base.OnMousePress( e ); return; }

		//Tabs are on TabStrip widgets of their own - see SyncTabStrips.
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );

		if ( !OnPanels ) return;

		var was = hovered;

		hovered = TabAt( e.LocalPosition );

		if ( hovered != was ) Update();
	}

	protected override void OnMouseLeave()
	{
		base.OnMouseLeave();

		if ( !hovered.IsValid() ) return;

		hovered = null;

		Update();
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		if ( !OnPanels ) { base.OnMouseReleased( e ); return; }

		dragging = null;

		//Let go of a carried dock wherever the pointer is. Dropped on the edge it came from, this
		//puts it back where it was, which is what somebody who changed their mind halfway expects.
		Drop( e.LocalPosition );
	}

	//Dock carrying polls the pointer per frame, since events go to whatever dock is under it
	private protected override void OnWidgetFrame()
	{
		base.OnWidgetFrame();

		if ( !OnPanels ) return;

		//Rescue docks from floating windows closed outside FloatingDock (window system, crashes)
		SweepFloating();

		if ( FollowArriving() ) return;

		//A drag in progress - an edge, or a tab pressed or carried - is followed on the frames it asks for, and it keeps asking until the button goes up.
		if ( dragging is not null || pressed.IsValid() || carrying.IsValid() ) RequestFrame();

		//An edge being moved is followed the same way, and for the same reason: the pointer walks off the handle the moment the edge starts moving.
		if ( dragging is not null )
		{
			if ( !Application.MouseButtons.HasFlag( MouseButtons.Left ) )
			{
				dragging = null;
				return;
			}

			DragEdge();
			return;
		}

		if ( !pressed.IsValid() && !carrying.IsValid() ) return;

		var local = Application.CursorPosition - ScreenPosition;

		if ( !Application.MouseButtons.HasFlag( MouseButtons.Left ) )
		{
			Drop( local );
			return;
		}

		if ( pressed.IsValid() && local.Distance( pressedAt ) > DragStarts )
		{
			carrying = pressed;
			pressed = null;

			ShowCarryLayer( true );
		}

		if ( !carrying.IsValid() ) return;

		Cursor = CursorShape.SizeAll;

		//The tab is drawn under the pointer, so the layer is repainted as the pointer moves and not only when the answer changes.
		ShowCarryLayer( true );

		//The compass goes to the question rather than the question to it: it sits in the middle
		//of whichever group the pointer is over, so what it offers is what happens to that group.
		var was = over;

		var wanted = AimAt( local, out var outer );

		if ( wanted == landing && outer == landingOuter && was == over ) return;

		landing = wanted;
		landingOuter = outer;

		Update();
	}

	//Puts down whatever was being carried, wherever the pointer is.
	private void Drop( Vector2 local )
	{
		if ( carrying.IsValid() )
		{
			//Onto whatever was lit, rather than working the answer out a second time: the two are the same, and only one of them is what somebody was looking
			//at when they let go. Nothing lit means the pointer was over no group at all, which is the gap between two of them or outside the window entirely.
			if ( landing != DockArea.Hidden ) Land( carrying );

			//Carried right out of the window: it becomes a window of its own.
			else if ( !LocalRect.IsInside( local ) ) FloatManaged( carrying );

			Cursor = CursorShape.None;
			Update();
		}

		pressed = null;
		EndCarry();
	}

	//Puts a dock where the lit target says
	private void Land( DockWidget dock )
	{
		if ( landingOuter || over is null ) PlaceManaged( dock, landing );
		else if ( landing == DockArea.Center ) PlaceManaged( dock, over, landingSlot );
		else SplitManaged( dock, over, landing );
	}

	private void EndCarry()
	{
		carrying = null;
		arriving = null;
		landing = DockArea.Hidden;
		landingOuter = false;
		landingSlot = -1;
		over = null;

		ShowCarryLayer( false );
	}

	//A floating window of this layout carried by its caption: the targets show while it is over the window this layout is in.
	//Its position is what the drag reports, the pointer being the drag's rather than any window's while it lasts
	private bool FollowArriving()
	{
		var carried = floating.Values.FirstOrDefault( x => x.IsValid() && x.IsCarried );

		if ( carried is null )
		{
			if ( arriving is null ) return false;

			EndCarry();
			Update();
			return false;
		}

		arriving = carried;
		carrying = carried.Dock;

		RequestFrame();

		if ( CoreUiWindow.Holding( Surface ) is not { } mine || CoreUiWindow.Of( CoreWindow.CarriedOver ) != mine )
		{
			landing = DockArea.Hidden;
			over = null;
			ShowCarryLayer( false );
			return true;
		}

		landing = AimAt( CoreWindow.CarriedAt - ScreenPosition, out landingOuter );

		ShowCarryLayer( true );
		return true;
	}

	//A carried floating window was put down: on a lit target of this layout's window, it docks there; anywhere else it stays where it was put
	private void Landed( FloatingDock window, CoreUiWindow on, Vector2 at )
	{
		var dock = window.Dock;

		if ( on is not null && on == CoreUiWindow.Holding( Surface ) && dock.IsValid() )
		{
			landing = AimAt( at - ScreenPosition, out landingOuter );

			if ( landing != DockArea.Hidden )
			{
				window.DockBack();
				Land( dock );
			}
		}

		EndCarry();
		Update();
	}

	//Docks in windows of their own. Dragged out of the window, they stay on the list of the edge they came from - that
	//is where they go back to when the window is closed, and it is the answer nobody wants to be asked for twice.

	private readonly Dictionary<DockWidget, FloatingDock> floating = new();

	//How small a dock in a window of its own is allowed to be.
	private static readonly Vector2 SmallestFloat = new( 420, 320 );

	//Tears a dock off into a window of its own.
	internal void FloatManaged( DockWidget dock )
	{
		if ( !dock.IsValid() ) return;
		if ( dock.Floating ) return;

		dock.Floating = true;

		var window = new FloatingDock( this, dock )
		{
			Title = dock.Title,
			DeleteOnClose = true,
			StartCentered = false,
			Size = new Vector2(
				MathF.Max( dock.Size.x, SmallestFloat.x ),
				MathF.Max( dock.Size.y, SmallestFloat.y ) ),
		};

		window.Canvas = dock;

		floating[dock] = window;

		window.Show();

		//Pulled by its caption it is carried, and put down on this layout it docks
		if ( window.Shell is { } shell ) shell.CarriesItself = true;

		MarkNeedsArrange();
		Update();
	}

	//Puts a floating dock back on the edge it came from.
	internal void ReturnManaged( DockWidget dock )
	{
		if ( dock is null ) return;

		floating.Remove( dock );

		if ( !dock.IsValid() ) return;

		dock.Floating = false;
		dock.SetPanelParent( this );

		MarkNeedsArrange();
		Update();
	}

	//Whether this dock is out in a window of its own.
	internal bool IsFloatingManaged( DockWidget dock ) => dock is not null && dock.Floating;

	//Brings back any dock whose window is no longer there.
	private void SweepFloating()
	{
		if ( floating.Count == 0 ) return;

		List<DockWidget> lost = null;

		foreach ( var (dock, window) in floating )
		{
			if ( window.IsValid() ) continue;

			lost ??= new List<DockWidget>();
			lost.Add( dock );
		}

		if ( lost is null ) return;

		foreach ( var dock in lost ) ReturnManaged( dock );
	}

	//Closes the window a floating dock is in, which puts the dock away. False where it is not floating
	internal bool CloseFloating( DockWidget dock )
	{
		if ( !floating.TryGetValue( dock, out var window ) || !window.IsValid() ) return false;

		window.Close();
		return true;
	}

	//A window holding one dock of this layout: no tab, only the panel. Pulled by its caption it is carried, and put down on the layout
	//it docks there - see Landed. Its close puts the panel away, as a tab's cross does
	private sealed class FloatingDock : Window
	{
		private static readonly List<FloatingDock> living = new();

		private readonly DockManager docks;
		private DockWidget dock;
		private bool goingBack;

		static FloatingDock()
		{
			CoreWindow.CarryStarted += Lifted;
			CoreWindow.CarryEnded += Put;
		}

		//Its layout follows the carry from here, frame by frame
		private static void Lifted( CoreWindow carried )
		{
			var shell = CoreUiWindow.Of( carried );

			if ( living.FirstOrDefault( x => x.IsValid() && x.Shell == shell ) is { } window )
				window.docks?.RequestFrame();
		}

		public FloatingDock( DockManager docks, DockWidget dock )
		{
			this.docks = docks;
			this.dock = dock;

			living.Add( this );
		}

		internal DockWidget Dock => dock;

		//Whether this is the window being carried
		internal bool IsCarried => Shell is { } shell && CoreUiWindow.Of( CoreWindow.Carried ) == shell;

		private static void Put( CoreWindow carried, CoreWindow on, Vector2 at )
		{
			var shell = CoreUiWindow.Of( carried );

			if ( living.FirstOrDefault( x => x.IsValid() && x.Shell == shell ) is { } window )
				window.docks?.Landed( window, CoreUiWindow.Of( on ), at );
		}

		//Back into the layout, open; the caller places it
		internal void DockBack()
		{
			goingBack = true;
			Close();
		}

		//The cross asks the panel first, as a tab's does - see DockWidget.CanClose
		protected override bool OnClose() => goingBack || dock is null || dock.MayPutAway();

		protected override void OnClosed()
		{
			var closing = !goingBack ? dock : null;

			Return();

			if ( closing.IsValid() ) closing.ToggleViewManaged( false );

			base.OnClosed();
		}

		public override void OnDestroyed()
		{
			living.Remove( this );
			Return();

			base.OnDestroyed();
		}

		private void Return()
		{
			if ( dock is null ) return;

			var going = dock;
			dock = null;

			//Let go of it before the window goes, or it is destroyed along with the window it was only visiting.
			Canvas = null;

			docks?.ReturnManaged( going );
		}
	}

	//Splitter handle over the gap between edges; a real widget so it is hittable and shows a cursor
	private sealed class DockHandle : Widget
	{
		private readonly DockManager docks;
		private readonly Side node;

		public DockHandle( DockManager docks, Side node ) : base( docks )
		{
			this.docks = docks;
			this.node = node;

			Cursor = node.SideBySide ? CursorShape.SizeH : CursorShape.SizeV;

			//Over the docks either side of it, which are its own siblings and were put there first.
			if ( Surface is not null ) Surface.Style.ZIndex = 5;
		}

		protected override void OnPaint()
		{
			//The gap itself, in the theme's separator colour, and the hover colour under the pointer. The handle is
			//wider than the gap so it can be grabbed, but only the gap is painted: the rest lies over the docks.
			var gap = Gap;

			var strip = node.SideBySide
				? new Rect( ( Size.x - gap ) * 0.5f, 0.0f, gap, Size.y )
				: new Rect( 0.0f, ( Size.y - gap ) * 0.5f, Size.x, gap );

			Paint.ClearPen();
			Paint.SetBrush( Paint.HasMouseOver ? Theme.SeparatorHoverColor : Theme.SeparatorColor );
			Paint.DrawRect( strip );
		}

		protected override void OnMousePress( MouseEvent e )
		{
			if ( !e.LeftMouseButton ) return;

			docks.BeginEdgeDrag( node );
		}

		protected override void OnMouseEnter()
		{
			base.OnMouseEnter();

			Update();
		}

		protected override void OnMouseLeave()
		{
			base.OnMouseLeave();

			Update();
		}
	}

	//The first word of a saved layout of ours. A layout saved by dock manager is a base64 blob, and
	//reading one as if it were ours restores an empty window; the word is how the two are told apart.
	private const string StateFormat = "rinten2";

	//What layouts said before the layout was a tree: five edges and their shares.
	private const string FlatFormat = "rinten1";

	//The whole tree, depth first: a split, then its two halves.
	internal string SaveStateManaged()
	{
		var parts = new List<string> { StateFormat };

		Write( Root, parts );

		return string.Join( ';', parts );
	}

	private static void Write( Side node, List<string> into )
	{
		if ( node.IsSplit )
		{
			into.Add( $"s:{( node.SideBySide ? "h" : "v" )}:{node.Share.ToString( CultureInfo.InvariantCulture )}" );

			Write( node.First, into );
			Write( node.Second, into );

			return;
		}

		//Only what is actually showing. A dock the user closed is still in its group - that is where
		//it goes when it is opened again - and writing it down would open it on the next start.
		var open = node.Docks.Where( x => x.IsValid() && !x.IsClosed ).Select( x => x.Title );

		into.Add( $"g:{node.Area}:{node.Current}:{string.Join( ',', open )}" );
	}

	internal bool RestoreStateManaged( string state )
	{
		ForgetMissing();

		if ( string.IsNullOrWhiteSpace( state ) ) return false;

		var parts = state.Split( ';', StringSplitOptions.RemoveEmptyEntries );

		if ( parts.Length == 0 ) return false;
		if ( parts[0] == FlatFormat ) return RestoreFlat( parts );
		if ( parts[0] != StateFormat ) return false;

		var words = new Queue<string>( parts.Skip( 1 ) );
		var placed = false;

		var built = Read( words, ref placed );

		if ( built is null ) return false;

		root = built;

		MarkNeedsArrange();

		return placed;
	}

	//One node and everything under it, off the front of the list.
	private Side Read( Queue<string> words, ref bool placed )
	{
		if ( words.Count == 0 ) return null;

		var bits = words.Dequeue().Split( ':' );

		if ( bits.Length >= 3 && bits[0] == "s" )
		{
			var node = new Side { SideBySide = bits[1] == "h" };

			if ( float.TryParse( bits[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var share ) )
				node.Share = share;

			node.First = Read( words, ref placed );
			node.Second = Read( words, ref placed );

			//A half that could not be read is not a split at all.
			if ( node.First is null || node.Second is null ) return node.First ?? node.Second;

			return node;
		}

		if ( bits.Length < 4 || bits[0] != "g" ) return null;

		var area = Enum.TryParse<DockArea>( bits[1], out var named ) ? named : DockArea.Hidden;

		var leaf = new Side { Area = area, Made = area == DockArea.Hidden };

		if ( int.TryParse( bits[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var current ) )
			leaf.Current = current;

		foreach ( var name in bits[3].Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
		{
			//A dock is made the first time it is wanted, and a saved layout is exactly that - looking
			//for one that has never been opened finds nothing and the layout comes back empty.
			SetDockState( name, true );

			if ( FindDockWidget( name ) is not { } dock ) continue;

			leaf.Docks.Add( dock );

			dock.Floating = false;
			dock.SetPanelParent( this );

			placed = true;
		}

		return leaf;
	}

	//A layout from before the tree: the five edges, and their shares.
	private bool RestoreFlat( string[] parts )
	{
		root = DefaultLayout();

		var placed = false;

		foreach ( var part in parts )
		{
			var bits = part.Split( ':' );

			if ( bits.Length < 3 ) continue;
			if ( !Enum.TryParse<DockArea>( bits[0], out var area ) ) continue;

			if ( float.TryParse( bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var share ) )
				Shared( area, share );

			foreach ( var name in bits[2].Split( ',', StringSplitOptions.RemoveEmptyEntries ) )
			{
				SetDockState( name, true );

				if ( FindDockWidget( name ) is not { } dock ) continue;

				PlaceManaged( dock, area );

				placed = true;
			}
		}

		MarkNeedsArrange();

		return placed;
	}

	//How much of the window an edge took, as the split that holds it.
	private void Shared( DockArea area, float share )
	{
		//The middle took whatever was left, and said so as a share of one - which as the share of a split would leave the bottom edge with nothing.
		if ( area is DockArea.Center or DockArea.Hidden ) return;

		var split = ParentOf( LeafFor( area ) );

		if ( split is null ) return;

		split.Share = split.First?.Area == area ? share : 1.0f - share;
	}
}

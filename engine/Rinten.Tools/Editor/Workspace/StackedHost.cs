using Rinten.Engine;

namespace Editor;

//Widgets stacked over each other with one shown, for the workspaces, the scene tabs and the documents. Switching swaps
//visibility and lays nothing out again; the outgoing one stays on top until the frame ends, when the incoming view has presented
internal class StackedHost : Widget
{
	Widget _current;

	/// <summary>What is painted where nothing covers the host; nothing, so the window shows through.</summary>
	public Color? Background { get; set; }

	public StackedHost( Widget parent ) : base( parent )
	{
	}

	/// <summary>The widget being shown.</summary>
	public Widget Shown => _current;

	/// <summary>Brings one stacked widget up and puts the rest away.</summary>
	public void Show( Widget widget )
	{
		if ( _current == widget ) return;

		var outgoing = _current;
		_current = widget;

		widget.Position = 0;
		widget.Size = Size;
		widget.Visible = true;

		HideTheRest( except: outgoing );

		if ( !outgoing.IsValid() || !outgoing.Visible )
			return;

		widget.Lower();

		EngineLoop.DisposeAtFrameEnd( new Rinten.Utility.DisposeAction( () =>
		{
			if ( outgoing.IsValid() && outgoing != _current )
				outgoing.Visible = false;

			if ( _current.IsValid() )
				_current.Raise();
		} ) );
	}

	/// <summary>Shows nothing.</summary>
	public void Clear()
	{
		_current = null;
		HideTheRest();
		Update();
	}

	//Hides everything but the one shown and the outgoing one: a covered view still renders at full cost.
	//The list itself rather than Children, which copies - the scene tabs sweep this every frame
	internal void HideTheRest( Widget except = null )
	{
		for ( var i = childWidgets.Count - 1; i >= 0; i-- )
		{
			var child = childWidgets[i];

			if ( child == _current || child == except ) continue;
			if ( !child.IsValid() || !child.Visible ) continue;

			child.Visible = false;
		}
	}

	protected override void OnPaint()
	{
		if ( Background is not { } background ) return;

		Paint.ClearPen();
		Paint.SetBrush( background );
		Paint.DrawRect( LocalRect );
	}

	protected override void OnResize()
	{
		foreach ( var child in Children )
		{
			if ( !child.Visible ) continue;

			child.Position = 0;
			child.Size = Size;
		}
	}
}

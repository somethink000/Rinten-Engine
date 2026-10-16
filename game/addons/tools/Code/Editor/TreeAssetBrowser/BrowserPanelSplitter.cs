using Rinten.UI;
using System;

namespace Editor.TreeBrowser;

/// <summary>Draggable divider between two browser panels.</summary>
[StyleSheet( "BrowserPanelSplitter.cs.scss" )]
public sealed class BrowserPanelSplitter : Panel
{
	public const float Thickness = 6f;
	const float MinPanelWidth = 150f;

	Panel _left;
	Panel _right;
	float _dragStartScreenX;
	float _leftStartWidth;

	public void Init( Panel left, Panel right )
	{
		_left = left;
		_right = right;
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		if ( e.MouseButton != MouseButtons.Left ) return;

		SetClass( "dragging", true );
		_dragStartScreenX = e.PointerPosition.x;
		_leftStartWidth = _left.Box.Rect.Width;
		e.StopPropagation();
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		if ( !HasClass( "dragging" ) ) return;

		SetClass( "dragging", false );
	}

	protected override void OnMouseMove( MousePanelEvent e )
	{
		if ( !HasClass( "dragging" ) ) return;

		var delta = e.PointerPosition.x - _dragStartScreenX;
		var minDelta = MinPanelWidth - _leftStartWidth;
		var maxDelta = (_right?.Box.Rect.Width ?? 0) - MinPanelWidth;
		delta = Math.Clamp( delta, minDelta, maxDelta );

		_left.Style.Width = Length.Pixels( _leftStartWidth + delta );
		_left.SetClass( "fixed", true );
	}
}

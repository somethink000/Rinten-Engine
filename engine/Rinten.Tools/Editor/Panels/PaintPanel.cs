using Rinten;
using Rinten.UI;

namespace Editor;

/// <summary>Panel drawn with the Paint API, repainted only on Repaint or resize</summary>
public abstract class PaintPanel : EditorPanel
{
	private readonly PanelBake bake = new();

	private bool asked = true;
	private bool paintedOnce;

	protected PaintPanel()
	{
		AddClass( "paint-panel" );
	}

	//Draw the panel. The rectangle starts at the origin and is its size.
	protected abstract void OnPaintContent( Rect rect );

	//Whether there is anything to draw at all. A panel with nothing to say takes its picture off rather than wearing an empty one.
	protected virtual bool HasPicture => true;

	/// <summary>Ask for the picture again, next time this panel is drawn.</summary>
	public void Repaint() => asked = true;

	public override void OnDeleted()
	{
		base.OnDeleted();

		bake.Dispose();
	}

	public override void Tick()
	{
		base.Tick();

		if ( !HasPicture )
		{
			bake.Clear( this );
			paintedOnce = false;
			return;
		}

		//The size the painter is given is the panel's own, in the pixels it is laid out in - not the device's.
		//A painter writes "a row is twenty-two high" and means the same twenty-two the stylesheet does.
		var size = Box.Rect.Size * ScaleFromScreen;

		var wanted = asked;
		asked = false;

		bake.Paint( this, new Rect( 0, 0, size.x, size.y ), wanted, paintedOnce, PaintInto, ScaleToScreen );

		paintedOnce = true;
	}

	private bool PaintInto( Bitmap bitmap, Rect rect )
	{
		var canvas = new PaintCanvas( bitmap );

		var flags = StateFlag.Enabled;

		if ( HasHovered ) flags |= StateFlag.MouseOver;
		if ( HasFocus ) flags |= StateFlag.HasFocus;

		//Says the paint is going into a picture rather than onto the screen, so anything that asks
		//- a widget drawing a snapshot of itself - answers the same way it does for a widget.
		Paint.Snapshot = true;

		try
		{
			using ( Paint.Start( canvas, flags, ScaleToScreen ) )
			{
				Paint.LocalRect = rect;

				OnPaintContent( rect );
			}
		}
		finally
		{
			Paint.Snapshot = false;
		}

		return canvas.Drew;
	}
}

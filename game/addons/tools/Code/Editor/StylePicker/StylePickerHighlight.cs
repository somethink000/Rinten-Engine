using Rinten.Internal;
using Rinten.UI;

namespace Editor;

//Highlight box while the style picker is active.
internal sealed class StylePickerHighlight
{
	readonly Panel box;
	EditorOverlay layer;

	public StylePickerHighlight()
	{
		box = new Panel();
		box.AddClass( StylePickerTool.FurnitureClass );
		box.AddClass( "style-picker-highlight" );
	}

	public void PointAt( IPanel target, EditorOverlay onto, Vector2 offset = default, float factor = 1.0f )
	{
		if ( onto is null || !target.IsValid() )
		{
			Hide();
			return;
		}

		if ( !ReferenceEquals( layer, onto ) )
		{
			layer = onto;
			layer.AddChild( box );
		}

		//In its own tree's space, mapped into the overlay's by offset and factor
		var rect = target is Panel panel ? panel.GetScreenPixelRect() : target.OuterRect;

		box.Style.Left = offset.x + rect.Left * factor;
		box.Style.Top = offset.y + rect.Top * factor;
		box.Style.Width = rect.Width * factor;
		box.Style.Height = rect.Height * factor;
		box.SetClass( "hidden", false );
	}

	public void Hide() => box.SetClass( "hidden", true );

	public void Destroy() => box?.Delete( true );
}

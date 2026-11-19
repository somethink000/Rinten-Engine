using Rinten.Internal;
using Rinten.UI;

namespace Editor;

//Highlight box with the target file name, drawn on the window overlay like a dock landing zone
internal sealed class SourceEyedropperHighlight
{
	//Room the caption needs above the box before it is put there rather than underneath.
	private const float CaptionHeight = 30.0f;

	readonly Panel box;
	readonly Panel caption;
	//Rinten.UI's, not the editor's widget of the same name: this namespace wins over the using above.
	readonly Rinten.UI.Label captionText;
	EditorOverlay layer;

	public SourceEyedropperHighlight()
	{
		box = new Panel();
		box.AddClass( SourceEyedropperTool.FurnitureClass );
		box.AddClass( "eyedropper-highlight" );

		//A child of the box, so it is placed in the box's own space and there is no second
		//coordinate to keep in step. Dressed as a tooltip, because that is what it is.
		caption = box.AddChild<Panel>();
		caption.AddClass( SourceEyedropperTool.FurnitureClass );
		caption.AddClass( "tooltip" );
		caption.AddClass( "eyedropper-caption" );

		captionText = caption.AddChild<Rinten.UI.Label>();
		captionText.Tokenize = false;
	}

	/// <summary>Moves the box onto a panel, given the layer it should be drawn on.</summary>
	public void PointAt( IPanel target, EditorOverlay onto, Vector2 offset = default, float factor = 1.0f, string label = null, Rect? part = null )
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

		//A part is already in window units; a panel's rect is in its own tree's, mapped by offset and factor
		var rect = part ?? new Rect( offset + target.OuterRect.Position * factor, target.OuterRect.Size * factor );

		box.Style.Left = rect.Left;
		box.Style.Top = rect.Top;
		box.Style.Width = rect.Width;
		box.Style.Height = rect.Height;
		box.SetClass( "hidden", false );

		Say( label, rect.Top );
	}

	//Writes the caption above the box, or under it where the box is against the top of the window.
	private void Say( string label, float top )
	{
		var empty = string.IsNullOrWhiteSpace( label );

		caption.SetClass( "hidden", empty );

		if ( empty ) return;

		captionText.Text = label;
		caption.SetClass( "below", top <= CaptionHeight );
	}

	public void Hide()
	{
		box.SetClass( "hidden", true );
	}

	public void Destroy()
	{
		box?.Delete( true );
	}
}

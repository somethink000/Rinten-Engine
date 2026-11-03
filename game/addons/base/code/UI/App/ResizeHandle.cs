namespace Rinten.UI;

/// <summary>A thin bar that resizes the panel beside it by dragging. The owner keeps the size and lays the panel out with it.</summary>
public class ResizeHandle : Panel
{
	/// <summary>The size now, in pixels of the sheet.</summary>
	public Func<float> GetSize { get; set; }

	/// <summary>Called with the new size while the bar is dragged.</summary>
	public Action<float> SetSize { get; set; }

	public float Min { get; set; } = 120;
	public float Max { get; set; } = 1200;

	/// <summary>Resizes a height instead of a width.</summary>
	public bool Vertical
	{
		get => HasClass( "vertical" );
		set => SetClass( "vertical", value );
	}

	/// <summary>The panel is after the bar (right or below it), so dragging towards it makes it smaller.</summary>
	public bool Reverse { get; set; }

	float startSize;
	Vector2 grabbed;

	public ResizeHandle()
	{
		AddClass( "resize-handle" );
	}

	public override bool WantsDrag => true;

	protected override void OnDragStart( DragEvent e )
	{
		startSize = GetSize?.Invoke() ?? 0;
		grabbed = Mouse.Position;
		AddClass( "dragging" );
	}

	protected override void OnDrag( DragEvent e )
	{
		var moved = ( Mouse.Position - grabbed ) * ScaleFromScreen;
		var by = Vertical ? moved.y : moved.x;
		SetSize?.Invoke( Math.Clamp( startSize + ( Reverse ? -by : by ), Min, Max ) );
	}

	protected override void OnDragEnd( DragEvent e )
	{
		RemoveClass( "dragging" );
	}
}

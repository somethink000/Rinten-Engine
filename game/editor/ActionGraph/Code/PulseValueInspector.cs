using Editor.NodeEditor;
using Rinten.UI.NodeGraph;
using Rinten.UI;

namespace Editor.ActionGraphs;

/// <summary>The value that just went down a wire, shown over whatever the pointer is on.</summary>
public class PulseValueInspector : GraphItem
{
	private readonly ValueChip _chip;

	private object _value;
	private Vector2 _targetPosition;

	/// <summary>What the pointer is over, so the view knows when to move on.</summary>
	public object Target { get; set; }

	//Sized by the value it is showing.
	protected override bool WritesSize => false;

	public object Value
	{
		get => _value;
		set
		{
			if ( Equals( _value, value ) ) return;

			_value = value;

			Show();
		}
	}

	/// <summary>Where in the graph the value passed through.</summary>
	public Vector2 TargetPosition
	{
		get => _targetPosition;
		set
		{
			_targetPosition = value;

			Place();
		}
	}

	public PulseValueInspector( GraphView graph ) : base( graph )
	{
		AddClass( "pulse-value" );

		//It follows the pointer, so anything it took the pointer for would be something the pointer could then no longer be over.

		ZIndex = 100.0f;

		_chip = AddChild<ValueChip>();

		Show();
	}

	public override void Tick()
	{
		base.Tick();

		if ( ReadSizeFromBox() ) Place();
	}

	private void Show()
	{
		var type = Value?.GetType() ?? typeof( object );
		var text = ValueText.FormatValue( type, Value, out var rawValue );

		_chip.Apply( Graph.GetHandleConfig( type ), text, null, rawValue, 1.0f );
	}

	//Sits above the point it belongs to, and centred on it.
	private void Place()
	{
		Position = _targetPosition - new Vector2( Size.x * 0.5f, Size.y + 10.0f );
	}
}

namespace Rinten;

internal sealed partial class Controller
{
	//A list of all of the controllers, active and inactive.
	internal static HashSet<Controller> All { get; private set; } = new();

	//Get the first controller that's connected (shortcut)
	internal static Controller First
	{
		get
		{
			foreach ( var controller in All )
				return controller;

			return null;
		}
	}

	//The input context for this controller.
	internal Input.Context InputContext { get; set; }

	//SDL reports values between this range
	internal static readonly Vector2 AXIS_RANGE = new( -32768, 32767 );

	List<InputAxis> ControllerAxes { get; set; } = new();

	//Triggers always use a fixed deadzone, they're not affected by Preferences.ControllerJoystickDeadzone.
	const float TriggerDeadzone = 0.125f;

	//Get an axis
	internal float GetAxis( NativeEngine.GameControllerAxis axis, float defaultValue = 0f )
	{
		var foundAxis = ControllerAxes.FirstOrDefault( x => x.Axis == axis );
		if ( foundAxis is null )
		{
			return defaultValue;
		}

		return foundAxis.Value;
	}

	internal void SetAxis( NativeEngine.GameControllerAxis axis, int inputValue )
	{
		float flValue = inputValue;
		float normalizedAxis = flValue.Remap( Controller.AXIS_RANGE.x, Controller.AXIS_RANGE.y, -1, 1 );

		var deadzone = axis < NativeEngine.GameControllerAxis.TriggerLeft
			? Preferences.ControllerJoystickDeadzone / 100.0f
			: TriggerDeadzone;

		if ( MathF.Abs( normalizedAxis ) <= deadzone ) normalizedAxis = 0f;

		if ( normalizedAxis > 0f )
		{
			Rinten.Input.UsingController = true;
		}

		var current = ControllerAxes.FirstOrDefault( x => x.Axis == axis );
		if ( current is null )
		{
			current = new InputAxis
			{
				Axis = axis,
			};
			ControllerAxes.Add( current );
		}

		current.Value = normalizedAxis;
	}

	//An axis
	internal record class InputAxis
	{
		internal NativeEngine.GameControllerAxis Axis { get; set; }
		internal float Value { get; set; }
	}

}

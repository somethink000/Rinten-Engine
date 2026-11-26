namespace Rinten.Movement;

partial class MoveMode
{
	/// <summary>Get the position of the player's eye</summary>
	public virtual Transform CalculateEyeTransform()
	{
		var transform = new Transform();
		var duck = Controller.DuckInputFraction;
		var height = Controller.BodyHeight + (Controller.DuckedHeight - Controller.BodyHeight) * duck;

		//A visible first person body crouches its head and chest up into the ducked camera, so lift the camera.
		//8 inches, in metres.
		var duckLift = !Controller.HideBodyInFirstPerson ? 0.2032f * duck : 0f;
		var eyeDist = Controller.EyeDistanceFromTop - duckLift;

		transform.Position = Controller.WorldPosition + Controller.UpDirection * (height - eyeDist);
		transform.Rotation = Controller.EyeAngles.ToRotation();
		return transform;
	}

	/// <summary>Reshape the player's view while this mode is active - swimming tilt, ladder lean, sitting offsets. Runs inside the PlayerController's camera stage, right after it writes the base view, so modes don't need to join the camera's modifier chain themselves. Base does nothing.</summary>
	public virtual void ModifyCamera( ref CameraView view ) { }
}

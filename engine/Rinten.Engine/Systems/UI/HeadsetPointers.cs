namespace Rinten.UI;

//Default VR pointers: each hand ray clicks with trigger and scrolls with stick, unless the game uses
//WorldInput. The ray line is drawn only while pointing at a panel
internal static class HeadsetPointers
{
	private static readonly WorldPanelInput[] hands = [new(), new()];

	//Point both hands at panels, where the scene is in a headset and has no pointer of its own. Otherwise let go of anything they were holding.
	internal static void Tick( Scene scene, List<RootPanel> panels, bool gameHasPointers )
	{
		var active = Game.IsRunningInVR && Input.VR is not null && !gameHasPointers && panels.Count > 0;

		for ( var i = 0; i < 2; i++ )
		{
			var input = hands[i];

			if ( !active )
			{
				if ( input.Hovered is not null || input.Active is not null )
				{
					input.MouseLeftPressed = false;
					input.MouseRightPressed = false;
					input.MouseWheel = default;
					input.Tick( [], true );
				}

				continue;
			}

			var hand = i == 0 ? Input.VR.LeftHand : Input.VR.RightHand;

			if ( hand is null || !hand.Active )
			{
				input.MouseLeftPressed = false;
				input.Tick( [], true );
				continue;
			}

			var aim = hand.AimTransform;
			var ray = new Ray( aim.Position, aim.Rotation.Forward );

			input.Ray = ray;
			input.MouseLeftPressed = hand.Trigger.Value > 0.75f;
			input.MouseRightPressed = false;

			//The screen's wheel is handed over negated - see WorldInput - and a stick pushed up scrolls up.
			input.MouseWheel = -hand.Joystick.Value;

			input.Tick( panels, true );

			Draw( scene, input, ray );
		}
	}

	//The ray as far as the panel it lands on, and a dot there; or a short stub when it lands on none.
	private static void Draw( Scene scene, WorldPanelInput input, Ray ray )
	{
		if ( scene is null ) return;

		var root = input.Hovered?.FindRootPanel();

		if ( root is not null && root.WorldDistance < float.MaxValue )
		{
			var hit = ray.Position + ray.Forward * root.WorldDistance;
			var colour = input.MouseLeftPressed ? new Color( 1.0f, 0.85f, 0.35f ) : new Color( 0.55f, 0.85f, 1.0f );

			Gizmo.Draw.Line( ray.Position, hit, colour.WithAlpha( 0.8f ) );
			Gizmo.Draw.LineSphere( new Sphere( hit, 0.006f ), color: colour, overlay: true );
			return;
		}

		Gizmo.Draw.Line( ray.Position, ray.Position + ray.Forward * 0.25f, new Color( 1.0f, 1.0f, 1.0f, 0.25f ) );
	}
}

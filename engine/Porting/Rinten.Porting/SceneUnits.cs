using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rinten.Porting;

/// <summary>Rewrites the measurements in a scene or a prefab from another engine's units into ours.</summary>
public static class SceneUnits
{
	//Seeded from the components a real scene actually contains. Entries of PortDimension.None are as important as the others: they are what stops a frame count or a fraction being reported over and over as something a person needs to look at.
	/// <summary>What a component's property measures, by <c>Type.Property</c>.</summary>
	public static Dictionary<string, PortDimension> Dimensions { get; } = new( StringComparer.OrdinalIgnoreCase )
	{
		//-- geometry -- Short wildcard list; ambiguous names (Height, Distance) get exact per-type entries
		["*.Center"] = PortDimension.Length,
		["*.Extents"] = PortDimension.Length,
		["*.Mins"] = PortDimension.Length,
		["*.Maxs"] = PortDimension.Length,
		["*.Bounds"] = PortDimension.Length,
		["*.LocalBounds"] = PortDimension.Length,
		["*.Radius"] = PortDimension.Length,

		//-- colliders ------------------------------------------------------- Scale on a collider is
		//the shape's own size rather than a ratio, which is the sort of thing only a table can know.
		["Rinten.BoxCollider.Scale"] = PortDimension.Length,
		["Rinten.PlaneCollider.Scale"] = PortDimension.Length,
		["Rinten.SphereCollider.Radius"] = PortDimension.Length,
		["Rinten.CapsuleCollider.Start"] = PortDimension.Length,
		["Rinten.CapsuleCollider.End"] = PortDimension.Length,
		["Rinten.CapsuleCollider.Radius"] = PortDimension.Length,

		//A conveyor drags what stands on it, so this is a speed.
		["*.SurfaceVelocity"] = PortDimension.Speed,

		//Velocity and Position are safe wildcards (never counts); also parts of small game structs
		["*.Pos"] = PortDimension.Length,
		["*.Position"] = PortDimension.Length,
		["*.Angle"] = PortDimension.None,
		["*.Angles"] = PortDimension.None,
		["*.Rotation"] = PortDimension.None,

		["*.Velocity"] = PortDimension.Speed,
		["*.AngularVelocity"] = PortDimension.None,
		["*.WorldPosition"] = PortDimension.Length,
		["*.LocalPosition"] = PortDimension.Length,
		["*.EndPosition"] = PortDimension.Length,
		["*.HitPosition"] = PortDimension.Length,
		["*.StartPosition"] = PortDimension.Length,

		//Turning a point from one space into another hands back a point. Named here so that a game's own helper - "ToWorld( local ) =>
		//WorldTransform.PointToWorld( local )" - is known to hand back metres, and what comes out of it is not converted a second time.
		["*.PointToWorld"] = PortDimension.Length,
		["*.PointToLocal"] = PortDimension.Length,
		["*.TransformPoint"] = PortDimension.Length,
		["*.InverseTransformPoint"] = PortDimension.Length,

		//A way to face. The same in any units, and scaling one is how a surface ends up lit from nowhere.
		["*.Normal"] = PortDimension.None,

		//How far along a trace went, which is a share of it and not a distance.
		["*.Fraction"] = PortDimension.None,
		["*.Friction"] = PortDimension.None,
		["*.IsTrigger"] = PortDimension.None,
		["*.Static"] = PortDimension.None,
		["*.Surface"] = PortDimension.None,

		//-- bodies ----------------------------------------------------------
		["Rinten.Rigidbody.Velocity"] = PortDimension.Speed,
		["Rinten.Rigidbody.MassCenterOverride"] = PortDimension.Length,

		//AngularVelocity is degrees a second and Gravity is a switch, not a pull - both read like measurements and neither is one.
		["Rinten.Rigidbody.AngularVelocity"] = PortDimension.None,
		["Rinten.Rigidbody.Gravity"] = PortDimension.None,
		["Rinten.Rigidbody.MassOverride"] = PortDimension.None,
		["Rinten.Rigidbody.LinearDamping"] = PortDimension.None,
		["Rinten.Rigidbody.AngularDamping"] = PortDimension.None,
		["Rinten.Rigidbody.Locking"] = PortDimension.None,
		["Rinten.Rigidbody.MotionEnabled"] = PortDimension.None,
		["Rinten.Rigidbody.OverrideMassCenter"] = PortDimension.None,
		["Rinten.Rigidbody.RigidbodyFlags"] = PortDimension.None,
		["Rinten.Rigidbody.StartAsleep"] = PortDimension.None,

		//-- camera ---------------------------------------------------------- The near and far planes are distances out from wherever is looking,
		//and a reflection probe measures them the same way a camera does - so by name, which also answers for an override that names no type at all.
		["*.ZNear"] = PortDimension.Length,
		["*.ZFar"] = PortDimension.Length,
		["Rinten.CameraComponent.OrthographicHeight"] = PortDimension.Length,
		["Rinten.CameraComponent.FieldOfView"] = PortDimension.None,
		["Rinten.CameraComponent.Orthographic"] = PortDimension.None,
		["Rinten.CameraComponent.Priority"] = PortDimension.None,
		["Rinten.CameraComponent.ClearFlags"] = PortDimension.None,
		["Rinten.CameraComponent.FovAxis"] = PortDimension.None,
		["Rinten.CameraComponent.IsMainCamera"] = PortDimension.None,
		["Rinten.CameraComponent.RenderTags"] = PortDimension.None,
		["Rinten.CameraComponent.RenderExcludeTags"] = PortDimension.None,
		["Rinten.CameraComponent.TargetEye"] = PortDimension.None,

		//A fraction of the screen, not a rectangle in the world.
		["Rinten.CameraComponent.Viewport"] = PortDimension.None,

		//-- lights ----------------------------------------------------------
		["Rinten.PointLight.Radius"] = PortDimension.Length,
		["Rinten.SpotLight.Radius"] = PortDimension.Length,
		["Rinten.SpotLight.ConeInner"] = PortDimension.None,
		["Rinten.SpotLight.ConeOuter"] = PortDimension.None,
		["Rinten.SpotLight.Attenuation"] = PortDimension.None,
		["Rinten.SpotLight.Cookie"] = PortDimension.None,
		["*.FogStrength"] = PortDimension.None,
		["*.FogMode"] = PortDimension.None,
		["*.Shadows"] = PortDimension.None,

		//-- environment ------------------------------------------------------
		["Rinten.EnvmapProbe.MaxDistance"] = PortDimension.Length,
		["Rinten.EnvmapProbe.Feathering"] = PortDimension.None,
		["Rinten.EnvmapProbe.FrameInterval"] = PortDimension.None,
		["Rinten.EnvmapProbe.DelayBetweenUpdates"] = PortDimension.None,
		["Rinten.EnvmapProbe.MultiBounce"] = PortDimension.None,
		["Rinten.EnvmapProbe.RenderDynamically"] = PortDimension.None,
		["Rinten.EnvmapProbe.Projection"] = PortDimension.None,
		["Rinten.EnvmapProbe.UpdateStrategy"] = PortDimension.None,
		["Rinten.EnvmapProbe.Texture"] = PortDimension.None,
		["Rinten.SkyBox2D.SkyMaterial"] = PortDimension.None,

		//-- renderers and post ------------------------------------------------ By name, because the
		//skinned renderer inherits every one of them and a scene writes whichever of the two it is.
		["*.Model"] = PortDimension.None,
		["*.RenderType"] = PortDimension.None,
		["*.RenderOptions"] = PortDimension.None,
		["*.MaterialGroup"] = PortDimension.None,
		["*.MaterialOverride"] = PortDimension.None,
		["*.BodyGroups"] = PortDimension.None,
		["*.CreateAttachments"] = PortDimension.None,
		["*.LodOverride"] = PortDimension.None,

		//Text is measured in its own scale rather than in the world, so this is a ratio however much it reads like a size.
		["Rinten.TextRenderer.Scale"] = PortDimension.None,
		["Rinten.TextRenderer.FontSize"] = PortDimension.None,
		["Rinten.TextRenderer.FontWeight"] = PortDimension.None,
		["Rinten.TextRenderer.BlendMode"] = PortDimension.None,
		["Rinten.TextRenderer.RenderOptions"] = PortDimension.None,
		["Rinten.TextRenderer.TextAlignment"] = PortDimension.None,
		["Rinten.TextRenderer.TextScope"] = PortDimension.None,

		["Rinten.Bloom.Mode"] = PortDimension.None,
		["Rinten.Bloom.Strength"] = PortDimension.None,
		["Rinten.Bloom.Threshold"] = PortDimension.None,
		["Rinten.Bloom.ThresholdWidth"] = PortDimension.None,
		["Rinten.Bloom.BloomCurve"] = PortDimension.None,

		//-- graphs -- State machine graph document; its Positions are canvas pixels, not world lengths
		["Rinten.States.StateMachineComponent.Serialized"] = PortDimension.None,

		//-- player -----------------------------------------------------------
		["Rinten.PlayerController.BodyRadius"] = PortDimension.Length,
		["Rinten.PlayerController.BodyHeight"] = PortDimension.Length,
		["Rinten.PlayerController.BodyMass"] = PortDimension.None,
		["Rinten.PlayerController.WalkSpeed"] = PortDimension.Speed,
		["Rinten.PlayerController.RunSpeed"] = PortDimension.Speed,
		["Rinten.PlayerController.DuckedSpeed"] = PortDimension.Speed,
		["Rinten.PlayerController.JumpSpeed"] = PortDimension.Speed,
		["Rinten.PlayerController.DuckedHeight"] = PortDimension.Length,
		["Rinten.PlayerController.ReachLength"] = PortDimension.Length,
		["Rinten.PlayerController.EyeDistanceFromTop"] = PortDimension.Length,

		["Rinten.PlayerController.CameraOffset"] = PortDimension.Length,
		["Rinten.PlayerController.AccelerationTime"] = PortDimension.None,
		["Rinten.PlayerController.DeaccelerationTime"] = PortDimension.None,
		["Rinten.PlayerController.AimStrengthBody"] = PortDimension.None,
		["Rinten.PlayerController.AimStrengthEyes"] = PortDimension.None,
		["Rinten.PlayerController.AimStrengthHead"] = PortDimension.None,
		["Rinten.PlayerController.LookSensitivity"] = PortDimension.None,
		["Rinten.PlayerController.FootstepVolume"] = PortDimension.None,
		["Rinten.PlayerController.RotationAngleLimit"] = PortDimension.None,
		["Rinten.PlayerController.RotationSpeed"] = PortDimension.None,
		["Rinten.PlayerController.PitchClamp"] = PortDimension.None,

		//Friction here is the fraction of speed taken away per second rather than a distance, and brake power is the multiplier on it.
		["Rinten.PlayerController.AirFriction"] = PortDimension.None,
		["Rinten.PlayerController.BrakePower"] = PortDimension.None,

		//The controller a ported project actually uses. Its three sizes are the capsule the player is, and the fourth number reads like
		//one and is not: acceleration on this component is how quickly the current velocity is pulled towards the wanted one, per second.
		["Rinten.CharacterController.Radius"] = PortDimension.Length,
		["Rinten.CharacterController.Height"] = PortDimension.Length,
		["Rinten.CharacterController.StepHeight"] = PortDimension.Length,
		["Rinten.CharacterController.Velocity"] = PortDimension.Speed,
		["Rinten.CharacterController.Acceleration"] = PortDimension.None,
		["Rinten.CharacterController.GroundAngle"] = PortDimension.None,
		["Rinten.CharacterController.Bounciness"] = PortDimension.None,
		["Rinten.CharacterController.IgnoreLayers"] = PortDimension.None,
		["Rinten.CharacterController.UseCollisionRules"] = PortDimension.None,

		//-- movement modes ---------------------------------------------------
		["Rinten.Movement.MoveModeWalk.StepUpHeight"] = PortDimension.Length,
		["Rinten.Movement.MoveModeWalk.StepDownHeight"] = PortDimension.Length,
		["Rinten.Movement.MoveModeWalk.GroundAngle"] = PortDimension.None,
		//Not a speed: a multiplier on the climb, nought to two. The mode works out how fast it climbs
		//itself and this scales it, so converting it left a ladder that took forty times as long to get up.
		["Rinten.Movement.MoveModeLadder.Speed"] = PortDimension.None,

		//A fraction of the body that has to be under water, not a height.
		["Rinten.Movement.MoveModeSwim.SwimLevel"] = PortDimension.None,

		//-- navigation -------------------------------------------------------
		["Rinten.NavMeshAgent.Height"] = PortDimension.Length,
		["Rinten.NavMeshAgent.MaxSpeed"] = PortDimension.Speed,
		["Rinten.NavMeshAgent.Acceleration"] = PortDimension.Acceleration,
		["Rinten.NavMeshAgent.Separation"] = PortDimension.None,
		["Rinten.NavMeshLink.LocalStartPosition"] = PortDimension.Length,
		["Rinten.NavMeshLink.LocalEndPosition"] = PortDimension.Length,
		["Rinten.NavMeshLink.ConnectionRadius"] = PortDimension.Length,

		//-- colliders, continued ---------------------------------------------
		["Rinten.HullCollider.BoxSize"] = PortDimension.Length,
		["Rinten.HullCollider.Height"] = PortDimension.Length,
		["Rinten.HullCollider.Radius2"] = PortDimension.Length,
		["Rinten.HullCollider.Slices"] = PortDimension.None,
		["Rinten.ManualHitbox.CenterA"] = PortDimension.Length,
		["Rinten.ManualHitbox.CenterB"] = PortDimension.Length,

		//A unit vector pointing out of the plane. It has a length already and it is one - but it points, so it turns: see Pointing.
		["Rinten.PlaneCollider.Normal"] = PortDimension.None,

		//-- bodies, continued -------------------------------------------------
		["Rinten.Rigidbody.MassCenter"] = PortDimension.Length,
		["Rinten.Rigidbody.MinImpactDamageSpeed"] = PortDimension.Speed,

		//A kilogram is a kilogram, damage is damage, and gravity scale is the multiplier on a pull that is already ours.
		["Rinten.Rigidbody.Mass"] = PortDimension.None,
		["Rinten.Rigidbody.GravityScale"] = PortDimension.None,
		["Rinten.Rigidbody.ImpactDamage"] = PortDimension.None,

		//-- joints -- Lengths limit, torques push/break; spring tuning is unitless. Inherited six by name
		["*.LocalFrame1"] = PortDimension.Length,
		["*.LocalFrame2"] = PortDimension.Length,
		["*.BreakForce"] = PortDimension.Force,
		["*.BreakTorque"] = PortDimension.Torque,
		["*.LinearStress"] = PortDimension.None,
		["*.AngularStress"] = PortDimension.None,

		["Rinten.BallJoint.MaxTorque"] = PortDimension.Torque,
		["Rinten.BallJoint.SwingLimit"] = PortDimension.None,
		["Rinten.BallJoint.TwistLimit"] = PortDimension.None,
		["Rinten.BallJoint.TargetVelocity"] = PortDimension.None,
		["Rinten.BallJoint.TargetRotation"] = PortDimension.None,

		["Rinten.HingeJoint.MaxTorque"] = PortDimension.Torque,
		["Rinten.HingeJoint.Axis"] = PortDimension.None,
		["Rinten.HingeJoint.Angle"] = PortDimension.None,
		["Rinten.HingeJoint.MinAngle"] = PortDimension.None,
		["Rinten.HingeJoint.MaxAngle"] = PortDimension.None,
		["Rinten.HingeJoint.TargetAngle"] = PortDimension.None,
		["Rinten.HingeJoint.TargetVelocity"] = PortDimension.None,
		["Rinten.HingeJoint.Speed"] = PortDimension.None,

		["Rinten.SliderJoint.MinLength"] = PortDimension.Length,
		["Rinten.SliderJoint.MaxLength"] = PortDimension.Length,
		["Rinten.SpringJoint.MinLength"] = PortDimension.Length,
		["Rinten.SpringJoint.MaxLength"] = PortDimension.Length,
		["Rinten.SpringJoint.RestLength"] = PortDimension.Length,

		["Rinten.ControlJoint.LinearVelocity"] = PortDimension.Speed,
		["Rinten.ControlJoint.AngularVelocity"] = PortDimension.None,
		["Rinten.ControlJoint.MaxVelocityForce"] = PortDimension.Force,
		["Rinten.ControlJoint.MaxVelocityTorque"] = PortDimension.Torque,

		["Rinten.FixedJoint.LinearFrequency"] = PortDimension.None,
		["Rinten.FixedJoint.AngularFrequency"] = PortDimension.None,
		["Rinten.FixedJoint.LinearDamping"] = PortDimension.None,
		["Rinten.FixedJoint.AngularDamping"] = PortDimension.None,

		["Rinten.UprightJoint.MaxTorque"] = PortDimension.Torque,
		["Rinten.UprightJoint.Hertz"] = PortDimension.None,

		//A wheel's suspension limits are how far it travels; its steering limits are how far it turns.
		["Rinten.WheelJoint.SuspensionLimits"] = PortDimension.Length,
		["Rinten.WheelJoint.SteeringLimits"] = PortDimension.None,
		["Rinten.WheelJoint.MaxSpinTorque"] = PortDimension.Torque,
		["Rinten.WheelJoint.MaxSteeringTorque"] = PortDimension.Torque,
		["Rinten.WheelJoint.SpinMotorSpeed"] = PortDimension.None,
		["Rinten.WheelJoint.TargetSteeringAngle"] = PortDimension.None,
		["Rinten.WheelJoint.SteeringHertz"] = PortDimension.None,
		["Rinten.WheelJoint.SuspensionHertz"] = PortDimension.None,
		["Rinten.WheelJoint.SteeringDampingRatio"] = PortDimension.None,
		["Rinten.WheelJoint.SuspensionDampingRatio"] = PortDimension.None,

		//-- lights and shadows, continued ------------------------------------- By name rather than by type: these are declared on the light every kind
		//of light derives from, and a scene writes the kind. An exact key would have to be repeated once per light and would still miss the next one.
		["Rinten.PointLight.Attenuation"] = PortDimension.None,

		//-- global illumination ------------------------------------------------ Probe density is probes per a fixed number of world units,
		//so it is the one number here that would move the other way; it is a resolution and is left as one rather than inverted on a guess.
		["Rinten.Probe.NormalBias"] = PortDimension.Length,
		["Rinten.Probe.ProbeDensity"] = PortDimension.None,
		["Rinten.Probe.Contrast"] = PortDimension.None,

		//-- fog, continued -----------------------------------------------------
		["Rinten.GradientFog.Height"] = PortDimension.Length,
		["Rinten.GradientFog.StartDistance"] = PortDimension.Length,
		["Rinten.GradientFog.EndDistance"] = PortDimension.Length,
		["Rinten.GradientFog.VerticalFalloffExponent"] = PortDimension.None,
		["Rinten.GradientFog.FalloffExponent"] = PortDimension.None,

		["Rinten.CubemapFog.StartDistance"] = PortDimension.Length,
		["Rinten.CubemapFog.EndDistance"] = PortDimension.Length,
		["Rinten.CubemapFog.HeightStart"] = PortDimension.Length,
		["Rinten.CubemapFog.HeightWidth"] = PortDimension.Length,
		["Rinten.CubemapFog.FalloffExponent"] = PortDimension.None,
		["Rinten.CubemapFog.HeightExponent"] = PortDimension.None,

		["Rinten.VolumetricFogVolume.Strength"] = PortDimension.None,
		["Rinten.VolumetricFogVolume.FalloffExponent"] = PortDimension.None,

		//-- post processing ---------------------------------------------------- Almost all of this is measured on the screen rather
		//than in the world, so almost none of it moves. The two that do are the only two that name a distance from the camera.
		["Rinten.DepthOfField.FocalDistance"] = PortDimension.Length,
		["Rinten.PostProcessVolume.BlendDistance"] = PortDimension.Length,

		["Rinten.DepthOfField.BlurSize"] = PortDimension.None,
		["Rinten.DepthOfField.FocusRange"] = PortDimension.None,
		["Rinten.PostProcessVolume.BlendWeight"] = PortDimension.None,

		//Its radius is a number of samples across the screen, which is exactly the case the Radius wildcard would have got wrong.

		//And its centre is in texture space, which is the same trap.
		["Rinten.Vignette.Center"] = PortDimension.None,
		["Rinten.Vignette.Roundness"] = PortDimension.None,
		["Rinten.Vignette.Smoothness"] = PortDimension.None,

		//Screen-space, like the vignette: a radius here is a share of the screen, not a distance in the world.
		["Rinten.AmbientOcclusion.Radius"] = PortDimension.None,

		["Rinten.ChromaticAberration.Offset"] = PortDimension.None,
		["Rinten.ColorGrading.BlendFactor"] = PortDimension.None,
		["Rinten.ColorGrading.ColorTempK"] = PortDimension.None,
		["Rinten.ColorGrading.RedCurve"] = PortDimension.None,
		["Rinten.ColorGrading.GreenCurve"] = PortDimension.None,
		["Rinten.ColorGrading.BlueCurve"] = PortDimension.None,
		["Rinten.ColorGrading.HueCurve"] = PortDimension.None,
		["Rinten.ColorGrading.SaturationCurve"] = PortDimension.None,
		["Rinten.ColorGrading.ValueCurve"] = PortDimension.None,
		["Rinten.FilmGrain.Response"] = PortDimension.None,
		["Rinten.Sharpen.TexelSize"] = PortDimension.None,
		["Rinten.BlitOverlay.Blend"] = PortDimension.None,
		["Rinten.BlitOverlay.Order"] = PortDimension.None,
		["Rinten.HighlightOutline.Width"] = PortDimension.None,
		["Rinten.Tonemapping.Rate"] = PortDimension.None,
		["Rinten.Tonemapping.ExposureCompensation"] = PortDimension.None,
		["Rinten.Tonemapping.MinimumExposure"] = PortDimension.None,
		["Rinten.Tonemapping.MaximumExposure"] = PortDimension.None,

		//-- decals and sprites -------------------------------------------------- Not a length: the decal's own width and height come from the
		//.decal it draws, and this multiplies them - see Decal.GetDecalVolume. Depth below it is a length, because nothing multiplies that.
		["Rinten.Decal.Size"] = PortDimension.None,
		["Rinten.Decal.Depth"] = PortDimension.Length,
		["Rinten.Decal.CoverageRange"] = PortDimension.None,
		["Rinten.Decal.CoverageAmount"] = PortDimension.None,
		["Rinten.Decal.AttenuationAngle"] = PortDimension.None,
		["Rinten.Decal.Parallax"] = PortDimension.None,
		["Rinten.Decal.ColorMix"] = PortDimension.None,
		["Rinten.Decal.SortLayer"] = PortDimension.None,
		["Rinten.Decal.SequenceId"] = PortDimension.None,
		["Rinten.Decal.LifeTime"] = PortDimension.None,
		["Rinten.DecalRenderer.Size"] = PortDimension.Length,

		["Rinten.SpriteRenderer.Size"] = PortDimension.Length,
		["Rinten.SpriteRenderer.AlphaCutoff"] = PortDimension.None,

		//-- lines, trails, beams, cables ---------------------------------------- A width and a spacing are across and along
		//the thing being drawn, both in the world. A spline's tension and bias are the shape of the curve between them.
		["Rinten.LineRenderer.Width"] = PortDimension.Length,
		//How much of the world one run of the texture covers, which is a length along the line.
		["Rinten.LineRenderer.UnitsPerTexture"] = PortDimension.Length,
		["Rinten.LineRenderer.SplineTension"] = PortDimension.None,
		["Rinten.LineRenderer.SplineContinuity"] = PortDimension.None,
		["Rinten.LineRenderer.SplineBias"] = PortDimension.None,
		["Rinten.LineRenderer.SplineInterpolation"] = PortDimension.None,
		["Rinten.LineRenderer.CylinderSegments"] = PortDimension.None,

		["Rinten.TrailRenderer.Width"] = PortDimension.Length,
		["Rinten.TrailRenderer.PointDistance"] = PortDimension.Length,
		["Rinten.TrailRenderer.LifeTime"] = PortDimension.None,
		["Rinten.TrailRenderer.MaxPoints"] = PortDimension.None,
		["Rinten.ParticleTrailRenderer.Width"] = PortDimension.Length,
		["Rinten.ParticleTrailRenderer.PointDistance"] = PortDimension.Length,
		["Rinten.ParticleTrailRenderer.LifeTime"] = PortDimension.None,
		["Rinten.ParticleTrailRenderer.MaxPoints"] = PortDimension.None,

		["Rinten.BeamEffect.TargetPosition"] = PortDimension.Length,
		["Rinten.BeamEffect.TargetRandom"] = PortDimension.Length,
		["Rinten.BeamEffect.BeamLifetime"] = PortDimension.None,
		["Rinten.BeamEffect.BeamsPerSecond"] = PortDimension.None,
		["Rinten.BeamEffect.InitialBurst"] = PortDimension.None,
		["Rinten.BeamEffect.MaxBeams"] = PortDimension.None,
		["Rinten.BeamEffect.TravelLerp"] = PortDimension.None,
		["Rinten.BeamEffect.TextureOffset"] = PortDimension.None,
		["Rinten.BeamEffect.TextureScale"] = PortDimension.None,
		["Rinten.BeamEffect.TextureScroll"] = PortDimension.None,
		["Rinten.BeamEffect.TextureScrollSpeed"] = PortDimension.None,

		["Rinten.TracerEffect.Length"] = PortDimension.Length,
		["Rinten.TracerEffect.StartDistance"] = PortDimension.Length,
		["Rinten.TracerEffect.LineWidth"] = PortDimension.Length,
		["Rinten.TracerEffect.LightRadius"] = PortDimension.Length,
		["Rinten.TracerEffect.DistancePerSecond"] = PortDimension.Speed,

		//Where along the drawn line the light sits, from nose to tail.
		["Rinten.TracerEffect.LightPosition"] = PortDimension.None,

		["Rinten.CableComponent.Size"] = PortDimension.Length,
		["Rinten.CableComponent.Slack"] = PortDimension.Length,
		["Rinten.CableComponent.LegacyFakeSlack"] = PortDimension.None,
		["Rinten.CableComponent.PathDetail"] = PortDimension.None,
		["Rinten.CableComponent.Subdivisions"] = PortDimension.None,
		["Rinten.CableComponent.TextureScale"] = PortDimension.None,
		["Rinten.CableComponent.TextureOffsetAlongPath"] = PortDimension.None,
		["Rinten.CableComponent.TextureOffsetCircumference"] = PortDimension.None,
		["Rinten.CableComponent.TextureRepeatsCircumference"] = PortDimension.None,
		["Rinten.CableNodeComponent.RadiusScale"] = PortDimension.None,
		["Rinten.CableNodeComponent.Roll"] = PortDimension.None,

		["Rinten.VerletRope.LengthOverride"] = PortDimension.Length,
		["Rinten.VerletRope.Slack"] = PortDimension.Length,
		["Rinten.VerletRope.SegmentCount"] = PortDimension.None,
		["Rinten.VerletRope.Stiffness"] = PortDimension.None,
		["Rinten.VerletRope.SoftBendFactor"] = PortDimension.None,
		["Rinten.VerletRope.DampingFactor"] = PortDimension.None,

		//-- particles -- Curves/ranges of speeds convert their values, the shape is left alone
		["Rinten.ParticleEffect.StartVelocity"] = PortDimension.Speed,
		["Rinten.ParticleEffect.InitialVelocity"] = PortDimension.Speed,
		["Rinten.ParticleEffect.ConstantMovement"] = PortDimension.Speed,
		["Rinten.ParticleEffect.PushStrength"] = PortDimension.Speed,
		["Rinten.ParticleEffect.ForceDirection"] = PortDimension.Acceleration,
		["Rinten.ParticleEffect.OrbitalForce"] = PortDimension.Acceleration,
		["Rinten.ParticleEffect.OrbitalPull"] = PortDimension.Acceleration,
		["Rinten.ParticleEffect.CollisionRadius"] = PortDimension.Length,

		//A particle's size, which a sprite draws in world units: a smoke puff of 140 came across as 140 metres
		["Rinten.ParticleEffect.Scale"] = PortDimension.Length,

		["Rinten.ParticleEffect.ForceScale"] = PortDimension.None,
		["Rinten.ParticleEffect.Damping"] = PortDimension.None,
		["Rinten.ParticleEffect.Bounce"] = PortDimension.None,
		["Rinten.ParticleEffect.Bumpiness"] = PortDimension.None,
		["Rinten.ParticleEffect.Alpha"] = PortDimension.None,
		["Rinten.ParticleEffect.Brightness"] = PortDimension.None,
		["Rinten.ParticleEffect.Gradient"] = PortDimension.None,
		["Rinten.ParticleEffect.Lifetime"] = PortDimension.None,
		["Rinten.ParticleEffect.LocalSpace"] = PortDimension.None,
		["Rinten.ParticleEffect.MaxParticles"] = PortDimension.None,
		["Rinten.ParticleEffect.PerParticleTimeScale"] = PortDimension.None,
		["Rinten.ParticleEffect.PreWarm"] = PortDimension.None,
		["Rinten.ParticleEffect.StartDelay"] = PortDimension.None,
		["Rinten.ParticleEffect.Stretch"] = PortDimension.None,
		["Rinten.ParticleEffect.Pitch"] = PortDimension.None,
		["Rinten.ParticleEffect.Yaw"] = PortDimension.None,
		["Rinten.ParticleEffect.Roll"] = PortDimension.None,
		["Rinten.ParticleEffect.SequenceId"] = PortDimension.None,
		["Rinten.ParticleEffect.SequenceSpeed"] = PortDimension.None,
		["Rinten.ParticleEffect.SequenceTime"] = PortDimension.None,
		["Rinten.ParticleEffect.SequenceFitToLifetime"] = PortDimension.None,
		["Rinten.ParticleEffect.InitialPitch"] = PortDimension.None,
		["Rinten.ParticleEffect.InitialYaw"] = PortDimension.None,
		["Rinten.ParticleEffect.InitialRoll"] = PortDimension.None,
		["Rinten.ParticleEffect.InitialScale"] = PortDimension.None,
		["Rinten.ParticleEffect.CollisionPrefabChance"] = PortDimension.None,
		["Rinten.ParticleEffect.CollisionPrefabRotation"] = PortDimension.None,
		["Rinten.ParticleEffect.DieOnCollisionChance"] = PortDimension.None,
		["Rinten.ParticleEffect.FollowerPrefabChance"] = PortDimension.None,

		//The emitter every shape of emitter derives from, so by name again: a scene says ParticleConeEmitter and the rate is the base class's.
		["*.Rate"] = PortDimension.None,
		["*.Burst"] = PortDimension.None,
		["*.Delay"] = PortDimension.None,
		["*.Duration"] = PortDimension.None,

		["Rinten.ParticleBoxEmitter.Size"] = PortDimension.Length,
		["Rinten.ParticleSphereEmitter.Velocity"] = PortDimension.Speed,

		//A spread per axis and a choice of sides: nothing to scale, but both are written against their axes and turn - see Pointing and AxisFlags.
		["Rinten.ParticleSphereEmitter.DistanceBias"] = PortDimension.None,
		["Rinten.ParticleSphereEmitter.OneSidedAxes"] = PortDimension.None,
		["Rinten.ParticleConeEmitter.ConeNear"] = PortDimension.Length,
		["Rinten.ParticleConeEmitter.ConeFar"] = PortDimension.Length,
		["Rinten.ParticleConeEmitter.ConeAngle"] = PortDimension.None,
		["Rinten.ParticleConeEmitter.CenterBias"] = PortDimension.None,
		["Rinten.ParticleConeEmitter.CenterBiasVelocity"] = PortDimension.None,
		["Rinten.ParticleConeEmitter.VelocityMultiplier"] = PortDimension.None,
		["Rinten.ParticleConeEmitter.VelocityRandom"] = PortDimension.None,
		["Rinten.ParticleRingEmitter.Thickness"] = PortDimension.Length,
		["Rinten.ParticleRingEmitter.VelocityFromCenter"] = PortDimension.Speed,
		["Rinten.ParticleRingEmitter.VelocityFromRing"] = PortDimension.Speed,
		["Rinten.ParticleRingEmitter.Angle"] = PortDimension.None,
		["Rinten.ParticleRingEmitter.AngleStart"] = PortDimension.None,
		["Rinten.ParticleRingEmitter.Flatness"] = PortDimension.None,

		["Rinten.ParticleAttractor.Force"] = PortDimension.Acceleration,
		["Rinten.ParticleAttractor.MaxForce"] = PortDimension.Acceleration,
		["Rinten.ParticleAttractor.Randomness"] = PortDimension.None,

		["Rinten.ParticleLightRenderer.Attenuation"] = PortDimension.None,
		["Rinten.ParticleLightRenderer.Brightness"] = PortDimension.None,
		["Rinten.ParticleLightRenderer.MaximumLights"] = PortDimension.None,
		["Rinten.ParticleLightRenderer.Ratio"] = PortDimension.None,

		["Rinten.ParticleSpriteRenderer.BlurAmount"] = PortDimension.None,
		["Rinten.ParticleSpriteRenderer.BlurOpacity"] = PortDimension.None,
		["Rinten.ParticleSpriteRenderer.BlurSpacing"] = PortDimension.None,
		["Rinten.ParticleSpriteRenderer.RotationOffset"] = PortDimension.None,
		["Rinten.ParticleTextRenderer.BlurAmount"] = PortDimension.None,
		["Rinten.ParticleTextRenderer.BlurOpacity"] = PortDimension.None,
		["Rinten.ParticleTextRenderer.BlurSpacing"] = PortDimension.None,
		["Rinten.ParticleTextRenderer.RotationOffset"] = PortDimension.None,
		["Rinten.ParticleTextRenderer.Pivot"] = PortDimension.None,


		//-- sound -- Distance is a length, loudness isn't; listed per subclass since scenes name the subclass
		["Rinten.BaseSoundComponent.Distance"] = PortDimension.Length,
		["Rinten.BaseSoundComponent.OcclusionRadius"] = PortDimension.Length,
		["Rinten.BaseSoundComponent.Falloff"] = PortDimension.None,
		["Rinten.BaseSoundComponent.MinRepeatTime"] = PortDimension.None,
		["Rinten.BaseSoundComponent.MaxRepeatTime"] = PortDimension.None,
		["Rinten.SoundPointComponent.Distance"] = PortDimension.Length,
		["Rinten.SoundPointComponent.OcclusionRadius"] = PortDimension.Length,
		["Rinten.SoundPointComponent.Falloff"] = PortDimension.None,
		["Rinten.SoundPointComponent.MinRepeatTime"] = PortDimension.None,
		["Rinten.SoundPointComponent.MaxRepeatTime"] = PortDimension.None,
		["Rinten.SoundBoxComponent.Distance"] = PortDimension.Length,
		["Rinten.SoundBoxComponent.OcclusionRadius"] = PortDimension.Length,
		["Rinten.SoundBoxComponent.Falloff"] = PortDimension.None,
		["Rinten.SoundBoxComponent.MinRepeatTime"] = PortDimension.None,
		["Rinten.SoundBoxComponent.MaxRepeatTime"] = PortDimension.None,
		["Rinten.Voice.Distance"] = PortDimension.Length,
		["Rinten.Voice.Falloff"] = PortDimension.None,
		["Rinten.Voice.MorphScale"] = PortDimension.None,
		["Rinten.Voice.MorphSmoothTime"] = PortDimension.None,
		["Rinten.SoundBoxComponent.Scale"] = PortDimension.Length,
		["Rinten.SoundscapeTrigger.BoxSize"] = PortDimension.Length,
		["Rinten.LipSync.MorphScale"] = PortDimension.None,
		["Rinten.LipSync.MorphSmoothTime"] = PortDimension.None,

		//-- panels -- Panel size is in panel pixels; RenderScale maps pixels to world
		["Rinten.WorldPanel.PanelSize"] = PortDimension.None,
		//A ratio between the panel's pixels and the world, not a length.
		["Rinten.WorldPanel.RenderScale"] = PortDimension.None,
		["Rinten.WorldPanel.InteractionRange"] = PortDimension.Length,
		["Rinten.ScreenPanel.ZIndex"] = PortDimension.None,

		//-- terrain and clutter --------------------------------------------------------
		["Rinten.Terrain.TerrainSize"] = PortDimension.Length,
		["Rinten.Terrain.TerrainHeight"] = PortDimension.Length,
		["Rinten.Terrain.ChunkSize"] = PortDimension.None,

		["Rinten.Clutter.SimpleScatterer.HeightOffset"] = PortDimension.Length,
		["Rinten.Clutter.SlopeScatterer.HeightOffset"] = PortDimension.Length,
		["Rinten.Clutter.TerrainMaterialScatterer.HeightOffset"] = PortDimension.Length,
		["Rinten.Clutter.SimpleScatterer.Density"] = PortDimension.None,
		["Rinten.Clutter.SlopeScatterer.Density"] = PortDimension.None,
		["Rinten.Clutter.TerrainMaterialScatterer.Density"] = PortDimension.None,
		//How far apart to sow, which is a distance and was arriving in inches: two hundred and fifty of them
		//is six metres, and left alone it is two hundred and fifty metres and a forest with four trees in it.
		["Rinten.Clutter.SimpleScatterer.MinSpacing"] = PortDimension.Length,
		["Rinten.Clutter.SlopeScatterer.MinSpacing"] = PortDimension.Length,
		["Rinten.Clutter.TerrainMaterialScatterer.MinSpacing"] = PortDimension.Length,
		["Rinten.Clutter.ClutterDefinition.MinSpacing"] = PortDimension.Length,

		["Rinten.Clutter.SlopeMapping.MinAngle"] = PortDimension.None,
		["Rinten.Clutter.SlopeMapping.MaxAngle"] = PortDimension.None,
		["Rinten.Clutter.SlopeMapping.EntryIndex"] = PortDimension.None,
		["Rinten.Clutter.ClutterEntry.LocalScale"] = PortDimension.None,
		["Rinten.Clutter.ClutterEntry.Weight"] = PortDimension.None,
		["Rinten.Clutter.ClutterDefinition.TileRadius"] = PortDimension.None,

		//A count of tiles and a slope in degrees. Named so the report stops asking about them, and so nothing ever converts them by wildcard.
		["Rinten.Clutter.ClutterDefinition.Resolution"] = PortDimension.None,
		["Rinten.Clutter.SlopeMapping.MaxSlope"] = PortDimension.None,
		["Rinten.Clutter.SlopeMapping.MinSlope"] = PortDimension.None,
		["Rinten.Clutter.SlopeMapping.DensityMultiplier"] = PortDimension.None,
		["Rinten.Terrain.Resolution"] = PortDimension.None,

		//-- water ----------------------------------------------------------------------- The density is a physical
		//constant rather than a measurement of theirs - water is a thousand in both engines - so it stays where it is.
		["Rinten.WaterVolume.SurfaceOffset"] = PortDimension.Length,
		["Rinten.WaterVolume.WaveAmplitude"] = PortDimension.Length,
		["Rinten.WaterVolume.FluidVelocity"] = PortDimension.Speed,
		["Rinten.WaterVolume.WaveFrequency"] = PortDimension.None,
		["Rinten.WaterVolume.FluidDensity"] = PortDimension.None,
		["Rinten.WaterVolume.LinearDrag"] = PortDimension.None,
		["Rinten.WaterVolume.AngularDrag"] = PortDimension.None,

		//-- mapping and gameplay ----------------------------------------------------------
		["Rinten.Mapping.Button.MoveDelta"] = PortDimension.Length,
		["Rinten.Mapping.Button.AnimationTime"] = PortDimension.None,
		["Rinten.Mapping.Button.ResetTime"] = PortDimension.None,
		["Rinten.Mapping.Button.AnimationCurve"] = PortDimension.None,
		["Rinten.Mapping.Door.AnimationCurve"] = PortDimension.None,
		["Rinten.Mapping.Door.SlideOffset"] = PortDimension.Length,
		["Rinten.Mapping.Door.TargetAngle"] = PortDimension.None,
		["Rinten.Mapping.Door.AutoCloseDelay"] = PortDimension.None,
		["Rinten.Mapping.EnvShake.Duration"] = PortDimension.None,
		["Rinten.Mapping.EnvShake.Amplitude"] = PortDimension.None,

		["Rinten.BaseInventoryComponent.PickupRadius"] = PortDimension.Length,
		["Rinten.BaseInventoryComponent.MaxSlots"] = PortDimension.None,
		["Rinten.BaseInventoryItem.PreferredSlot"] = PortDimension.None,
		["Rinten.BaseInventoryItem.SlotOrder"] = PortDimension.None,
		["Rinten.BaseInventoryItem.Value"] = PortDimension.None,
		["Rinten.BaseAmmoPickup.Amount"] = PortDimension.None,

		//A chair's sit height is a fraction from lowest to highest, and its two ranges are how far a sitting player may look.
		["Rinten.BaseChair.SitHeight"] = PortDimension.None,
		["Rinten.BaseChair.PitchRange"] = PortDimension.None,
		["Rinten.BaseChair.YawRange"] = PortDimension.None,

		["Rinten.BaseCombatWeapon.ClipMaxSize"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.SecondaryClipSize"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.SecondaryDefaultClip"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.StartingAmmo"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.DeployTime"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.ReloadTime"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.ReloadStartTime"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.FirstShellReloadTime"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.PrimaryDelay"] = PortDimension.None,
		["Rinten.BaseCombatWeapon.SecondaryDelay"] = PortDimension.None,

		["Rinten.Prop.Health"] = PortDimension.None,
		["Rinten.TriggerHurt.Damage"] = PortDimension.None,
		["Rinten.TriggerHurt.Rate"] = PortDimension.None,
		["Rinten.FireDamage.DamagePerSecond"] = PortDimension.None,
		["Rinten.RadiusDamage.DamageAmount"] = PortDimension.None,
		["Rinten.RadiusDamage.PhysicsForceScale"] = PortDimension.None,

		//TemporaryEffect is declared in no namespace at all, in either engine, so a scene names it with
		//nothing in front of it. By property name, then, rather than by a type key that would never match.
		["*.DestroyAfterSeconds"] = PortDimension.None,

		["Rinten.Dresser.ManualAge"] = PortDimension.None,
		["Rinten.Dresser.ManualHeight"] = PortDimension.None,
		["Rinten.Dresser.ManualTint"] = PortDimension.None,
		["Rinten.MeshComponent.SmoothingAngle"] = PortDimension.None,

		//Mesh component geometry per field; scaled in the mesh, not via the object's scale
		["Rinten.MeshComponent.Positions"] = PortDimension.Length,
		["Rinten.MeshComponent.TextureOrigin"] = PortDimension.Length,

		//World units per texel: shrink the world and leave this, and every surface is tiled thirty nine times over.
		["Rinten.MeshComponent.TextureScale"] = PortDimension.Length,

		//Texels, axes, indices and the packed topology - none of them a length.
		["Rinten.MeshComponent.TextureOffset"] = PortDimension.None,
		["Rinten.MeshComponent.TextureCoord"] = PortDimension.None,
		["Rinten.MeshComponent.TextureUAxis"] = PortDimension.None,
		["Rinten.MeshComponent.TextureVAxis"] = PortDimension.None,
		["Rinten.MeshComponent.TextureRotation"] = PortDimension.None,
		["Rinten.MeshComponent.TextureAngle"] = PortDimension.None,
		["Rinten.MeshComponent.MaterialIndex"] = PortDimension.None,
		["Rinten.MeshComponent.Materials"] = PortDimension.None,
		["Rinten.MeshComponent.EdgeFlags"] = PortDimension.None,
		["Rinten.MeshComponent.EdgeSmoothing"] = PortDimension.None,
		["Rinten.MeshComponent.Blends"] = PortDimension.None,
		["Rinten.MeshComponent.Colors"] = PortDimension.None,
		["Rinten.MeshComponent.Topology"] = PortDimension.None,
		["Rinten.ModelRenderer.LodOverride"] = PortDimension.None,
		["Rinten.MovieMaker.MoviePlayer.PositionSeconds"] = PortDimension.None,

		//A game object's own transform, for the rare component that serialises one instead of letting the object hold it.
		["Rinten.GameTransform.LocalPosition"] = PortDimension.Length,
		["Rinten.GameTransform.LocalRotation"] = PortDimension.None,

		//-- names that mean the same thing anywhere -- rotation, priority, seed, scale; exact entries win
		["*.Rotation"] = PortDimension.None,
		["*.WorldRotation"] = PortDimension.None,
		["*.LocalRotation"] = PortDimension.None,
		["*.Scale"] = PortDimension.None,
		["*.LocalScale"] = PortDimension.None,

		//Unit direction vectors; listed as None so code conversion doesn't treat terms with them as metres
		["*.Up"] = PortDimension.None,
		["*.Down"] = PortDimension.None,
		["*.Left"] = PortDimension.None,
		["*.Right"] = PortDimension.None,
		["*.Forward"] = PortDimension.None,
		["*.Backward"] = PortDimension.None,
		["*.Priority"] = PortDimension.None,
		["*.Seed"] = PortDimension.None,
		["*.TimeScale"] = PortDimension.None,
		["*.PlaybackSpeed"] = PortDimension.None,
		["*.PlaybackRate"] = PortDimension.None,
		["*.Opacity"] = PortDimension.None,
		["*.Intensity"] = PortDimension.None,
		["*.Volume"] = PortDimension.None,
		["*.Threshold"] = PortDimension.None,
		["*.Elasticity"] = PortDimension.None,
		["*.RollingResistance"] = PortDimension.None,
		["*.Damping"] = PortDimension.None,
		["*.DampingRatio"] = PortDimension.None,
		["*.Frequency"] = PortDimension.None,
		["*.DepthFeather"] = PortDimension.None,
		["*.CameraFadeNear"] = PortDimension.Length,
		["*.CameraFadeFar"] = PortDimension.Length,

		//Gizmos are drawn at a width on the screen rather than a width in the world, so a thickness of two is two pixels
		//and stays two pixels. It reads as a length and is the one in this list that a name alone would get wrong.
		["*.LineThickness"] = PortDimension.None,

		//Deliberately absent, and worth saying so: a door's speed is degrees a second when it swings and units a second when it slides, and
		//an emitter's rate over distance is particles per unit and so would move the other way. Both are reported rather than guessed at.
	};

	//Property names that are never a measurement whatever type they are on. Parts of a compound value that never share the dimension of the whole.
	//The serialiser writes a lot of these - callbacks, identity, the flags every component has - and none of them is worth a line in a report.
	private static readonly HashSet<string> NotMeasuredInside = new( StringComparer.OrdinalIgnoreCase )
	{
		"Rotation", "Scale", "Angles",
	};

	private static readonly HashSet<string> NeverMeasured = new( StringComparer.OrdinalIgnoreCase )
	{
		"__guid", "__type", "__version", "__enabled", "Name", "Tags", "Flags", "Enabled",
		"NetworkMode", "NetworkFlags", "NetworkOrphaned", "NetworkTransmit", "OwnerTransfer",
	};

	/// <summary>Converts one scene or prefab document in place.</summary>
	public static int Convert( JsonNode root, PortUnits units, ForeignBasis axes, string file, PortReport report )
	{
		if ( root is null ) return 0;

		//Nothing to scale and nothing to turn. Either on its own is still work: a game measuring in metres can count its axes the other way round.
		if ( units.IsIdentity && axes.IsIdentity ) return 0;

		var changed = 0;

		//A scene is a list of GameObjects; a prefab is one. Both nest the same way, so one walk does either.
		if ( root is JsonObject document )
		{
			if ( document["GameObjects"] is JsonArray objects )
			{
				foreach ( var node in objects )
					changed += ConvertGameObject( node as JsonObject, units, axes, file, report );
			}
			else if ( document["RootObject"] is JsonObject rootObject )
			{
				//A prefab wraps its root object; convert the wrapped object, not the wrapper
				changed += ConvertGameObject( rootObject, units, axes, file, report );
			}
			else
			{
				changed += ConvertGameObject( document, units, axes, file, report );
			}

			changed += ConvertNavMesh( document, units, axes );
		}

		return changed;
	}

	//The scene's own navmesh settings, which are not a component and so are not on any GameObject the walk
	//above visits. An agent 64 tall and 16 wide read as metres fits under nothing, and the mesh comes out empty.
	private static int ConvertNavMesh( JsonObject document, PortUnits units, ForeignBasis axes )
	{
		if ( document["SceneProperties"] is not JsonObject properties ) return 0;
		if ( properties["NavMesh"] is not JsonObject navmesh ) return 0;

		var changed = 0;

		//AgentMaxSlope is degrees.
		changed += ConvertValue( navmesh, "AgentHeight", PortDimension.Length, units, axes );
		changed += ConvertValue( navmesh, "AgentRadius", PortDimension.Length, units, axes );
		changed += ConvertValue( navmesh, "AgentStepSize", PortDimension.Length, units, axes );
		changed += ConvertValue( navmesh, "Bounds", PortDimension.Length, units, axes );

		return changed;
	}

	private static int ConvertGameObject( JsonObject go, PortUnits units, ForeignBasis axes, string file, PortReport report )
	{
		if ( go is null ) return 0;

		var changed = 0;
		var name = go["Name"]?.GetValue<string>() ?? "?";

		//Position scales and turns; Rotation and Scale only turn onto our axes
		changed += ConvertValue( go, "Position", PortDimension.Length, units, axes );
		changed += Turn( go, "Rotation", axes );
		changed += Turn( go, "Scale", axes );

		//Where the object is an instance of a prefab, the scene holds none of its properties - only what was changed about it, in a patch.
		changed += ConvertPrefabPatch( go["__PrefabInstancePatch"] as JsonObject, units, axes, $"{file} > {name}", report );

		if ( go["Components"] is JsonArray components )
		{
			var asModels = components.Any( x => x?["__type"]?.GetValue<string>() == "Rinten.ParticleModelRenderer" );

			foreach ( var node in components )
			{
				var type = node?["__type"]?.GetValue<string>() ?? "";
				if ( asModels && type == "Rinten.ParticleEffect" ) type += DrawnAsModels;

				changed += ConvertComponent( node as JsonObject, type, units, axes, $"{file} > {name}", report );
			}
		}

		if ( go["Children"] is JsonArray children )
		{
			foreach ( var node in children )
				changed += ConvertGameObject( node as JsonObject, units, axes, file, report );
		}

		return changed;
	}

	//Converts a placed prefab's overrides; component ones are by id, so by name only or reported
	private static int ConvertPrefabPatch( JsonObject patch, PortUnits units, ForeignBasis axes, string where, PortReport report )
	{
		if ( patch is null ) return 0;

		var changed = 0;

		if ( patch["PropertyOverrides"] is JsonArray overrides )
		{
			foreach ( var node in overrides )
			{
				if ( node is not JsonObject entry ) continue;

				var property = entry["Property"]?.GetValue<string>();
				if ( string.IsNullOrWhiteSpace( property ) ) continue;

				var target = entry["Target"]?["Type"]?.GetValue<string>() ?? "";
				var onObject = target.Equals( "GameObject", StringComparison.OrdinalIgnoreCase );

				//A GameObject's own properties, which the format guarantees, and otherwise whatever is written down by name.
				var dimension = onObject
					? ObjectDimension( property )
					: Dimension( "", property );

				//An override of a rotation or a scale measures nothing and still turns, the same as the property it is overriding - and
				//a prefab instance is where a scene keeps its player and its doors, so this is most of what a ported scene is made of.
				if ( dimension == PortDimension.None )
				{
					changed += Turn( entry, "Value", property, axes );
					continue;
				}

				if ( dimension == PortDimension.Unknown )
				{
					var turned = Turn( entry, "Value", property, axes );

					if ( turned > 0 )
					{
						changed += turned;
						continue;
					}

					if ( LooksMeasurable( entry["Value"] ) )
					{
						report.CouldNotConvert( where, $"prefab override {property}",
							entry["Value"]?.ToJsonString() ?? "null",
							"the patch names the component by id rather than by type, so there is nothing to look up" );
					}

					continue;
				}

				changed += ConvertValue( entry, "Value", dimension, units, axes );
			}
		}

		//Objects added to an instance are whole GameObjects, written the way any other is.
		if ( patch["AddedObjects"] is JsonArray added )
		{
			foreach ( var node in added )
				changed += ConvertGameObject( node as JsonObject, units, axes, where, report );
		}

		return changed;
	}

	//What a GameObject's own property measures. The format defines these, so they are the same on every object and are not in the table.
	private static PortDimension ObjectDimension( string property )
		=> property.Equals( "Position", StringComparison.OrdinalIgnoreCase )
			? PortDimension.Length
			: PortDimension.None;

	private static int ConvertComponent( JsonObject component, PortUnits units, ForeignBasis axes, string where, PortReport report )
		=> ConvertComponent( component, component?["__type"]?.GetValue<string>() ?? "", units, axes, where, report );

	//The same, for an object that does not carry its own type. A resource file is one object with no __type in it - the file's
	//extension is what says what it is - so the type has to be handed in or every property of it is unknown and nothing converts.
	private static int ConvertComponent( JsonObject component, string type, PortUnits units, ForeignBasis axes, string where, PortReport report )
	{
		if ( component is null ) return 0;

		var changed = 0;

		foreach ( var (key, value) in component.ToList() )
		{
			if ( NeverMeasured.Contains( key ) ) continue;
			if ( key.StartsWith( "On", StringComparison.Ordinal ) && value is null ) continue;

			var dimension = Dimension( type, key );

			//None means no length, but rotations and scales still have to turn
			if ( dimension == PortDimension.None )
			{
				if ( Pointing.Contains( $"{type}.{key}" ) )
					changed += TurnPointing( component, key, axes );
				else if ( AxisFlags.Contains( $"{type}.{key}" ) )
					changed += TurnAxisFlags( component, key, axes );
				else
					changed += Turn( component, key, axes );

				continue;
			}

			if ( dimension == PortDimension.Unknown )
			{
				//The same for one nobody has written a dimension for. A game's own types are deliberately Unknown - their numbers are their
				//units - but their axes are still Source's, so a rotation on one turns like any other and is not reported as unconverted.
				var turned = Turn( component, key, axes );

				if ( turned > 0 )
				{
					changed += turned;
					continue;
				}

				//Game struct property (e.g. position + angle): convert its parts individually, not as a whole
				if ( value is JsonObject nested )
				{
					changed += ConvertParts( nested, type, key, units, axes, where, report, 1 );
					continue;
				}

				//And a list of them, which is how a game keeps a grid: one entry a cell, its place in three fields and its contents beside them.
				if ( value is JsonArray )
				{
					changed += TurnList( value, type, key, units, axes, where, report, 1 );
					continue;
				}

				//Only worth a line if it is actually a number. A string that is a resource path, a bool, an enum - none of those measure anything,
				//and the porter saying so is noise. Named in full, because the line is meant to be pasteable straight into Dimensions as a key.
				if ( LooksMeasurable( value ) )
				{
					report.CouldNotConvert( where, $"{type}.{key}", value?.ToJsonString() ?? "null",
						"no dimension is written down for this property" );
				}

				continue;
			}

			changed += ConvertValue( component, key, dimension, units, axes );
		}

		return changed;
	}

	//Converts parts of an object property one by one against the component type; reports by full path
	private static int ConvertParts( JsonObject parts, string type, string owner, PortUnits units, ForeignBasis axes,
		string where, PortReport report, int depth )
	{
		//Structs inside structs are shallow in practice. The bound is here so that a file which is not what it claims to be cannot walk forever.
		if ( depth > 4 ) return 0;

		//A vector written as three fields, before the parts are walked one at a time - otherwise each of the
		//three is asked about on its own, measures nothing anybody wrote down, and is reported as unconverted.
		var changed = TurnFields( parts, axes );
		var vector = changed > 0;

		//A spline point, the engine's or a game's copy of one: its tangents are offsets from it and its up only turns
		var spline = IsSplinePoint( parts );
		if ( spline )
		{
			changed += ConvertValue( parts, "In", PortDimension.Length, units, axes );
			changed += ConvertValue( parts, "Out", PortDimension.Length, units, axes );
			changed += TurnDirection( parts, "Up", axes );
		}

		foreach ( var (key, value) in parts.ToList() )
		{
			if ( NeverMeasured.Contains( key ) ) continue;
			if ( vector && ( key == "X" || key == "Y" || key == "Z" ) ) continue;
			if ( spline && key is "In" or "Out" or "Up" ) continue;

			var dimension = Dimension( type, key );

			//Measures nothing, turns anyway - see the same case in ConvertComponent.
			if ( dimension == PortDimension.None )
			{
				changed += Turn( parts, key, axes );
				continue;
			}

			if ( dimension == PortDimension.Unknown )
			{
				var turned = Turn( parts, key, axes );

				if ( turned > 0 )
				{
					changed += turned;
					continue;
				}

				if ( value is JsonObject deeper )
				{
					changed += ConvertParts( deeper, type, $"{owner}.{key}", units, axes, where, report, depth + 1 );
					continue;
				}

				if ( value is JsonArray )
				{
					changed += TurnList( value, type, $"{owner}.{key}", units, axes, where, report, depth + 1 );
					continue;
				}

				if ( LooksMeasurable( value ) )
				{
					report.CouldNotConvert( where, $"{type}.{owner}.{key}", value?.ToJsonString() ?? "null",
						"no dimension is written down for this part of a property" );
				}

				continue;
			}

			changed += ConvertValue( parts, key, dimension, units, axes );
		}

		return changed;
	}

	/// <summary>What a property measures: the exact entry, then the wildcard by name, then unknown.</summary>
	//A particle effect beside a model renderer: its Scale sizes the models by a factor, not a length
	private const string DrawnAsModels = " (drawn as models)";

	public static PortDimension Dimension( string type, string property )
	{
		if ( type.EndsWith( DrawnAsModels, StringComparison.Ordinal ) )
		{
			if ( property == "Scale" ) return PortDimension.None;

			type = type[..^DrawnAsModels.Length];
		}

		if ( Dimensions.TryGetValue( $"{type}.{property}", out var exact ) ) return exact;

		if ( Dimensions.TryGetValue( $"*.{property}", out var byName ) ) return byName;

		//Colours are written the same way as vectors and are not lengths.
		if ( property.Contains( "Color", StringComparison.OrdinalIgnoreCase ) ) return PortDimension.None;
		if ( property.Contains( "Tint", StringComparison.OrdinalIgnoreCase ) ) return PortDimension.None;

		return PortDimension.Unknown;
	}

	//Multiplies whatever is under key, in place. Three shapes turn up: a bare number, a vector written as a comma string -
	//which is how this engine and s&box both serialise them - and an object of more of the same, which is what a BBox is.
	private static int ConvertValue( JsonObject owner, string key, PortDimension dimension, PortUnits units, ForeignBasis axes )
	{
		var node = owner[key];
		if ( node is null ) return 0;

		var factor = units.Factor( dimension );

		switch ( node )
		{
			case JsonValue value when value.TryGetValue<double>( out var number ):
				//A factor of one is nothing to do, and writing the number back would turn an integer into a
				//double - a scene's "Radius": 16 came back as 16.0 and no longer read as the int it was.
				if ( factor == 1.0 ) return 0;

				owner[key] = JsonValue.Create( number * factor );
				return 1;

			case JsonValue value when value.TryGetValue<string>( out var text ):
			{
				var scaled = ConvertCommaVector( text, factor, axes, KindOf( key, Parts( text ), dimension ) );
				if ( scaled is null ) return 0;

				owner[key] = JsonValue.Create( scaled );
				return 1;
			}

			case JsonObject nested:
			{
				var changed = 0;

				foreach ( var (childKey, _) in nested.ToList() )
				{
					//Rotations/scales inside compound values (transforms, joint frames) only turn, never scale
					if ( NotMeasuredInside.Contains( childKey ) )
					{
						changed += Turn( nested, childKey, axes );
						continue;
					}

					//A curve's x is its time along the life or the seed, never the quantity
					if ( string.Equals( childKey, "rangex", StringComparison.OrdinalIgnoreCase ) ) continue;

					changed += ConvertValue( nested, childKey, dimension, units, axes );
				}

				//X, Y and Z beside each other are a vector written the long way - which is how a particle effect keeps a velocity, one curve per axis.
				//The loop above scaled each of them; this is what moves them onto our axes, and turning after scaling is the same as turning before it.
				if ( IsDirectional( dimension ) )
				{
					changed += TurnFields( nested, axes );
					changed += TurnMembers( nested, axes );
				}

				changed += Reorder( nested, axes );

				return changed;
			}

			case JsonArray array:
			{
				var changed = 0;

				for ( var i = 0; i < array.Count; i++ )
				{
					if ( array[i] is not JsonValue item ) continue;

					if ( item.TryGetValue<double>( out var number ) )
					{
						array[i] = JsonValue.Create( number * factor );
						++changed;
						continue;
					}

					//A list of vectors written as strings, which is how a mesh keeps its vertices: "-9.303772,-1.767578,0" and a few thousand
					//more. Left alone, a room came across at thirty nine times its size and everything else in the scene converted around it.
					if ( item.TryGetValue<string>( out var text ) &&
						ConvertCommaVector( text, factor, axes, KindOf( key, Parts( text ), dimension ) ) is { } scaled )
					{
						array[i] = JsonValue.Create( scaled );
						++changed;
					}
				}

				return changed;
			}
		}

		return 0;
	}

	//Turns a three-field vector of non-numbers (curves, ranges) by permuting; refuses if a sign flips
	private static int TurnMembers( JsonObject owner, ForeignBasis axes )
	{
		if ( axes.IsIdentity ) return 0;

		var source = new[] { owner["X"], owner["Y"], owner["Z"] };
		if ( source.Any( x => x is null ) ) return 0;

		//Already done arithmetically, and doing it twice would turn it back.
		if ( source.All( x => x is JsonValue v && v.TryGetValue<double>( out _ ) ) ) return 0;

		var columns = new[] { axes.AxisX, axes.AxisY, axes.AxisZ };
		var moved = new JsonNode[3];

		for ( var to = 0; to < 3; to++ )
		{
			var found = false;

			for ( var from = 0; from < 3 && !found; from++ )
			{
				var sign = to switch { 0 => columns[from].x, 1 => columns[from].y, _ => columns[from].z };
				if ( sign == 0.0f ) continue;

				//A flip needs the value negated, which only a number allows.
				if ( sign < 0.0f )
				{
					if ( source[from] is not JsonValue value || !value.TryGetValue<double>( out var number ) )
						return 0;

					moved[to] = JsonValue.Create( -number );
				}
				else
				{
					moved[to] = source[from].DeepClone();
				}

				found = true;
			}

			if ( !found ) return 0;
		}

		owner["X"] = moved[0];
		owner["Y"] = moved[1];
		owner["Z"] = moved[2];

		return 1;
	}

	//Swaps BBox corners back after turning, since an axis flip leaves Mins above Maxs
	private static int Reorder( JsonObject box, ForeignBasis axes )
	{
		if ( axes.IsIdentity ) return 0;

		if ( box["Mins"] is not JsonValue minValue || box["Maxs"] is not JsonValue maxValue ) return 0;
		if ( !minValue.TryGetValue<string>( out var minText ) || !maxValue.TryGetValue<string>( out var maxText ) ) return 0;

		if ( !TryVector( minText, out var mins ) || !TryVector( maxText, out var maxs ) ) return 0;

		var low = new Vector3( MathF.Min( mins.x, maxs.x ), MathF.Min( mins.y, maxs.y ), MathF.Min( mins.z, maxs.z ) );
		var high = new Vector3( MathF.Max( mins.x, maxs.x ), MathF.Max( mins.y, maxs.y ), MathF.Max( mins.z, maxs.z ) );

		if ( low == mins && high == maxs ) return 0;

		box["Mins"] = JsonValue.Create( Write( low ) );
		box["Maxs"] = JsonValue.Create( Write( high ) );

		return 1;
	}

	private static bool TryVector( string text, out Vector3 vector )
	{
		vector = default;

		var parts = text.Split( ',' );
		if ( parts.Length != 3 ) return false;

		var numbers = new float[3];

		for ( var i = 0; i < 3; i++ )
		{
			if ( !float.TryParse( parts[i].Trim(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out numbers[i] ) )
				return false;
		}

		vector = new Vector3( numbers[0], numbers[1], numbers[2] );
		return true;
	}

	private static string Write( Vector3 v ) =>
		string.Join( ",", ((float[])[v.x, v.y, v.z]).Select( x =>
			x.ToString( "0.#######", System.Globalization.CultureInfo.InvariantCulture ) ) );

	//What a run of numbers is, so the turn onto our axes knows what to do with it. A place and a size
	//both permute; only the place carries signs, and a rotation is conjugated rather than permuted.
	private enum AxisKind
	{
		/// <summary>Angles, a colour, a Vector2, anything of no fixed axis.</summary>
		None,
		Vector,
		Size,
		Quaternion,
	}

	private static readonly string[] SizeNames = ["Scale", "Size", "Extents"];

	private static bool IsSizeName( string key ) =>
		SizeNames.Any( x => key.EndsWith( x, StringComparison.OrdinalIgnoreCase ) );

	private static bool IsRotationName( string key ) =>
		key.EndsWith( "Rotation", StringComparison.OrdinalIgnoreCase );

	private static bool IsAngleName( string key ) =>
		key.EndsWith( "Angles", StringComparison.OrdinalIgnoreCase );

	//Axis kind of a value by name and count; unnamed triples turn only if a length (colours are triples)
	private static AxisKind KindOf( string key, int count, PortDimension dimension )
	{
		if ( count == 4 && IsRotationName( key ) ) return AxisKind.Quaternion;
		if ( count != 3 ) return AxisKind.None;
		if ( IsAngleName( key ) ) return AxisKind.None;
		if ( IsSizeName( key ) ) return AxisKind.Size;

		return IsDirectional( dimension ) ? AxisKind.Vector : AxisKind.None;
	}

	//Dimensions whose triples point somewhere and must turn with the world (e.g. ForceDirection)
	private static bool IsDirectional( PortDimension dimension ) => dimension
		is PortDimension.Length or PortDimension.Speed
		or PortDimension.Acceleration or PortDimension.Force;

	//A vector written as "x,y,z", turned onto our axes and scaled. Null where the string was not one - a resource path, a tag list, a name.
	private static string ConvertCommaVector( string text, double factor, ForeignBasis axes, AxisKind kind )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return null;

		var parts = text.Split( ',' );
		if ( parts.Length is < 2 or > 4 ) return null;

		var numbers = new double[parts.Length];

		for ( var i = 0; i < parts.Length; i++ )
		{
			if ( !double.TryParse( parts[i].Trim(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out numbers[i] ) )
				return null;
		}

		switch ( kind )
		{
			case AxisKind.Vector:
			{
				var turned = axes.Vector( new Vector3( (float)numbers[0], (float)numbers[1], (float)numbers[2] ) );
				numbers = [turned.x, turned.y, turned.z];
				break;
			}

			case AxisKind.Size:
			{
				var turned = axes.Size( new Vector3( (float)numbers[0], (float)numbers[1], (float)numbers[2] ) );
				numbers = [turned.x, turned.y, turned.z];
				break;
			}

			case AxisKind.Quaternion:
			{
				var turned = axes.Quat( new Rotation(
					(float)numbers[0], (float)numbers[1], (float)numbers[2], (float)numbers[3] ) );
				numbers = [turned.x, turned.y, turned.z, turned.w];
				break;
			}
		}

		return string.Join( ",", numbers.Select( n =>
			( n * factor ).ToString( "0.######", System.Globalization.CultureInfo.InvariantCulture ) ) );
	}

	//The shape test the old scaler was doubling as.
	private static string ScaleCommaVector( string text, double factor ) =>
		ConvertCommaVector( text, factor, ForeignBasis.Same, AxisKind.None );

	//How many numbers a comma vector holds, or zero if it is not one.
	private static int Parts( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return 0;

		var parts = text.Split( ',' );

		return parts.Length is >= 2 and <= 4 ? parts.Length : 0;
	}

	//Turns and scales X/Y/Z-field vectors and lists of them; whole numbers with a payload are cells
	private static int TurnFields( JsonObject owner, ForeignBasis axes )
	{
		if ( axes.IsIdentity ) return 0;

		if ( owner["X"] is not JsonValue jx || !jx.TryGetValue<double>( out var x ) ) return 0;
		if ( owner["Y"] is not JsonValue jy || !jy.TryGetValue<double>( out var y ) ) return 0;
		if ( owner["Z"] is not JsonValue jz || !jz.TryGetValue<double>( out var z ) ) return 0;

		//Whole numbers are a cell only with a payload, which must turn too, or nothing moves
		var whole = x == Math.Floor( x ) && y == Math.Floor( y ) && z == Math.Floor( z );
		var cells = false;

		if ( whole )
		{
			if ( !TurnBricks( owner, axes, out var bricks ) ) return 0;

			cells = bricks > 0;
		}

		var turned = axes.Vector( cells
			? new Vector3( (float)x + 0.5f, (float)y + 0.5f, (float)z + 0.5f )
			: new Vector3( (float)x, (float)y, (float)z ) );

		if ( cells )
		{
			owner["X"] = JsonValue.Create( (long)MathF.Floor( turned.x ) );
			owner["Y"] = JsonValue.Create( (long)MathF.Floor( turned.y ) );
			owner["Z"] = JsonValue.Create( (long)MathF.Floor( turned.z ) );
		}
		else
		{
			owner["X"] = JsonValue.Create( turned.x );
			owner["Y"] = JsonValue.Create( turned.y );
			owner["Z"] = JsonValue.Create( turned.z );
		}

		return 1;
	}

	//Turns a cell's voxel bricks (base64 run-length, cube-sized); false if any can't be read
	private static bool TurnBricks( JsonObject owner, ForeignBasis axes, out int turned )
	{
		turned = 0;

		foreach ( var (key, value) in owner.ToList() )
		{
			if ( key is "X" or "Y" or "Z" ) continue;
			if ( value is not JsonValue held || !held.TryGetValue<string>( out var text ) ) continue;
			if ( text.Length < 4 || !IsBase64( text ) ) continue;

			byte[] packed;

			try { packed = System.Convert.FromBase64String( text ); }
			catch ( FormatException ) { continue; }

			if ( packed.Length < 2 || ( packed.Length & 1 ) != 0 ) continue;

			var total = 0;
			var empty = false;

			for ( var i = 0; i < packed.Length; i += 2 )
			{
				if ( packed[i] == 0 ) { empty = true; break; }

				total += packed[i];
			}

			var side = (int)Math.Round( Math.Cbrt( total ) );
			var brick = !empty && side >= 2 && side * side * side == total;

			if ( !brick )
			{
				//Not a brick: short strings are names and ignored; larger unreadable payloads block the move
				if ( packed.Length < 32 ) continue;

				return false;
			}

			owner[key] = JsonValue.Create( Packed( Turned( Unpacked( packed, total ), side, axes ) ) );
			++turned;
		}

		return true;
	}

	//Whether a string could be base64 at all, before paying to decode it.
	private static bool IsBase64( string text )
	{
		foreach ( var c in text )
		{
			if ( c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=' ) continue;

			return false;
		}

		return true;
	}

	private static byte[] Unpacked( byte[] packed, int total )
	{
		var voxels = new byte[total];
		var at = 0;

		for ( var i = 0; i < packed.Length; i += 2 )
			for ( var n = 0; n < packed[i]; ++n )
				voxels[at++] = packed[i + 1];

		return voxels;
	}

	private static string Packed( byte[] voxels )
	{
		var packed = new List<byte>( voxels.Length / 8 );

		for ( var i = 0; i < voxels.Length; )
		{
			var run = 1;
			while ( i + run < voxels.Length && voxels[i + run] == voxels[i] && run < 255 ) ++run;

			packed.Add( (byte)run );
			packed.Add( voxels[i] );
			i += run;
		}

		return System.Convert.ToBase64String( packed.ToArray() );
	}

	//One brick turned. Where each of its axes lands, and which way round, is read off the basis: an
	//axis that arrives negated is also mirrored inside the brick, because the brick moved with it.
	private static byte[] Turned( byte[] voxels, int side, ForeignBasis axes )
	{
		Span<int> lands = stackalloc int[3];
		Span<bool> flips = stackalloc bool[3];

		for ( var i = 0; i < 3; ++i )
		{
			var went = axes.Vector( i == 0 ? new Vector3( 1, 0, 0 ) : i == 1 ? new Vector3( 0, 1, 0 ) : new Vector3( 0, 0, 1 ) );
			var to = MathF.Abs( went.x ) > 0.5f ? 0 : MathF.Abs( went.y ) > 0.5f ? 1 : 2;

			lands[i] = to;
			flips[i] = ( to == 0 ? went.x : to == 1 ? went.y : went.z ) < 0.0f;
		}

		var turned = new byte[voxels.Length];
		Span<int> from = stackalloc int[3];
		Span<int> into = stackalloc int[3];

		for ( from[2] = 0; from[2] < side; ++from[2] )
		for ( from[1] = 0; from[1] < side; ++from[1] )
		for ( from[0] = 0; from[0] < side; ++from[0] )
		{
			for ( var i = 0; i < 3; ++i )
				into[lands[i]] = flips[i] ? side - 1 - from[i] : from[i];

			turned[into[0] + ( side * ( into[1] + ( side * into[2] ) ) )] =
				voxels[from[0] + ( side * ( from[1] + ( side * from[2] ) ) )];
		}

		return turned;
	}

	//Every one of those in a list, and every object in it walked.
	private static int TurnList( JsonNode node, string type, string owner, PortUnits units, ForeignBasis axes,
		string where, PortReport report, int depth )
	{
		if ( node is not JsonArray array ) return 0;

		var changed = 0;

		foreach ( var item in array )
		{
			if ( item is not JsonObject one ) continue;

			changed += ConvertParts( one, type, owner, units, axes, where, report, depth + 1 );
		}

		return changed;
	}

	//Position and two tangents beside it, which is what a spline point is in any of its spellings
	private static bool IsSplinePoint( JsonObject parts )
		=> ( parts.ContainsKey( "Pos" ) || parts.ContainsKey( "Position" ) ) && parts.ContainsKey( "In" ) && parts.ContainsKey( "Out" );

	//A unit triple that points somewhere, turned and never scaled
	private static int TurnDirection( JsonObject owner, string key, ForeignBasis axes )
	{
		if ( axes.IsIdentity ) return 0;
		if ( owner[key] is not JsonValue value || !value.TryGetValue<string>( out var text ) ) return 0;
		if ( ConvertCommaVector( text, 1.0, axes, AxisKind.Vector ) is not { } turned ) return 0;

		owner[key] = JsonValue.Create( turned );
		return 1;
	}

	//The turn onto our axes with no scaling: what a rotation or a ratio needs.
	private static int Turn( JsonObject owner, string key, ForeignBasis axes ) => Turn( owner, key, key, axes );

	//The same, where what the value is called and where it is written are not the same thing - a prefab override
	//keeps the property's name beside a slot called Value , and the name is what says whether it turns.
	private static int Turn( JsonObject owner, string key, string named, ForeignBasis axes )
	{
		if ( axes.IsIdentity ) return 0;
		if ( owner[key] is not JsonValue value || !value.TryGetValue<string>( out var text ) ) return 0;

		var kind = KindOf( named, Parts( text ), PortDimension.None );
		if ( kind == AxisKind.None ) return 0;

		var turned = ConvertCommaVector( text, 1.0, axes, kind );
		if ( turned is null ) return 0;

		owner[key] = JsonValue.Create( turned );
		return 1;
	}

	//Unitless triples that still point somewhere (normals, emitter bias), named per type.property
	private static readonly HashSet<string> Pointing = new( StringComparer.OrdinalIgnoreCase )
	{
		"Rinten.PlaneCollider.Normal",
		"Rinten.ParticleSphereEmitter.DistanceBias",
	};

	//Flags that name axes one bit each - X, Y, Z - by type and property. The axes move, so the bits
	//do: s&box's Z restricted a sphere emitter to its top half, and the same bit here is the back half.
	private static readonly HashSet<string> AxisFlags = new( StringComparer.OrdinalIgnoreCase )
	{
		"Rinten.ParticleSphereEmitter.OneSidedAxes",
	};

	//A value in Pointing, turned as a direction and not scaled.
	private static int TurnPointing( JsonObject owner, string key, ForeignBasis axes )
	{
		if ( axes.IsIdentity ) return 0;
		if ( owner[key] is not JsonValue value || !value.TryGetValue<string>( out var text ) ) return 0;
		if ( Parts( text ) != 3 ) return 0;

		var turned = ConvertCommaVector( text, 1.0, axes, AxisKind.Vector );
		if ( turned is null ) return 0;

		owner[key] = JsonValue.Create( turned );
		return 1;
	}

	//A value in AxisFlags, each bit moved to the axis its own lands on. Written back the
	//way it came: a number, or the names the serializer writes a flags enum as ("X, Z").
	private static int TurnAxisFlags( JsonObject owner, string key, ForeignBasis axes )
	{
		if ( axes.IsIdentity ) return 0;
		if ( owner[key] is not JsonValue value ) return 0;

		int bits;
		var named = false;

		if ( value.TryGetValue<double>( out var number ) )
		{
			bits = (int)number;
		}
		else if ( value.TryGetValue<string>( out var text ) )
		{
			named = true;
			bits = 0;

			foreach ( var part in text.Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries ) )
			{
				if ( part.Equals( "None", StringComparison.OrdinalIgnoreCase ) ) continue;
				else if ( part.Equals( "X", StringComparison.OrdinalIgnoreCase ) ) bits |= 1;
				else if ( part.Equals( "Y", StringComparison.OrdinalIgnoreCase ) ) bits |= 2;
				else if ( part.Equals( "Z", StringComparison.OrdinalIgnoreCase ) ) bits |= 4;
				else if ( int.TryParse( part, out var raw ) ) bits |= raw;
				else return 0;
			}
		}
		else
		{
			return 0;
		}

		var columns = new[] { axes.AxisX, axes.AxisY, axes.AxisZ };
		var turned = bits & ~7;

		for ( var from = 0; from < 3; from++ )
		{
			if ( ( bits & ( 1 << from ) ) == 0 ) continue;

			var to = columns[from];
			turned |= to.x != 0.0f ? 1 : to.y != 0.0f ? 2 : 4;
		}

		if ( turned == bits ) return 0;

		if ( !named )
		{
			owner[key] = JsonValue.Create( turned );
			return 1;
		}

		var names = new List<string>( 3 );
		if ( ( turned & 1 ) != 0 ) names.Add( "X" );
		if ( ( turned & 2 ) != 0 ) names.Add( "Y" );
		if ( ( turned & 4 ) != 0 ) names.Add( "Z" );

		owner[key] = JsonValue.Create( names.Count == 0 ? "None" : string.Join( ", ", names ) );
		return 1;
	}

	//Whether a value is the sort of thing that could be a measurement.
	private static bool LooksMeasurable( JsonNode node )
	{
		switch ( node )
		{
			case JsonValue value when value.TryGetValue<double>( out var number ):
				//Zero and one are almost never a length and always a default.
				return number != 0.0 && number != 1.0 && number != -1.0;

			case JsonValue value when value.TryGetValue<string>( out var text ):
				return ScaleCommaVector( text, 1.0 ) is not null;

			case JsonObject nested:
				return nested.Any( pair => LooksMeasurable( pair.Value ) );

			//A list was invisible here, so a grid of cells that nothing turned was skipped without a word in the report.
			case JsonArray array:
				return array.Any( LooksMeasurable );

			default:
				return false;
		}
	}

	//Resource file extensions and the type of their single object (no __type in these files)
	private static readonly Dictionary<string, string> Resources = new( StringComparer.OrdinalIgnoreCase )
	{
		[".terrain"] = "Rinten.Terrain",
		[".clutter"] = "Rinten.Clutter.ClutterDefinition",
	};

	public static bool ConvertFile( string path, PortUnits units, ForeignBasis axes, string relative, PortReport report )
	{
		JsonNode root;

		try
		{
			root = JsonNode.Parse( System.IO.File.ReadAllText( path ) );
		}
		catch ( Exception e )
		{
			report.Skipped.Add( $"{relative}: {e.Message}" );
			return false;
		}

		if ( root is null ) return false;

		var extension = System.IO.Path.GetExtension( path );

		var changed = Resources.TryGetValue( extension, out var type )
			? ConvertComponent( root as JsonObject, type, units, axes, relative, report )
			: Convert( root, units, axes, relative, report );

		if ( changed == 0 ) return false;

		System.IO.File.WriteAllText( path,
			root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

		report.Notes.Add( $"{relative}: {changed} measurements from {units.SourceUnitName}s" );
		return true;
	}
}

using Rinten.Audio;
using Rinten.Volumes;
using Rinten.VR;
using System.Text.Json.Nodes;

namespace SceneTests.Components;

//Pure-state components: defaults, JSON round trips, enable/disable, lookups; GradientFog disabled
[TestClass]
[DoNotParallelize]
public class PureComponentStateTest : SceneTest
{
	//SpawnPoint is pure state (the model load lives in DrawGizmos, which never runs headless): its Color default matches the source initializer, it shows
	//up in scene and component lookups while enabled, disappears from the enabled-only scene index when disabled, and ticking with it enabled is safe.
	[TestMethod]
	public void SpawnPointDefaultsAndLookup()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var spawn = go.Components.Create<SpawnPoint>();

		Assert.AreEqual( (Color)"#E3510D", spawn.Color );

		Assert.AreSame( spawn, scene.GetAllComponents<SpawnPoint>().Single() );
		Assert.AreSame( spawn, go.Components.Get<SpawnPoint>( FindMode.EnabledInSelf ) );

		spawn.Enabled = false;
		Assert.AreEqual( 0, scene.GetAllComponents<SpawnPoint>().Count() );
		Assert.AreSame( spawn, go.Components.Get<SpawnPoint>( FindMode.EverythingInSelf ) );

		spawn.Enabled = true;
		Assert.AreSame( spawn, scene.GetAllComponents<SpawnPoint>().Single() );

		for ( int i = 0; i < 10; i++ ) scene.GameTick();

		Assert.IsTrue( spawn.IsValid );
		Assert.IsTrue( spawn.Active );
	}

	//A SpawnPoint with a non-default Color survives a GameObject-level serialize, then deserialize into a completely separate scene.
	[TestMethod]
	public void SpawnPointColorRoundTrip()
	{
		var color = new Color( 0.5f, 0.25f, 0.125f, 1.0f );

		JsonObject node;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			go.Name = "Spawn Holder";
			go.Components.Create<SpawnPoint>().Color = color;

			node = go.Serialize();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var restored = new GameObject();
			restored.Deserialize( node );

			var spawn = restored.Components.Get<SpawnPoint>( FindMode.EverythingInSelf );
			Assert.IsNotNull( spawn );
			Assert.AreEqual( color, spawn.Color );
		}
	}

	//SceneInformation has no lifecycle at all - a fresh component reports an empty metadata
	//dictionary, and GetMetadata only emits the keys that have actually been filled in.
	[TestMethod]
	public void SceneInformationMetadata()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var info = go.Components.Create<SceneInformation>();

		Assert.IsNull( info.Title );
		Assert.IsNull( info.Author );
		Assert.IsNotNull( info.SceneTags );
		Assert.AreEqual( 0, info.GetMetadata().Count );

		info.Title = "My Map";
		info.Author = "garry";
		info.Version = "1.2";
		info.SceneTags.Add( "outdoor" );

		var metadata = info.GetMetadata();

		Assert.AreEqual( "My Map", metadata["Title"] );
		Assert.AreEqual( "garry", metadata["Author"] );
		Assert.AreEqual( "1.2", metadata["Version"] );
		Assert.AreEqual( "outdoor", metadata["Tags"] );
		Assert.IsFalse( metadata.ContainsKey( "Description" ) );
		Assert.IsFalse( metadata.ContainsKey( "Group" ) );
	}

	//SceneInformation's string properties and TagSet survive a component-level
	//serialize, then DeserializeImmediately onto a fresh component in a new scene.
	[TestMethod]
	public void SceneInformationRoundTrip()
	{
		JsonObject json;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			var info = go.Components.Create<SceneInformation>();
			info.Title = "Round Trip";
			info.Description = "a description";
			info.SceneTags.Add( "outdoor" );

			json = (JsonObject)info.Serialize();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var go = sceneB.CreateObject();
			var copy = go.Components.Create<SceneInformation>( false );
			copy.DeserializeImmediately( json );

			Assert.IsTrue( copy.Enabled );
			Assert.AreEqual( "Round Trip", copy.Title );
			Assert.AreEqual( "a description", copy.Description );
			Assert.IsTrue( copy.SceneTags.Has( "outdoor" ) );
		}
	}

	//AudioListener's OnEnabled/OnDisabled only add and remove a managed Audio.Listener from the static active list -
	//enabling registers exactly one listener, disabling removes it again, and destroy is a no-op once already disabled.
	[TestMethod]
	public void AudioListenerRegistration()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var baseline = Rinten.Audio.Listener.Active.Count;

		var go = scene.CreateObject();
		var listener = go.Components.Create<AudioListener>();

		Assert.IsTrue( listener.UseCameraDirection );
		Assert.AreEqual( baseline + 1, Rinten.Audio.Listener.Active.Count );

		listener.Enabled = false;
		Assert.AreEqual( baseline, Rinten.Audio.Listener.Active.Count );

		listener.Enabled = true;
		Assert.AreEqual( baseline + 1, Rinten.Audio.Listener.Active.Count );

		listener.Destroy();
		Assert.AreEqual( baseline, Rinten.Audio.Listener.Active.Count );
	}

	//A non-default UseCameraDirection on AudioListener survives the component serialize/deserialize round trip into a new scene. Both
	//the original and the copy are destroyed afterwards so no Audio.Listener entries leak into the static active list for later tests.
	[TestMethod]
	public void AudioListenerRoundTrip()
	{
		JsonObject json;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			var listener = go.Components.Create<AudioListener>();
			listener.UseCameraDirection = false;

			json = (JsonObject)listener.Serialize();
			listener.Destroy();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var go = sceneB.CreateObject();
			var copy = go.Components.Create<AudioListener>( false );
			copy.DeserializeImmediately( json );

			Assert.IsFalse( copy.UseCameraDirection );
			copy.Destroy();
		}
	}

	//SoundscapeTrigger defaults match the source initializers, and TestListenerPosition implements the documented shapes: Point is heard
	//everywhere, Sphere tests squared distance against Radius around the world position, Box tests the local-space box spanning -BoxSize..BoxSize.
	[TestMethod]
	public void SoundscapeListenerPositionShapes()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		go.WorldPosition = new Vector3( 1000, 0, 0 );

		var trigger = go.Components.Create<SoundscapeTrigger>();

		Assert.AreEqual( SoundscapeTrigger.TriggerType.Point, trigger.Type );
		Assert.IsTrue( trigger.StayActiveOnExit );
		Assert.AreEqual( 1.0f, trigger.Volume );
		Assert.AreEqual( 12.5f, trigger.Radius );
		Assert.AreEqual( new Vector3( 1.27f ), trigger.BoxSize );
		Assert.IsFalse( trigger.Playing );

		//Point: heard from anywhere
		Assert.IsTrue( trigger.TestListenerPosition( new Vector3( 99999, 0, 0 ) ) );

		//Sphere: strict squared-distance check around the world position
		trigger.Type = SoundscapeTrigger.TriggerType.Sphere;
		trigger.Radius = 100.0f;
		Assert.IsTrue( trigger.TestListenerPosition( new Vector3( 1050, 0, 0 ) ) );
		Assert.IsFalse( trigger.TestListenerPosition( new Vector3( 1200, 0, 0 ) ) );

		//Box: local-space box from -BoxSize to +BoxSize
		trigger.Type = SoundscapeTrigger.TriggerType.Box;
		Assert.IsTrue( trigger.TestListenerPosition( new Vector3( 1000, 0, 1 ) ) );
		Assert.IsFalse( trigger.TestListenerPosition( new Vector3( 1000, 0, 2 ) ) );
	}

	//A sphere trigger far away from the listener (which sits at the origin headless) is never
	//selected by the scene soundscape system, no matter how often the scene ticks - it stays silent.
	[TestMethod]
	public void SoundscapeOutOfRangeStaysIdle()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		go.WorldPosition = new Vector3( 10000, 0, 0 );

		var trigger = go.Components.Create<SoundscapeTrigger>();
		trigger.Type = SoundscapeTrigger.TriggerType.Sphere;
		trigger.Radius = 10.0f;

		for ( int i = 0; i < 30; i++ ) scene.GameTick();

		Assert.IsFalse( trigger.Playing );
		Assert.IsTrue( trigger.IsValid );
	}

	//A Soundscape trigger with no resource stays playing inertly; disabling it stops and clears Playing
	[TestMethod]
	public void SoundscapeTriggerPlayingLifecycle()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var trigger = go.Components.Create<SoundscapeTrigger>();

		trigger.Playing = true;

		for ( int i = 0; i < 10; i++ ) scene.GameTick();

		Assert.IsTrue( trigger.Playing );
		Assert.IsTrue( trigger.IsValid );

		trigger.Enabled = false;

		Assert.IsFalse( trigger.Playing, "disabling must stop the soundscape immediately" );
	}

	//SoundscapeTrigger's shape, volume and mixer configuration survives the component round
	//trip into a new scene. The MixerHandle is written as an object with a lower-cased name.
	[TestMethod]
	public void SoundscapeTriggerRoundTrip()
	{
		JsonObject json;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			var trigger = go.Components.Create<SoundscapeTrigger>();
			trigger.Type = SoundscapeTrigger.TriggerType.Box;
			trigger.BoxSize = new Vector3( 10, 20, 30 );
			trigger.Radius = 250.0f;
			trigger.Volume = 0.5f;
			trigger.StayActiveOnExit = false;
			trigger.TargetMixer = new MixerHandle { Name = "music" };

			json = (JsonObject)trigger.Serialize();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var go = sceneB.CreateObject();
			var copy = go.Components.Create<SoundscapeTrigger>( false );
			copy.DeserializeImmediately( json );

			Assert.AreEqual( SoundscapeTrigger.TriggerType.Box, copy.Type );
			Assert.AreEqual( new Vector3( 10, 20, 30 ), copy.BoxSize );
			Assert.AreEqual( 250.0f, copy.Radius );
			Assert.AreEqual( 0.5f, copy.Volume );
			Assert.IsFalse( copy.StayActiveOnExit );
			Assert.AreEqual( "music", copy.TargetMixer.Name );
		}
	}

	//DspVolume defaults match source and VolumeSystem finds it by position (or anywhere if infinite)
	[TestMethod]
	public void DspVolumeDefaultsAndVolumeLookup()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var dsp = go.Components.Create<DspVolume>();

		Assert.AreEqual( "Game", dsp.TargetMixer.Name );
		Assert.AreEqual( 0, dsp.Priority );
		Assert.IsNull( dsp.Dsp.Name );
		Assert.AreEqual( SceneVolume.VolumeTypes.Box, dsp.SceneVolume.Type );
		Assert.AreEqual( BBox.FromPositionAndSize( 0, 2.54f ), dsp.SceneVolume.Box );
		Assert.IsFalse( dsp.IsInfinite );

		Assert.AreSame( dsp, scene.Volumes.FindSingle<DspVolume>( Vector3.Zero ) );
		Assert.IsNull( scene.Volumes.FindSingle<DspVolume>( new Vector3( 500, 0, 0 ) ) );

		dsp.SceneVolume = new SceneVolume { Type = SceneVolume.VolumeTypes.Infinite };
		Assert.IsTrue( dsp.IsInfinite );
		Assert.AreSame( dsp, scene.Volumes.FindSingle<DspVolume>( new Vector3( 99999, 0, 0 ) ) );

		for ( int i = 0; i < 10; i++ ) scene.GameTick();

		Assert.IsFalse( DspVolumeGameSystem.IsActive );
	}

	//DspVolume's preset, mixer, priority and scene volume shape survive the component round
	//trip into a new scene. The DspPresetHandle serializes as a plain lower-cased string.
	[TestMethod]
	public void DspVolumeRoundTrip()
	{
		JsonObject json;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			var dsp = go.Components.Create<DspVolume>();
			dsp.Priority = 5;
			dsp.Dsp = "underwater";
			dsp.TargetMixer = new MixerHandle { Name = "music" };
			dsp.SceneVolume = new SceneVolume { Type = SceneVolume.VolumeTypes.Sphere, Sphere = new Sphere( 0, 64 ) };

			json = (JsonObject)dsp.Serialize();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var go = sceneB.CreateObject();
			var copy = go.Components.Create<DspVolume>( false );
			copy.DeserializeImmediately( json );

			Assert.AreEqual( 5, copy.Priority );
			Assert.AreEqual( "underwater", copy.Dsp.Name );
			Assert.AreEqual( "music", copy.TargetMixer.Name );
			Assert.AreEqual( SceneVolume.VolumeTypes.Sphere, copy.SceneVolume.Type );
			Assert.AreEqual( 64.0f, copy.SceneVolume.Sphere.Radius );
		}
	}

	//NavMeshLink only stores managed link data when the navmesh is disabled (the default): its world position accessors are plain
	//transform math around the owning GameObject, the on-navmesh positions stay unset headless, and toggling plus ticking is safe.
	[TestMethod]
	public void NavMeshLinkWorldPositions()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		go.WorldPosition = new Vector3( 100, 0, 0 );

		var link = go.Components.Create<NavMeshLink>();

		Assert.IsTrue( link.IsBiDirectional );

		//Forty centimetres. It was sixteen, a value meant for
		//inches; here a unit is a metre, so sixteen of them is a link that reaches across a building.
		Assert.AreEqual( 0.4f, link.ConnectionRadius, 0.0001f );

		link.LocalEndPosition = new Vector3( 0, 0, 50 );
		Assert.AreEqual( new Vector3( 100, 0, 50 ), link.WorldEndPosition );

		link.WorldStartPosition = new Vector3( 90, 0, 0 );
		Assert.AreEqual( new Vector3( -10, 0, 0 ), link.LocalStartPosition );

		Assert.IsFalse( link.WorldStartPositionOnNavmesh.HasValue );
		Assert.IsFalse( link.WorldEndPositionOnNavmesh.HasValue );

		link.Enabled = false;
		link.Enabled = true;

		for ( int i = 0; i < 10; i++ ) scene.GameTick();

		Assert.IsTrue( link.IsValid );
	}

	//NavMeshLink's positions, direction flag and connection radius - a mix of [Property]
	//properties and public fields - survive the component round trip into a new scene.
	[TestMethod]
	public void NavMeshLinkRoundTrip()
	{
		JsonObject json;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			var link = go.Components.Create<NavMeshLink>();
			link.LocalStartPosition = new Vector3( 1, 2, 3 );
			link.LocalEndPosition = new Vector3( 4, 5, 6 );
			link.IsBiDirectional = false;
			link.ConnectionRadius = 32.0f;

			json = (JsonObject)link.Serialize();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var go = sceneB.CreateObject();
			var copy = go.Components.Create<NavMeshLink>( false );
			copy.DeserializeImmediately( json );

			Assert.AreEqual( new Vector3( 1, 2, 3 ), copy.LocalStartPosition );
			Assert.AreEqual( new Vector3( 4, 5, 6 ), copy.LocalEndPosition );
			Assert.IsFalse( copy.IsBiDirectional );
			Assert.AreEqual( 32.0f, copy.ConnectionRadius );
		}
	}

	//NavMeshArea is pure managed state while the scene navmesh is disabled: IsBlocker defaults to true, toggling it while active (which adds/removes
	//the managed area data), moving the object and disabling/enabling are all safe under ticks, and the blocker flag plus volume survive a round trip.
	[TestMethod]
	public void NavMeshAreaToggleAndRoundTrip()
	{
		JsonObject json;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			var area = go.Components.Create<NavMeshArea>();

			Assert.IsTrue( area.IsBlocker );
			Assert.IsNull( area.Area );

			//Not a blocker and no area definition - removes the managed data
			area.IsBlocker = false;
			area.IsBlocker = true;

			//Transform changes re-sync the area data
			go.WorldPosition = new Vector3( 10, 0, 0 );

			area.Enabled = false;
			area.Enabled = true;

			for ( int i = 0; i < 10; i++ ) sceneA.GameTick();

			Assert.IsTrue( area.IsValid );

			area.IsBlocker = false;
			area.SceneVolume = new SceneVolume { Type = SceneVolume.VolumeTypes.Sphere, Sphere = new Sphere( 0, 32 ) };

			json = (JsonObject)area.Serialize();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var go = sceneB.CreateObject();
			var copy = go.Components.Create<NavMeshArea>( false );
			copy.DeserializeImmediately( json );

			Assert.IsFalse( copy.IsBlocker );
			Assert.AreEqual( SceneVolume.VolumeTypes.Sphere, copy.SceneVolume.Type );
			Assert.AreEqual( 32.0f, copy.SceneVolume.Sphere.Radius );
		}
	}

	//VolumetricFogController carries no [Property] members at all - its GlobalScale defaults to 1 but is
	//deliberately not part of the serialized component, and having it enabled in a ticking scene is inert.
	[TestMethod]
	public void VolumetricFogControllerStateNotSerialized()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var controller = go.Components.Create<VolumetricFogController>();

		Assert.AreEqual( 1.0f, controller.GlobalScale );
		Assert.IsNull( controller.BakedFogTexture );

		controller.GlobalScale = 2.0f;

		var json = (JsonObject)controller.Serialize();
		Assert.IsFalse( json.ContainsKey( "GlobalScale" ) );
		Assert.IsFalse( json.ContainsKey( "BakedFogTexture" ) );

		for ( int i = 0; i < 10; i++ ) scene.GameTick();

		controller.Enabled = false;
		controller.Enabled = true;

		Assert.IsTrue( controller.IsValid );
	}

	//Without an active VR system both update paths of VRTrackedObject early out, so a tracked object never moves
	//its GameObject when ticked headless, and its pose configuration survives a round trip into a new scene.
	[TestMethod]
	public void VrTrackedObjectInertWithoutVr()
	{
		Assert.IsFalse( Game.IsRunningInVR );

		JsonObject json;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			go.WorldPosition = new Vector3( 5, 6, 7 );

			var tracked = go.Components.Create<VRTrackedObject>();

			Assert.AreEqual( VRTrackedObject.PoseSources.Head, tracked.PoseSource );
			Assert.AreEqual( VRTrackedObject.PoseTypes.Grip, tracked.PoseType );
			Assert.AreEqual( VRTrackedObject.TrackingTypes.All, tracked.TrackingType );
			Assert.IsFalse( tracked.UseRelativeTransform );

			for ( int i = 0; i < 30; i++ ) sceneA.GameTick();

			Assert.AreEqual( new Vector3( 5, 6, 7 ), go.WorldPosition );

			tracked.PoseSource = VRTrackedObject.PoseSources.RightHand;
			tracked.PoseType = VRTrackedObject.PoseTypes.Aim;
			tracked.TrackingType = VRTrackedObject.TrackingTypes.Rotation;
			tracked.UseRelativeTransform = true;

			json = (JsonObject)tracked.Serialize();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var go = sceneB.CreateObject();
			var copy = go.Components.Create<VRTrackedObject>( false );
			copy.DeserializeImmediately( json );

			Assert.AreEqual( VRTrackedObject.PoseSources.RightHand, copy.PoseSource );
			Assert.AreEqual( VRTrackedObject.PoseTypes.Aim, copy.PoseType );
			Assert.AreEqual( VRTrackedObject.TrackingTypes.Rotation, copy.TrackingType );
			Assert.IsTrue( copy.UseRelativeTransform );
		}
	}

	//VRAnchor's OnUpdate/OnPreRender both early out when VR isn't running, so an enabled anchor
	//survives bounded ticks and enable/disable cycles without touching any VR or render state.
	[TestMethod]
	public void VrAnchorTicksInertWithoutVr()
	{
		var scene = new Scene();
		using var sceneScope = scene.Push();

		var go = scene.CreateObject();
		var anchor = go.Components.Create<VRAnchor>();

		for ( int i = 0; i < 30; i++ ) scene.GameTick();

		Assert.IsTrue( anchor.IsValid );
		Assert.IsTrue( anchor.Active );

		anchor.Enabled = false;
		anchor.Enabled = true;

		for ( int i = 0; i < 10; i++ ) scene.GameTick();

		Assert.IsTrue( anchor.Active );
	}

	//GradientFog stays disabled here; checks defaults, lookups and a round trip that stays disabled
	[TestMethod]
	public void GradientFogDisabledStateAndRoundTrip()
	{
		JsonObject json;

		var sceneA = new Scene();
		using ( sceneA.Push() )
		{
			var go = sceneA.CreateObject();
			var fog = go.Components.Create<GradientFog>( false );

			Assert.AreEqual( Color.White, fog.Color );
			Assert.AreEqual( 2.54f, fog.Height );
			Assert.AreEqual( 1.0f, fog.VerticalFalloffExponent );
			Assert.AreEqual( 0.0f, fog.StartDistance );
			Assert.AreEqual( 26.0f, fog.EndDistance );
			Assert.AreEqual( 1.0f, fog.FalloffExponent );

			Assert.AreSame( fog, go.Components.Get<GradientFog>( FindMode.EverythingInSelf ) );
			Assert.AreEqual( 0, sceneA.GetAllComponents<GradientFog>().Count(), "disabled components must not appear in the enabled-only scene index" );

			fog.Color = new Color( 0.5f, 0.25f, 1.0f, 0.5f );
			fog.Height = 250.0f;
			fog.StartDistance = 10.0f;
			fog.EndDistance = 2048.0f;
			fog.FalloffExponent = 2.0f;
			fog.VerticalFalloffExponent = 0.5f;

			json = (JsonObject)fog.Serialize();
		}

		var sceneB = new Scene();
		using ( sceneB.Push() )
		{
			var go = sceneB.CreateObject();
			var copy = go.Components.Create<GradientFog>( false );
			copy.DeserializeImmediately( json );

			Assert.IsFalse( copy.Enabled, "the serialized fog was disabled, the copy must stay disabled" );
			Assert.AreEqual( new Color( 0.5f, 0.25f, 1.0f, 0.5f ), copy.Color );
			Assert.AreEqual( 250.0f, copy.Height );
			Assert.AreEqual( 10.0f, copy.StartDistance );
			Assert.AreEqual( 2048.0f, copy.EndDistance );
			Assert.AreEqual( 2.0f, copy.FalloffExponent );
			Assert.AreEqual( 0.5f, copy.VerticalFalloffExponent );
		}
	}
}

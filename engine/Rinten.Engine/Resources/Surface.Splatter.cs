namespace Rinten;

public partial class Surface
{
	/// <summary>Decals drawn behind a hit on this surface, traced on along the shot onto whatever is in reach.</summary>
	public struct SplatterData
	{
		/// <summary>Decals drawn as splatter, one picked at random each time.</summary>
		public List<DecalDefinition> Decals { get; set; }

		/// <summary>How far past the hit to look for something to splatter onto, in metres.</summary>
		[DefaultValue( 2.44f )]
		public float Distance { get; set; } = 2.44f;

		/// <summary>How large these decals will be?</summary>
		public RangedFloat Scale { get; set; } = 1.0f;

		/// <summary>Seconds the height coverage takes to grow in; 0 shows it at once. Needs a decal heightmap.</summary>
		[DefaultValue( 0.25f )]
		public float FadeIn { get; set; } = 0.25f;

		/// <summary>Softness of height coverage mask</summary>
		[DefaultValue( 0.33f ), Range( 0.0f, 0.5f )]
		public float CoverageRange { get; set; } = 0.33f;

		public SplatterData() { }
	}

	/// <summary>Decals drawn behind hits on this surface - see PlaceSplatter.</summary>
	[InlineEditor, Title( "Splatter" )]
	public SplatterData Splatter { get; set; } = new();

	/// <summary>Traces the splatter on from the hit along the shot and puts a decal where it lands.</summary>
	public GameObject PlaceSplatter( Vector3 position, Vector3 direction, GameObject hitObject = null )
	{
		if ( Application.IsDedicatedServer || direction.IsNearlyZero() )
			return null;

		var splatter = HasSplatter ? Splatter : GetBaseSurface()?.Splatter ?? default;
		return PlaceSplatter( splatter, position, direction, hitObject );
	}

	bool HasSplatter => Splatter.Decals is { Count: > 0 };

	//This surface's splatter, or its base surface's when it has no decals
	internal SplatterData GetSplatter( Surface baseSurface ) => HasSplatter ? Splatter : baseSurface?.Splatter ?? default;

	internal static GameObject PlaceSplatter( in SplatterData splatter, Vector3 position, Vector3 direction, GameObject hitObject )
	{
		if ( Application.IsDedicatedServer || direction.IsNearlyZero() )
			return null;

		if ( splatter.Decals is not { Count: > 0 } || splatter.Distance <= 0 )
			return null;

		var scene = hitObject.IsValid() ? hitObject.Scene : Game.ActiveScene;
		if ( !scene.IsValid() )
			return null;

		var definition = Random.Shared.FromList( splatter.Decals );
		if ( definition is null )
			return null;

		direction = direction.Normal;

		var trace = scene.Trace.Ray( position, position + direction * splatter.Distance );
		var tr = TraceBehind( scene, trace, position, direction, hitObject );
		if ( !tr.Hit )
			return null;

		var go = scene.CreateObject( false );
		go.Name = "splatter";
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.WorldTransform = new Transform( tr.HitPosition, Rotation.LookAt( -tr.Normal ) );
		go.AddComponent<TemporaryEffect>();

		var decal = go.AddComponent<Decal>();
		decal.Decals = [definition];
		decal.Scale = splatter.Scale.GetValue();
		decal.CoverageFadeIn = splatter.FadeIn;
		decal.CoverageRange = splatter.CoverageRange;
		decal.Transient = true;

		BaseCombatWeapon.AttachToHit( go, tr.GameObject, tr.HitPosition );
		go.Enabled = true;

		return go;
	}

	//The first thing behind <paramref name="hitObject"/> along the trace.
	static SceneTraceResult TraceBehind( Scene scene, SceneTrace trace, Vector3 position, Vector3 direction, GameObject hitObject )
	{
		if ( !hitObject.IsValid() )
			return trace.Run();

		var ignore = hitObject.GetComponentInParent<ModelPhysics>( true )?.GameObject ?? hitObject;
		return trace.IgnoreGameObjectHierarchy( ignore ).Run();
	}
}

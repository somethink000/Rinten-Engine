namespace Rinten;

public partial class BaseCombatWeapon
{
	//Shot presentation: ShootEffects and ImpactEffect. ShootEffects plays the muzzle/fire presentation and relays it across
	//the network; ImpactEffect is a static utility that spawns the surface impact at a hit. Both are pure presentation - no gameplay, no damage.

	/// <summary>Play the shot's muzzle/fire presentation with no ranged hit - muzzle flash, fire sound, shell eject. For melee swings and projectile launches. See <see cref="ShootEffects(ShotEffect[])"/>.</summary>
	public void ShootEffects() => ShootEffects( [new ShotEffect()] );

	/// <summary>Play the presentation for a single resolved shot. See <see cref="ShootEffects(ShotEffect[])"/>.</summary>
	public void ShootEffects( ShotEffect shot ) => ShootEffects( [shot] );

	//Play AttackSound from the weapon - non-spatialized when the local player is the shooter, so your own gun doesn't pan around your head.
	protected void PlayAttackSound()
	{
		if ( Application.IsDedicatedServer ) return;
		if ( AttackSound is null ) return;

		var snd = GameObject.PlaySound( AttackSound );

		if ( snd.IsValid() && Network.IsOwner )
			snd.SpacialBlend = 0;
	}

	/// <summary>A resolved shot's presentation data - the muzzle <see cref="Origin"/> (for the tracer, null uses the muzzle transform) and the hit result (for the impact). Handed to <see cref="OnShootEffects(ShotEffect)"/>.</summary>
	public record struct ShotEffect(
		Vector3 HitPosition,
		bool Hit,
		Vector3 Normal,
		GameObject HitObject,
		Surface Surface,
		Vector3? Origin = null,
		bool NoEvents = false );

	/// <summary>Play the presentation for a volley - every pellet through <see cref="OnShootEffects(ShotEffect)"/>. Plays immediately for the shooter and relays through the host to everyone else as one message, so the shot is seen exactly once everywhere. Call from your attack; override <see cref="OnShootEffects(ShotEffect)"/> to define what plays.</summary>
	public void ShootEffects( ShotEffect[] volley )
	{
		if ( volley is null || volley.Length == 0 )
			return;

		PlayShotEffects( volley );

		if ( Networking.IsHost )
		{
			using ( Rpc.FilterExclude( c => c == Network.Owner || c == Connection.Local ) )
				BroadcastShotEffectVolley( volley );
		}
		else if ( !IsProxy )
		{
			//Owning client - the attack only runs here, so ask the host to relay for us.
			RelayShotEffectVolley( volley );
		}
	}

	void PlayShotEffects( ShotEffect[] volley )
	{
		foreach ( var shot in volley )
			OnShootEffects( shot );
	}

	//Runs on the host, where ShootEffects takes the play-and-broadcast branch.
	[Rpc.Host( NetFlags.OwnerOnly )]
	private void RelayShotEffectVolley( ShotEffect[] volley ) => ShootEffects( volley );

	[Rpc.Broadcast]
	private void BroadcastShotEffectVolley( ShotEffect[] volley )
	{
		if ( volley is null )
			return;

		PlayShotEffects( volley );
	}

	//Shot presentation: impacts, tracers, sound, gesture and model effects. Runs on every viewing peer
	protected virtual void OnShootEffects( ShotEffect shot )
	{
		if ( Application.IsDedicatedServer ) return;

		//Every pellet leaves its mark, even the NoEvents ones.
		OnShootImpact( shot );

		//A default ShotEffect (melee, projectile launch) has no trajectory - no tracer. Every pellet flies its own, so a shotgun blast is a fan of them.
		Vector3? hitPoint = shot.Hit || shot.HitPosition != default ? shot.HitPosition : null;
		if ( hitPoint.HasValue )
			WeaponModel?.DoTracerEffect( hitPoint.Value, shot.Origin );

		if ( shot.NoEvents ) return;

		PlayAttackSound();
		HolderRenderer?.Set( "b_attack", true );
		WeaponModel?.OnAttack( hitPoint, shot.Origin );
	}

	protected virtual void OnShootImpact( in ShotEffect shot ) => ImpactEffect( shot, GetShotDirection( shot ) );

	protected Vector3 GetShotDirection( in ShotEffect shot )
	{
		var owner = Owner;
		var from = shot.Origin
			?? (!IsProxy ? AimRay.Position
			: owner.IsValid() ? owner.EyeTransform.Position
			: GetMuzzleTransform().Position);

		return (shot.HitPosition - from).Normal;
	}

	/// <summary>Spawns the surface's impact sound and decal at a trace hit, parented to the hit object or bone</summary>
	public static void ImpactEffect( SceneTraceResult tr )
	{
		if ( !tr.Hit )
			return;

		ImpactEffect( tr.GameObject, tr.Surface, tr.HitPosition, tr.Normal, tr.Direction );
	}

	/// <inheritdoc cref="ImpactEffect(SceneTraceResult)"/>
	public static void ImpactEffect( in ShotEffect shot ) => ImpactEffect( shot, Vector3.Zero );

	/// <inheritdoc cref="ImpactEffect(SceneTraceResult)"/>
	/// <param name="shot">The resolved shot.</param>
	/// <param name="direction">The direction the shot travelled, for the surface's <see cref="Surface.Splatter"/>. Zero skips it.</param>
	public static void ImpactEffect( in ShotEffect shot, Vector3 direction )
	{
		if ( !shot.Hit )
			return;

		ImpactEffect( shot.HitObject, shot.Surface, shot.HitPosition, shot.Normal, direction );
	}

	static void ImpactEffect( GameObject hitObject, Surface surface, Vector3 position, Vector3 normal, Vector3 direction )
	{
		if ( Application.IsDedicatedServer )
			return;

		if ( !surface.IsValid() )
			return;

		var baseSurface = surface.GetBaseSurface();

		var sound = surface.SoundCollection.Bullet ?? baseSurface?.SoundCollection.Bullet;
		if ( sound.IsValid() )
			Sound.Play( sound, position );

		ImpactPrefab( hitObject, surface, position, normal, direction );
	}

	/// <summary>Spawn just the surface impact prefabs (decal/particles) at a hit, stuck to what was hit - no sound. For attacks that want the visual but their own impact audio (a melee thunk instead of a ricochet).</summary>
	public static void ImpactPrefab( GameObject hitObject, Surface surface, Vector3 position, Vector3 normal )
	{
		ImpactPrefab( hitObject, surface, position, normal, Vector3.Zero );
	}

	/// <inheritdoc cref="ImpactPrefab(GameObject, Surface, Vector3, Vector3)"/>
	/// <param name="hitObject">What was hit.</param>
	/// <param name="surface">The surface that was hit.</param>
	/// <param name="position">Where the hit landed.</param>
	/// <param name="normal">The surface normal at the hit.</param>
	/// <param name="direction">The direction the hit travelled, for the surface's <see cref="Surface.Splatter"/>. Zero skips it.</param>
	public static void ImpactPrefab( GameObject hitObject, Surface surface, Vector3 position, Vector3 normal, Vector3 direction )
	{
		if ( Application.IsDedicatedServer )
			return;

		if ( !surface.IsValid() )
			return;

		var baseSurface = surface.GetBaseSurface();
		var impactPrefab = surface.PrefabCollection.BulletImpact ?? baseSurface?.PrefabCollection.BulletImpact;
		var decalPrefab = surface.PrefabCollection.BulletImpactDecal ?? baseSurface?.PrefabCollection.BulletImpactDecal;

		SpawnImpactPrefab( impactPrefab, hitObject, position, normal, true );

		if ( hitObject.IsValid() )
		{
			SpawnImpactPrefab( decalPrefab, hitObject, position, normal, false );
		}

		Surface.PlaceSplatter( surface.GetSplatter( baseSurface ), position, direction, hitObject );
	}

	static void SpawnImpactPrefab( GameObject prefab, GameObject hitObject, Vector3 position, Vector3 normal, bool becomeOrphan )
	{
		if ( prefab is null )
			return;

		var impact = prefab.Clone( new CloneConfig
		{
			Transform = new Transform( position, Rotation.LookAt( -normal, Vector3.Random ) ),
			StartEnabled = true
		} );

		//Each peer spawns its own - never networked.
		impact.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

		if ( impact.GetComponent<TemporaryEffect>() is { } temporaryEffect )
		{
			temporaryEffect.BecomeOrphan = becomeOrphan;
		}

		AttachToHit( impact, hitObject, position );
	}

	/// <summary>Stick an impact to what was hit, and to the nearest bone of a skinned model so it follows animation.</summary>
	internal static void AttachToHit( GameObject impact, GameObject hitObject, Vector3 position )
	{
		//The hit object can be gone by the time the impact plays - this shot killed it - and the
		//Impact still belongs where it landed, anchored to the world.
		if ( !hitObject.IsValid() )
			return;

		var skinned = hitObject.GetComponentInChildren<SkinnedModelRenderer>();
		if ( skinned is { CreateBoneObjects: true } )
		{
			var closest = skinned.GetClosestBone( position );
			if ( closest is not null && skinned.GetBoneObject( closest ) is { } boneObject )
			{
				impact.SetParent( boneObject, true );
				return;
			}
		}

		impact.SetParent( hitObject, true );
	}
}

namespace Rinten;

public partial class BaseCombatWeapon
{
	/// <summary>Traces a bullet and, on the host only, applies damage and impulse; stateless utility</summary>
	public static SceneTraceResult ShootBullet( Scene scene, Ray ray, float distance, float radius, float damage, float force, GameObject attacker, GameObject weapon, GameObject ignore = null, TagSet tags = null )
	{
		var tr = scene.Trace.Ray( ray, distance )
			.IgnoreGameObjectHierarchy( ignore ?? attacker )
			.Radius( radius )
			.UseHitboxes()
			.Run();

		return ShootBullet( tr, damage, force, attacker, weapon, tags );

	}

	/// <summary>Applies damage and impulse for a trace you ran yourself, host only; returns tr unchanged</summary>
	public static SceneTraceResult ShootBullet( SceneTraceResult tr, float damage, float force, GameObject attacker, GameObject weapon, TagSet tags = null, bool hitboxTags = true )
	{
		//Damage and physics are host authoritative - clients only get the trace back for effects.
		if ( !Networking.IsHost )
			return tr;

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return tr;

		tags = MergeHitboxTags( tr, tags, hitboxTags );

		ApplyBulletDamage( tr.GameObject, tr.StartPosition, tr.HitPosition, tr.Direction, damage, force, attacker, weapon, tags, tr.Hitbox, tr.Shape );

		return tr;
	}

	//The hit hitbox's tags (e.g. "head") merged with tags into a fresh set, so a
	//caller-shared TagSet isn't polluted with one hit's tags. Off or no hitbox returns tags.
	static TagSet MergeHitboxTags( in SceneTraceResult tr, TagSet tags, bool hitboxTags )
	{
		if ( !hitboxTags || tr.Hitbox?.Tags is null )
			return tags;

		var merged = new TagSet();
		if ( tags is not null ) merged.Add( tags );
		merged.Add( tr.Hitbox.Tags );
		return merged;
	}

	//The damage + impulse core shared by trace hits and client hit claims (BaseCombatWeapon.HitClaims).
	static void ApplyBulletDamage( GameObject hitObject, Vector3 origin, Vector3 position, Vector3 direction, float damage, float force, GameObject attacker, GameObject weapon, TagSet tags, Hitbox hitbox = null, PhysicsShape shape = null )
	{
		if ( !hitObject.IsValid() )
			return;

		if ( hitObject.GetComponentInParent<IDamageable>() is { } damageable )
		{
			damageable.OnDamage( new DamageInfo( damage, attacker, weapon )
			{
				Position = position,
				Origin = origin,
				Hitbox = hitbox,
				Shape = shape,
				Tags = tags ?? new(),
			} );
		}

		if ( hitObject.GetComponentInParent<Rigidbody>() is { } body )
			body.ApplyImpulseAt( position, direction * force );
	}

	/// <summary>Fires a volley of bullets - one trace per pellet, each randomly deflected within <paramref name="spread"/>, resolved through <see cref="ShootBullet(SceneTraceResult, float, float, GameObject, GameObject, TagSet, bool)"/>. A pure utility like ShootBullet - damage and impulse apply on the host only. Returns every pellet's trace so the caller can drive tracers and impacts.</summary>
	public static SceneTraceResult[] ShootBullets( Scene scene, Ray ray, int count, Vector2 spread, float distance, float radius, float damage, float force, GameObject attacker, GameObject weapon, GameObject ignore = null, TagSet tags = null )
	{
		count = Math.Max( count, 0 );
		var results = new SceneTraceResult[count];

		for ( var i = 0; i < count; i++ )
		{
			var pellet = ray with { Forward = ray.Forward.WithAimCone( spread.x, spread.y ) };
			results[i] = ShootBullet( scene, pellet, distance, radius, damage, force, attacker, weapon, ignore, tags );
		}

		return results;
	}

	//The GameObject credited for this weapon's damage - the holding player, or the weapon
	//itself when unheld. Override to credit someone else (e.g. the driver of a mounted weapon).
	protected virtual GameObject Attacker => Owner?.GameObject ?? GameObject;

	/// <summary>The impulse one of this weapon's hits applies to <paramref name="target"/>, decided on the host.</summary>
	protected virtual float GetHitForce( GameObject target, float force ) => force;

	float HitForce( GameObject target, float force )
	{
		if ( !target.IsValid() )
			return force;

		var result = GetHitForce( target, force );
		return float.IsFinite( result ) ? Math.Max( result, 0f ) : 0f;
	}

	protected virtual SceneTrace BulletTrace( Ray ray, float distance, float radius )
	{
		return Scene.Trace.Ray( ray, distance )
			.IgnoreGameObjectHierarchy( Attacker )
			.Radius( radius )
			.UseHitboxes();
	}

	//Resolves a trace for this weapon: host applies damage directly, the owning client sends a claim
	protected SceneTraceResult ShootBullet( SceneTraceResult tr, float damage, float force, TagSet tags = null, bool hitboxTags = true )
	{
		if ( Networking.IsHost )
			return ShootBullet( tr, damage, HitForce( tr.GameObject, force ), Attacker, GameObject, tags, hitboxTags );

		//Only the owner can claim hits with this weapon.
		if ( IsProxy )
			return tr;

		if ( TryBuildPelletClaim( tr, tags, hitboxTags, out var pellet ) )
			SendShotClaim( damage, force, new[] { pellet } );

		return tr;
	}

	//Fires a bullet from this weapon's BaseCombatWeapon.AimRay - traced with BulletTrace, resolved through ShootBullet.
	protected SceneTraceResult ShootBullet( float distance, float radius, float damage, float force, TagSet tags = null )
	{
		var tr = BulletTrace( AimRay, distance, radius ).Run();
		return ShootBullet( tr, damage, force, tags );
	}

	//Fires count pellets from AimRay within spread; the client claims the whole volley in one message
	protected SceneTraceResult[] ShootBullets( int count, Vector2 spread, float distance, float radius, float damage, float force, TagSet tags = null )
	{
		count = Math.Max( count, 0 );
		var results = new SceneTraceResult[count];
		var ray = AimRay;

		if ( Networking.IsHost )
		{
			for ( var i = 0; i < count; i++ )
			{
				var pellet = ray with { Forward = ray.Forward.WithAimCone( spread.x, spread.y ) };
				var tr = BulletTrace( pellet, distance, radius ).Run();
				results[i] = ShootBullet( tr, damage, HitForce( tr.GameObject, force ), Attacker, GameObject, tags );
			}

			return results;
		}

		//Owning client - trace every pellet, then claim the volley to the host as one message.
		List<PelletClaim> claims = null;

		for ( var i = 0; i < count; i++ )
		{
			var pellet = ray with { Forward = ray.Forward.WithAimCone( spread.x, spread.y ) };
			var tr = BulletTrace( pellet, distance, radius ).Run();
			results[i] = tr;

			if ( !IsProxy && TryBuildPelletClaim( tr, tags, hitboxTags: true, out var claim ) )
				(claims ??= new()).Add( claim );
		}

		if ( claims is not null )
			SendShotClaim( damage, force, claims.ToArray() );

		return results;
	}
}

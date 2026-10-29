namespace Rinten;

public partial class BaseCombatWeapon
{
	//Client-authoritative hits (Rust-style): owner claims each trigger pull, host validates and applies

	/// <summary>One pellet's hit inside a <see cref="ShotClaim"/> - where it was fired from, what it hit and where. Pellets that missed, or hit nothing that can take damage or a push, aren't claimed.</summary>
	public record struct PelletClaim(
		Vector3 Origin,
		Vector3 Position,
		Vector3 Direction,
		GameObject HitObject,
		TagSet Tags );

	/// <summary>One trigger pull's client-reported hits; hitgroups arrive as pellet tags, Hitbox is null</summary>
	public record struct ShotClaim(
		int Sequence,
		float Damage,
		float Force,
		PelletClaim[] Pellets );

	//Numbers this weapon's claims so the host can tell shots apart - rate and duplicate checks in a validator key on it.
	int _shotSequence;

	//Builds the claim for one traced pellet - false when the hit isn't worth claiming: it missed, or hit nothing that
	//can take damage or a push. Component presence replicates, so checking here saves the host a pointless message.
	bool TryBuildPelletClaim( in SceneTraceResult tr, TagSet tags, bool hitboxTags, out PelletClaim pellet )
	{
		pellet = default;

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		if ( tr.GameObject.GetComponentInParent<IDamageable>() is null && tr.GameObject.GetComponentInParent<Rigidbody>() is null )
			return false;

		//Resolve the hitbox tags here - the host can't reconstruct the hitbox from a claim.
		pellet = new PelletClaim( tr.StartPosition, tr.HitPosition, tr.Direction, tr.GameObject, MergeHitboxTags( tr, tags, hitboxTags ) ?? new TagSet() );
		return true;
	}

	void SendShotClaim( float damage, float force, PelletClaim[] pellets )
	{
		ClaimShot( new ShotClaim( _shotSequence++, damage, force, pellets ) );
	}

	[Rpc.Host( NetFlags.OwnerOnly )]
	private void ClaimShot( ShotClaim claim )
	{
		//Basic wire validity - not anticheat, just hygiene no game should have to opt into. A malformed float must never reach the
		//damage or physics systems (a NaN impulse corrupts the body for everyone), and negative damage would heal through IDamageable.
		if ( claim.Pellets is null || claim.Pellets.Length == 0 )
			return;

		if ( !float.IsFinite( claim.Damage ) || claim.Damage < 0 )
			return;

		if ( !float.IsFinite( claim.Force ) || claim.Force < 0 )
			return;

		if ( !OnValidateShotClaim( claim ) )
			return;

		foreach ( var pellet in claim.Pellets )
		{
			if ( !pellet.HitObject.IsValid() )
				continue;

			if ( pellet.Origin.IsNaN || pellet.Origin.IsInfinity )
				continue;

			if ( pellet.Position.IsNaN || pellet.Position.IsInfinity )
				continue;

			if ( pellet.Direction.IsNaN || pellet.Direction.IsInfinity )
				continue;

			//Normalized so a huge-but-finite claimed direction can't multiply the impulse past any
			//Force clamp a validator applies.
			ApplyBulletDamage( pellet.HitObject, pellet.Origin, pellet.Position, pellet.Direction.Normal, claim.Damage, HitForce( pellet.HitObject, claim.Force ), Attacker, GameObject, pellet.Tags );
		}
	}

	//Host hook to reject impossible shot claims (anticheat); base accepts everything
	protected virtual bool OnValidateShotClaim( in ShotClaim claim ) => true;
}

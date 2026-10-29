namespace Rinten;

public partial class BaseCombatWeapon
{
	//Primary/secondary attacks; the controller fires and claims hits to the host

	/// <summary>Seconds between primary shots - the fire rate.</summary>
	[Property, Feature( "Shooting" )] public float PrimaryDelay { get; set; } = 0.1f;

	/// <summary>Seconds between secondary shots.</summary>
	[Property, Feature( "Shooting" )] public float SecondaryDelay { get; set; } = 0.5f;

	/// <summary>When true primary fire keeps firing while the button is held; when false it fires once per press.</summary>
	[Property, Feature( "Shooting" )] public bool PrimaryAutomatic { get; set; } = true;

	/// <summary>When true secondary fire is full-auto while held.</summary>
	[Property, Feature( "Shooting" )] public bool SecondaryAutomatic { get; set; } = false;

	/// <summary>Seconds after deploying (switching to) this weapon before it can fire.</summary>
	[Property, Feature( "Shooting" )] public float DeployTime { get; set; } = 0.5f;

	/// <summary>Sound played when the weapon attacks - the gunshot, swing, launch. Played by the base <see cref="OnShootEffects"/>, so it reaches every peer that sees the shot.</summary>
	[Property, Feature( "Shooting" )] public SoundEvent AttackSound { get; set; }

	/// <summary>Sound played when the trigger is pulled with no ammo.</summary>
	[Property, Feature( "Shooting" )] public SoundEvent DryFireSound { get; set; }

	//Time until primary fire is off cooldown. Not synced - the firing side gates itself.
	//The host doesn't enforce the rate; a hardened game rate-checks incoming shot claims instead (see OnValidateShotClaim).
	protected TimeUntil NextPrimaryFire { get; set; }

	//Time until secondary fire is off cooldown.
	protected TimeUntil NextSecondaryFire { get; set; }

	/// <summary>Put primary fire on cooldown for <paramref name="delay"/> seconds.</summary>
	public void SetNextPrimaryFire( float delay ) => NextPrimaryFire = delay;

	/// <summary>Put secondary fire on cooldown for <paramref name="delay"/> seconds.</summary>
	public void SetNextSecondaryFire( float delay ) => NextSecondaryFire = delay;

	/// <summary>Put both triggers on cooldown for <paramref name="delay"/> seconds - for weapons where a shot blocks primary and secondary alike.</summary>
	public void SetNextFire( float delay )
	{
		SetNextPrimaryFire( delay );
		SetNextSecondaryFire( delay );
	}

	//Per-frame weapon logic, pumped by the inventory on the owning client. Reads input and drives the
	//fire/reload loop. Override the individual hooks (PrimaryAttack, Think, ...) rather than this.
	protected override void OnControl()
	{
		Think();

		if ( WantsReload() && CanReload() )
			Reload();

		//Pulling a trigger cancels a cancellable reload, as long as that trigger has a round to fire.
		if ( IsReloading && CanCancelReload && ((WantsPrimaryAttack() && HasPrimaryAmmo()) || (WantsSecondaryAttack() && HasSecondaryAmmo())) )
			CancelReload();

		if ( WantsPrimaryAttack() )
		{
			if ( CanPrimaryAttack() )
				FirePrimary();
			else if ( NextPrimaryFire <= 0 && !IsReloading && !HasPrimaryAmmo() )
				DryFire();
		}

		if ( WantsSecondaryAttack() )
		{
			if ( CanSecondaryAttack() )
				FireSecondary();
			else if ( NextSecondaryFire <= 0 && !IsReloading && !HasSecondaryAmmo() )
				DryFire();
		}
	}

	//Runs every frame before attack handling - continuous logic (spin-up, charge, ...). Base does nothing.
	protected virtual void Think() { }

	//Is the player asking to fire primary this frame? Held when automatic, pressed otherwise.
	protected virtual bool WantsPrimaryAttack() => PrimaryAutomatic ? Input.Down( "attack1" ) : Input.Pressed( "attack1" );

	//Is the player asking to fire secondary this frame?
	protected virtual bool WantsSecondaryAttack() => SecondaryAutomatic ? Input.Down( "attack2" ) : Input.Pressed( "attack2" );

	//Is the player asking to reload this frame?
	protected virtual bool WantsReload() => Input.Pressed( "reload" );

	/// <summary>Can primary fire right now? Base checks the cooldown, that we're not mid-reload, and that there's ammo. A pure check, safe to call from HUDs - the trigger dry-fires on empty, not this. Override to add conditions.</summary>
	public virtual bool CanPrimaryAttack()
	{
		if ( NextPrimaryFire > 0 ) return false;
		if ( IsReloading ) return false;
		if ( !HasPrimaryAmmo() ) return false;

		return true;
	}

	public virtual bool CanSecondaryAttack()
	{
		if ( NextSecondaryFire > 0 ) return false;
		if ( IsReloading ) return false;
		if ( !HasSecondaryAmmo() ) return false;

		return true;
	}

	/// <summary>Pull the primary trigger - fires if <see cref="CanPrimaryAttack"/> allows, putting it on cooldown. Returns whether it fired. The way to shoot from code (AI, turrets) - calling <see cref="PrimaryAttack"/> directly skips the fire rate. Runs on whoever controls the weapon; the attack doesn't re-run on the host.</summary>
	public bool FirePrimary()
	{
		if ( !CanPrimaryAttack() ) return false;

		SetNextPrimaryFire( PrimaryDelay );
		PrimaryAttack();
		return true;
	}

	public bool FireSecondary()
	{
		if ( !CanSecondaryAttack() ) return false;

		SetNextSecondaryFire( SecondaryDelay );
		SecondaryAttack();
		return true;
	}

	/// <summary>Default primary attack: spend a round, shoot the volley, play effects. Runs on the controller</summary>
	public virtual void PrimaryAttack()
	{
		//Unheld weapons (seats, NPCs, world) have no ammo pool to spend from.
		if ( IsHeld && !TakePrimaryAmmo() )
			return;

		var config = Ballistics;
		var pellets = ShootBullets( config.Pellets, CurrentSpread, config.Range, config.Radius, config.Damage, config.Force );

		TimeSinceShoot = 0;

		//The first pellet carries the muzzle/anim/sound events - every pellet still flies its
		//tracer and leaves its impact. The volley relays across the network as one message.
		var volley = new ShotEffect[pellets.Length];

		for ( var i = 0; i < pellets.Length; i++ )
		{
			var tr = pellets[i];
			volley[i] = new ShotEffect( tr.EndPosition, tr.Hit, tr.Normal, tr.GameObject, tr.Surface, NoEvents: i > 0 );
		}

		ShootEffects( volley );
	}

	/// <summary>Fire the secondary attack. Base does nothing - override it. Same contract as <see cref="PrimaryAttack"/>: runs once, on whoever controls the weapon.</summary>
	public virtual void SecondaryAttack() { }

	/// <summary>The player pulled the trigger with no ammo. Base plays <see cref="DryFireSound"/>, throttles both triggers so it doesn't spam, and starts a reload when <see cref="AutoReload"/> allows. Override to extend.</summary>
	public virtual void DryFire()
	{
		if ( DryFireSound is not null )
			GameObject.PlaySound( DryFireSound );

		WeaponModel?.OnDryFire();
		SetNextFire( 0.15f );

		if ( AutoReload && CanReload() )
			Reload();
	}
}

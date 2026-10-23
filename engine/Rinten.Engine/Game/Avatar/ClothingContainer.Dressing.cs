using Rinten.Engine;
using System.Threading;

namespace Rinten;

/// <summary>Holds a collection of clothing items. Won't let you add items that aren't compatible.</summary>
public partial class ClothingContainer
{
	/// <summary>Dresses a skinned model with an outfit.</summary>
	public Task ApplyAsync( SkinnedModelRenderer body, CancellationToken token )
	{
		if ( body.IsValid() )
			Apply( body );

		return Task.CompletedTask;
	}

	/// <summary>Dress a skinned model renderer with an outfit. Doesn't download missing clothing.</summary>
	public void Apply( SkinnedModelRenderer body )
	{
		using var SceneScope = body.Scene.Push();

		bool isHuman = DetermineHuman( body );

		//remove our outfit
		Reset( body );
		Normalize();

		//apply indicentals
		body.Set( "scale_height", Height.Remap( 0, 1, 0.8f, 1.2f, true ) );
		body.Set( "scale_heel", Clothing?.Select( x => x.Clothing?.HeelHeight ?? 0.0f ).DefaultIfEmpty( 0 ).Max() ?? 0.0f );

		//TODO - we should expose the render attributes, somehow, in a way accessible to editor inspector and serialization!
		body.Attributes.Set( "skin_age", Age );
		body.Attributes.Set( "skin_tint", Tint );

		//Clean the clothing. Remove any invalid items, any items with broken models any items that can't be worn with other items.
		List<ClothingContainer.ClothingEntry> set = Clothing?
														.Where( x => IsValidClothing( x, isHuman ) )
														.ToList() ?? new();

		TagSet tags = new();

		Material skinMaterial = default;
		Material eyesMaterial = default;

		//apply alternate human skin, if we have one
		if ( isHuman )
		{
			skinMaterial = set.Select( x => x.Clothing.HumanSkinMaterial ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => Material.Load( x ) ).FirstOrDefault();
			eyesMaterial = set.Select( x => x.Clothing.HumanEyesMaterial ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => Material.Load( x ) ).FirstOrDefault();

			tags.Add( "human" );

			var humanskin = set.Where( x => x.Clothing.HasHumanSkin ).FirstOrDefault();
			if ( humanskin is not null && Model.Load( humanskin.Clothing.HumanSkinModel ) is Model model && model.IsValid() )
			{
				body.Model = model;
				tags.Add( humanskin.Clothing.HumanSkinTags );

				body.BodyGroups = humanskin.Clothing.HumanSkinBodyGroups;
				body.MaterialGroup = humanskin.Clothing.HumanSkinMaterialGroup;
			}
			else
			{
				//Saying nothing rather than working the default out here: a model with no body groups in it
				//has a default mask of nought, and nought is not "the usual body" but "draw none of it".
				body.BodyGroups = ulong.MaxValue;
				body.MaterialGroup = "default";
			}
		}
		else
		{
			skinMaterial = set.Select( x => x.Clothing.SkinMaterial ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => Material.Load( x ) ).FirstOrDefault();
			eyesMaterial = set.Select( x => x.Clothing.EyesMaterial ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => Material.Load( x ) ).FirstOrDefault();
		}

		body.SetMaterialOverride( skinMaterial, "skin" );
		body.SetMaterialOverride( eyesMaterial, "eyes" );

		if ( isHuman )
		{
			EnsureHumanUnderwear( set, tags.Has( "female" ) );
		}

		//Create clothes models
		foreach ( var entry in set )
		{
			var c = entry.Clothing;

			var modelPath = c.GetModel( set.Select( x => x.Clothing ).Except( new[] { c } ), tags );

			if ( string.IsNullOrEmpty( modelPath ) || !string.IsNullOrEmpty( c.SkinMaterial ) )
				continue;

			var model = Model.Load( modelPath );
			if ( !model.IsValid() || model.IsError )
				continue;

			var go = new GameObject( false, $"Clothing - {c.ResourceName}" );
			go.Parent = body.GameObject;
			go.Tags.Add( "clothing" );

			var r = go.Components.Create<SkinnedModelRenderer>();
			r.Model = model;
			r.BoneMergeTarget = body;

			//TODO - we should expose the render attributes, somehow, in a way accessible to editor inspector and serialization!
			r.Attributes.Set( "skin_age", Age );
			r.Attributes.Set( "skin_tint", Tint );

			r.SetMaterialOverride( skinMaterial, "skin" );
			r.SetMaterialOverride( eyesMaterial, "eyes" );

			if ( !string.IsNullOrEmpty( c.MaterialGroup ) )
				r.MaterialGroup = c.MaterialGroup;

			if ( c.AllowTintSelect )
			{
				var tintValue = entry.Tint?.Clamp( 0, 1 ) ?? c.TintDefault;
				var tintColor = c.TintSelection.Evaluate( tintValue );
				r.Tint = tintColor;
			}

			go.Enabled = true;
		}

		//Set body groups
		foreach ( var (name, value) in GetBodyGroups( set.Select( x => x.Clothing ), body.Model ) )
		{
			if ( value == 0 ) continue;

			body.SetBodyGroup( name, value );
		}
	}

	/// <summary>Clear the outfit from this model, make it named</summary>
	public void Reset( SkinnedModelRenderer body )
	{
		//Start with defaults
		body.Set( "scale_height", 1.0f );
		body.MaterialGroup = "default";
		body.MaterialOverride = null;
		//Back to whatever the model shows on its own, which is what an undressed character is - not nought, which would undress it of its body as well.
		body.BodyGroups = ulong.MaxValue;

		//Remove old models
		foreach ( var children in body.GameObject.Children )
		{
			if ( children.Tags.Has( "clothing" ) )
			{
				children.Destroy();
			}
		}
	}

	//Default underwear paths, cached to avoid repeated allocations
	const string DefaultUnderwearPath = "models/citizen_clothes/underwear/y_front_pants/y_front_pants_white.clothing";
	const string DefaultBraPath = "models/citizen_clothes/underwear/bra/bra_white.clothing";
	static readonly Lazy<Rinten.Clothing> DefaultUnderwear = new( () => ResourceLibrary.Get<Rinten.Clothing>( DefaultUnderwearPath ) );
	static readonly Lazy<Rinten.Clothing> DefaultBra = new( () => ResourceLibrary.Get<Rinten.Clothing>( DefaultBraPath ) );

	static bool DetermineHuman( SkinnedModelRenderer b, bool defaultValue = false )
	{
		if ( b?.Model is null ) return defaultValue;

		var model = b.Model.BaseModel ?? b.Model;
		return !model.Name.Contains( "citizen.vmdl", StringComparison.OrdinalIgnoreCase );
	}

	static bool IsValidModel( string modelName )
	{
		if ( string.IsNullOrWhiteSpace( modelName ) )
			return false;

		var model = Model.Load( modelName );

		if ( !model.IsValid() ) return false;
		if ( model.IsError ) return false;

		return true;
	}

	static void EnsureHumanUnderwear( List<ClothingEntry> set, bool isFemale )
	{
		var hiddenParts = set.Select( x => x.Clothing.HideBody ).DefaultIfEmpty().Aggregate( ( a, b ) => a | b );

		//Don't add underwear if legs are hidden
		if ( !( ( hiddenParts & Rinten.Clothing.BodyGroups.Legs ) == Rinten.Clothing.BodyGroups.Legs ) )
		{
			bool hasUnderwear = set.Any( x => x.Clothing.Category is Rinten.Clothing.ClothingCategory.Underwear or Rinten.Clothing.ClothingCategory.Underpants );
			if ( !hasUnderwear )
				TryAddDefault( set, DefaultUnderwear.Value, isHuman: true );
		}

		//Don't add bra if chest is hidden
		if ( isFemale && !( ( hiddenParts & Rinten.Clothing.BodyGroups.Chest ) == Rinten.Clothing.BodyGroups.Chest ) && !set.Any( x => x.Clothing.Category == Rinten.Clothing.ClothingCategory.Bra ) )
			TryAddDefault( set, DefaultBra.Value, isHuman: true );
	}

	static void TryAddDefault( List<ClothingEntry> set, Rinten.Clothing clothing, bool isHuman )
	{
		if ( clothing is null ) return;

		var entry = new ClothingEntry( clothing );

		if ( !IsValidClothing( entry, isHuman ) ) return;
		if ( set.Any( x => !(x.Clothing?.CanBeWornWith( clothing ) ?? true) ) ) return;

		set.Add( entry );
	}

	static bool IsValidClothing( ClothingContainer.ClothingEntry e, bool targetIsHuman )
	{
		if ( e is null ) return false;
		if ( e.Clothing is null ) return false;
		if ( targetIsHuman && e.Clothing.HasHumanSkin ) return true;

		var model = e.Clothing.Model;

		if ( targetIsHuman )
		{
			model = e.Clothing.HumanAltModel;

			//If we have a citizen model, but not a human model, make clothing invalid
			if ( string.IsNullOrEmpty( model ) && !string.IsNullOrEmpty( e.Clothing.Model ) )
				return false;
		}

		if ( string.IsNullOrEmpty( model ) )
			return true;

		if ( !IsValidModel( model ) )
		{
			Log.Warning( $"Clothing model '{model}' in {e.Clothing} is invalid, removing" );
			return false;
		}

		return true;
	}
}

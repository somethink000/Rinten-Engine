namespace Rinten;

using Rinten.Engine;

//TODO 1. Move MaterialOverride to Materials, somehow. 2. Make SetMaterialOverride with attribute targets obsolete (need to fix avatar/skin stuff)

public partial class ModelRenderer : MaterialAccessor.ITarget
{

	[Property]
	public Material MaterialOverride
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;

			DrawChanged();
			UpdateObject();
		}
	}

	Dictionary<string, Material> taggedMaterialOverrides;

	/// <summary>Completely stop overriding materials</summary>
	public void ClearMaterialOverrides()
	{
		taggedMaterialOverrides = null;
		MaterialOverride = null;
		_sceneObject?.ClearMaterialOverride();
	}

	/// <summary>Set a material override for a material with a specific attribute set. For example, if you have a model with lots of different materials, but one of them has an attribute "skin" set to "1", then calling this with a material and "skin" will override only that material.</summary>
	public void SetMaterialOverride( Material material, string target )
	{
		if ( material is null && taggedMaterialOverrides is null )
			return;

		taggedMaterialOverrides ??= new Dictionary<string, Material>();

		if ( material is null )
		{
			taggedMaterialOverrides.Remove( target );
			_sceneObject?.SetMaterialOverride( null, target );
		}
		else
		{
			taggedMaterialOverrides[target] = material;
		}

		ApplyMaterialOverrides();
	}

	//Apply any saved material overrides to the scene object.
	void ApplyMaterialOverrides()
	{
		if ( !_sceneObject.IsValid() ) return;

		if ( taggedMaterialOverrides is not null )
		{
			foreach ( var o in taggedMaterialOverrides )
			{
				_sceneObject.SetMaterialOverride( o.Value, o.Key );
			}
		}

		_materialAccessor?.Apply();
	}

	MaterialAccessor _materialAccessor;

	/// <summary>Access to the materials</summary>
	[Property, Group( "Materials", StartFolded = true )]
	public MaterialAccessor Materials => _materialAccessor ??= new MaterialAccessor( this );

	int MaterialAccessor.ITarget.GetMaterialCount()
	{
		if ( !Model.IsValid() ) return 0;
		return Model.Materials.Length;
	}

	Material MaterialAccessor.ITarget.Get( int index )
	{
		if ( !Model.IsValid() ) return default;
		if ( index < 0 ) return default;
		if ( index >= Model.Materials.Length ) return default;

		return Model.Materials[index];
	}

	//Overrides by the model's material index (one a mesh), as the scene table reads them; null while there are none
	internal Dictionary<int, Material> SlotOverrides { get; private set; }

	void MaterialAccessor.ITarget.SetOverride( int index, Material material )
	{
		if ( material is null )
		{
			if ( SlotOverrides is null || !SlotOverrides.Remove( index ) ) return;
			if ( SlotOverrides.Count == 0 ) SlotOverrides = null;
		}
		else
		{
			SlotOverrides ??= new();
			if ( SlotOverrides.TryGetValue( index, out var had ) && had == material ) return;
			SlotOverrides[index] = material;
		}

		DrawChanged();
	}

	void MaterialAccessor.ITarget.ClearOverrides()
	{
		if ( SlotOverrides is null ) return;

		SlotOverrides = null;
		DrawChanged();
	}

}

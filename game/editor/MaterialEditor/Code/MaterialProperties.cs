using System;
using System.Collections.Generic;
using System.Linq;

namespace Editor.Materials;

/// <summary>What an edit to a material touched, for whoever is showing it to decide how much of the screen to build again.</summary>
public enum MaterialEditKind
{
	/// <summary>The shader: everything it declares is different now.</summary>
	Shader,

	/// <summary>A feature went on or off, which decides which blocks there are under the switches.</summary>
	Feature,

	/// <summary>A map slot was filled or emptied, which is what a feature nobody set is worked out from.</summary>
	Map,

	/// <summary>A number, a colour, a picture the shader asked for by name, or one of the plain fields.</summary>
	Value,
}

/// <summary>What a material's shader declares, as properties an inspector sheet can build controls for.</summary>
public sealed class MaterialProperties
{
	readonly MaterialDefinition _definition;
	readonly SerializedObject _serialized;
	readonly Action<MaterialEditKind> _edited;

	//The features this shader declares, by name.
	HashSet<string> _features = new( StringComparer.Ordinal );

	//The numbers the shader declares, read once per Refresh.
	IReadOnlyList<MaterialDefinition.ShaderParameter> _parameters = Array.Empty<MaterialDefinition.ShaderParameter>();

	public MaterialProperties( MaterialDefinition definition, SerializedObject serialized, Action<MaterialEditKind> edited )
	{
		_definition = definition ?? throw new ArgumentNullException( nameof( definition ) );
		_serialized = serialized ?? throw new ArgumentNullException( nameof( serialized ) );
		_edited = edited;
	}

	/// <summary>The material as the sheet edits it.</summary>
	public SerializedObject Serialized => _serialized;

	/// <summary>The shader, which is the one property everything else here depends on.</summary>
	public SerializedProperty Shader => _serialized.GetProperty( nameof( MaterialDefinition.Shader ) );

	//Before anything is asked for after the shader, a feature or a map changed: which block each number stands in is worked out from the one list the shader declares, read here.
	/// <summary>Read the shader's declarations again.</summary>
	public void Refresh()
	{
		_parameters = MaterialDefinition.ParametersFor( _definition.Shader );

		_features = new HashSet<string>(
			MaterialDefinition.FeaturesFor( _definition.Shader ).Select( x => x.Name ), StringComparer.Ordinal );
	}

	//A tick where the shader allows nought or one, a list where it allows more - which is the usual reading of the same declaration, and what the values in brackets after the range are for.
	/// <summary>Every feature the shader declares, in its order, with a property for its switch.</summary>
	public IReadOnlyList<(MaterialDefinition.ShaderFeature Feature, SerializedProperty Property)> Features()
	{
		return MaterialDefinition.FeaturesFor( _definition.Shader )
			.Select( x => (x, FeatureProperty( x )) )
			.ToList();
	}

	/// <summary>What one feature is on just now; nought is off.</summary>
	public int FeatureValue( string name ) => _definition.FeatureValue( name );

	/// <summary>Whether the shader filed any number under a feature - a slider, a colour, a tick - whether it is on or not.</summary>
	public bool HasNumbers( MaterialDefinition.ShaderFeature feature )
		=> _parameters.Any( x => !x.Texture && x.Feature == feature.Name && !Hidden( x ) );

	/// <summary>Whether a feature is over a picture of its own - a mask, a map - whether it is on or not, for its card to say so before it is ticked.</summary>
	public bool HasMaps( MaterialDefinition.ShaderFeature feature )
		=> MaterialDefinition.MapsFor( _definition.Shader )
			.Concat( MaterialDefinition.DeclaredMaps( _definition.Shader ) )
			.Any( x => x.Feature == feature.Name );

	/// <summary>What every feature is worth just now, as one string - to tell whether an edit to a map moved any of them.</summary>
	public string FeatureSignature() => string.Join( ",",
		MaterialDefinition.FeaturesFor( _definition.Shader ).Select( x => _definition.FeatureValue( x.Name ) ) );

	/// <summary>One block of the sheet: the pictures a feature, or the surface itself, is about and the numbers filed under it.</summary>
	public readonly record struct Block( string Title, IReadOnlyList<MaterialDefinition.MapSlot> Slots,
		List<SerializedProperty> Numbers )
	{
		/// <summary>Whether there is anything in it to set.</summary>
		public bool IsEmpty => Slots.Count == 0 && Numbers.Count == 0;
	}

	/// <summary>Blocks of textures and numbers under no feature, one per shader heading</summary>
	public IReadOnlyList<Block> SurfaceBlocks()
	{
		var found = new List<Block>();

		foreach ( var slot in _definition.Maps() )
		{
			if ( slot.Feature is null ) found.Add( new Block( slot.Title, [slot], new List<SerializedProperty>() ) );
		}

		foreach ( var (group, properties) in Grouped( Unswitched() ) )
		{
			found.Add( new Block( string.IsNullOrWhiteSpace( group ) ? "Surface" : group, [], properties.ToList() ) );
		}

		return found;
	}

	/// <summary>A feature's textures and numbers; empty while the feature is off</summary>
	public Block FeatureBlock( MaterialDefinition.ShaderFeature feature )
	{
		if ( _definition.FeatureValue( feature.Name ) <= 0 )
			return new Block( feature.Title, [], new List<SerializedProperty>() );

		var slots = _definition.Maps().Where( x => x.Feature == feature.Name ).ToList();

		return new Block( feature.Title, slots, Under( feature.Name ) );
	}

	//Shader numbers under no feature (or an unknown one), with their heading; built-ins map to fields
	IEnumerable<(string Group, SerializedProperty Property)> Unswitched()
	{
		foreach ( var parameter in _parameters )
		{
			if ( parameter.Texture ) continue;
			if ( Hidden( parameter ) ) continue;
			if ( parameter.Feature is not null && _features.Contains( parameter.Feature ) ) continue;

			yield return (parameter.Group, PropertyFor( parameter ));
		}
	}

	//Whether the shader filed a number under "Hidden": one something else writes while the game runs -
	//where water was splashed, see water.shader - and a slider for it would be a slider that is overwritten.
	static bool Hidden( MaterialDefinition.ShaderParameter parameter )
		=> string.Equals( parameter.Group, "Hidden", StringComparison.OrdinalIgnoreCase );

	/// <summary>The maps the shader is actually built to sample, in the order it declared them. A slot whose feature is off is not a slot that would do anything.</summary>
	public IReadOnlyList<MaterialDefinition.MapSlot> Slots() => _definition.Maps();

	/// <summary>The path of one map's picture, for a frame to edit.</summary>
	public SerializedProperty Path( MaterialDefinition.MapSlot slot )
		=> slot.IsDeclared ? TextureProperty( slot ) : _serialized.GetProperty( slot.Property );

	//What the shader said, and nothing worked out here: a number names the feature it is part of, and a map slot is what that feature is for. A shader that says nothing keeps every number in the loose list, which is where they all used to be.
	/// <summary>The numbers that belong under one map's picture.</summary>
	public List<SerializedProperty> Beside( MaterialDefinition.MapSlot slot )
		=> slot.Feature is null ? new List<SerializedProperty>() : Under( slot.Feature );

	//The numbers the shader filed under one feature, in its own order.
	List<SerializedProperty> Under( string feature )
	{
		var found = new List<SerializedProperty>();

		if ( feature is null ) return found;

		foreach ( var parameter in _parameters )
		{
			if ( parameter.Texture || parameter.Feature != feature || Hidden( parameter ) ) continue;

			if ( PropertyFor( parameter ) is { } property ) found.Add( property );
		}

		return found;
	}

	/// <summary>True for material-level fields shown at the top (tint, surface), not maps or shader-read numbers</summary>
	public bool IsRest( SerializedProperty property )
	{
		if ( property.IsMethod ) return true;
		if ( !property.IsProperty ) return false;
		if ( property.HasAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() ) return false;
		if ( property.Name == nameof( MaterialDefinition.Shader ) ) return false;
		if ( MaterialDefinition.IsMapProperty( property.Name ) ) return false;

		if ( property.Name is nameof( MaterialDefinition.Roughness ) or nameof( MaterialDefinition.Metalness )
			or nameof( MaterialDefinition.EmissionStrength ) or nameof( MaterialDefinition.AlphaCutoff ) )
		{
			return false;
		}

		return property.IsPublic || property.HasAttribute<System.Text.Json.Serialization.JsonIncludeAttribute>();
	}

	/// <summary>Rows under their headings, in the order the shader declared them: the ungrouped ones under an empty heading, and every other heading once.</summary>
	public static IEnumerable<(string Group, SerializedProperty[] Properties)> Grouped(
		IEnumerable<(string Group, SerializedProperty Property)> rows )
	{
		return rows
			.Where( x => x.Property is not null )
			.GroupBy( x => x.Group )
			.Select( group => (group.Key, group.Select( x => x.Property ).ToArray()) );
	}

	/// <summary>What kind of thing a change to one of the material's own properties was.</summary>
	public static MaterialEditKind KindOf( string propertyName )
	{
		if ( propertyName == nameof( MaterialDefinition.Shader ) ) return MaterialEditKind.Shader;
		if ( MaterialDefinition.IsMapProperty( propertyName ) ) return MaterialEditKind.Map;

		return MaterialEditKind.Value;
	}

	//One feature as a property, for the sheet to build the control for.
	SerializedProperty FeatureProperty( MaterialDefinition.ShaderFeature feature )
	{
		var name = feature.Name;

		if ( feature.Least == 0 && feature.Most == 1 )
		{
			return TypeLibrary.CreateProperty<bool>( feature.Title,
				() => _definition.FeatureValue( name ) > 0,
				value => SetFeature( name, value ? 1 : 0 ),
				null, _serialized );
		}

		var names = feature.Names;
		var least = feature.Least;

		return TypeLibrary.CreateProperty<string>( feature.Title,
			() => ValueName( feature, _definition.FeatureValue( name ) ),
			value =>
			{
				var at = Array.IndexOf( names, value );
				if ( at >= 0 ) SetFeature( name, least + at );
			},
			[new ShaderFeatureNamesAttribute( names )],
			_serialized );
	}

	//What a feature calls the value it is on.
	static string ValueName( MaterialDefinition.ShaderFeature feature, int value )
	{
		var at = value - feature.Least;

		return at >= 0 && at < feature.Names.Length ? feature.Names[at] : $"{value}";
	}

	//Writes a feature down. Written even where it is what the material would have been given anyway:
	//what is derived is what a material that never said gets, and touching the switch is saying.
	void SetFeature( string name, int value )
	{
		_definition.Features ??= new Dictionary<string, int>();
		_definition.Features[name] = value;

		_edited?.Invoke( MaterialEditKind.Feature );
	}

	//One declared number as a property, whichever kind of number it is. One of the material's own fields is handed over
	//as itself, so there is one number and one control that edits it; anything else the shader invented is kept by name.
	SerializedProperty PropertyFor( MaterialDefinition.ShaderParameter parameter )
	{
		return parameter.Property is not null
			? _serialized.GetProperty( parameter.Property )
			: NumberProperty( parameter );
	}

	//One declared number as a property, whose type is what decides its control. A colour opens the colour picker, a width of more than one is
	//that many boxes side by side, and a range is a slider with the number beside it - all of which the inspector already knows how to draw.
	SerializedProperty NumberProperty( MaterialDefinition.ShaderParameter parameter )
	{
		if ( parameter.Colour )
		{
			return TypeLibrary.CreateProperty<Color>( parameter.Title,
				() => _definition.NumberValue( parameter ),
				value => SetNumber( parameter, value ),
				null, _serialized );
		}

		//A tick, where the shader declared one. Kept in the same four floats as everything
		//else - the constant buffer has no other shape - and only what draws it is different.
		if ( parameter.Boolean )
		{
			return TypeLibrary.CreateProperty<bool>( parameter.Title,
				() => _definition.NumberValue( parameter ).x > 0.5f,
				value =>
				{
					var kept = _definition.NumberValue( parameter );
					SetNumber( parameter, new Vector4( value ? 1 : 0, kept.y, kept.z, kept.w ) );
				},
				null, _serialized );
		}

		//Only where the shader gave one: equal ends mean a number to type rather than a slider to pull.
		var ranged = parameter.Most > parameter.Least
			? new Attribute[] { new RangeAttribute( parameter.Least, parameter.Most ) }
			: null;

		Vector4 Kept() => _definition.NumberValue( parameter );

		return parameter.Width switch
		{
			2 => TypeLibrary.CreateProperty<Vector2>( parameter.Title,
				() => new Vector2( Kept().x, Kept().y ),
				value => SetNumber( parameter, new Vector4( value.x, value.y, Kept().z, Kept().w ) ),
				ranged, _serialized ),

			3 => TypeLibrary.CreateProperty<Vector3>( parameter.Title,
				() => new Vector3( Kept().x, Kept().y, Kept().z ),
				value => SetNumber( parameter, new Vector4( value.x, value.y, value.z, Kept().w ) ),
				ranged, _serialized ),

			4 => TypeLibrary.CreateProperty<Vector4>( parameter.Title,
				Kept,
				value => SetNumber( parameter, value ),
				ranged, _serialized ),

			_ => TypeLibrary.CreateProperty<float>( parameter.Title,
				() => Kept().x,
				value => SetNumber( parameter, new Vector4( value, Kept().y, Kept().z, Kept().w ) ),
				ranged, _serialized ),
		};
	}

	//Writes a declared number down.
	void SetNumber( MaterialDefinition.ShaderParameter parameter, Vector4 value )
	{
		_definition.Numbers ??= new Dictionary<string, Vector4>();
		_definition.Numbers[parameter.Name] = value;

		_edited?.Invoke( MaterialEditKind.Value );
	}

	//A shader texture as a property, keyed by its shader name, with the standard path picker
	SerializedProperty TextureProperty( MaterialDefinition.MapSlot slot )
	{
		var name = slot.Texture;

		return TypeLibrary.CreateProperty<string>( slot.Title,
			() => _definition.TextureValue( name ),
			value => SetTexture( name, value ),
			[new FilePathAttribute( MaterialDefinition.ImageFiles )],
			_serialized );
	}

	//Writes one of the shader's own pictures down.
	void SetTexture( string name, string path )
	{
		_definition.SetTexture( name, path );

		_edited?.Invoke( MaterialEditKind.Value );
	}
}

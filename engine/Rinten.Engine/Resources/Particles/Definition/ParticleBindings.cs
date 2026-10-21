using System.Text.Json.Serialization;

namespace Rinten;

//Which kind of parameter is what the attribute says; the control that answers it lives in the particle editor, because only the editor knows which definition is open.
/// <summary>Marks a string that names a parameter of the effect being edited, so the inspector offers the ones that are declared rather than a text box.</summary>
[AttributeUsage( AttributeTargets.Property )]
public sealed class ParticleParameterNameAttribute : Attribute
{
	public ParticleParameterNameAttribute( ParticleParameterKind kind ) => Kind = kind;

	/// <summary>Which of the three lists this name is picked from.</summary>
	public ParticleParameterKind Kind { get; }
}

/// <summary>Which kind of named parameter is meant.</summary>
[Expose]
public enum ParticleParameterKind
{
	Float,
	Vector,
	Color
}

//Named parameters let one effect file serve different uses without copies
/// <summary>A number a module reads: either written down here, or the name of one of the effect's float parameters. Which of a slot's four numbers a float reads.</summary>
[Expose]
public enum ParticleSlotChannel
{
	X,
	Y,
	Z,
	W,

	/// <summary>How long x, y, z are together - a speed out of a velocity.</summary>
	Length,
}

/// <summary>What a parameter does to the number it is bound to.</summary>
[Expose]
public enum ParticleBindingMode
{
	/// <summary>The value, curve and all, times the parameter.</summary>
	Multiply,

	/// <summary>The parameter, flat, instead of the value.</summary>
	Replace,
}

//Name resolved per definition revision, cached in one immutable object for thread safety
internal sealed record ParticleSlotAt( ParticleDefinition Of, int Revision, int Index )
{
	internal static int Resolve( ref ParticleSlotAt held, ParticleDefinition definition, string name )
	{
		if ( definition is null ) return -1;

		var have = held;
		var revision = definition.Revision;

		if ( have is not null && ReferenceEquals( have.Of, definition ) && have.Revision == revision )
			return have.Index;

		var made = new ParticleSlotAt( definition, revision, definition.SlotIndex( name ) );

		held = made;

		return made.Index;
	}
}

//A shape's name resolved against a definition, kept until it changes - the same trade as ParticleSlotAt, for the modules that follow one.
internal sealed record ParticleShapeAt( ParticleDefinition Of, int Revision, ParticleShape Shape )
{
	internal static ParticleShape Resolve( ref ParticleShapeAt held, ParticleDefinition definition, string name )
	{
		if ( definition is null ) return null;

		var have = held;
		var revision = definition.Revision;

		if ( have is not null && ReferenceEquals( have.Of, definition ) && have.Revision == revision )
			return have.Shape;

		var made = new ParticleShapeAt( definition, revision, definition.Shape( name ) );

		held = made;

		return made.Shape;
	}
}

[Expose]
public sealed class ParticleFloatBinding
{
	/// <summary>Whether a named parameter has a say in this number.</summary>
	[Property]
	public bool UseParameter { get; set; }

	/// <summary>The value itself - a constant, a range, or a curve over a life. With a parameter in <see cref="ParticleBindingMode.Multiply"/> this is what the parameter scales; in <see cref="ParticleBindingMode.Replace"/> it is what stands in where the parameter has not been set.</summary>
	[Property]
	public ParticleFloat Value { get; set; }

	/// <summary>Which of the effect's float parameters this reads.</summary>
	[Property, ShowIf( nameof( UseParameter ), true ), ParticleParameterName( ParticleParameterKind.Float )]
	public string ParameterName { get; set; }

	/// <summary>One of the effect's slots read instead of, or times, the value - what the particle remembered. Read per particle, so only where a module evaluates per particle; empty is none.</summary>
	[Property, ParticleSlotName]
	public string SlotName { get; set; }

	/// <summary>Which of the slot's four numbers.</summary>
	[Property, ShowIf( nameof( HasSlot ), true )]
	public ParticleSlotChannel SlotChannel { get; set; } = ParticleSlotChannel.X;

	/// <summary>Whether the slot multiplies the value, or stands in for it.</summary>
	[Property, ShowIf( nameof( HasSlot ), true ), Title( "Slot Mode" )]
	public ParticleBindingMode SlotMode { get; set; } = ParticleBindingMode.Replace;

	//Which slot SlotName is, found once - see ParticleSlotAt.
	private ParticleSlotAt resolvedSlot;

	[Hide, JsonIgnore]
	public bool HasSlot => !string.IsNullOrEmpty( SlotName );

	//Multiply is the default because it is what a parameter is nearly always for - a bigger fire, a longer life, a faster spray - and it keeps what was authored: a size that grows and shrinks over a life still does, only twice as large. Replace is for a number that really is the parameter, like a count.
	/// <summary>What the parameter does to the value: scales it, curve and all, or takes its place as one flat number.</summary>
	[Property, ShowIf( nameof( UseParameter ), true )]
	public ParticleBindingMode Mode { get; set; } = ParticleBindingMode.Multiply;

	/// <summary>What the parameter is multiplied by on the way in.</summary>
	[Property, ShowIf( nameof( UseParameter ), true )]
	public float Multiplier { get; set; } = 1.0f;

	public ParticleFloatBinding() { }

	public ParticleFloatBinding( float value ) => Value = value;

	public ParticleFloatBinding( float a, float b ) => Value = new ParticleFloat( a, b );

	public static implicit operator ParticleFloatBinding( float value ) => new( value );

	public static implicit operator ParticleFloatBinding( ParticleFloat value ) => new() { Value = value };

	/// <summary>Whether this actually names a parameter that could be read.</summary>
	public bool IsBound => UseParameter && !string.IsNullOrEmpty( ParameterName );

	//The same for every particle of a player this frame: no slot, a plain number - see Evaluate( ParticlePlayer )
	internal bool Uniform => !HasSlot && Value.IsPlainConstant;

	/// <summary>This as something the engine can hold: a parameter collapses to the constant it is worth for this player.</summary>
	public ParticleFloat Resolve( ParticlePlayer player )
	{
		if ( !IsBound || player is null )
			return Value;

		var parameter = player.GetFloat( ParameterName ) * Multiplier;

		if ( Mode == ParticleBindingMode.Multiply )
			return Kept( Value, parameter );

		return new ParticleFloat
		{
			Type = ParticleFloat.ValueType.Constant,
			Evaluation = ParticleFloat.EvaluationType.Seed,
			ConstantValue = parameter
		};
	}

	//Read at the start of a life, because that is the only moment a value that varies over one has to mean something here.
	/// <summary>One number, now - for a module writing something that is not a <see cref="ParticleFloat"/> at the other end, like a light's brightness.</summary>
	public float Evaluate( ParticlePlayer player = null )
	{
		var value = Value.Evaluate( 0.0f, Random.Shared.Float( 0.0f, 1.0f ) );

		if ( !IsBound || player is null )
			return value;

		var parameter = player.GetFloat( ParameterName ) * Multiplier;

		return Mode == ParticleBindingMode.Multiply ? value * parameter : parameter;
	}

	/// <summary>The number for one particle, now: a range picks the same number for that particle every time, and a curve reads where it is in its life.</summary>
	public float Evaluate( Particle particle, ParticlePlayer player )
	{
		if ( particle is null )
			return Evaluate( player );

		//Plain constants skip evaluation; the random is still drawn so later Rand indices don't shift
		var value = Value.IsPlainConstant
			? Value.ConstantValue
			: Value.Evaluate( Value.Evaluation == ParticleFloat.EvaluationType.Index ? particle.IndexDelta : particle.LifeDelta, particle.Rand( 5, 251 ) );

		if ( HasSlot && player?.Definition is { } definition )
		{
			var index = ParticleSlotAt.Resolve( ref resolvedSlot, definition, SlotName );

			if ( index >= 0 )
			{
				var slot = Channel( particle.Slot( index ), SlotChannel );

				value = SlotMode == ParticleBindingMode.Multiply ? value * slot : slot;
			}
		}

		if ( !IsBound || player is null )
			return value;

		var parameter = player.GetFloat( ParameterName ) * Multiplier;

		return Mode == ParticleBindingMode.Multiply ? value * parameter : parameter;
	}

	/// <summary>One number out of a slot.</summary>
	public static float Channel( Vector4 slot, ParticleSlotChannel channel ) => channel switch
	{
		ParticleSlotChannel.Y => slot.y,
		ParticleSlotChannel.Z => slot.z,
		ParticleSlotChannel.W => slot.w,
		ParticleSlotChannel.Length => new Vector3( slot.x, slot.y, slot.z ).Length,
		_ => slot.x,
	};

	/// <summary>This, with everything in it multiplied - constants, ranges and curves alike.</summary>
	public ParticleFloat Scaled( ParticlePlayer player, float scale )
	{
		//A multiplying parameter and the scale make one factor, so the curve is scaled - and kept - once
		if ( IsBound && player is not null && Mode == ParticleBindingMode.Multiply )
			return Kept( Value, player.GetFloat( ParameterName ) * Multiplier * scale );

		return Kept( Resolve( player ), scale );
	}

	//Scale, with the last scaled curves kept by their frames and scale - a module asks for one every particle it starts
	//One reference written whole, so a step on another thread reads a matching pair
	ParticleFloat Kept( ParticleFloat value, float scale )
	{
		if ( value.Type is not ( ParticleFloat.ValueType.Curve or ParticleFloat.ValueType.CurveRange ) )
			return Scale( value, scale );

		if ( scaledCurves is { } kept && kept.Scale == scale && kept.FromA == value.CurveA.Frames && kept.FromB == value.CurveB.Frames )
		{
			value.CurveA = kept.A;
			value.CurveB = kept.B;
			return value;
		}

		var scaled = Scale( value, scale );
		scaledCurves = new ScaledCurves( value.CurveA.Frames, value.CurveB.Frames, scale, scaled.CurveA, scaled.CurveB );
		return scaled;
	}

	private sealed record ScaledCurves( System.Collections.Immutable.ImmutableArray<Curve.Frame> FromA,
		System.Collections.Immutable.ImmutableArray<Curve.Frame> FromB, float Scale, Curve A, Curve B );

	private ScaledCurves scaledCurves;

	/// <summary>A <see cref="ParticleFloat"/> with everything in it multiplied.</summary>
	public static ParticleFloat Scale( ParticleFloat value, float scale )
	{
		var scaled = value;

		switch ( value.Type )
		{
			case ParticleFloat.ValueType.Constant:
				scaled.ConstantValue = value.ConstantValue * scale;
				break;

			case ParticleFloat.ValueType.Range:
				scaled.ConstantA = value.ConstantA * scale;
				scaled.ConstantB = value.ConstantB * scale;
				break;

			case ParticleFloat.ValueType.Curve:
			case ParticleFloat.ValueType.CurveRange:
				scaled.CurveA = ScaleCurve( value.CurveA, scale );
				scaled.CurveB = ScaleCurve( value.CurveB, scale );
				break;
		}

		return scaled;
	}

	static Curve ScaleCurve( Curve curve, float scale )
	{
		var frames = curve.Frames;

		if ( frames.IsDefaultOrEmpty )
			return curve;

		var scaled = new Curve.Frame[frames.Length];

		for ( var i = 0; i < frames.Length; i++ )
			scaled[i] = new Curve.Frame( frames[i].Time, frames[i].Value * scale );

		return new Curve( scaled );
	}
}

/// <summary>A direction or offset a module reads: either written down here, or the name of one of the effect's vector parameters.</summary>
[Expose]
public sealed class ParticleVectorBinding
{
	/// <summary>Whether this reads a named vector parameter instead of its own value.</summary>
	[Property]
	public bool UseParameter { get; set; }

	/// <summary>The value, where it is not a parameter.</summary>
	[Property, ShowIf( nameof( UseParameter ), false )]
	public ParticleVector3 Value { get; set; } = Vector3.Zero;

	/// <summary>Which of the effect's vector parameters this reads.</summary>
	[Property, ShowIf( nameof( UseParameter ), true ), ParticleParameterName( ParticleParameterKind.Vector )]
	public string ParameterName { get; set; }

	/// <summary>What the parameter is multiplied by on the way in.</summary>
	[Property, ShowIf( nameof( UseParameter ), true )]
	public float Multiplier { get; set; } = 1.0f;

	/// <summary>A float parameter the whole vector is multiplied by, whichever way it came - so a velocity with a random spread in it can still be a Speed.</summary>
	[Property, Title( "Scale By" ), ParticleParameterName( ParticleParameterKind.Float )]
	public string ScaleParameterName { get; set; }

	/// <summary>One of the effect's slots read as x, y, z instead of, or times, the value - where the particle was born, the way it was thrown.</summary>
	[Property, ParticleSlotName]
	public string SlotName { get; set; }

	/// <summary>Whether the slot multiplies the value, or stands in for it.</summary>
	[Property, ShowIf( nameof( HasSlot ), true ), Title( "Slot Mode" )]
	public ParticleBindingMode SlotMode { get; set; } = ParticleBindingMode.Replace;

	//Which slot SlotName is, found once - see ParticleSlotAt.
	private ParticleSlotAt resolvedSlot;

	[Hide, JsonIgnore]
	public bool HasSlot => !string.IsNullOrEmpty( SlotName );

	public ParticleVectorBinding() { }

	public ParticleVectorBinding( Vector3 value ) => Value = value;

	public static implicit operator ParticleVectorBinding( Vector3 value ) => new( value );

	public static implicit operator ParticleVectorBinding( ParticleVector3 value ) => new() { Value = value };

	/// <summary>Whether this actually names a parameter that could be read.</summary>
	public bool IsBound => UseParameter && !string.IsNullOrEmpty( ParameterName );

	//The same for every particle of a player this frame: no slot, and a parameter or three plain numbers - see Evaluate
	internal bool Uniform => !HasSlot && ( IsBound || ( Value.X.IsPlainConstant && Value.Y.IsPlainConstant && Value.Z.IsPlainConstant ) );

	/// <summary>The vector for one particle - or, with no particle, for the effect as a whole, where the random components read as zero.</summary>
	public Vector3 Evaluate( Particle particle, ParticlePlayer player = null )
	{
		Vector3 value;

		if ( IsBound && player is not null )
			value = player.GetVector( ParameterName ) * Multiplier;
		else if ( particle is null )
			value = Value.Evaluate( Time.Delta, 0.0f, 0.0f, 0.0f );
		else
			value = Value.Evaluate( particle, 1 );

		if ( particle is not null && HasSlot && player?.Definition is { } definition )
		{
			var index = ParticleSlotAt.Resolve( ref resolvedSlot, definition, SlotName );

			if ( index >= 0 )
			{
				var slot = particle.Slot( index );
				var xyz = new Vector3( slot.x, slot.y, slot.z );

				value = SlotMode == ParticleBindingMode.Multiply ? value * xyz : xyz;
			}
		}

		if ( player is not null && !string.IsNullOrEmpty( ScaleParameterName ) )
			value *= player.GetFloat( ScaleParameterName );

		return value;
	}
}

/// <summary>A colour a module reads: either written down here as a gradient, or the name of one of the effect's colour parameters.</summary>
[Expose]
public sealed class ParticleColorBinding
{
	/// <summary>Whether this reads a named parameter instead of its own value.</summary>
	[Property]
	public bool UseParameter { get; set; }

	/// <summary>The value, where it is not a parameter.</summary>
	[Property, ShowIf( nameof( UseParameter ), false )]
	public ParticleGradient Value { get; set; } = Color.White;

	/// <summary>Which of the effect's colour parameters this reads.</summary>
	[Property, ShowIf( nameof( UseParameter ), true ), ParticleParameterName( ParticleParameterKind.Color )]
	public string ParameterName { get; set; }

	public ParticleColorBinding() { }

	public ParticleColorBinding( Color value ) => Value = value;

	public static implicit operator ParticleColorBinding( Color value ) => new( value );

	public static implicit operator ParticleColorBinding( ParticleGradient value ) => new() { Value = value };

	/// <summary>Whether this actually names a parameter that could be read.</summary>
	public bool IsBound => UseParameter && !string.IsNullOrEmpty( ParameterName );

	/// <summary>The gradient, from the parameter where it names one.</summary>
	public ParticleGradient Evaluate( ParticlePlayer player = null )
	{
		if ( IsBound && player is not null )
			return player.GetColor( ParameterName );

		return Value;
	}
}

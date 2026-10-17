using Rinten.Rendering;

namespace Rinten;

/// <summary>Named values for the renderer (numbers, textures, buffers) read by shaders via Attribute( "Name" )</summary>
public partial class RenderAttributes
{
	private readonly Dictionary<StringToken, Held> values = new();
	private readonly Dictionary<StringToken, int> combos = new();

	//Bumped on every write, so whoever drew with these can tell if they changed
	internal int Version { get; private set; }

	//Says the bag has been written to. Public setters all go through it.
	private void Wrote() => Version++;

	//A number put in: the same number again is no change, so what reads the bag by its version - a scene object's draw - is not made again
	private void Put( in StringToken k, in Held held )
	{
		if ( values.TryGetValue( k, out var had ) && had.Same( held ) ) return;

		values[k] = held;
		Wrote();
	}

	//Holds a texture, buffer or matrix, which forces the classic path; sticky until Clear
	internal bool HoldsReference { get; private set; }

	//Says the bag now holds something that is not a number.
	private void WroteReference()
	{
		HoldsReference = true;
		Version++;
	}

	internal static RenderAttributePool Pool = new();

	public RenderAttributes()
	{
	}

	//How many values are set on this.
	internal int Count => values.Count;

	//One value as it is held, for whoever is drawing with it.
	internal bool TryGetHeld( StringToken name, out Held held ) => values.TryGetValue( name, out held );

	//The first live buffer set here, or null - for a draw that reads one beside its geometry.
	internal GpuBuffer FirstBuffer()
	{
		foreach ( var held in values.Values )
		{
			if ( held.Reference is GpuBuffer buffer && buffer.IsValid ) return buffer;
		}

		return null;
	}

	//The feature switches set on this, for whoever is drawing with it.
	internal IReadOnlyDictionary<StringToken, int> Combos => combos;

	//Keys sorted by name, cached until the key set changes, since getting names allocates
	internal System.ReadOnlySpan<StringToken> OrderedKeys()
	{
		var mix = 0u;

		foreach ( var key in values.Keys ) mix ^= key.Value;

		if ( orderedCount == values.Count && orderedMix == mix )
			return ordered.AsSpan( 0, orderedCount );

		if ( ordered.Length < values.Count ) ordered = new StringToken[values.Count];

		var at = 0;

		foreach ( var key in values.Keys ) ordered[at++] = key;

		//Insertion, by hand: a pass binds a handful, and Array.Sort with a comparer makes a delegate of it on every call
		//- which a command list, whose one set of attributes changes keys between its dispatches, did at every dispatch.
		for ( var i = 1; i < at; i++ )
		{
			var key = ordered[i];
			var name = NameOf( key );
			var j = i - 1;

			while ( j >= 0 && Before( name, key, ordered[j] ) )
			{
				ordered[j + 1] = ordered[j];
				j--;
			}

			ordered[j + 1] = key;
		}

		orderedCount = at;
		orderedMix = mix;

		return ordered.AsSpan( 0, at );
	}

	private StringToken[] ordered = System.Array.Empty<StringToken>();
	private int orderedCount = -1;
	private uint orderedMix;

	//A key's name - looked up, not made: a token has no ToString of its own, and the one it inherits answers its
	//type's name, which put every key level with every other and left them in whatever order the dictionary held.
	private static string NameOf( StringToken key ) => StringToken.GetValue( key.Value );

	//Whether the key named name goes before other: by name, ordinal; a key with no name known by its number, after those with one.
	private static bool Before( string name, StringToken key, StringToken other )
	{
		var otherName = NameOf( other );

		if ( name is null || otherName is null )
			return name is not null ? true : otherName is null && key.Value < other.Value;

		return string.CompareOrdinal( name, otherName ) < 0;
	}

	//Per-draw shader feature combo, if set; selects a pipeline, not a material
	internal bool TryGetCombo( string name, out int value )
	{
		value = 0;

		return !string.IsNullOrEmpty( name ) && combos.TryGetValue( (StringToken)name, out value );
	}

	//Writes set values into a draw's number lanes, matching name spellings (g_flX, X, x_y) per lane
	internal void IntoDraw( Span<float> numbers, Span<byte> ranks,
		Dictionary<StringToken, CoreShaderParams.DrawPlace[]> lanes )
	{
		foreach ( var (token, held) in values )
		{
			if ( !lanes.TryGetValue( token, out var places ) ) continue;

			Vector4 value;

			switch ( held.Kind )
			{
				case HeldKind.Float:
				case HeldKind.Bool:
				case HeldKind.Vector2:
				case HeldKind.Vector3:
				case HeldKind.Vector4:
					value = held.Numbers;
					break;

				case HeldKind.Int:
					value = new Vector4( held.Integers.x, 0, 0, 0 );
					break;

				default:
					continue;
			}

			foreach ( var place in places )
			{
				if ( place.At + place.Width > numbers.Length ) continue;
				if ( ranks[place.At] <= place.Rank ) continue;

				ranks[place.At] = place.Rank;

				numbers[place.At] = value.x;
				if ( place.Width > 1 ) numbers[place.At + 1] = value.y;
				if ( place.Width > 2 ) numbers[place.At + 2] = value.z;
				if ( place.Width > 3 ) numbers[place.At + 3] = value.w;
			}
		}
	}

	internal bool TryGetDeclared( string declared, out Vector4 value )
		=> TryGetDeclared( Spellings( declared ), out value );

	//TryGetDeclared with precomputed spellings, avoiding a name hash per parameter per frame
	internal bool TryGetDeclared( StringToken[] spellings, out Vector4 value )
	{
		value = default;

		if ( spellings is null ) return false;

		for ( var i = 0; i < spellings.Length; i++ )
		{
			if ( TryGetValue( spellings[i], out value ) ) return true;
		}

		return false;
	}

	//Cached TryGetDeclared delegate, so hot callers don't allocate a closure per call
	internal System.Func<string, Vector4?> DeclaredLookup
		=> declaredLookup ??= name => TryGetDeclared( name, out var value ) ? value : null;

	private System.Func<string, Vector4?> declaredLookup;

	//Cached tokens for the names a declared parameter may be set under, in try order
	internal static StringToken[] Spellings( string declared )
		=> spellings.GetOrAdd( declared, static name =>
		{
			var bare = CoreShaderParams.Bare( name );
			var tokens = new List<StringToken>( 4 );

			foreach ( var spelling in new[] { bare, bare.ToLowerInvariant(), Underscored( bare ), name } )
			{
				if ( string.IsNullOrEmpty( spelling ) ) continue;

				var token = new StringToken( spelling );
				if ( !tokens.Contains( token ) ) tokens.Add( token );
			}

			return tokens.ToArray();
		} );

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, StringToken[]> spellings = new( StringComparer.Ordinal );

	//HueRotate as hue_rotate .
	private static string Underscored( string name )
	{
		var made = new System.Text.StringBuilder( name.Length + 4 );

		for ( var i = 0; i < name.Length; i++ )
		{
			if ( i > 0 && char.IsUpper( name[i] ) && !char.IsUpper( name[i - 1] ) ) made.Append( '_' );

			made.Append( char.ToLowerInvariant( name[i] ) );
		}

		return made.ToString();
	}

	//One attribute as four floats, whatever shape it was set as.
	private bool TryGetValue( StringToken name, out Vector4 value )
	{
		value = default;

		if ( !values.TryGetValue( name, out var held ) ) return false;

		switch ( held.Kind )
		{
			//Held zero-filled past their own lanes, so these are already the four floats.
			case HeldKind.Float:
			case HeldKind.Bool:
			case HeldKind.Vector2:
			case HeldKind.Vector3:
			case HeldKind.Vector4:
				value = held.Numbers;
				return true;

			case HeldKind.Int:
				value = new Vector4( held.Integers.x, 0, 0, 0 );
				return true;

			default:
				return false;
		}
	}

	public void Clear()
	{
		values.Clear();
		combos.Clear();
		HoldsReference = false;
		Wrote();
	}

	internal void Clear( bool freeMemory ) => Clear();

	public void SetCombo( in StringToken k, in int value ) { combos[k] = value; Wrote(); }

	[Obsolete( "Please use SetComboEnum" )]
	public void SetCombo( in string k, in Enum value ) => combos[(StringToken)k] = (int)(object)value;

	public void SetComboEnum<T>( in StringToken k, in T value ) where T : unmanaged, Enum { combos[k] = value.AsInt(); Wrote(); }

	public void SetCombo( in StringToken k, in bool value ) => SetCombo( k, value ? 1 : 0 );

	public T GetComboEnum<T>( in StringToken k, in T defaultValue ) where T : Enum
	{
		return combos.TryGetValue( k, out var value ) ? (T)(object)value : defaultValue;
	}

	public bool GetComboBool( in StringToken k, in bool defaultValue = false )
	{
		return combos.TryGetValue( k, out var value ) ? value == 1 : defaultValue;
	}

	public int GetComboInt( in StringToken k, in int defaultValue = 0 )
	{
		return combos.TryGetValue( k, out var value ) ? value : defaultValue;
	}

	//Internal for a reason - don't expose!
	internal void SetPointer( in StringToken k, in IntPtr value ) { values[k] = Held.OfReference( value ); WroteReference(); }

	//Numbers are held as themselves, not boxed - see Held.
	public void Set( in StringToken k, in int value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in uint value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in Vector2Int value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in Vector3Int value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in SamplerState value ) { values[k] = Held.OfReference( value ); WroteReference(); }
	public void Set( in StringToken k, in float value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in double value ) => Put( k, Held.Of( (float)value ) );
	public void Set( in StringToken k, in string value ) { values[k] = Held.OfReference( value ); WroteReference(); }
	public void Set( in StringToken k, in bool value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in Vector4 value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in Angles value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in Vector3 value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in Vector2 value ) => Put( k, Held.Of( value ) );
	public void Set( in StringToken k, in Matrix value ) { values[k] = Held.OfReference( value ); WroteReference(); }
	public void Set( in StringToken k, in GpuBuffer value ) { values[k] = Held.OfReference( value ); WroteReference(); }

	/// <summary>A texture, and which mip of it to read. -1 is all of them.</summary>
	public void Set( in StringToken k, in Texture value, in int mip = -1 )
	{
		values[k] = Held.OfReference( value );
		WroteReference();

		if ( value is not null ) value.UsedOutsidePanels = true;
		SetUsedTexture( k, value );
	}

	/// <summary>Raw data, kept as its own bytes so whoever draws can upload it.</summary>
	public unsafe void SetData<T>( in StringToken k, Span<T> value ) where T : unmanaged
	{
		var bytes = new byte[value.Length * sizeof( T )];

		System.Runtime.InteropServices.MemoryMarshal.AsBytes( value ).CopyTo( bytes );

		values[k] = Held.OfReference( bytes );
		WroteReference();
	}

	public unsafe void SetData<T>( in StringToken k, T value ) where T : unmanaged
	{
		SetData( k, new Span<T>( ref value ) );
	}

	public unsafe void SetData<T>( in StringToken k, T[] value ) where T : unmanaged
	{
		SetData( k, value.AsSpan() );
	}

	public unsafe void SetData<T>( in StringToken k, List<T> value ) where T : unmanaged
	{
		SetData( k, value.ToArray() );
	}

	private T Get<T>( in StringToken name, in T defaultValue )
	{
		return values.TryGetValue( name, out var held ) && held.Is<T>( out var typed ) ? typed : defaultValue;
	}

	/// <summary>Get a bool value - else defaultValue if missing</summary>
	public bool GetBool( in StringToken name, in bool defaultValue = default ) => Get( name, defaultValue );

	/// <summary>Get a vector3 value - else defaultValue if missing</summary>
	public Vector3 GetVector( in StringToken name, in Vector3 defaultValue = default ) => Get( name, defaultValue );

	/// <summary>Get a vector4 value - else defaultValue if missing</summary>
	public Vector4 GetVector4( in StringToken name, in Vector4 defaultValue = default ) => Get( name, defaultValue );

	/// <summary>Get an angles value - else defaultValue if missing</summary>
	public Angles GetAngles( in StringToken name, in Angles defaultValue = default ) => Get( name, defaultValue );

	/// <summary>Get a float value - else defaultValue if missing</summary>
	public float GetFloat( in StringToken name, in float defaultValue = default ) => Get( name, defaultValue );

	/// <summary>Get a int value - else defaultValue if missing</summary>
	public int GetInt( in StringToken name, in int defaultValue = default ) => Get( name, defaultValue );

	/// <summary>Get a uint value - else defaultValue if missing</summary>
	public uint GetUInt( in StringToken name, in uint defaultValue = default ) => Get( name, defaultValue );

	/// <summary>Get a matrix value - else defaultValue if missing</summary>
	public Matrix GetMatrix( in StringToken name, in Matrix defaultValue = default ) => Get( name, defaultValue );

	/// <summary>Get a texture value - else defaultValue if missing</summary>
	public Texture GetTexture( in StringToken name, in Texture defaultValue = default ) => Get( name, defaultValue );

	//What sort of value a Held is.
	internal enum HeldKind : byte
	{
		Reference,
		Float,
		Int,
		UInt,
		Bool,
		Vector2,
		Vector3,
		Vector4,
		Vector2Int,
		Vector3Int,
		Angles,
	}

	//A stored value: up to four floats inline, anything else by reference, to avoid boxing
	internal readonly struct Held
	{
		public readonly object Reference;
		public readonly Vector4 Numbers;
		public readonly Vector3Int Integers;
		public readonly HeldKind Kind;

		private Held( HeldKind kind, Vector4 numbers = default, Vector3Int integers = default, object reference = null )
		{
			Kind = kind;
			Numbers = numbers;
			Integers = integers;
			Reference = reference;
		}

		public static Held Of( float value ) => new( HeldKind.Float, new Vector4( value, 0, 0, 0 ) );
		public static Held Of( bool value ) => new( HeldKind.Bool, new Vector4( value ? 1 : 0, 0, 0, 0 ) );
		public static Held Of( Vector2 value ) => new( HeldKind.Vector2, new Vector4( value.x, value.y, 0, 0 ) );
		public static Held Of( Vector3 value ) => new( HeldKind.Vector3, new Vector4( value.x, value.y, value.z, 0 ) );
		public static Held Of( Vector4 value ) => new( HeldKind.Vector4, value );
		public static Held Of( Angles value ) => new( HeldKind.Angles, new Vector4( value.pitch, value.yaw, value.roll, 0 ) );
		public static Held Of( int value ) => new( HeldKind.Int, integers: new Vector3Int( value, 0, 0 ) );
		public static Held Of( uint value ) => new( HeldKind.UInt, integers: new Vector3Int( unchecked( (int)value ), 0, 0 ) );
		public static Held Of( Vector2Int value ) => new( HeldKind.Vector2Int, integers: new Vector3Int( value.x, value.y, 0 ) );
		public static Held Of( Vector3Int value ) => new( HeldKind.Vector3Int, integers: value );

		/// <summary>Whether two hold the same number set the same way; never true of a reference, which may have changed inside.</summary>
		public bool Same( in Held other )
			=> Kind == other.Kind && Kind != HeldKind.Reference && Numbers == other.Numbers && Integers == other.Integers;

		/// <summary>Anything that is not one of the numbers - named apart so no overload can pick it by accident.</summary>
		public static Held OfReference( object value ) => new( HeldKind.Reference, reference: value );

		/// <summary>The value as <typeparamref name="T"/>, if that is what it was set as.</summary>
		public bool Is<T>( out T value )
		{
			switch ( Kind )
			{
				case HeldKind.Reference when Reference is T typed: value = typed; return true;
				case HeldKind.Float when typeof( T ) == typeof( float ): value = As<float, T>( Numbers.x ); return true;
				case HeldKind.Bool when typeof( T ) == typeof( bool ): value = As<bool, T>( Numbers.x != 0 ); return true;
				case HeldKind.Vector2 when typeof( T ) == typeof( Vector2 ): value = As<Vector2, T>( new Vector2( Numbers.x, Numbers.y ) ); return true;
				case HeldKind.Vector3 when typeof( T ) == typeof( Vector3 ): value = As<Vector3, T>( new Vector3( Numbers.x, Numbers.y, Numbers.z ) ); return true;
				case HeldKind.Vector4 when typeof( T ) == typeof( Vector4 ): value = As<Vector4, T>( Numbers ); return true;
				case HeldKind.Angles when typeof( T ) == typeof( Angles ): value = As<Angles, T>( new Angles( Numbers.x, Numbers.y, Numbers.z ) ); return true;
				case HeldKind.Int when typeof( T ) == typeof( int ): value = As<int, T>( Integers.x ); return true;
				case HeldKind.UInt when typeof( T ) == typeof( uint ): value = As<uint, T>( unchecked( (uint)Integers.x ) ); return true;
				case HeldKind.Vector2Int when typeof( T ) == typeof( Vector2Int ): value = As<Vector2Int, T>( new Vector2Int( Integers.x, Integers.y ) ); return true;
				case HeldKind.Vector3Int when typeof( T ) == typeof( Vector3Int ): value = As<Vector3Int, T>( Integers ); return true;
			}

			value = default;
			return false;
		}

		private static TTo As<TFrom, TTo>( TFrom value ) => System.Runtime.CompilerServices.Unsafe.As<TFrom, TTo>( ref value );
	}

	//Copy everything set here onto another set of attributes.
	internal void MergeTo( RenderAttributes renderAttributes )
	{
		if ( renderAttributes is null ) throw new ArgumentNullException( nameof( renderAttributes ) );

		foreach ( var (key, value) in values )
			renderAttributes.values[key] = value;

		foreach ( var (key, value) in combos )
			renderAttributes.combos[key] = value;
	}

	//These are for backwards compatibility with compiled shit
	public void SetCombo( [StringToken.Convert] in string k, in int value ) => SetCombo( (StringToken)k, value );
	public void SetCombo( [StringToken.Convert] in string k, in bool value ) => SetCombo( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in bool value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in int value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in uint value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in Vector2Int value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in Vector3Int value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in Vector4 value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in Vector3 value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in Vector2 value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in Matrix value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in Angles value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in string value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in Texture value, in int mip = -1 ) => Set( (StringToken)k, value, mip );
	public void Set( [StringToken.Convert] in string k, in float value ) => Set( (StringToken)k, value );
	public void Set( [StringToken.Convert] in string k, in double value ) => Set( (StringToken)k, (float)value );
	public Texture GetTexture( [StringToken.Convert] in string name, in Texture defaultValue = default ) => GetTexture( (StringToken)name, defaultValue );
}

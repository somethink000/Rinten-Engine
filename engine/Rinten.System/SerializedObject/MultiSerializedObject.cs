namespace Rinten;

using System.Reflection;

/// <summary>An object (or data) that can be accessed as an object</summary>
public class MultiSerializedObject : SerializedObject
{
	List<SerializedObject> children = new List<SerializedObject>();

	private static TResult SelectDistincSingleOrFallback<TSource, TResult>( IEnumerable<TSource> source, Func<TSource, TResult> selector, TResult fallback )
	{
		//Last 2 elements is all we need to check if there is more than 2 we want to return fallback
		var distinct = source.Select( selector ).Distinct().Take( 2 );
		var isSingle = distinct.Count() == 1;
		return isSingle ? distinct.FirstOrDefault() : fallback;
	}

	public override string TypeIcon => SelectDistincSingleOrFallback( children, x => x.TypeIcon, "multiple-icon" );
	public override string TypeName => SelectDistincSingleOrFallback( children, x => x.TypeName, "MultipleTypes" );
	public override string TypeTitle => SelectDistincSingleOrFallback( children, x => x.TypeTitle, "Multiple Objects" );

	public override bool IsValid => children.Any( x => x.IsValid() );

	public MultiSerializedObject()
	{
		PropertyList = new List<SerializedProperty>();
	}

	/// <summary>Add an object. Don't forget to rebuild after editing!</summary>
	public void Add( SerializedObject obj )
	{
		obj.OnPropertyChanged += p => OnPropertyChanged?.Invoke( p );
		obj.OnPropertyPreChange += p => OnPropertyPreChange?.Invoke( p );
		obj.OnPropertyStartEdit += p => OnPropertyStartEdit?.Invoke( p );
		obj.OnPropertyFinishEdit += p => OnPropertyFinishEdit?.Invoke( p );
		children.Add( obj );
	}

	/// <summary>Rebuild the object after modifying. This updates PropertyList.</summary>
	public void Rebuild()
	{
		PropertyList.Clear();

		SerializedProperty[] childProperties = children.SelectMany( x => x ).ToArray();
		var groupedProperties = childProperties.GroupBy( GetPropertyGroupKey ).ToArray();

		foreach ( var group in groupedProperties )
		{
			if ( group.Count() == 1 )
			{
				PropertyList.Add( group.First() );
				continue;
			}

			var msp = new MultiSerializedProperty( this, group.Select( x => x ) );
			PropertyList.Add( msp );
		}

	}

	/// <summary>True if the target is multiple objects</summary>
	public override bool IsMultipleTargets => children.Count > 1;

	/// <summary>A list of actual target objects - if applicable</summary>
	public override IEnumerable<object> Targets => children.Where( x => x.IsValid() ).SelectMany( x => x.Targets );

	static string GetPropertyGroupKey( SerializedProperty property )
	{
		if ( !property.IsMethod )
			return property.Name;

		var parameters = property.MethodParameters;
		if ( parameters.Length == 0 )
			return $"{property.Name}()";

		return $"{property.Name}({string.Join( ",", parameters.Select( p => p.ParameterType.FullName ) )})";
	}
}


class MultiSerializedProperty : SerializedProperty
{
	readonly List<SerializedProperty> properties;
	readonly SerializedObject parent;

	public override SerializedObject Parent => parent;

	public override string Name => properties.First().Name;
	public override string DisplayName => properties.First().DisplayName;
	public override string Description => properties.First().Description;
	public override string GroupName => properties.First().GroupName;
	public override int Order => properties.First().Order;
	public override bool IsEditable => properties.First().IsEditable;
	public override bool IsPublic => properties.First().IsPublic;
	public override Type PropertyType => properties.First().PropertyType;
	public override string SourceFile => properties.First().SourceFile;
	public override int SourceLine => properties.First().SourceLine;
	public override bool HasChanges => properties.First().HasChanges;
	public override bool IsProperty => properties.First().IsProperty;
	public override bool IsField => properties.First().IsField;
	public override bool IsMethod => properties.First().IsMethod;

	public override IEnumerable<Attribute> GetAttributes()
	{
		//TODO: maybe this should be attributes that all targets share?
		return properties?.FirstOrDefault()?.GetAttributes() ?? base.GetAttributes();
	}

	public override bool TryGetAsObject( out SerializedObject obj )
	{
		var objects = new List<SerializedObject>();

		foreach ( var e in properties )
		{
			if ( e.TryGetAsObject( out var o ) ) objects.Add( o );
		}

		obj = null;

		if ( objects.Count == 0 ) return false;

		//A list on every one of them is edited as a list: an object of their properties is not a collection, and the list control
		//has nothing to add to or take from without one. Several things selected showed an empty list with a button that did nothing.
		if ( objects.All( x => x is SerializedCollection ) )
		{
			obj = new MultiSerializedCollection( objects.Cast<SerializedCollection>().ToList() );
			return true;
		}

		var mso = new MultiSerializedObject();
		foreach ( var o in objects ) mso.Add( o );
		mso.Rebuild();

		obj = mso;
		return true;
	}

	public MultiSerializedProperty( SerializedObject parent, IEnumerable<SerializedProperty> enumerable )
	{
		this.parent = parent;
		properties = enumerable.ToList();
	}

	public override T GetValue<T>( T defaultValue = default )
	{
		return properties.First().GetValue<T>( defaultValue );
	}

	public override void SetValue<T>( T value )
	{
		NotePreChange();

		differentKnown = false;

		foreach ( var prop in properties )
		{
			prop.SetValue( value );
		}

		NoteChanged();
	}

	protected override void NoteStartEdit()
	{
		foreach ( var prop in properties )
		{
			prop.NoteStartEdit( prop );
		}
	}

	internal override void NoteStartEdit( SerializedProperty childProperty )
	{
		foreach ( var prop in properties )
		{
			prop.NoteStartEdit( prop );
		}
	}

	protected override void NoteFinishEdit()
	{
		foreach ( var prop in properties )
		{
			prop.NoteFinishEdit( prop );
		}
	}

	internal override void NoteFinishEdit( SerializedProperty childProperty )
	{
		foreach ( var prop in properties )
		{
			prop.NoteFinishEdit( prop );
		}
	}

	public override bool IsMultipleValues => properties.Count > 1;

	public override bool IsMultipleDifferentValues
	{
		get
		{
			//Asked every frame by every control: over a big selection of equal values that is a full walk each time
			if ( differentKnown && properties.Count >= ManyTargets && sinceDifferent < DifferentSeconds ) return different;

			different = FindDifferent();
			differentKnown = true;
			sinceDifferent = 0;

			return different;
		}
	}

	private const int ManyTargets = 64;
	private const float DifferentSeconds = 0.25f;
	private bool different, differentKnown;
	private RealTimeSince sinceDifferent;

	private bool FindDifferent()
	{
		var first = properties[0].GetValue<object>();

		for ( var i = 1; i < properties.Count; i++ )
			if ( !Equals( first, properties[i].GetValue<object>() ) ) return true;

		return false;
	}

	public override IEnumerable<SerializedProperty> MultipleProperties => properties;

	public override ParameterInfo[] MethodParameters => properties.First().MethodParameters;

	public override object Invoke()
	{
		object result = null;

		foreach ( var prop in properties )
			result = prop.Invoke();

		return result;
	}
}


//The same list on several objects edited as one; only indices all lists share are shown
internal sealed class MultiSerializedCollection : SerializedCollection
{
	readonly List<SerializedCollection> collections;

	public MultiSerializedCollection( List<SerializedCollection> collections )
	{
		this.collections = collections;

		KeyType = collections[0].KeyType;
		ValueType = collections[0].ValueType;
	}

	public override bool IsMultipleTargets => collections.Count > 1;

	public override IEnumerable<object> Targets => collections.SelectMany( x => x.Targets );

	public override bool IsValid => collections.All( x => x.IsValid );

	protected override void PrepareEnumerator()
	{
		//Asked again every time: any one of the lists can change underneath without this hearing of it.
		var lists = collections.Select( x => x.ToList() ).ToList();
		var count = lists.Min( x => x.Count );

		PropertyList = new List<SerializedProperty>( count );

		for ( var i = 0; i < count; i++ )
		{
			var index = i;
			PropertyList.Add( new MultiSerializedProperty( this, lists.Select( x => x[index] ) ) );
		}
	}

	public override bool Add( object value ) => Each( x => x.Add( value ), added: true );

	public override bool Add( object key, object value ) => Each( x => x.Add( key, value ), added: true );

	public override bool RemoveAt( object index ) => Each( x => x.RemoveAt( index ), added: false );

	public override bool Remove( SerializedProperty property )
	{
		PrepareEnumerator();

		var index = PropertyList.IndexOf( property );
		if ( index < 0 ) return false;

		//A list's entries go by where they are; anything else by its key.
		return KeyType == typeof( int )
			? RemoveAt( index )
			: Each( x => x.Remove( x.ElementAt( index ) ), added: false );
	}

	bool Each( Func<SerializedCollection, bool> change, bool added )
	{
		var any = false;

		foreach ( var collection in collections )
			any |= change( collection );

		if ( !any ) return false;

		if ( added ) OnEntryAdded?.Invoke();
		else OnEntryRemoved?.Invoke();

		return true;
	}
}

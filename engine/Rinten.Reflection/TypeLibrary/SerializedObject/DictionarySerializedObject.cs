using System.Text.Json;

namespace Rinten.Internal;

//An implementation of TypeLibrary which uses TypeLibrary to fill out properties
class DictionarySerializedObject : SerializedObject
{
	internal CaseInsensitiveDictionary<string> ValueStore;
	internal TypeDescription TypeDescription;

	public override string TypeIcon => TypeDescription?.Icon ?? base.TypeIcon;
	public override string TypeName => TypeDescription?.Name ?? base.TypeName;
	public override string TypeTitle => TypeDescription?.Title ?? base.TypeTitle;

	public DictionarySerializedObject( CaseInsensitiveDictionary<string> target, TypeDescription typeDescription )
	{
		ValueStore = target;
		TypeDescription = typeDescription;

		BuildProperties();
	}

	void BuildProperties()
	{
		PropertyList ??= new();

		foreach ( var prop in TypeDescription.Properties )
		{
			PropertyList.Add( new TypeSerializedProperty( this, prop ) );
		}
	}
}


file class TypeSerializedProperty : SerializedProperty
{
	private DictionarySerializedObject typeSerializedObject;
	private PropertyDescription prop;

	public override SerializedObject Parent => typeSerializedObject;
	public override string DisplayName => prop.Title;
	public override string Name => prop.Name;
	public override string Description => prop.Description;

	public TypeSerializedProperty( DictionarySerializedObject typeSerializedObject, PropertyDescription prop )
	{
		this.typeSerializedObject = typeSerializedObject;
		this.prop = prop;
	}

	public override void SetValue<T>( T value )
	{
		if ( value.GetType() == typeof( string ) )
		{
			NotePreChange();
			typeSerializedObject.ValueStore[prop.Name] = (string)(object)value;
			NoteChanged();
			return;
		}

		NotePreChange();
		typeSerializedObject.ValueStore[prop.Name] = JsonSerializer.Serialize( value, EnumConvert.Plain );
		NoteChanged();
	}

	public override T GetValue<T>( T defaultValue )
	{
		if ( !typeSerializedObject.ValueStore.TryGetValue( prop.Name, out var value ) )
			return defaultValue;

		if ( typeof( T ) == typeof( string ) )
			return (T)(object)value;

		return JsonSerializer.Deserialize<T>( value, EnumConvert.Plain );
	}
}

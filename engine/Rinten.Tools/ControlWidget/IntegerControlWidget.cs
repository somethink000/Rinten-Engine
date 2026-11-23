
using System;

namespace Editor;

[CustomEditor( typeof( char ) )]
[CustomEditor( typeof( byte ) )]
[CustomEditor( typeof( sbyte ) )]
[CustomEditor( typeof( short ) )]
[CustomEditor( typeof( ushort ) )]
[CustomEditor( typeof( int ) )]
[CustomEditor( typeof( uint ) )]
[CustomEditor( typeof( long ) )]
[CustomEditor( typeof( ulong ) )]
public class IntegerControlWidget : FloatControlWidget
{
	public IntegerControlWidget( SerializedProperty property ) : base( property )
	{
		Label = "i";
		HighlightColor = Theme.Blue;
	}

	internal new static string ValueToStringImpl( SerializedProperty property )
	{
		return property.GetValue<long>().ToString( "0" );
	}

	internal new static object StringToValueImpl( string text, SerializedProperty property )
	{
		Type underlyingType = Nullable.GetUnderlyingType( property.PropertyType ) ?? property.PropertyType;

		//ToLongEval goes through a double, which loses an Int64's low bits
		if ( long.TryParse( text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var exact ) )
			return Convert.ChangeType( exact, underlyingType );

		return Convert.ChangeType( text.ToLongEval( property.As.Long ), underlyingType );
	}

	protected override string ValueToString() => ValueToStringImpl( SerializedProperty );
	protected override object StringToValue( string text ) => StringToValueImpl( text, SerializedProperty );
}

using System.Globalization;
using System.Text.Json.Serialization;

namespace Rinten;

//Every enum by name, flags as "A, B", numbers read and written where no name fits - as JsonStringEnumConverter does, but one
//converter for all of them: its EnumConverter<T> is code and name tables made again for each enum, megabytes on a web module
internal sealed class EnumConvert : JsonConverterFactory
{
	/// <summary>The serializer's own defaults with enums by name, for code that has no options of the engine's to hand.</summary>
	internal static readonly JsonSerializerOptions Plain = new( JsonSerializerOptions.Default ) { Converters = { new EnumConvert() } };

	public override bool CanConvert( Type typeToConvert ) => ( Nullable.GetUnderlyingType( typeToConvert ) ?? typeToConvert ).IsEnum;

	public override JsonConverter CreateConverter( Type typeToConvert, JsonSerializerOptions options ) => new EnumByName( Nullable.GetUnderlyingType( typeToConvert ) ?? typeToConvert );

	//Over object rather than Enum: the serializer wants the converter's type related to the member's, and a nullable enum is not an Enum
	private sealed class EnumByName( Type type ) : JsonConverter<object>
	{
		public override bool CanConvert( Type typeToConvert ) => ( Nullable.GetUnderlyingType( typeToConvert ) ?? typeToConvert ) == type;

		public override object Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
		{
			if ( reader.TokenType == JsonTokenType.Number ) return Number( ref reader );
			if ( reader.TokenType == JsonTokenType.String ) return Named( reader.GetString() );

			throw new JsonException( $"{type.Name}: expected a name or a number, got {reader.TokenType}" );
		}

		public override void Write( Utf8JsonWriter writer, object value, JsonSerializerOptions options )
		{
			var name = value.ToString();

			if ( !Numeric( name ) ) writer.WriteStringValue( name );
			else if ( Unsigned ) writer.WriteNumberValue( Convert.ToUInt64( value, CultureInfo.InvariantCulture ) );
			else writer.WriteNumberValue( Convert.ToInt64( value, CultureInfo.InvariantCulture ) );
		}

		public override object ReadAsPropertyName( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options ) => Named( reader.GetString() );

		public override void WriteAsPropertyName( Utf8JsonWriter writer, object value, JsonSerializerOptions options ) => writer.WritePropertyName( value.ToString() );

		//A name, names joined by commas, or a number written as text
		private object Named( string text )
		{
			if ( text is not null && Enum.TryParse( type, text, ignoreCase: true, out var value ) ) return value;

			throw new JsonException( $"{type.Name} has no value \"{text}\"" );
		}

		private object Number( ref Utf8JsonReader reader )
		{
			return Unsigned ? Enum.ToObject( type, reader.GetUInt64() ) : Enum.ToObject( type, reader.GetInt64() );
		}

		private bool Unsigned => Type.GetTypeCode( Enum.GetUnderlyingType( type ) ) is TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64;

		//What ToString gives a value no name covers
		private static bool Numeric( string name ) => name.Length > 0 && ( char.IsDigit( name[0] ) || name[0] == '-' );
	}
}

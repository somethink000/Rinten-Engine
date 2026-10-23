using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Rinten;

public static partial class Json
{
	//Closed generic types System.Text.Json needs, so the AOT web build can pre-generate them
	internal static HashSet<System.Type> MadeGenerics( IEnumerable<System.Type> types ) => MadeGenerics( types, out _ );

	//The same, and every type the serializer was asked to describe on the way - what it builds by reflection, constructors and all.
	internal static HashSet<System.Type> MadeGenerics( IEnumerable<System.Type> types, out HashSet<System.Type> described )
	{
		var made = new HashSet<System.Type>();

		void Recorded( JsonTypeInfo info )
		{
			lock ( made ) Note( made, info );
		}

		var ours = new JsonSerializerOptions( options ) { TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier( Recorded ) };
		var plain = new JsonSerializerOptions( EnumConvert.Plain ) { TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier( Recorded ) };

		//The records a converter class reads and writes through the serializer - its own nested types, often in a file-local class no library lists.
		var queueSeed = new List<System.Type>( types );

		foreach ( var assembly in types.Where( x => x is not null ).Select( x => x.Assembly ).Distinct() )
		{
			System.Type[] all;
			try { all = assembly.GetTypes(); } catch ( System.Reflection.ReflectionTypeLoadException e ) { all = e.Types.Where( x => x is not null ).ToArray(); }

			foreach ( var converter in all.Where( x => typeof( System.Text.Json.Serialization.JsonConverter ).IsAssignableFrom( x ) ) )
				queueSeed.AddRange( converter.GetNestedTypes( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic ) );
		}

		//To the closure: the serializer makes a member's own metadata only when it first reads or writes
		//one, so a dictionary of enums on a settings class was never made by asking for the class alone.
		var queue = new Queue<System.Type>( queueSeed );
		var asked = new HashSet<System.Type>();

		while ( queue.Count > 0 )
		{
			var type = queue.Dequeue();
			if ( type is null || type.IsByRef || type.IsPointer || type.IsByRefLike || type.ContainsGenericParameters || CompilerMade( type ) || !asked.Add( type ) ) continue;

			foreach ( var each in new[] { ours, plain } )
			{
				try
				{
					var info = each.GetTypeInfo( type );

					foreach ( var property in info.Properties ) queue.Enqueue( property.PropertyType );
					if ( info.ElementType is { } element ) queue.Enqueue( element );
					if ( info.KeyType is { } key ) queue.Enqueue( key );

					//A type that writes itself hands its own nested records to the serializer in a
					//container no member names: those, in the containers a converter reaches for.
					if ( WritesItself( type ) ) foreach ( var nested in type.GetNestedTypes( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic ) ) Nested( queue, nested );
				}
				catch
				{
					//Not something the serializer takes.
				}
			}
		}

		//Round-trips each value type and settings class once so converter-made generics get instantiated
		foreach ( var type in asked )
		{
			if ( !( type.IsValueType && !type.IsPrimitive && !type.IsEnum ) && !typeof( ConfigData ).IsAssignableFrom( type ) ) continue;
			if ( type.IsAbstract || type.IsGenericTypeDefinition ) continue;

			foreach ( var each in new[] { ours, plain } )
			{
				try
				{
					var value = System.Activator.CreateInstance( type );
					var text = JsonSerializer.Serialize( value, type, each );
					_ = JsonSerializer.Deserialize( text, type, each );
				}
				catch
				{
					//It wanted more than its default to be written.
				}
			}
		}

		//And whatever the running editor has already built for the options it uses.
		lock ( made ) NoteBuilt( made, asked );

		described = asked;
		return made;
	}

	//A state machine, a closure or a lambda's class: in the library, never serialized, and each would cost a set of generics
	private static bool CompilerMade( System.Type type )
	{
		for ( var each = type; each is not null; each = each.DeclaringType )
		{
			if ( each.Name.Contains( '<' ) || each.IsDefined( typeof( System.Runtime.CompilerServices.CompilerGeneratedAttribute ), false ) ) return true;
		}

		return false;
	}

	private static bool WritesItself( System.Type type )
		=> typeof( IJsonConvert ).IsAssignableFrom( type ) || typeof( ConfigData ).IsAssignableFrom( type )
			|| type.IsDefined( typeof( System.Text.Json.Serialization.JsonConverterAttribute ), true );

	//A nested record of a type that writes itself, and - where it is a value, whose
	//containers have no shared code - the containers a converter keeps those in.
	private static void Nested( Queue<System.Type> queue, System.Type element )
	{
		if ( element.ContainsGenericParameters || element.IsByRefLike || element.IsAbstract && element.IsSealed ) return;

		queue.Enqueue( element );

		if ( !element.IsValueType || element.IsEnum ) return;

		try
		{
			queue.Enqueue( element.MakeArrayType() );
			queue.Enqueue( typeof( List<> ).MakeGenericType( element ) );
			queue.Enqueue( typeof( System.Collections.Immutable.ImmutableList<> ).MakeGenericType( element ) );
		}
		catch
		{
			//A type no container takes.
		}
	}

	private static void Note( HashSet<System.Type> into, JsonTypeInfo info )
	{
		Add( into, info.GetType() );
		Add( into, info.Converter?.GetType() );

		foreach ( var property in info.Properties )
		{
			Add( into, property.GetType() );
			Add( into, property.CustomConverter?.GetType() );
		}

		static void Add( HashSet<System.Type> into, System.Type type )
		{
			if ( type is { IsGenericType: true, ContainsGenericParameters: false } ) into.Add( type );
		}
	}

	//What the running editor built for the plain and the engine's options, which a modifier cannot reach - they are read-only.
	//Read out of the options' own cache; a tool for making the list, not something the game leans on.
	private static void NoteBuilt( HashSet<System.Type> into, HashSet<System.Type> described = null )
	{
		const System.Reflection.BindingFlags Inside = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

		foreach ( var each in new[] { EnumConvert.Plain, options } )
		{
			try
			{
				var context = typeof( JsonSerializerOptions ).GetField( "_cachingContext", Inside )?.GetValue( each );
				if ( context?.GetType().GetField( "_cache", Inside )?.GetValue( context ) is not System.Collections.IDictionary cache ) continue;

				foreach ( var entry in cache.Values )
				{
					if ( entry is null ) continue;

					foreach ( var field in entry.GetType().GetFields( Inside | System.Reflection.BindingFlags.Public ) )
					{
						if ( field.GetValue( entry ) is not JsonTypeInfo info ) continue;

						Note( into, info );
						described?.Add( info.Type );
					}
				}
			}
			catch ( System.Exception e )
			{
				Log.Warning( $"json generics: an options cache could not be read - {e.Message}" );
			}
		}
	}

	//RINTEN_JSON_RECORD=<file> : the same, recorded from a running game, one type per line - for what is serialized
	//only by running, from inside the engine's own converters where no member says so. Off unless asked for.
	private static void Record( JsonSerializerOptions options )
	{
		var path = System.Environment.GetEnvironmentVariable( "RINTEN_JSON_RECORD" );
		if ( string.IsNullOrWhiteSpace( path ) ) return;

		recordPath = path;
		options.TypeInfoResolver = ( options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver() ).WithAddedModifier( info =>
		{
			lock ( recorded )
			{
				Note( recorded, info );
				if ( recorded.Count - recordedWritten >= 64 ) WriteRecord();
			}
		} );

		System.AppDomain.CurrentDomain.ProcessExit += ( _, _ ) => WriteRecord();
	}

	private static string recordPath;
	private static readonly HashSet<System.Type> recorded = new();
	private static int recordedWritten;

	private static void WriteRecord()
	{
		lock ( recorded )
		{
			try
			{
				NoteBuilt( recorded );

				var lines = new HashSet<string>( recorded.Select( x => x.AssemblyQualifiedName ).Where( x => x is not null ) );
				if ( System.IO.File.Exists( recordPath ) ) lines.UnionWith( System.IO.File.ReadAllLines( recordPath ) );
				System.IO.File.WriteAllLines( recordPath, lines.Order() );

				recordedWritten = recorded.Count;
			}
			catch ( System.Exception e )
			{
				Log.Warning( $"json record: {e.Message}" );
			}
		}
	}
}

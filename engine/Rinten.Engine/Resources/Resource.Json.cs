using Rinten.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rinten;

public partial class Resource
{
	/// <summary>Embedded data for this resource</summary>
	[Hide, JsonIgnore]
	public EmbeddedResource? EmbeddedResource { get; set; }

	//Read the resource from a JSON element. This is usually a string, describing the path to the resource
	static object IJsonConvert.JsonRead( ref Utf8JsonReader reader, Type typeToConvert )
	{
		return LoadJsonReference( typeToConvert, ref reader );
	}

	//Write the resource reference to a json element. This is usually a string, describing the path to the resource
	static void IJsonConvert.JsonWrite( object value, Utf8JsonWriter writer )
	{
		if ( value is not Resource resource )
		{
			writer.WriteNullValue();
			return;
		}

		resource.WriteJsonReference( writer );
	}

	//Load a resource from a path with improved deferred loading support
	internal static Resource LoadFromPath( Type typeToConvert, string path )
	{
		if ( typeToConvert.IsAssignableTo( typeof( GameResource ) ) )
		{
			//GameResource: Fetch it from the cache, or setup a deferred load

			if ( !path.EndsWith( "_c" ) ) path += "_c";

			//at this point the type may be a common base class but we want to make sure we're loading this resource as the type it ACTUALLY is
			var extension = System.IO.Path.GetExtension( path );
			if ( Game.Resources.TryGetType( extension, out var resourceAttribute ) )
			{
				typeToConvert = resourceAttribute.TargetType;
			}

			return GameResource.GetPromise( typeToConvert, path );
		}

		//For native resource types, use direct loading
		return Load( typeToConvert, path );
	}

	//Load a resource reference from JSON data. Handles both string paths and embedded resource objects for all resource types.
	internal static Resource LoadJsonReference( Type targetType, ref Utf8JsonReader reader )
	{
		//Just a path?
		if ( reader.TokenType == JsonTokenType.String )
		{
			return LoadFromPath( targetType, reader.GetString() );
		}

		if ( reader.TokenType != JsonTokenType.StartObject )
		{
			//null, a number, anything else - no reference. Skip whatever it was, so the reader is left where the caller expects it.
			reader.Skip();
			return default;
		}

		using var doc = JsonDocument.ParseValue( ref reader );
		var root = doc.RootElement;

		//An object reference without $compiler ({"Id", "Path"}): read the path only, write it back plain
		if ( !root.TryGetProperty( "$compiler", out _ ) )
		{
			if ( root.TryGetProperty( "Path", out var pathElement ) &&
				pathElement.ValueKind == JsonValueKind.String &&
				pathElement.GetString() is { Length: > 0 } referenced )
			{
				return LoadFromPath( targetType, referenced );
			}

			return default;
		}

		//from an object (embedded resource)
		{
			EmbeddedResource serializedResource;

			try
			{
				serializedResource = root.Deserialize<EmbeddedResource>();
			}
			catch ( System.Exception e )
			{
				Log.Warning( e, $"Couldn't deserialize embedded resource data for {targetType.Name}" );
				return default;
			}

			//If there's a compiled version then use it, but store the generation data too
			if ( !string.IsNullOrWhiteSpace( serializedResource.CompiledPath ) )
			{
				var resource = LoadFromPath( targetType, serializedResource.CompiledPath );

				//Store embedded resource data if the resource supports it
				if ( resource is not null )
				{
					resource.EmbeddedResource = serializedResource;

					return resource;
				}

				//Compiled file missing: still build it from its embedded description
			}

			//This is an embedded type, it's edited inline wherever it is We could make this applicable
			//for Resources too surely, the only thing stopping us is PushDeserializationScope
			if ( targetType.IsAssignableTo( typeof( GameResource ) ) && serializedResource.ResourceCompiler == "embed" )
			{
				//Inherited resource
				if ( !string.IsNullOrEmpty( serializedResource.TypeName ) )
				{
					var type = Game.TypeLibrary.GetType( serializedResource.TypeName );
					if ( type is not null )
					{
						targetType = type.TargetType;
					}
				}

				var resource = System.Activator.CreateInstance( targetType ) as GameResource;
				if ( resource is not null )
				{
					resource.Deserialize( serializedResource.Data );
					resource.EmbeddedResource = serializedResource;

					return resource;
				}
			}

			var options = ResourceGenerator.Options.Default;
			var generated = ResourceGenerator.CreateResource( serializedResource, options, targetType );

			if ( generated is not null )
			{
				//Keep what it was made from, so saving writes the reference back the way it came rather than losing the recipe.
				generated.EmbeddedResource = serializedResource;

				//Register under its compiled path so lookups by that path find it
				if ( !string.IsNullOrWhiteSpace( serializedResource.CompiledPath ) )
				{
					generated.RegisterWeakResourceId( serializedResource.CompiledPath );
				}
			}

			return generated;
		}
	}

	//Allows a resource type to override how it reference entry is written. This is generally
	//just going to be a path to the on disk resource, but we can use this to store metadata too.
	internal virtual void WriteJsonReference( Utf8JsonWriter writer )
	{
		//if we have an embedded resource, write that instead of the path
		if ( EmbeddedResource.HasValue )
		{
			//This is an embedded resource, so we want to store all the data inline
			if ( EmbeddedResource.Value is var resource && resource.ResourceCompiler == "embed" )
			{
				EmbeddedResource = resource with { Data = Json.SerializeAsObject( this ) };
			}

			JsonSerializer.Serialize( writer, EmbeddedResource, EnumConvert.Plain );
			return;
		}

		//default write ResourcePath
		writer.WriteStringValue( ResourcePath );
	}
}

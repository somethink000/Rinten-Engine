using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Rinten.Internal;

/// <summary>Index of which types declare members with which attribute, read from metadata to avoid reflection</summary>
public partial class TypeLibrary
{
	//Attribute type -> declaring types, plus whether a member is inherited (non-private instance)
	readonly ConcurrentDictionary<Type, ConcurrentDictionary<Type, bool>> memberAttributeOwners = new();

	//Types declaring a member with an attribute that would not resolve - nobody can say what it is.
	readonly ConcurrentDictionary<Type, byte> ownersOfUnnamedAttributes = new();

	//Assemblies with no metadata to read, made at runtime - nobody can say what their members carry.
	readonly ConcurrentDictionary<Assembly, byte> unindexedAssemblies = new();

	//The types whose members can carry an attribute of this kind: those declaring a
	//member with one, and those inheriting a member with one, which list it as theirs.
	IReadOnlyList<TypeDescription> TypesWithMemberAttribute<T>() where T : Attribute
		=> Cached( HashCode.Combine( "TypesWithMemberAttribute", typeof( T ) ), this, static lib =>
		{
			//Nobody can say what an unnamed attribute is, or whether its member is inherited.
			var declaring = new HashSet<Type>( lib.ownersOfUnnamedAttributes.Keys );
			var passing = new HashSet<Type>( lib.ownersOfUnnamedAttributes.Keys );

			foreach ( var (attribute, types) in lib.memberAttributeOwners )
			{
				if ( !attribute.IsAssignableTo( typeof( T ) ) ) continue;

				foreach ( var (type, inherited) in types )
				{
					declaring.Add( type );
					if ( inherited ) passing.Add( type );
				}
			}

			if ( declaring.Count == 0 && lib.unindexedAssemblies.IsEmpty ) return ImmutableList<TypeDescription>.Empty;

			return lib.typedata.Values.Where( x => lib.MayCarry( x.TargetType, declaring, passing ) ).ToImmutableList();
		} );

	//Whether a type declares one of these members, or inherits one from a type passing it down, or
	//is or comes from an assembly nobody could read - generic types compared by their definition.
	bool MayCarry( Type type, HashSet<Type> declaring, HashSet<Type> passing )
	{
		if ( Among( type, declaring ) ) return true;

		for ( var at = type; at is not null; at = at.BaseType )
		{
			if ( unindexedAssemblies.ContainsKey( at.Assembly ) ) return true;
			if ( !ReferenceEquals( at, type ) && Among( at, passing ) ) return true;
		}

		return false;
	}

	static bool Among( Type type, HashSet<Type> set )
		=> set.Contains( type ) || ( type.IsConstructedGenericType && set.Contains( type.GetGenericTypeDefinition() ) );

	//Indexes member attributes from raw metadata without describing or resolving the members
	unsafe void IndexMemberAttributes( Assembly assembly )
	{
		if ( !assembly.TryGetRawMetadata( out var blob, out var length ) )
		{
			log.Info( $"{assembly.GetName().Name}: no metadata to read member attributes from - every scan looks at its types" );
			unindexedAssemblies[assembly] = 0;
			return;
		}

		var reader = new MetadataReader( blob, length );
		var module = assembly.ManifestModule;

		//Properties and events do not say which type declares them; their types do.
		var declaring = new Dictionary<EntityHandle, TypeDefinitionHandle>();

		foreach ( var typeHandle in reader.TypeDefinitions )
		{
			var definition = reader.GetTypeDefinition( typeHandle );

			foreach ( var property in definition.GetProperties() ) declaring[property] = typeHandle;
			foreach ( var e in definition.GetEvents() ) declaring[e] = typeHandle;
		}

		var attributeTypes = new Dictionary<EntityHandle, Type>();
		var ownerTypes = new Dictionary<TypeDefinitionHandle, Type>();

		var unnamed = 0;
		string firstUnnamed = null;

		foreach ( var handle in reader.CustomAttributes )
		{
			var attribute = reader.GetCustomAttribute( handle );
			var parent = attribute.Parent;

			TypeDefinitionHandle owner;
			bool inherited;

			switch ( parent.Kind )
			{
				case HandleKind.MethodDefinition:
				{
					var method = reader.GetMethodDefinition( (MethodDefinitionHandle)parent );
					owner = method.GetDeclaringType();
					inherited = Inherited( method.Attributes );
					break;
				}

				case HandleKind.FieldDefinition:
				{
					var field = reader.GetFieldDefinition( (FieldDefinitionHandle)parent );
					owner = field.GetDeclaringType();
					inherited = ( field.Attributes & System.Reflection.FieldAttributes.Static ) == 0
						&& ( field.Attributes & System.Reflection.FieldAttributes.FieldAccessMask ) != System.Reflection.FieldAttributes.Private;
					break;
				}

				case HandleKind.PropertyDefinition:
				{
					if ( !declaring.TryGetValue( parent, out owner ) ) continue;
					var accessors = reader.GetPropertyDefinition( (PropertyDefinitionHandle)parent ).GetAccessors();
					inherited = Inherited( reader, accessors.Getter ) || Inherited( reader, accessors.Setter );
					break;
				}

				case HandleKind.EventDefinition:
				{
					if ( !declaring.TryGetValue( parent, out owner ) ) continue;
					var accessors = reader.GetEventDefinition( (EventDefinitionHandle)parent ).GetAccessors();
					inherited = Inherited( reader, accessors.Adder ) || Inherited( reader, accessors.Remover );
					break;
				}

				default:
					continue;
			}

			if ( !ownerTypes.TryGetValue( owner, out var ownerType ) )
			{
				ownerType = module.ResolveType( MetadataTokens.GetToken( owner ) );
				ownerTypes[owner] = ownerType;
			}

			if ( !attributeTypes.TryGetValue( attribute.Constructor, out var attributeType ) )
			{
				attributeType = AttributeTypeOf( module, attribute.Constructor, out var why );
				attributeTypes[attribute.Constructor] = attributeType;

				if ( attributeType is null ) firstUnnamed ??= $"on {ownerType}: {why}";
			}

			if ( attributeType is null )
			{
				unnamed++;
				ownersOfUnnamedAttributes[ownerType] = 0;
				continue;
			}

			memberAttributeOwners.GetOrAdd( attributeType, static _ => new() )
				.AddOrUpdate( ownerType, inherited, ( _, was ) => was || inherited );
		}

		//Said, because each of these widens every scan to the types wearing them and everything under those.
		if ( unnamed > 0 )
			log.Info( $"{assembly.GetName().Name}: {unnamed} member attributes would not resolve - every scan looks at the types wearing them; the first {firstUnnamed}" );
	}

	//Whether a method is listed by the types deriving from its own: an instance method, not private, not a constructor.
	static bool Inherited( System.Reflection.MethodAttributes attributes )
		=> ( attributes & System.Reflection.MethodAttributes.Static ) == 0
			&& ( attributes & System.Reflection.MethodAttributes.RTSpecialName ) == 0
			&& ( attributes & System.Reflection.MethodAttributes.MemberAccessMask ) != System.Reflection.MethodAttributes.Private;

	static bool Inherited( MetadataReader reader, MethodDefinitionHandle accessor )
		=> !accessor.IsNil && Inherited( reader.GetMethodDefinition( accessor ).Attributes );

	//The type an attribute constructor belongs to, or null and why if it will not resolve.
	static Type AttributeTypeOf( Module module, EntityHandle constructor, out string why )
	{
		why = null;

		try
		{
			return module.ResolveMethod( MetadataTokens.GetToken( constructor ) )?.DeclaringType;
		}
		catch ( System.Exception e )
		{
			//A missing reference or a shape this cannot name - whatever it is, the members wearing it are looked at by every scan rather than by none.
			why = e.Message;
			return null;
		}
	}

	//An assembly's types and attributes out of the index, as it goes.
	void ForgetMemberAttributes( Assembly assembly )
	{
		unindexedAssemblies.TryRemove( assembly, out _ );

		foreach ( var type in ownersOfUnnamedAttributes.Keys )
		{
			if ( type.Assembly == assembly ) ownersOfUnnamedAttributes.TryRemove( type, out _ );
		}

		foreach ( var (attribute, types) in memberAttributeOwners )
		{
			if ( attribute.Assembly == assembly )
			{
				memberAttributeOwners.TryRemove( attribute, out _ );
				continue;
			}

			foreach ( var type in types.Keys )
			{
				if ( type.Assembly == assembly ) types.TryRemove( type, out _ );
			}
		}
	}

	//Describes, now, the types of this assembly whose members code generation wraps - an RPC, a synced or watched property, a console
	//variable. Those are asked for by identity from the network and from generated code, which cannot say which type to describe.
	void DescribeCodeGenerated( Assembly assembly, Type[] types )
	{
		IEnumerable<Type> wanted;

		if ( unindexedAssemblies.ContainsKey( assembly ) )
		{
			//Nothing says which, so all of them - which is what every one was before.
			wanted = types;
		}
		else
		{
			var mine = new HashSet<Type>( types );
			var owners = new HashSet<Type>( ownersOfUnnamedAttributes.Keys.Where( mine.Contains ) );

			foreach ( var (attribute, declaring) in memberAttributeOwners )
			{
				if ( !attribute.IsDefined( typeof( CodeGeneratorAttribute ), true ) ) continue;

				owners.UnionWith( declaring.Keys.Where( mine.Contains ) );
			}

			wanted = owners;
		}

		System.Threading.Tasks.Parallel.ForEach( wanted, t =>
		{
			if ( typedata.TryGetValue( t, out var description ) ) description.EnsureMembersBuilt();
		} );
	}
}

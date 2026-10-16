using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;

namespace Editor;

//Tells the AOT web compiler to make serializer generics over game value types, impossible at runtime
internal static class WebDirectives
{
	//An rd.xml for these closed generic types.
	internal static string For( IEnumerable<Type> made, ISet<string> inModule )
	{
		var byAssembly = new SortedDictionary<string, SortedSet<string>>( StringComparer.Ordinal );
		var accessors = new SortedSet<string>( StringComparer.Ordinal );

		void Want( string assembly, string line )
		{
			if ( !byAssembly.TryGetValue( assembly, out var lines ) ) byAssembly[assembly] = lines = new( StringComparer.Ordinal );
			lines.Add( line );
		}

		foreach ( var type in made )
		{
			if ( !Valued( type ) || !Within( type, inModule ) ) continue;

			var definition = type.GetGenericTypeDefinition();
			Want( definition.Assembly.GetName().Name, $"   <Type Name=\"{Escape( Name( type ) )}\"><Method Name=\".ctor\" Dynamic=\"Required\" /></Type>" );

			var args = type.GetGenericArguments();

			//A property's getter and setter are made generic over its type by the serializer's member accessor.
			if ( definition.FullName == "System.Text.Json.Serialization.Metadata.JsonPropertyInfo`1" )
			{
				foreach ( var method in new[] { "CreatePropertyGetter", "CreatePropertySetter", "CreateFieldGetter", "CreateFieldSetter" } )
					accessors.Add( $"    <Method Name=\"{method}\" Dynamic=\"Required\"><GenericArgument Name=\"{Escape( Qualified( args[0] ) )}\" /></Method>" );
			}

			//An immutable collection is filled through its factory's CreateRange<T>, found by reflection.
			if ( definition.FullName == "System.Text.Json.Serialization.Converters.ImmutableEnumerableOfTConverterWithReflection`2" && args[0].IsGenericType )
			{
				var collection = args[0].GetGenericTypeDefinition();
				var factory = collection.FullName.Split( '`' )[0];
				Want( collection.Assembly.GetName().Name, $"   <Type Name=\"{factory}\"><Method Name=\"CreateRange\" Dynamic=\"Required\"><GenericArgument Name=\"{Escape( Qualified( args[1] ) )}\" /></Method></Type>" );
			}
		}

		var text = new StringBuilder();
		text.AppendLine( "<Directives xmlns=\"http://schemas.microsoft.com/netfx/2013/01/metadata\">" );
		text.AppendLine( " <Application>" );

		foreach ( var (assembly, lines) in byAssembly )
		{
			text.AppendLine( $"  <Assembly Name=\"{assembly}\">" );
			foreach ( var line in lines ) text.AppendLine( line );

			if ( assembly == "System.Text.Json" && accessors.Count > 0 )
			{
				text.AppendLine( "   <Type Name=\"System.Text.Json.Serialization.Metadata.ReflectionMemberAccessor\" Dynamic=\"Required All\">" );
				foreach ( var line in accessors ) text.AppendLine( line );
				text.AppendLine( "   </Type>" );
			}

			text.AppendLine( "  </Assembly>" );
		}

		text.AppendLine( " </Application>" );
		text.AppendLine( "</Directives>" );

		return text.ToString();
	}

	//An ILLink descriptor for types made by name - what the type library and the serializer hold - kept as far as reflection reads them
	internal static string Descriptor( IEnumerable<Type> kept, ISet<string> inModule )
	{
		var text = new StringBuilder( "<linker>\n" );

		foreach ( var assembly in kept.Where( x => !x.ContainsGenericParameters && x.FullName is not null && !x.FullName.Contains( '<' ) && Within( x, inModule ) )
			.GroupBy( x => x.Assembly.GetName().Name ).OrderBy( x => x.Key, StringComparer.Ordinal ) )
		{
			text.AppendLine( $" <assembly fullname=\"{Escape( assembly.Key )}\">" );

			foreach ( var type in assembly.OrderBy( x => x.FullName, StringComparer.Ordinal ) )
				text.AppendLine( Reflected( type ) );

			text.AppendLine( " </assembly>" );
		}

		return text.Append( "</linker>\n" ).ToString();
	}

	//What reflection reads off a type made by name: its fields, constructors, property and event accessors, methods an attribute marks
	//(RPCs, commands, graph nodes), and overrides and interface methods, which "does it override OnDraw" asks. The rest only where called
	private static string Reflected( Type type )
	{
		const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

		var names = new SortedSet<string>( StringComparer.Ordinal );

		if ( type.GetConstructors( BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance ).Length > 0 ) names.Add( ".ctor" );

		foreach ( var property in type.GetProperties( all ) )
			foreach ( var accessor in property.GetAccessors( true ) ) names.Add( accessor.Name );

		foreach ( var each in type.GetEvents( all ) )
		{
			if ( each.AddMethod is { } add ) names.Add( add.Name );
			if ( each.RemoveMethod is { } remove ) names.Add( remove.Name );
		}

		var implementing = new HashSet<MethodInfo>();

		if ( !type.IsInterface )
		{
			foreach ( var contract in type.GetInterfaces() )
			{
				try { implementing.UnionWith( type.GetInterfaceMap( contract ).TargetMethods.Where( x => x.DeclaringType == type ) ); }
				catch ( ArgumentException ) { }
			}
		}

		foreach ( var method in type.GetMethods( all ) )
		{
			if ( method.IsGenericMethodDefinition || method.IsSpecialName ) continue;

			var marked = method.CustomAttributes.Any( x => !x.AttributeType.Namespace?.StartsWith( "System.Runtime.CompilerServices", StringComparison.Ordinal ) ?? true );
			var overrides = method.IsVirtual && method.GetBaseDefinition().DeclaringType != type;

			if ( marked || overrides || implementing.Contains( method ) ) names.Add( method.Name );
		}

		//Nested types are written Outer/Inner in a descriptor
		var text = new StringBuilder( $"  <type fullname=\"{Escape( type.FullName.Replace( '+', '/' ) )}\" preserve=\"fields\">" );

		foreach ( var name in names.Where( x => !x.Contains( '<' ) ) )
			text.Append( $"<method name=\"{Escape( name )}\" />" );

		return text.Append( "</type>" ).ToString();
	}

	//Whether every assembly the type names - its own, its arguments' at any depth - is in the module.
	private static bool Within( Type type, ISet<string> inModule )
	{
		if ( type.IsArray ) return Within( type.GetElementType(), inModule );
		if ( !inModule.Contains( type.Assembly.GetName().Name ) ) return false;
		return !type.IsGenericType || type.GetGenericArguments().All( a => Within( a, inModule ) );
	}

	//Whether a value type is among the arguments, at any depth.
	private static bool Valued( Type type )
		=> type.IsGenericType && type.GetGenericArguments().Any( a => a.IsValueType || ( a.IsArray && a.GetElementType().IsValueType ) || Valued( a ) );

	//A type's name as a directive writes it: generic arguments in double brackets, assembly-qualified, no versions.
	private static string Name( Type type )
	{
		if ( type.IsArray ) return Name( type.GetElementType() ) + "[]";
		if ( !type.IsGenericType ) return type.FullName;

		return type.GetGenericTypeDefinition().FullName + "[" + string.Join( ",", type.GetGenericArguments().Select( a => "[" + Qualified( a ) + "]" ) ) + "]";
	}

	private static string Qualified( Type type )
		=> $"{Name( type )}, {( type.IsArray ? type.GetElementType() : type ).Assembly.GetName().Name}";

	private static string Escape( string text ) => SecurityElement.Escape( text );
}

using Editor.NodeEditor;
using Rinten.UI.NodeGraph;
using Facepunch.ActionGraphs;
using Rinten;
using Rinten.ActionGraphs;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Rinten.Diagnostics;

namespace Editor.ActionGraphs;

//The menu of types an action graph offers, wherever one has to be picked. A property row is a panel control now - TypeControl
//- but the graph view still asks for a type from inside a widget context menu, which is the part that lives on here.
internal static class TypeMenu
{
	private static HashSet<Type> SystemTypes { get; } = new()
	{
		typeof(int),
		typeof(float),
		typeof(string),
		typeof(bool),
		typeof(GameObject),
		typeof(GameTransform),
		typeof(Color),
		typeof(Vector2),
		typeof(Vector3),
		typeof(Vector4),
		typeof(Angles),
		typeof(Rotation),
		typeof(object)
	};

	private static TypeOption GetTypeOption( Type type )
	{
		var typeDesc = TypeLibrary.GetType( type );

		var path = typeDesc is { }
			? GetTypePath( typeDesc )
			: $"System/{type.Name}";

		return new TypeOption( Menu.GetSplitPath( path ),
			type,
			type.Name,
			typeDesc?.Description,
			typeDesc?.Icon );
	}

	private static string FormatAssemblyName( Assembly asm )
	{
		var name = asm.GetName().Name!;

		if ( name.StartsWith( "package.", StringComparison.OrdinalIgnoreCase ) )
		{
			name = name.Substring( "package.".Length );
		}

		if ( name.StartsWith( "local.", StringComparison.OrdinalIgnoreCase ) )
		{
			name = name.Substring( "local.".Length );
		}

		return name.ToTitleCase();
	}

	private static string GetAssemblyQualifiedPath( TypeDescription typeDesc )
	{
		var path = !string.IsNullOrEmpty( typeDesc.Namespace )
			? typeDesc.Namespace.Replace( '.', '/' )
			: FormatAssemblyName( typeDesc.TargetType.Assembly );

		return path;
	}

	private static string GetTypePath( TypeDescription typeDesc )
	{
		if ( typeDesc.TargetType.DeclaringType != null )
		{
			return $"{GetTypePath( TypeLibrary.GetType( typeDesc.TargetType.DeclaringType ) )}/{typeDesc.Title}";
		}

		var prefix = typeDesc.Group ?? GetAssemblyQualifiedPath( typeDesc );
		var icon = typeDesc.Icon;

		if ( typeDesc.TargetType.IsAssignableTo( typeof( Resource ) ) )
		{
			prefix = $"Resource/{prefix}";
			icon ??= "description";
		}
		else if ( typeDesc.TargetType.IsAssignableTo( typeof( Component ) ) )
		{
			prefix = $"Component/{prefix}";
			icon ??= "category";
		}
		else if ( SystemTypes.Contains( typeDesc.TargetType ) )
		{
			prefix = typeDesc.Group ?? typeDesc.Namespace?.Replace( '.', '/' ) ?? "Rinten";
		}

		icon ??= "check_box_outline_blank";

		return $"{prefix}/{typeDesc.Title}:{icon}@2000";
	}

	private record TypeOption( Menu.PathElement[] Path, Type Type, string Title, string Description, string Icon );

	private static bool SatisfiesConstraints( Type type, Type genericParam )
	{
		if ( genericParam is null )
		{
			return true;
		}

		if ( !genericParam.GenericParameterAttributes.AreSatisfiedBy( type ) )
		{
			return false;
		}

		foreach ( var constraint in genericParam.GetGenericParameterConstraints() )
		{
			if ( ApplyGenericArgument( constraint, genericParam, type ) is not { } resolvedConstraint )
			{
				return false;
			}

			if ( resolvedConstraint.IsGenericParameter )
			{
				//A bit worried about a stack overflow here

				if ( resolvedConstraint != genericParam && !SatisfiesConstraints( type, resolvedConstraint ) )
				{
					return false;
				}
			}
			else if ( !type.IsAssignableTo( resolvedConstraint ) )
			{
				return false;
			}
		}

		foreach ( var hasImpl in genericParam.GetCustomAttributes<HasImplementationAttribute>() )
		{
			//Easy case

			if ( type.IsAssignableTo( hasImpl.BaseType ) )
			{
				continue;
			}

			var anyImplementing = TypeLibrary.GetTypes( hasImpl.BaseType )
				.Where( x => !x.IsAbstract && !x.IsInterface )
				.Any( x => x.TargetType.IsAssignableTo( type ) );

			if ( !anyImplementing )
			{
				return false;
			}
		}

		return true;
	}

	//Given a type Foo<T1,T2> , a genericParam T1 , and a genericArg Bar , replace references to T1 with Bar to
	//create the type Foo<Bar,T2> . Note that genericParam might come from some other method or type definition.
	[return: MaybeNull]
	private static Type ApplyGenericArgument( Type type, Type genericParam, Type genericArg )
	{
		if ( type == genericParam ) return genericArg;
		if ( !type.ContainsGenericParameters ) return type;

		if ( type.IsByRef || type.IsArray )
		{
			if ( ApplyGenericArgument( type.GetElementType(), genericParam, genericArg ) is not { } elemType )
			{
				return null;
			}

			if ( type.IsByRef )
			{
				return elemType.MakeByRefType();
			}

			Assert.True( type.IsArray );

			return type.GetArrayRank() == 1
				? elemType.MakeArrayType()
				: elemType.MakeArrayType( type.GetArrayRank() );
		}

		if ( !type.IsGenericType ) return null;

		try
		{
			return type.GetGenericTypeDefinition()
				.MakeGenericType( type.GetGenericArguments()
					.Select( x => ApplyGenericArgument( x, genericParam, genericArg ) )
					.ToArray() );
		}
		catch ( ArgumentException )
		{
			//Generic constraints not satisfied

			return null;
		}
	}

	private static bool CanConvert( Type from, Type to )
	{
		if ( from == typeof( object ) ) return true;
		if ( to == typeof( object ) || to == typeof( string ) ) return true;

		//Interfaces false positive for some reason

		if ( to.IsInterface && from.IsSealed && !from.IsAssignableTo( to ) )
		{
			return false;
		}

		//TODO: cache

		try
		{
			_ = Expression.Convert( Expression.Throw( null, from ), to );
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static IEnumerable<TypeOption> GetPossibleTypes( Type[] options = null, Type genericParam = null, Attribute[] attributes = null, Type parentType = null )
	{
		if ( options is not null )
		{
			foreach ( var option in options )
			{
				yield return GetTypeOption( option );
			}

			yield break;
		}

		var listedTypes = new HashSet<Type>();
		var convertFrom = attributes?.OfType<HasConversionFromAttribute>().FirstOrDefault()?.Type;

		foreach ( var type in SystemTypes )
		{
			if ( parentType is not null && !type.IsAssignableTo( parentType ) ) continue;
			if ( !listedTypes.Add( type ) ) continue;
			if ( !SatisfiesConstraints( type, genericParam ) ) continue;
			if ( convertFrom is not null && !CanConvert( convertFrom, type ) ) continue;
			yield return GetTypeOption( type );
		}

		var componentTypes = TypeLibrary.GetTypes<Component>();
		var resourceTypes = TypeLibrary.GetTypes<GameResource>();
		var userTypes = TypeLibrary.GetTypes()
			.Where( x => x.TargetType.Assembly.IsPackage() );

		foreach ( var typeDesc in componentTypes.Union( resourceTypes ).Union( userTypes ) )
		{
			if ( parentType is not null && !typeDesc.TargetType.IsAssignableTo( parentType ) ) continue;
			if ( typeDesc.IsStatic ) continue;
			if ( typeDesc.IsGenericType ) continue;
			if ( typeDesc.HasAttribute<CompilerGeneratedAttribute>() ) continue;
			if ( typeDesc.Name.StartsWith( "<" ) || typeDesc.Name.StartsWith( "_" ) ) continue;
			if ( !listedTypes.Add( typeDesc.TargetType ) ) continue;
			if ( !SatisfiesConstraints( typeDesc.TargetType, genericParam ) ) continue;
			if ( convertFrom is not null && !CanConvert( convertFrom, typeDesc.TargetType ) ) continue;

			yield return GetTypeOption( typeDesc.TargetType );
		}
	}

	public static Menu Create( Type[] options = null, Type genericParam = null, Attribute[] attributes = null, Action<Type> action = null, Type parentType = null, bool canSetNone = false )
	{
		var types = GetPossibleTypes( options, genericParam, attributes, parentType )
			.ToArray();

		var menu = new ContextMenu( null );

		menu.AddLineEdit( "Filter",
			placeholder: "Filter Types..",
			autoFocus: true,
			onChange: s => PopulateTypeMenu( menu, types, action, s ) );

		menu.AboutToShow += () =>
		{
			PopulateTypeMenu( menu, types, action, canSetNone: canSetNone );
		};

		return menu;
	}

	private static void PopulateTypeMenu( Menu menu, IEnumerable<TypeOption> types, Action<Type> action, string filter = null, bool canSetNone = false )
	{
		menu.RemoveMenus();
		menu.RemoveOptions();

		foreach ( var widget in menu.Widgets.Skip( 1 ) )
		{
			menu.RemoveWidget( widget );
		}

		if ( canSetNone )
		{
			menu.AddOption( "None", "cancel", () => action?.Invoke( null ) );
		}

		const int maxFiltered = 10;

		var useFilter = !string.IsNullOrEmpty( filter );
		var truncated = 0;

		if ( useFilter )
		{
			var filtered = types.Where( x => x.Type.Name.Contains( filter, StringComparison.OrdinalIgnoreCase ) ).ToArray();

			if ( filtered.Length > maxFiltered + 1 )
			{
				truncated = filtered.Length - maxFiltered;
				types = filtered.Take( maxFiltered );
			}
			else
			{
				types = filtered;
			}
		}

		menu.AddOptions( types, x => x.Path, x => action?.Invoke( x.Type ), flat: useFilter );

		if ( truncated > 0 )
		{
			menu.AddOption( $"...and {truncated} more" );
		}

		menu.AdjustSize();
		menu.Update();
	}
}

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Facepunch.ActionGraphs;
using Rinten;
using DisplayInfo = Rinten.DisplayInfo;

namespace Editor.ActionGraphs;

#nullable enable

/// <summary>Node creation menu entry representing a constructor call.</summary>
public class NewInstanceNodeType : LibraryNodeType
{
	[Event( FindReflectionNodeTypesEvent.EventName )]
	public static void OnFindReflectionNodeTypes( FindReflectionNodeTypesEvent e )
	{
		if ( e.Type.IsAbstract ) return;
		if ( e.Type.IsGenericType ) return; //TODO

		var constructors = EditorNodeLibrary.TypeLoader.GetConstructors( e.Type.TargetType );

		if ( constructors.Count == 0 ) return;

		e.Output.Add( new NewInstanceNodeType( constructors.ToArray() ) );
	}

	public ConstructorInfo[] Constructors { get; }

	public override bool AutoExpand { get; }

	public override bool IsCommon => false;

	private static IReadOnlyList<GraphMenu.PathElement> GetPath( ConstructorInfo ctor )
	{
		var path = new List<GraphMenu.PathElement>();

		var typeDesc = TypeLibrary.GetType( ctor.DeclaringType );
		var ctorDesc = Rinten.DisplayInfo.ForMember( ctor );

		path.AddRange( MemberPath( typeDesc ) );
		path.Add( new GraphMenu.PathElement( "Static Expressions",
			Order: MethodNodeType.ExpressionsOrder - 50,
			IsHeading: true ) );
		path.Add( new GraphMenu.PathElement( "New", "add_circle_outline", ctorDesc.Description ?? typeDesc.Description, Order: -1 ) );

		return path;
	}

	private static IReadOnlyDictionary<string, object?> GetProperties( ConstructorInfo ctor )
	{
		return new Dictionary<string, object?>
		{
			{ ParameterNames.Type, ctor.DeclaringType }
		};
	}

	public NewInstanceNodeType( ConstructorInfo[] constructors )
		: base( EditorNodeLibrary.NewInstance, GetPath( constructors[0] ), GetProperties( constructors[0] ) )
	{
		Constructors = constructors;
		AutoExpand = constructors[0].GetCustomAttribute<ActionGraphIncludeAttribute>()?.AutoExpand ?? false;
	}
}

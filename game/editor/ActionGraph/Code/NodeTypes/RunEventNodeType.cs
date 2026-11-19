using Facepunch.ActionGraphs;
using Rinten;
using System.Collections.Generic;
using System.Linq;
using Rinten.ActionGraphs;

namespace Editor.ActionGraphs;

#nullable enable

/// <summary>Node creation menu entry representing calling <see cref="ISceneEvent{T}.Post(System.Action{T})"/> for a method on an event interface type.</summary>
public class RunEventNodeType : LibraryNodeType
{
	[Event( FindReflectionNodeTypesEvent.EventName )]
	public static void OnFindReflectionNodeTypes( FindReflectionNodeTypesEvent e )
	{
		if ( !IsSceneEventInterface( e.Type ) ) return;

		foreach ( var method in e.Members.OfType<MethodDescription>().DistinctBy( x => x.Name ) )
		{
			if ( !method.AreParametersActionGraphSafe() ) continue;

			e.Output.Add( new RunEventNodeType( method ) );
		}
	}

	private static bool IsSceneEventInterface( TypeDescription typeDesc )
	{
		return typeDesc.IsInterface && typeDesc.Interfaces.Any( x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof( ISceneEvent<> ) );
	}

	private static IReadOnlyList<GraphMenu.PathElement> GetPath( MethodDescription method )
	{
		return new GraphMenu.PathElement[]
		{
			new( "Common Nodes", IsHeading: true ),
			new( "Scene" ),
			new( "Run Event", Icon: "bolt", Description: "Dispatch an event in the scene." ),
			new( method.TypeDescription.Title, method.TypeDescription.Icon, method.TypeDescription.Description ),
			new( method.Title, method.Icon, method.Description )
		};
	}

	private static IReadOnlyDictionary<string, object?> GetProperties( MethodDescription method )
	{
		return new Dictionary<string, object?>
		{
			{ ParameterNames.Type, method.TypeDescription.TargetType },
			{ ParameterNames.Name, method.Name }
		};
	}

	public RunEventNodeType( MethodDescription method )
		: base( EditorNodeLibrary.Get( "scene.run" )!, GetPath( method ), GetProperties( method ) )
	{

	}
}

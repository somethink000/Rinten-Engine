using System;
using Editor.NodeEditor;
using Rinten.UI.NodeGraph;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Rinten;
using Facepunch.ActionGraphs;
using DisplayInfo = Rinten.DisplayInfo;

namespace Editor.ActionGraphs;

#nullable enable

/// <summary>Node creation menu entry for nodes targeting a new connection dragged from an output.</summary>
public class SelectedOutputNodeType : INodeType
{
	/// <summary>For each global node type that accepts the dragged plug value type as a target input, create a helper node type.</summary>
	[Event( QueryNodeTypesEvent.EventName )]
	public static void OnQueryNodeTypes( QueryNodeTypesEvent ev )
	{
		if ( ev.Query.Plug is not IPlugOut { Type: { } targetType } )
		{
			return;
		}

		var targetTypeTitle = Rinten.DisplayInfo.ForType( targetType ).Name;

		Parallel.ForEach( ev.GlobalNodeTypes, x =>
		{
			if ( x is not LibraryNodeType libraryNode ) return;
			if ( libraryNode.TargetInput is not { } targetInput ) return;
			if ( libraryNode.Inputs.ContainsKey( targetInput.Name ) ) return;
			if ( !targetInput.Type.IsAssignableFromExtended( targetType ) ) return;

			ev.Output.Add( new SelectedOutputNodeType( libraryNode, targetTypeTitle ) );
		} );
	}

	public LibraryNodeType Inner { get; }
	public GraphMenu.PathElement[] Path { get; }

	public bool IsCommon => Inner.IsCommonWithTarget;

	public SelectedOutputNodeType( LibraryNodeType inner, string typeTitle )
	{
		Inner = inner;
		Path = inner.Path.ToArray();

		Path[0] = new GraphMenu.PathElement( $"Selected Output ({typeTitle})", Order: -200, IsHeading: true );
	}

	public bool TryGetInput( Type valueType, [NotNullWhen( true )] out string? name )
	{
		return Inner.TryGetInput( valueType, out name );
	}

	public bool TryGetOutput( Type valueType, [NotNullWhen( true )] out string? name )
	{
		return Inner.TryGetOutput( valueType, out name );
	}

	public INode CreateNode( IGraph graph )
	{
		return Inner.CreateNode( graph );
	}
}

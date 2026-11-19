using Editor.NodeEditor;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Runs another model's anim graph as a child, passing matching control parameters</summary>
[Title( "Internal Graph" ), Group( "Graphs" ), Icon( "account_tree" )]
public class InternalReferencedGraphNode : VariationDataNode<InternalReferencedGraphNode.Data>
{
	/// <summary>The parts of an internal graph node that can differ per variation.</summary>
	public sealed class Data : VariationDataNode.VariationData
	{
		/// <summary>The model whose graph to run - a graph is kept in the model it animates.</summary>
		[Property, ResourceType( "mdl" ), Title( "Model" )]
		public string GraphPath { get; set; } = "";

		/// <summary>Variation of that model's graph to run. Blank runs its default variation.</summary>
		[Property]
		public string GraphVariation { get; set; } = "";

		public override VisualState GetVisualState()
		{
			return string.IsNullOrWhiteSpace( GraphPath ) ? VisualState.HasUnsetData : VisualState.None;
		}
	}

	/// <summary>The model whose graph to run, for the variation being edited.</summary>
	[Hide, JsonIgnore]
	public string GraphPath
	{
		get => SelectedData.GraphPath;
		set => SelectedData.GraphPath = value;
	}

	/// <summary>Pose used when the child graph can't be loaded.</summary>
	[Input( typeof( PoseLink ) )]
	public NodeInput Fallback { get; set; }

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Color.Parse( "#9575cd" ).Value;

	public override GraphMenu CreateContextMenu( NodeUI node )
	{
		if ( node.Graph is not IInternalGraphEditorActions editor )
			return null;

		var menu = new GraphMenu( "Internal Graph" );
		menu.AddOption( "Reflect Child Parameters to Parent", "arrow_back", () => editor.ReflectChildParametersToParent( this ),
			enabled: !string.IsNullOrWhiteSpace( GraphPath ) );
		return menu;
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var fallback = compiler.EmitValue( this, Fallback );
		var data = (Data)GetResolvedVariationData( compiler.VariationHierarchy, compiler.VariationID );

		if ( string.IsNullOrWhiteSpace( data.GraphPath ) )
		{
			compiler.Warn( "Sub-Graph has no model assigned", this );
			return fallback ?? compiler.ReferencePose();
		}

		var variation = data.GraphVariation is { Length: > 0 } named && named != Variation.DefaultVariationID ? named : null;

		if ( AnimGraph.ModelGraphName( data.GraphPath, variation ) is not { } graph )
		{
			compiler.Warn( $"Sub-Graph names '{data.GraphPath}', which is not a model - a graph is kept in the model it animates", this );
			return fallback ?? compiler.ReferencePose();
		}

		return compiler.Builder.ReferencedGraph( graph, fallback );
	}
}

using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Named slot game code can plug another AnimGraph into; plays a fallback until then</summary>
[Title( "External Graph" ), Group( "Graphs" ), Icon( "extension" )]
[AvailableIn( GraphKind.BlendTree )]
public class ExternalReferencedGraphNode : Node
{
	/// <summary>Slot id game code uses to connect a graph. Must be unique.</summary>
	[Property]
	public string SlotName { get; set; } = "External Graph";

	/// <summary>Pose used while no graph is connected.</summary>
	[Input( typeof( PoseLink ) )]
	public NodeInput Fallback { get; set; }

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			if ( !string.IsNullOrWhiteSpace( SlotName ) )
				info.Name = $"External Graph: {SlotName}";
			return info;
		}
	}

	public override Color PrimaryColor => Color.Parse( "#9575cd" ).Value;

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var fallback = compiler.EmitValue( this, Fallback );
		return compiler.RegisterExternalGraphSlot( SlotName, this )
			? compiler.Builder.ExternalGraphSlot( SlotName, fallback )
			: fallback ?? compiler.ReferencePose();
	}
}

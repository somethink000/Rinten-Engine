using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Turns a model-defined bone chain so an attachment aims at a target direction</summary>
[Title( "Look At" ), Group( "IK" ), Icon( "visibility" )]
public class LookAtNode : Node
{
	[Input( typeof( PoseLink ) )]
	public NodeInput Input { get; set; }

	/// <summary>Where to point, in the model's own space.</summary>
	[Input( typeof( VectorLink ) )]
	public NodeInput Direction { get; set; }

	/// <summary>How much of the turn to do, from nothing to all of it.</summary>
	[Input( typeof( FloatLink ) )]
	public NodeInput Weight { get; set; }

	/// <summary>The chain the model names - which bones bend, and how much of the bend each takes.</summary>
	[Property, AnimGraphLookChain]
	public string Chain { get; set; } = "";

	/// <summary>Attachment aimed at the target; if empty, the chain's first bone forward is used</summary>
	[Property]
	public string Attachment { get; set; } = "";

	/// <summary>How far round the chain may go, in degrees, measured as one cone about where it already points.</summary>
	[Property, Range( 0, 180 )]
	public float Limit { get; set; } = 180.0f;

	//Held apart from PitchLimit because a spine is not limited alike in both: it turns a long way round and leans hardly at all, and one cone covering both would give it the smaller of the two everywhere.
	/// <summary>How far the chain may turn to either side, in degrees, measured from the way the model faces.</summary>
	[Property, Range( 0, 180 ), Title( "Yaw Limit" )]
	public float YawLimit { get; set; } = 180.0f;

	/// <summary>How far the chain may tip up or down, in degrees.</summary>
	[Property, Range( 0, 180 ), Title( "Pitch Limit" )]
	public float PitchLimit { get; set; } = 180.0f;

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Color.Parse( "#56b6c2" ).Value;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			if ( !string.IsNullOrWhiteSpace( Chain ) )
				info.Name = $"Look At: {Chain}";
			return info;
		}
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var pose = compiler.EmitPose( this, Input, "Look At" );

		//A chain the model does not name turns nothing at runtime, which on a head reads as
		//a character that has stopped looking rather than as a name that no longer matches.
		if ( !string.IsNullOrWhiteSpace( Chain )
			&& compiler.TargetModel is { IsValid: true } model
			&& !IkChainCatalog.HasLook( model, Chain ) )
		{
			compiler.Warn( $"Look At names chain '{Chain}', which this model does not have", this );
		}

		var direction = compiler.EmitValue( this, Direction );

		//Nothing to point at is not an error and not a turn either: the pose underneath arrives unchanged,
		//which is a character looking straight ahead rather than one whose neck is somewhere it cannot be.
		if ( direction is null )
		{
			compiler.Warn( "Look At has no direction connected", this );
			return pose;
		}

		return compiler.Builder.LookAt( pose, Chain ?? "", direction,
			new AnimGraphBuilder.LookAtSettings
			{
				Weight = compiler.EmitValue( this, Weight ),
				Attachment = Attachment ?? "",
				Limit = Limit,
				YawLimit = YawLimit,
				PitchLimit = PitchLimit,
			} );
	}
}

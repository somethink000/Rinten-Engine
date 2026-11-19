using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Legacy two-bone IK, kept so old graphs load; use IK in new graphs</summary>
[Title( "Two Bone IK (legacy)" ), Group( "IK" ), Icon( "pan_tool_alt" )]
public class TwoBoneIKNode : VariationDataNode<TwoBoneIKNode.Data>
{
	/// <summary>The parts of a two bone IK node that can differ per variation.</summary>
	public sealed class Data : VariationDataNode.VariationData
	{
		/// <summary>The limb the model names, which says which bones turn and which way they fold.</summary>
		[Property, AnimGraphIkChain]
		public string Chain { get; set; } = "";

		/// <summary>The effector bone, where no chain is named.</summary>
		[Property, AnimGraphBone]
		public string EffectorBone { get; set; } = "";

		/// <summary>The amount of time to blend IK in or out.</summary>
		[Property]
		public float BlendTime { get; set; }

		public override VisualState GetVisualState()
		{
			return string.IsNullOrWhiteSpace( Chain ) && string.IsNullOrWhiteSpace( EffectorBone )
				? VisualState.HasUnsetData
				: VisualState.None;
		}
	}

	[Input( typeof( PoseLink ) )]
	public NodeInput Input { get; set; }

	[Input( typeof( TargetLink ) ), Title( "Effector Target" )]
	public NodeInput EffectorTarget { get; set; }

	[Input( typeof( BoolLink ) )]
	public NodeInput Enable { get; set; }

	[Property]
	public AnimGraphBuilder.IKBlendMode BlendMode { get; set; } = AnimGraphBuilder.IKBlendMode.Effector;

	[Property]
	public bool IsTargetInWorldSpace { get; set; }

	/// <summary>How much of the target's roll is spread back into the bone above the effector. Zero leaves the chain's own answer to it.</summary>
	[Property, Range( 0, 1 )]
	public float ReferencePoseTwistWeight { get; set; }

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Color.Parse( "#56b6c2" ).Value;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			var chain = SelectedData?.Chain;

			if ( !string.IsNullOrWhiteSpace( chain ) )
				info.Name = $"Two Bone IK: {chain}";

			return info;
		}
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var child = compiler.EmitPose( this, Input, "Two Bone IK" );
		var data = (Data)GetResolvedVariationData( compiler.VariationHierarchy, compiler.VariationID );

		if ( string.IsNullOrWhiteSpace( data.Chain ) && string.IsNullOrWhiteSpace( data.EffectorBone ) )
		{
			compiler.Warn( "Two Bone IK has no chain or effector bone set", this );
			return child;
		}

		//A chain the model does not name reaches for nothing at runtime, and saying so here is
		//the difference between a rename somebody notices and an arm that quietly stops moving.
		if ( !string.IsNullOrWhiteSpace( data.Chain )
			&& compiler.TargetModel is { IsValid: true } model
			&& !IkChainCatalog.Has( model, data.Chain ) )
		{
			compiler.Warn( $"Two Bone IK names ik chain '{data.Chain}', which this model does not have", this );
		}

		var target = compiler.EmitValue( this, EffectorTarget );
		if ( target is null )
		{
			compiler.Warn( "Two Bone IK has no effector target connected", this );
			return child;
		}

		var enabled = compiler.EmitValue( this, Enable );
		return compiler.Builder.TwoBoneIK(
			child,
			data.EffectorBone,
			target,
			new AnimGraphBuilder.IkSettings
			{
				Chain = data.Chain,
				Enabled = enabled,
				BlendTime = MathF.Max( data.BlendTime, 0 ),
				WorldSpace = IsTargetInWorldSpace,
				BlendMode = BlendMode,
				TwistWeight = ReferencePoseTwistWeight,
			} );
	}
}

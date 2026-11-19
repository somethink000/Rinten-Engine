using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Bone mask from a list of bones and weights; each weight applies to its children</summary>
[Title( "Bone Mask Weights" ), Group( "Bone Mask" ), Icon( "tune" )]
public class BoneMaskWeightsNode : Node
{
	/// <summary>One bone, and what share of the layer reaches it.</summary>
	public sealed class BoneWeight
	{
		[Property, KeyProperty, AnimGraphBone]
		public string Bone { get; set; } = "";

		[Property, Range( 0, 1 )]
		public float Weight { get; set; } = 1.0f;
	}

	/// <summary>What the mask was called where it was authored.</summary>
	[Property]
	public string MaskName { get; set; } = "";

	[Property]
	public List<BoneWeight> Weights { get; set; } = [];

	[Output( typeof( BoneMaskLink ) ), Title( "Bone Mask" )]
	[JsonIgnore]
	public object Mask => default;

	public override Color PrimaryColor => Color.Parse( "#d19a66" ).Value;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			if ( !string.IsNullOrWhiteSpace( MaskName ) )
				info.Name = MaskName;
			return info;
		}
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var listed = Weights?
			.Where( entry => !string.IsNullOrWhiteSpace( entry.Bone ) )
			.Select( entry => (entry.Bone, entry.Weight) )
			.ToList() ?? [];

		//A mask that weighs nothing anywhere would turn its layer off altogether, which reads on screen
		//as the layer not being there. The name on its own is the better answer: it says what was meant.
		if ( listed.Count == 0 )
		{
			compiler.Warn( "Bone Mask Weights has no bones listed", this );
			return compiler.Builder.BoneMask( MaskName ?? "" );
		}

		return compiler.Builder.BoneMask( MaskName ?? "", listed );
	}
}

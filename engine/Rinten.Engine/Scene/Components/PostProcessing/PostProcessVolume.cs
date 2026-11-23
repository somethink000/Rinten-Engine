using Rinten.Volumes;

namespace Rinten;

/// <summary>A volume that defines a region in the scene where post processing effects will be applied.</summary>
[EditorHandle( Icon = "contrast" )]
[Category( "Post Processing" )]
[Icon( "contrast" )]
public class PostProcessVolume : VolumeComponent, Component.ExecuteInEditor
{
	/// <summary>Higher priority volumes override lower priority ones. The default priority is 0.</summary>
	[Property]
	public int Priority { get; set; } = 0;

	/// <summary>Allows fading in and out</summary>
	[Property]
	[Range( 0, 1 )]
	public float BlendWeight { get; set; } = 1.0f;

	/// <summary>Distance from the edge of the volume where blending starts. 0 means hard edge, higher values create softer transitions.</summary>
	[Property]
	[Range( 0, 13 )]
	[HideIf( "IsInfinite", true )]
	public float BlendDistance { get; set; } = 1.27f;

	/// <summary>Preview the post processing when this object is selected in the editor, or when the editor camera is inside the volume.</summary>
	[Property]
	public bool EditorPreview { get; set; } = true;

	/// <summary>Get weight based on position</summary>
	public float GetWeight( Vector3 pos )
	{
		if ( IsInfinite )
		{
			return BlendWeight;
		}

		//Outside the volume there is nothing to blend with. The edge distance is unsigned, so the containment
		//test is what tells the two apart - without it every point in the scene weighed the same as the middle.
		if ( !SceneVolume.Test( WorldTransform, pos ) )
			return 0.0f;

		var distance = GetEdgeDistance( pos );
		return MathX.Remap( distance, 0, BlendDistance, 0, BlendWeight );
	}
}

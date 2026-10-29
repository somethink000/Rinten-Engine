using System.Text.Json.Serialization;
using Rinten.Volumes;

namespace Rinten;

/// <summary>Deforms an ancestor model by a stack of masked operations before its bones; collision is unaffected.</summary>
[Title( "Model Deformer" ), Category( "Rendering" ), Icon( "bubble_chart" )]
[Alias( "DeformationVolume", "Sandbox.DeformationVolume" )]
public sealed class ModelDeformer : VolumeComponent, Component.ExecuteInEditor
{
	/// <summary>An operation in the stack; each one works on the result of the one before.</summary>
	[Expose]
	public enum OperationType
	{
		/// <summary>Inflate or deflate the model. Negative Inflation shrinks; positive expands.</summary>
		[Icon( "open_in_full" )]
		Inflate = 0,

		/// <summary>Flatten or lengthen the model along the shape's local up axis, adjusting its width to match.</summary>
		[Icon( "unfold_more" ), Title( "Squash / Stretch" )]
		SquashStretch = 1,

		/// <summary>Move, rotate or scale the affected part of the model around the shape centre.</summary>
		[Icon( "transform" )]
		Transform = 2,

		/// <summary>Pull the model toward the shape centre. Move the shape to aim the pull.</summary>
		[Icon( "filter_center_focus" )]
		Suck = 3,

		/// <summary>Push vertices out to the shape's surface: a Box squares it, a Sphere rounds it.</summary>
		[Icon( "settings_overscan" )]
		Project = 4
	}

	/// <summary>How a volume changes the shading, apart from how it moves the vertices.</summary>
	[Expose]
	public enum NormalModeType
	{
		/// <summary>Keep the lighting aligned with the deformed mesh.</summary>
		[Icon( "change_history" )]
		Accurate = 0,

		/// <summary>Soften abrupt changes in the lighting over Smoothing.</summary>
		[Icon( "blur_on" )]
		Smooth = 1,

		/// <summary>Keep the incoming lighting while the shape changes.</summary>
		[Icon( "lock" ), Title( "Original" )]
		Preserve = 2
	}

	//Float4s a packed volume takes - DeformVolumeWords in mesh_deform.slang
	internal const int Words = 13;

	private ModelRenderer owner;

	/// <summary>The renderer it deforms, as last found.</summary>
	internal ModelRenderer Owner => owner;

	/// <summary>Where the volume sits in its renderer's model space, as last found.</summary>
	internal Transform Placement { get; private set; }

	/// <summary>Whether it can change any vertex at all.</summary>
	internal bool IsActive => Weight > 0 && Operation switch
	{
		OperationType.Transform => Translation != Vector3.Zero || RotationOffset != Angles.Zero || ScaleFactor != Vector3.One,
		OperationType.SquashStretch => StretchRatio != 1,
		OperationType.Inflate => Inflation != 0,
		OperationType.Project => !IsInfinite,
		_ => true
	};

	/// <summary>Starts as a sphere with a broad, smooth falloff.</summary>
	public ModelDeformer()
	{
		SceneVolume = new SceneVolume
		{
			Type = SceneVolume.VolumeTypes.Sphere,
			Sphere = new Sphere( 0, 0.6f ),
			Box = BBox.FromPositionAndSize( 0, 1.2f ),
			Capsule = new Capsule( Vector3.Down * 0.3f, Vector3.Up * 0.3f, 0.3f )
		};
	}

	/// <summary>Operation applied inside the shape.</summary>
	[Property, Order( 100 )]
	public OperationType Operation { get; set; } = OperationType.Inflate;

	/// <summary>Edge fade as a share of the shape's radius or smallest half-extent.</summary>
	[Property, Hide]
	public float Falloff
	{
		get;
		set => field = float.IsFinite( value ) ? Math.Clamp( value, 0, 1 ) : 0.5f;
	} = 0.5f;

	/// <summary>Blend from the original shape at zero to the full effect at one.</summary>
	[Property, Order( 101 ), Title( "Value" ), Range( 0, 1 )]
	public float Weight
	{
		get;
		set => field = float.IsFinite( value ) ? Math.Clamp( value, 0, 1 ) : 0;
	} = 1;

	/// <summary>Accurate follows the deformation, Smooth softens its shading, Preserve leaves the shading alone.</summary>
	[Property, Title( "Normals" ), Space( 8 ), Order( 50 )]
	public NormalModeType NormalMode { get; set; }

	/// <summary>Metres over which Smooth softens the shading; the shape itself is unchanged.</summary>
	[Property, Order( 51 ), Title( "Smoothing" ), Range( 0.001f, 0.5f ), ShowIf( nameof( NormalMode ), NormalModeType.Smooth )]
	public float NormalSmoothingRadius
	{
		get;
		set => field = float.IsFinite( value ) ? Math.Max( 0.001f, value ) : 0.0254f;
	} = 0.0254f;

	/// <summary>Also deform bone merged clothing and accessories on the same model.</summary>
	[Property, Order( 111 ), Title( "Include Bone Merged" )]
	public bool ApplyToBoneMergedChildren { get; set; }

	/// <summary>Lower priorities deform the model first; higher ones work on their result.</summary>
	[Property, Order( 112 )]
	public int Priority { get; set; }

	/// <summary>The ancestor renderer this deforms.</summary>
	public ModelRenderer Target => Components.GetInAncestors<ModelRenderer>();

	[Hide, JsonIgnore]
	public OperationType? BoundedOperation => IsInfinite ? null : Operation;

	/// <summary>How gradually inflation fades near the edge: zero keeps it broad.</summary>
	[Property, JsonIgnore, Order( 110 ), Title( "Edge Softness" ), Range( 0, 1 ), ShowIf( nameof( BoundedOperation ), OperationType.Inflate )]
	public float InflationSoftness { get => Falloff; set => Falloff = value; }

	/// <summary>How much of the shape blends the transform into the rest of the mesh.</summary>
	[Property, JsonIgnore, Order( 110 ), Title( "Blend Width" ), Range( 0, 1 ), ShowIf( nameof( BoundedOperation ), OperationType.Transform )]
	public float TransformBlend { get => Falloff; set => Falloff = value; }

	/// <summary>Width of the transition into the rest of the mesh.</summary>
	[Property, JsonIgnore, Order( 110 ), Title( "Transition Width" ), Range( 0, 1 ), ShowIf( nameof( BoundedOperation ), OperationType.SquashStretch )]
	public float StretchTransition { get => Falloff; set => Falloff = value; }

	/// <summary>How strongly the pull gathers at the centre: one leaves the rim.</summary>
	[Property, JsonIgnore, Order( 110 ), Title( "Pull Focus" ), Range( 0, 1 ), ShowIf( nameof( BoundedOperation ), OperationType.Suck )]
	public float PullFocus { get => Falloff; set => Falloff = value; }

	/// <summary>Scale along the shape's local up axis; the width compensates.</summary>
	[Property, Order( 102 ), Range( 0, 2 ), ShowIf( nameof( Operation ), OperationType.SquashStretch )]
	public float StretchRatio
	{
		get;
		set => field = float.IsFinite( value ) ? Math.Clamp( value, 0, 2 ) : 1;
	} = 1.25f;

	/// <summary>Negative contracts, positive expands; a finite shape's boundary limits how far.</summary>
	[Property, Order( 102 ), Range( -1, 1 ), ShowIf( nameof( Operation ), OperationType.Inflate )]
	public float Inflation
	{
		get;
		set => field = float.IsFinite( value ) ? Math.Clamp( value, -1, 1 ) : 0;
	} = 1;

	/// <summary>Offset in the shape's local axes, in metres.</summary>
	[Property, Order( 102 ), Title( "Offset" ), ShowIf( nameof( Operation ), OperationType.Transform )]
	public Vector3 Translation
	{
		get;
		set => field = Finite( value ) ? value : Vector3.Zero;
	}

	/// <summary>Rotation round the shape centre, after scale and before the offset.</summary>
	[Property, Order( 103 ), Title( "Rotation" ), ShowIf( nameof( Operation ), OperationType.Transform )]
	public Angles RotationOffset { get; set; }

	/// <summary>Scale on each local axis round the shape centre.</summary>
	[Property, Order( 104 ), Title( "Scale" ), ShowIf( nameof( Operation ), OperationType.Transform )]
	public Vector3 ScaleFactor
	{
		get;
		set => field = Finite( value ) ? value : Vector3.One;
	} = Vector3.One;

	private static bool Finite( Vector3 v ) => float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );

	protected override void OnEnabled() => Scene.GetSystem<SceneDeformationSystem>().Add( this );

	protected override void OnDisabled()
	{
		Scene.GetSystem<SceneDeformationSystem>().Remove( this );
		owner?.ModelDeformers.Remove( this );
		owner = null;
	}

	internal void UpdateVolume()
	{
		var found = Target;
		if ( found != owner )
		{
			owner?.ModelDeformers.Remove( this );
			owner = found;
			owner?.ModelDeformers.Add( this );
		}

		if ( owner.IsValid() ) Placement = owner.WorldTransform.ToLocal( WorldTransform );
	}

	//Packs the volume at a placement in a model's space into thirteen float4s; false where it cannot be packed
	internal bool Pack( Transform placement, Span<System.Numerics.Vector4> into )
	{
		var volume = SceneVolume;
		var center = volume.Type switch
		{
			SceneVolume.VolumeTypes.Sphere => volume.Sphere.Center,
			SceneVolume.VolumeTypes.Capsule => ( volume.Capsule.CenterA + volume.Capsule.CenterB ) * 0.5f,
			SceneVolume.VolumeTypes.Box => volume.Box.Center,
			_ => Vector3.Zero,
		};

		var size = volume.Type switch
		{
			SceneVolume.VolumeTypes.Box => volume.Box.Size * 0.5f,
			SceneVolume.VolumeTypes.Capsule => ( volume.Capsule.CenterB - volume.Capsule.CenterA ) * 0.5f,
			_ => Vector3.Zero,
		};

		var radius = volume.Type switch
		{
			SceneVolume.VolumeTypes.Sphere => volume.Sphere.Radius,
			SceneVolume.VolumeTypes.Capsule => volume.Capsule.Radius,
			_ => 0.0f,
		};

		if ( !Finite( center ) || !Finite( size ) || !float.IsFinite( radius ) || radius < 0 ) return false;
		if ( volume.Type == SceneVolume.VolumeTypes.Box && ( size.x <= 0 || size.y <= 0 || size.z <= 0 ) ) return false;

		System.Numerics.Matrix4x4 toModel = Matrix.FromTransform( placement );
		if ( !System.Numerics.Matrix4x4.Invert( toModel, out var toVolume ) ) return false;

		//Rows for column vectors: the shader multiplies the matrix by the point
		Rows( System.Numerics.Matrix4x4.Transpose( toVolume ), into[0..3] );
		Rows( System.Numerics.Matrix4x4.Transpose( toModel ), into[3..6] );

		into[6] = new( size.x, size.y, size.z, radius );
		into[7] = new( StretchRatio, Inflation, 0, Falloff );
		into[8] = new( Weight, (int)Operation, (int)NormalMode, NormalSmoothingRadius );
		into[9] = new( center.x, center.y, center.z, (int)volume.Type );

		if ( Operation == OperationType.Transform )
			Rows( System.Numerics.Matrix4x4.Transpose( Matrix.FromTransform( new Transform( Translation, Rotation.From( RotationOffset ), ScaleFactor ) ) ), into[10..13] );
		else
			into[10..13].Clear();

		foreach ( var word in into[..Words] )
			if ( !float.IsFinite( word.X ) || !float.IsFinite( word.Y ) || !float.IsFinite( word.Z ) || !float.IsFinite( word.W ) ) return false;

		return true;
	}

	private static void Rows( System.Numerics.Matrix4x4 m, Span<System.Numerics.Vector4> into )
	{
		into[0] = new( m.M11, m.M12, m.M13, m.M14 );
		into[1] = new( m.M21, m.M22, m.M23, m.M24 );
		into[2] = new( m.M31, m.M32, m.M33, m.M34 );
	}

	//How far past the model's own bounds this can move it, in model space
	internal BBox Reach( BBox model )
	{
		if ( !IsInfinite ) return model.AddBBox( SceneVolume.GetBounds().Transform( Placement ) );

		//shortcut: an infinite volume grows the model's bounds by its biggest scale round their centre, not exactly
		var grow = Operation switch
		{
			OperationType.Inflate => 1 + MathF.Abs( Inflation ),
			OperationType.SquashStretch => MathF.Max( StretchRatio, 1 / MathF.Sqrt( MathF.Max( 0.01f, StretchRatio ) ) ),
			OperationType.Transform => MathF.Max( 1, MathF.Max( MathF.Abs( ScaleFactor.x ), MathF.Max( MathF.Abs( ScaleFactor.y ), MathF.Abs( ScaleFactor.z ) ) ) ),
			_ => 1,
		};

		var half = model.Size * 0.5f * grow + Translation.Length * Placement.Scale.x;
		return BBox.FromPositionAndSize( model.Center, half * 2 );
	}
}

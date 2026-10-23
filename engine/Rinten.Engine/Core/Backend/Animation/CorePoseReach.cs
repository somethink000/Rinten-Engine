namespace Rinten.CoreAnimation;

//How far a skinned model's vertices reach from each bone, so a pose's bounds come from its bones, as Godot's per-bone
//boxes do. Exact for linear skinning: a vertex goes where the weighted average of its bones puts it, inside their boxes
internal sealed class CorePoseReach
{
	/// <summary>Each bone's vertices in its own rest frame - through its inverse bind - or null where none follows it.</summary>
	public BBox?[] Bones;

	/// <summary>The vertices no bone moves, in the model's space; null where every one is skinned.</summary>
	public BBox? Unskinned;

	/// <summary>The most a vertex's weights sum away from one: bytes of 255 seldom make it exactly.</summary>
	public float Slack;

	/// <summary>The furthest any vertex moves at a whole weight of each blend shape, in the model's space.</summary>
	public float[] Shapes = System.Array.Empty<float>();

	/// <summary>The most an inverse bind stretches, which a shape's move in the model's space is stretched by into a bone's frame.</summary>
	public float RestScale = 1.0f;

	//Null where a pose's bounds cannot be promised from the bones: no skin, or dual quaternions, whose blend can
	//bow a vertex past every one of its bones' boxes
	public static unsafe CorePoseReach Of( CoreAssets.ModelFile file )
	{
		var boneCount = file?.BoneCount ?? 0;
		var vertexCount = file?.VertexCount ?? 0;

		if ( boneCount <= 0 || vertexCount <= 0 || file.SkinCount != vertexCount || file.DualQuaternion ) return null;

		var vertices = new PackedVertex[vertexCount];
		var skin = new byte[vertexCount * 8];

		fixed ( PackedVertex* v = vertices ) file.CopyPackedVertices( v, vertexCount );
		fixed ( byte* s = skin ) file.CopySkin( s, vertexCount );

		var toRest = new Transform[boneCount];
		var matrix = stackalloc float[16];

		for ( var bone = 0; bone < boneCount; bone++ )
		{
			file.BoneInverseBind( bone, matrix );
			toRest[bone] = AnimTrs.FromMatrix( new System.ReadOnlySpan<float>( matrix, 16 ) ).ToTransform();
		}

		var reach = new CorePoseReach { Bones = new BBox?[boneCount] };

		foreach ( var rest in toRest )
			reach.RestScale = System.MathF.Max( reach.RestScale, System.MathF.Max( System.MathF.Abs( rest.Scale.x ), System.MathF.Max( System.MathF.Abs( rest.Scale.y ), System.MathF.Abs( rest.Scale.z ) ) ) );

		for ( var i = 0; i < vertexCount; i++ )
		{
			var position = vertices[i].Position;
			var sum = 0;

			for ( var k = 0; k < 4; k++ )
			{
				var weight = skin[( i * 8 ) + 4 + k];
				if ( weight == 0 ) continue;

				sum += weight;

				//A link past the skeleton reads as its last bone on the card - see mesh_skin.slang
				var bone = System.Math.Min( (int)skin[( i * 8 ) + k], boneCount - 1 );
				var rest = toRest[bone].PointToWorld( position );

				reach.Bones[bone] = reach.Bones[bone] is { } box ? box.AddPoint( rest ) : new BBox( rest, rest );
			}

			if ( sum == 0 )
			{
				reach.Unskinned = reach.Unskinned is { } loose ? loose.AddPoint( position ) : new BBox( position, position );
				continue;
			}

			reach.Slack = System.MathF.Max( reach.Slack, System.MathF.Abs( ( sum / 255.0f ) - 1.0f ) );
		}

		var shapeCount = file.MorphCount;
		var deltaCount = file.MorphDeltaCount;

		if ( shapeCount > 0 && deltaCount > 0 )
		{
			reach.Shapes = new float[shapeCount];

			var deltas = new byte[deltaCount * CoreModel.MorphDeltaStride];
			fixed ( byte* d = deltas ) file.CopyMorphDeltas( d, deltaCount );

			var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( deltas );
			var shapes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>( deltas );
			const int stride = CoreModel.MorphDeltaStride / 4;

			for ( var d = 0; d < deltaCount; d++ )
			{
				var shape = shapes[d * stride];
				if ( shape >= (uint)shapeCount ) continue;

				var moved = new Vector3( words[( d * stride ) + 1], words[( d * stride ) + 2], words[( d * stride ) + 3] ).Length;
				reach.Shapes[shape] = System.MathF.Max( reach.Shapes[shape], moved );
			}
		}

		return reach;
	}

	//The bounds of a pose in the model's space: each bone's box where the pose puts the bone, every box first widened by
	//as far as the shapes set can move a vertex. Null where no vertex is placed
	public BBox? Bounds( System.ReadOnlySpan<AnimTrs> pose, System.ReadOnlySpan<float> shapeWeights )
	{
		var shaped = 0.0f;

		for ( var s = 0; s < shapeWeights.Length && s < Shapes.Length; s++ )
			shaped += System.MathF.Abs( shapeWeights[s] ) * Shapes[s];

		BBox? all = Unskinned?.Grow( shaped );
		var count = System.Math.Min( pose.Length, Bones.Length );

		for ( var bone = 0; bone < count; bone++ )
		{
			if ( Bones[bone] is not { } rest ) continue;

			var box = rest.Grow( shaped * RestScale );
			var at = pose[bone].ToTransform();

			for ( var corner = 0; corner < 8; corner++ )
			{
				var point = at.PointToWorld( new Vector3(
					( corner & 1 ) != 0 ? box.Maxs.x : box.Mins.x,
					( corner & 2 ) != 0 ? box.Maxs.y : box.Mins.y,
					( corner & 4 ) != 0 ? box.Maxs.z : box.Mins.z ) );

				all = all is { } grown ? grown.AddPoint( point ) : new BBox( point, point );
			}
		}

		if ( all is not { } bounds || Slack <= 0.0f ) return all;

		//Weights summing to s place a vertex s times as far from the origin as the average would: widened by the most that moves it
		var furthest = new Vector3(
			System.MathF.Max( System.MathF.Abs( bounds.Mins.x ), System.MathF.Abs( bounds.Maxs.x ) ),
			System.MathF.Max( System.MathF.Abs( bounds.Mins.y ), System.MathF.Abs( bounds.Maxs.y ) ),
			System.MathF.Max( System.MathF.Abs( bounds.Mins.z ), System.MathF.Abs( bounds.Maxs.z ) ) ).Length;

		return bounds.Grow( Slack * furthest );
	}
}

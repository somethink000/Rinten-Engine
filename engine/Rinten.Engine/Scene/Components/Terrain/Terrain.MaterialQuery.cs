using System.Collections.Generic;

namespace Rinten;

public partial class Terrain
{
	public struct TerrainMaterialInfo
	{
		/// <summary>The layer painted heaviest here, and the next.</summary>
		public int BaseTextureId { get; set; }
		public int OverlayTextureId { get; set; }

		/// <summary>How much of the second there is against the first, nought to one.</summary>
		public float BlendFactor { get; set; }

		public bool IsHole { get; set; }

		public Material BaseMaterial { get; set; }
		public Material OverlayMaterial { get; set; }

		public Material GetDominantMaterial() => BlendFactor < 0.5f ? BaseMaterial : OverlayMaterial;
		public int GetDominantMaterialIndex() => BlendFactor < 0.5f ? BaseTextureId : OverlayTextureId;
	}

	private readonly List<int> queried = new();

	//Out of the paint mask where it covers the point, the nearest vertex's paint for the rest; weights a layer each, added to,
	//and the nearest vertex's normal. False where no ground is near. Main thread: the mesh is sculpted there
	internal bool PaintAt( Vector3 local, System.Span<float> weights, out Vector3 normal )
	{
		normal = Vector3.Up;
		if ( Storage?.Mesh is not { } mesh ) return false;

		var reach = MathF.Max( Storage.EdgeLength * 2.0f, 0.5f );

		queried.Clear();
		mesh.VerticesInSphere( local, reach, queried );

		var nearest = -1;
		var best = float.MaxValue;

		foreach ( var v in queried )
		{
			var distance = ( mesh.PositionOf( v ) - local ).LengthSquared;
			if ( distance >= best ) continue;

			best = distance;
			nearest = v;
		}

		if ( nearest < 0 ) return false;

		var vertex = mesh.GetVertex( nearest );
		normal = vertex.Normal;

		var cover = mesh.MaskAt( local, vertex.Normal.y, weights );
		vertex.Paint.AddTo( weights, 1.0f - cover );

		return true;
	}

	/// <summary>The two heaviest layers painted nearest a world point; null where there is no ground near.</summary>
	public TerrainMaterialInfo? GetMaterialAtWorldPosition( Vector3 worldPosition )
	{
		if ( Storage?.Mesh is null ) return null;

		System.Span<float> weights = stackalloc float[MostLayers];
		weights.Clear();

		if ( !PaintAt( WorldTransform.PointToLocal( worldPosition ), weights, out _ ) ) return new TerrainMaterialInfo { IsHole = true };

		var first = 0;
		var second = -1;

		for ( var i = 1; i < weights.Length; i++ )
		{
			if ( weights[i] > weights[first] ) { second = first; first = i; }
			else if ( second < 0 || weights[i] > weights[second] ) second = i;
		}

		if ( second < 0 || weights[second] <= 0.0f ) second = first;

		var total = weights[first] + ( second == first ? 0.0f : weights[second] );

		var result = new TerrainMaterialInfo
		{
			BaseTextureId = first,
			OverlayTextureId = second,
			BlendFactor = second == first || total <= 0.0f ? 0.0f : weights[second] / total,
		};

		if ( first < Storage.Materials.Count ) result.BaseMaterial = Storage.Materials[first];
		if ( second < Storage.Materials.Count ) result.OverlayMaterial = Storage.Materials[second];

		return result;
	}
}

using System.Collections.Generic;

namespace Rinten;

public partial class Terrain
{
	/// <summary>The ground itself, to sculpt: see <see cref="TerrainMesh"/>. Its brushes work in the terrain's own space.</summary>
	[Hide, System.Text.Json.Serialization.JsonIgnore]
	public TerrainMesh Mesh => Storage?.Mesh;

	/// <summary>Takes a model into the ground for good: a real union with the mesh, where it stands in the world. The ground inside it goes, what it has under the ground goes, and what is left is triangles of the terrain like any others - in the model's own materials, and sculptable from then on.</summary>
	public TerrainMeshEdit Embed( Model model, Transform world, Material materialOverride = null, bool carve = false )
	{
		if ( Storage?.Mesh is null ) return null;

		return Embed( TerrainModelModifier.From( model, WorldTransform.ToLocal( world ), materialOverride, false, carve ) );
	}

	/// <summary>The same for any modifier with a solid: what it was laying over the ground becomes ground.</summary>
	public TerrainMeshEdit Embed( TerrainModifier modifier )
	{
		if ( Storage?.Mesh is not { } mesh || modifier?.Solid is not { } solid || solid.Indices.Count < 3 ) return null;

		//The solid's materials as the storage numbers them.
		var numbered = new TerrainSoup();
		numbered.Vertices.AddRange( solid.Vertices );
		numbered.Indices.AddRange( solid.Indices );

		foreach ( var material in solid.Materials )
		{
			if ( material == 0 ) { numbered.Materials.Add( 0 ); continue; }

			var worn = material <= modifier.Materials.Count ? modifier.Materials[material - 1] : null;
			var at = Storage.InsetMaterials.IndexOf( worn );

			if ( at < 0 ) { Storage.InsetMaterials.Add( worn ); at = Storage.InsetMaterials.Count - 1; }

			numbered.Materials.Add( (ushort)( at + 1 ) );
		}

		var margin = MathF.Max( Storage.EdgeLength * 3.0f, 1.0f );
		var edit = TerrainMeshCopy.Embed( mesh, numbered, modifier.Bounds, margin, modifier.Carve, modifier.Shape, out var report );

		if ( report is { Touched: false } && !modifier.Carve ) Log.Info( "Terrain.Embed: the model does not meet the ground; it was added as it stands" );
		if ( report is { Unclosed: true } ) Log.Warning( "Terrain.Embed: the seam could not be closed everywhere; there may be a gap along it" );

		return edit;
	}

	/// <summary>Paints the mesh from a side by side control map (CompactTerrainMaterial) by where vertices stand.</summary>
	public TerrainMeshEdit RepaintFromControlMap( uint[] control, int side )
	{
		if ( Storage?.Mesh is not { } mesh || control is null ) return null;
		if ( side <= 0 || control.Length < side * side || Storage.TerrainSize <= 0.0f ) return null;

		var texel = Storage.TerrainSize / side;

		mesh.BeginEdit();

		//The map is the paint now, and the mask over the top of the ground would hide it.
		mesh.ClearMask();

		System.Span<float> weights = stackalloc float[MostLayers];

		for ( var v = 0; v < mesh.VertexSlots; v++ )
		{
			if ( !mesh.IsVertexAlive( v ) ) continue;

			var vertex = mesh.GetVertex( v );

			var x = Math.Clamp( (int)MathF.Floor( vertex.Position.x / texel ), 0, side - 1 );
			var z = Math.Clamp( (int)MathF.Floor( vertex.Position.z / texel ), 0, side - 1 );

			var painted = new CompactTerrainMaterial( control[( z * side ) + x] );
			var blend = painted.BlendFactor / 255.0f;

			weights.Clear();

			weights[Math.Min( (int)painted.BaseTextureId, MostLayers - 1 )] += 1.0f - blend;
			weights[Math.Min( (int)painted.OverlayTextureId, MostLayers - 1 )] += blend;

			vertex.Paint = TerrainPaint.Strongest( weights );

			mesh.SetVertex( v, vertex );
		}

		return mesh.EndEdit();
	}
}

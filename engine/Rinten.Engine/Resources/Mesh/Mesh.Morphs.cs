using System.Runtime.InteropServices;

namespace Rinten;

/// <summary>Describes a per-vertex delta for a morph target (blend shape).</summary>
[StructLayout( LayoutKind.Sequential )]
public readonly record struct MorphDelta( int VertexIndex, Vector3 PositionDelta, Vector3 NormalDelta );

public partial class Mesh
{
	//What a mesh may hold, so a model build can't be made to allocate or number past what the format carries.
	internal const int MaxMorphTargets = 4096;
	internal const int MaxMorphDeltas = 1 << 24;

	private Dictionary<string, MorphDelta[]> _morphTargets;

	internal IReadOnlyDictionary<string, MorphDelta[]> MorphTargets => _morphTargets;

	/// <summary>Add a morph target (blend shape) to this mesh. Each delta describes how a vertex should be displaced when the morph is fully active. Morph targets are applied when the mesh is created through <see cref="ModelBuilder"/>.</summary>
	public Mesh AddMorph( string name, ReadOnlySpan<MorphDelta> deltas )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( name );

		if ( deltas.Length == 0 )
			throw new ArgumentException( "Morph deltas cannot be empty", nameof( deltas ) );

		if ( deltas.Length > MaxMorphDeltas )
			throw new ArgumentException( $"A morph can have at most {MaxMorphDeltas} deltas", nameof( deltas ) );

		if ( HasVertexBuffer )
		{
			foreach ( var delta in deltas )
			{
				if ( delta.VertexIndex < 0 || delta.VertexIndex >= VertexCount )
					throw new ArgumentOutOfRangeException( nameof( deltas ), $"Morph delta vertex {delta.VertexIndex} is outside a mesh of {VertexCount} vertices" );
			}
		}

		_morphTargets ??= new( StringComparer.OrdinalIgnoreCase );

		if ( _morphTargets.Count >= MaxMorphTargets && !_morphTargets.ContainsKey( name ) )
			throw new InvalidOperationException( $"A mesh can have at most {MaxMorphTargets} morph targets" );

		//The builder packs every delta of every target into one set of arrays, so the whole mesh has the limit, not each target.
		var replaced = _morphTargets.TryGetValue( name, out var had ) ? had.Length : 0;
		long total = (long)deltas.Length - replaced;
		foreach ( var existing in _morphTargets.Values ) total += existing.Length;

		if ( total > MaxMorphDeltas )
			throw new ArgumentException( $"A mesh can have at most {MaxMorphDeltas} morph deltas in total", nameof( deltas ) );

		_morphTargets[name] = deltas.ToArray();

		//The builder reads the core mesh rather than this one, so the shapes have to be
		//on both - a shape only this side knew about was one the model was built without.
		if ( core is not null ) core.MorphTargets = _morphTargets;

		return this;
	}

	[StructLayout( LayoutKind.Sequential )]
	internal struct MorphNativeDesc
	{
		public int NameOffset;
		public int NameLength;
		public int StartDelta;
		public int NumDeltas;
	}
}

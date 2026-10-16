using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rinten;

public sealed partial class SkinnedModelRenderer
{
	//What the game has said to our graph - see AnimParameterStore. The Set/Get below are that store with our scene model attached.
	AnimParameterStore _animParameters;

	AnimParameterStore AnimParameters => _animParameters ??= new AnimParameterStore( this );

	public void Set( string v, Vector3 value ) => AnimParameters.Set( v, value );
	public void Set( string v, int value ) => AnimParameters.Set( v, value );
	public void Set( string v, float value ) => AnimParameters.Set( v, value );
	public void Set( string v, bool value ) => AnimParameters.Set( v, value );
	public void Set( string v, Rotation value ) => AnimParameters.Set( v, value );

	/// <summary>A place in the world for the graph to reach for - a hand on a ledge, a look at something.</summary>
	public void Set( string v, Transform value ) => AnimParameters.Set( v, value );

	/// <summary>Set an enum parameter by option name (e.g. Set( "holdtype", "pistol" )).</summary>
	public void Set( string v, string option ) => AnimParameters.Set( v, option );

	//Kept as a name rather than resolved once, because a bone moves: what the graph reaches for is wherever that bone is when it is asked.
	/// <summary>The same, following one of the model's own bones.</summary>
	public void SetTargetBone( string v, string bone ) => AnimParameters.SetTargetBone( v, bone );

	/// <summary>Nothing to reach for, which is not the same as reaching for the origin.</summary>
	public void UnsetTarget( string v ) => AnimParameters.UnsetTarget( v );

	public bool GetBool( string v ) => AnimParameters.GetBool( v );
	public int GetInt( string v ) => AnimParameters.GetInt( v );
	public float GetFloat( string v ) => AnimParameters.GetFloat( v );
	public Vector3 GetVector( string v ) => AnimParameters.GetVector( v );
	public Rotation GetRotation( string v ) => AnimParameters.GetRotation( v );

	/// <summary>Remove any stored parameters</summary>
	public void ClearParameters() => AnimParameters.ClearAll();

	internal void ClearParameter( string name ) => AnimParameters.Clear( name );

	internal void ResetParameter( string name ) => AnimParameters.Reset( name );

	internal bool ContainsParameter( string name ) => AnimParameters.Contains( name );

	internal int StoredParameterCount => AnimParameters.Count;

	internal void RefreshAnimParameters() => AnimParameters.WriteIfGraphRebuilt();

	//Tell a freshly built graph everything, then step it once so the first frame drawn is the posed one rather than the bind pose.
	void ApplyStoredAnimParameters()
	{
		AnimParameters.Write();

		//Tick the animation by a frame so we're fully up to date on the first frame.
		if ( Scene.IsEditor && !CanUpdateInEditor() )
		{
			SceneModel.UpdateToBindPose( ReadBonesFromGameObjects );
		}
		else
		{
			SceneModel.Update( Time.Delta, ReadBonesFromGameObjects );
		}
	}

	/// <summary>Converts value to vector local to this entity's eyepos and passes it to SetAnimVector</summary>
	public void SetLookDirection( string name, Vector3 eyeDirectionWorld )
	{
		var delta = eyeDirectionWorld * WorldRotation.Inverse;
		Set( name, delta );
	}

	/// <summary>Converts value to vector local to this entity's eyepos and passes it to SetAnimVector. This also sets {name}_weight to the weight value.</summary>
	public void SetLookDirection( string name, Vector3 eyeDirectionWorld, float weight )
	{
		var delta = eyeDirectionWorld * WorldRotation.Inverse;
		Set( name, delta );
		Set( $"{name}_weight", weight );
	}

	/// <summary>Sets an IK parameter. This sets 3 variables that should be set in the animgraph: 1. ik.{name}.enabled 2. ik.{name}.position 3. ik.{name}.rotation</summary>
	public void SetIk( string name, Transform tx )
	{
		//convert local to model
		tx = WorldTransform.ToLocal( tx );

		Set( $"ik.{name}.enabled", true );
		Set( $"ik.{name}.position", tx.Position );
		Set( $"ik.{name}.rotation", tx.Rotation );
	}

	/// <summary>This sets ik.{name}.enabled to false.</summary>
	public void ClearIk( string name )
	{
		Set( $"ik.{name}.enabled", false );
	}

	ParameterAccessor _parameters;

	/// <summary>Access to the animgraph parameters for this model</summary>
	[Property, Group( "Parameters", StartFolded = true ), ShowIf( nameof( ShouldShowParametersEditor ), true )]
	public ParameterAccessor Parameters
	{
		get
		{
			_parameters ??= new( this );
			return _parameters;
		}
	}

	/// <summary>Whether there is anything to show under Parameters.</summary>
	public bool ShouldShowParametersEditor
	{
		get
		{
			if ( !UseAnimGraph ) return false;

			var graph = EffectiveAnimationGraph;
			if ( graph is null ) return false;

			return graph.Parameters.Count > 0;
		}
	}

	public sealed class ParameterAccessor : IJsonPopulator
	{
		/// <summary>The graph whose parameters these are - what the renderer plays, which is its override or the one its model names.</summary>
		public AnimGraph Graph => _renderer.IsValid() ? _renderer.EffectiveAnimationGraph : null;

		readonly SkinnedModelRenderer _renderer;
		readonly Dictionary<string, bool> _bools = new( StringComparer.OrdinalIgnoreCase );
		readonly Dictionary<string, int> _ints = new( StringComparer.OrdinalIgnoreCase );
		readonly Dictionary<string, float> _floats = new( StringComparer.OrdinalIgnoreCase );
		readonly Dictionary<string, Vector3> _vectors = new( StringComparer.OrdinalIgnoreCase );
		readonly Dictionary<string, Rotation> _rotations = new( StringComparer.OrdinalIgnoreCase );

		internal ParameterAccessor( SkinnedModelRenderer renderer )
		{
			_renderer = renderer;
		}

		public void Clear()
		{
			_bools.Clear();
			_ints.Clear();
			_floats.Clear();
			_vectors.Clear();
			_rotations.Clear();

			_renderer.ClearParameters();
		}

		/// <summary>Forget what was set on one parameter and put it back to the graph's own default.</summary>
		public void Reset( string name )
		{
			_bools.Remove( name );
			_ints.Remove( name );
			_floats.Remove( name );
			_vectors.Remove( name );
			_rotations.Remove( name );

			_renderer.ResetParameter( name );
		}

		public void Clear( string name )
		{
			_bools.Remove( name );
			_ints.Remove( name );
			_floats.Remove( name );
			_vectors.Remove( name );
			_rotations.Remove( name );

			_renderer.ClearParameter( name );
		}

		public bool Contains( string name )
		{
			return _renderer.ContainsParameter( name );
		}

		public bool GetBool( string v ) => _renderer.GetBool( v );
		public int GetInt( string v ) => _renderer.GetInt( v );
		public float GetFloat( string v ) => _renderer.GetFloat( v );
		public Vector3 GetVector( string v ) => _renderer.GetVector( v );
		public Rotation GetRotation( string v ) => _renderer.GetRotation( v );

		public void Set( string v, Vector3 value )
		{
			_vectors[v] = value;
			_renderer.Set( v, value );
		}

		public void Set( string v, int value )
		{
			_ints[v] = value;
			_renderer.Set( v, value );
		}

		public void Set( string v, float value )
		{
			_floats[v] = value;
			_renderer.Set( v, value );
		}

		public void Set( string v, bool value )
		{
			_bools[v] = value;
			_renderer.Set( v, value );
		}

		public void Set( string v, Rotation value )
		{
			_rotations[v] = value;
			_renderer.Set( v, value );
		}

		JsonNode IJsonPopulator.Serialize()
		{
			var obj = new JsonObject();

			var boolsObj = new JsonObject();
			var intsObj = new JsonObject();
			var floatsObj = new JsonObject();
			var vectorsObj = new JsonObject();
			var rotationsObj = new JsonObject();

			foreach ( var value in _bools )
			{
				boolsObj.Add( value.Key, value.Value );
			}

			foreach ( var value in _ints )
			{
				intsObj.Add( value.Key, value.Value );
			}

			foreach ( var value in _floats )
			{
				floatsObj.Add( value.Key, value.Value );
			}

			foreach ( var value in _vectors )
			{
				vectorsObj.Add( value.Key, JsonSerializer.SerializeToNode( value.Value, EnumConvert.Plain ) );
			}

			foreach ( var value in _rotations )
			{
				rotationsObj.Add( value.Key, JsonSerializer.SerializeToNode( value.Value, EnumConvert.Plain ) );
			}

			obj.Add( "bools", boolsObj );
			obj.Add( "ints", intsObj );
			obj.Add( "floats", floatsObj );
			obj.Add( "vectors", vectorsObj );
			obj.Add( "rotations", rotationsObj );

			return obj;
		}

		void IJsonPopulator.Deserialize( JsonNode e )
		{
			if ( e is not JsonObject jso )
				return;

			_bools.Clear();
			_ints.Clear();
			_floats.Clear();
			_vectors.Clear();
			_rotations.Clear();

			if ( jso.TryGetPropertyValue( "bools", out var boolsNode ) && boolsNode is JsonObject boolsObj )
			{
				foreach ( var o in boolsObj )
				{
					Set( o.Key, o.Value.GetValue<bool>() );
				}
			}

			if ( jso.TryGetPropertyValue( "ints", out var intsNode ) && intsNode is JsonObject intsObj )
			{
				foreach ( var o in intsObj )
				{
					Set( o.Key, o.Value.GetValue<int>() );
				}
			}

			if ( jso.TryGetPropertyValue( "floats", out var floatsNode ) && floatsNode is JsonObject floatsObj )
			{
				foreach ( var o in floatsObj )
				{
					Set( o.Key, o.Value.GetValue<float>() );
				}
			}

			if ( jso.TryGetPropertyValue( "vectors", out var vectorsNode ) && vectorsNode is JsonObject vectorsObj )
			{
				foreach ( var o in vectorsObj )
				{
					Set( o.Key, JsonSerializer.Deserialize<Vector3>( o.Value, EnumConvert.Plain ) );
				}
			}

			if ( jso.TryGetPropertyValue( "rotations", out var rotationsNode ) && rotationsNode is JsonObject rotationsObj )
			{
				foreach ( var o in rotationsObj )
				{
					Set( o.Key, JsonSerializer.Deserialize<Rotation>( o.Value, EnumConvert.Plain ) );
				}
			}
		}
	}
}

using Editor.NodeEditor;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Blends Local Layer or State Machine Layer definitions over a base pose</summary>
[Title( "Layer Blend" ), Group( "Layers" ), Icon( "layers" )]
public class LayerBlendNode : Node, IReorderableInputNode
{
	private readonly record struct CompiledLayer(
		AnimGraphBuilder.Node Input,
		AnimGraphBuilder.Node Weight,
		AnimGraphBuilder.Node BoneMask,
		AnimGraphBuilder.Node RootMotionWeight,
		AnimGraphBuilder.LayerBlendMode BlendMode,
		bool ModelSpace,
		bool RotationOnly,
		bool ChannelsApart,
		bool IsSynchronized,
		bool IgnoreEvents,
		StateMachineLayerNode StateMachineLayer );

	[Input( typeof( PoseLink ) )]
	public NodeInput Base { get; set; }

	[Property, Hide]
	public List<NodeInput> Layers { get; set; } = [default];

	/// <summary>Sample root motion only from the base pose, ignoring layer root motion.</summary>
	[Property]
	public bool OnlySampleBaseRootMotion { get; set; } = true;

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Theme.Blue;

	private readonly List<IPlugIn> _layerPlugs = new();

	protected override IEnumerable<IPlugIn> DynamicInputs
	{
		get
		{
			while ( _layerPlugs.Count > Layers.Count )
				_layerPlugs.RemoveAt( _layerPlugs.Count - 1 );

			while ( _layerPlugs.Count < Layers.Count )
			{
				var i = _layerPlugs.Count;
				var info = new AnimPlugInfo( "Layer", i, index => $"Layer {index}",
					( _, index ) => index < Layers.Count ? Layers[index] : default,
					( _, index, value ) => { if ( index < Layers.Count ) Layers[index] = value; } );

				_layerPlugs.Add( new AnimPlugIn( this, info, typeof( LayerLink ) ) );
			}

			return _layerPlugs;
		}
	}

	bool IReorderableInputNode.CanReorderInput( IPlugIn input ) => _layerPlugs.Count > 1 && _layerPlugs.Contains( input );
	void IReorderableInputNode.ReorderInput( IPlugIn input, IPlugIn target ) => ReorderInput( input, target, Layers, _layerPlugs );

	public override GraphMenu CreateContextMenu( NodeUI node )
	{
		var menu = new GraphMenu( "Layer Blend" );
		menu.AddOption( "Add Layer", "add", () => { Layers.Add( default ); Update(); } );

		if ( Layers.Count > 1 )
			menu.AddOption( "Remove Last Layer", "remove", () => { Layers.RemoveAt( Layers.Count - 1 ); Update(); } );

		return menu;
	}

	//Whether nothing here needs a pose under it: every layer wired on is a difference, or there is nothing wired on at all.
	private bool OnlyAdds()
	{
		foreach ( var input in Layers )
		{
			if ( GetConnectedNode( input ) is not LocalLayerNode layer ) continue;
			if ( layer.GetConnectedNode( layer.Input ) is null ) continue;

			if ( layer.BlendMode != AnimGraphBuilder.LayerBlendMode.Additive ) return false;
		}

		return true;
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		//An additive-only stack with no base stands on the zero pose, or offsets get applied twice
		var basePose = GetConnectedNode( Base ) is null && OnlyAdds()
			? compiler.Builder.ZeroPose()
			: compiler.EmitPose( this, Base, "Layer Blend" );

		var compiledLayers = new List<CompiledLayer>();

		for ( var i = 0; i < Layers.Count; i++ )
		{
			switch ( GetConnectedNode( Layers[i] ) )
			{
				case LocalLayerNode layer:
					{
						//A layer with nothing plugged into it does nothing. Given the reference pose instead - which is what an unconnected pose input used to come to - an
						//overlay snaps whatever its mask lets through back to the pose the model was built in, and a difference adds a whole skeleton on top of the body.
						if ( layer.GetConnectedNode( layer.Input ) is null )
						{
							compiler.Warn( "Layer has no pose connected - it does nothing", layer );
							break;
						}

						if ( layer.GetConnectedNode( layer.Input ) is StateMachineNode )
							compiler.Warn( "State machines must be layered with a State Machine Layer", layer );

						var mask = compiler.EmitValue( layer, layer.BoneMask );

						if ( layer.BlendMode == AnimGraphBuilder.LayerBlendMode.ModelSpace && mask is null )
							compiler.Warn( "Model space layers require a bone mask", layer );

						var inputIdx = compiler.EmitPose( layer, layer.Input, "Local Layer", synchronized: layer.IsSynchronized );
						compiler.SetEmitted( layer, inputIdx );

						compiledLayers.Add( new(
							inputIdx,
							compiler.EmitValue( layer, layer.Weight ),
							mask,
							compiler.EmitValue( layer, layer.RootMotionWeight ),
							layer.BlendMode,
							layer.BlendInModelSpace,
							layer.RotationOnly,
							layer.ChannelsApart,
							layer.IsSynchronized,
							layer.IgnoreEvents,
							null ) );
						break;
					}

				case StateMachineLayerNode layer:
					{
						if ( layer.GetConnectedNode( layer.StateMachine ) is not StateMachineNode )
						{
							compiler.Warn( "State Machine Layer requires a State Machine connected", layer );
							break;
						}

						var stateMachineIdx = compiler.EmitPose( layer, layer.StateMachine, "State Machine Layer", synchronized: layer.IsSynchronized );
						compiler.SetEmitted( layer, stateMachineIdx );

						compiledLayers.Add( new(
							stateMachineIdx,
							default,
							default,
							default,
							layer.BlendMode,
							false,
							false,
							false,
							layer.IsSynchronized,
							layer.IgnoreEvents,
							layer ) );
						break;
					}

				default:
					compiler.Warn( $"Layer Blend has nothing connected to Layer {i}", this );
					break;
			}
		}

		if ( compiledLayers.Count == 0 )
		{
			compiler.Warn( "Layer Blend has no connected layers", this );
			return basePose;
		}

		var layers = compiler.Builder.Layers( basePose );
		AnimGraphBuilder.Node emitted = layers;

		foreach ( var layer in compiledLayers )
		{
			if ( layer.StateMachineLayer is { } stateMachineLayer )
			{
				var layerIndex = layers.AddStateMachineLayer( layer.Input, new AnimGraphBuilder.LayerSettings
				{
					BlendMode = layer.BlendMode,
					Synchronized = layer.IsSynchronized,
					IgnoreEvents = layer.IgnoreEvents,
				} );
				compiler.RegisterLayerDebug( stateMachineLayer, emitted, layerIndex );
				continue;
			}

			//Model space is additive authored in model space; pass both flags or the layer replaces
			var modelSpace = layer.BlendMode == AnimGraphBuilder.LayerBlendMode.ModelSpace;

			layers.AddLayer(
				layer.Input,
				new AnimGraphBuilder.LayerSettings
				{
					Weight = layer.Weight,
					BoneMask = layer.BoneMask,
					BlendMode = modelSpace ? AnimGraphBuilder.LayerBlendMode.Additive : layer.BlendMode,
					ModelSpace = modelSpace || layer.ModelSpace,
					RotationOnly = layer.RotationOnly,
					ChannelsApart = layer.ChannelsApart,
					Synchronized = layer.IsSynchronized,
					IgnoreEvents = layer.IgnoreEvents,
					RootMotionWeight = layer.RootMotionWeight,
				} );
		}

		layers.IncludeLayerRootMotion( !OnlySampleBaseRootMotion );

		return emitted;
	}
}

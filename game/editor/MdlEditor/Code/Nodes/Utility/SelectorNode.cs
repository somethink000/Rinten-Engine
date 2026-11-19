using Editor.NodeEditor;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Picks the first pose whose condition is true, on entry only</summary>
[Title( "Selector" ), Group( "Flow" ), Icon( "call_split" )]
public class SelectorNode : Node
{
	public override void BuildContent( Rinten.UI.Panel body ) => EntrySelectionHint.Add( body );

	public class Option
	{
		[Property, KeyProperty]
		public string Label { get; set; } = "";

		[Hide]
		public NodeInput Pose { get; set; }
	}

	[Property]
	public List<Option> Options { get; set; } =
	[
		new(),
		new(),
	];

	[Hide]
	public Data ConditionGraph { get; set; } = Data.CreateSubGraph<SelectorConditionNode>( GraphKind.SelectorConditions );

	[Output( typeof( PoseLink ) )]
	[JsonIgnore]
	public object Result => default;

	public override Color PrimaryColor => Color.Parse( "#e5c07b" ).Value;

	public override Data SecondaryGraph
	{
		get
		{
			SyncConditions();
			return ConditionGraph;
		}
	}

	public override IEnumerable<Data> SubGraphs
	{
		get
		{
			SyncConditions();
			yield return ConditionGraph;
		}
	}

	private readonly List<IPlugIn> _optionPlugs = new();

	protected override IEnumerable<IPlugIn> DynamicInputs
	{
		get
		{
			while ( _optionPlugs.Count > Options.Count )
				_optionPlugs.RemoveAt( _optionPlugs.Count - 1 );

			while ( _optionPlugs.Count < Options.Count )
			{
				var i = _optionPlugs.Count;
				var info = new AnimPlugInfo( $"Option{i}",
					() => i < Options.Count && !string.IsNullOrWhiteSpace( Options[i].Label ) ? Options[i].Label : "Option",
					_ => i < Options.Count ? Options[i].Pose : default,
					( _, value ) => { if ( i < Options.Count ) Options[i].Pose = value; } );

				_optionPlugs.Add( new AnimPlugIn( this, info, typeof( PoseLink ) ) );
			}

			return _optionPlugs;
		}
	}

	public override GraphMenu CreateContextMenu( NodeUI node )
	{
		var menu = new GraphMenu( DisplayInfo.Name );
		menu.AddOption( "Add Option", "add", () =>
		{
			Options.Add( new Option() );
			SyncConditions();
			Update();
		} );

		if ( Options.Count > 0 )
		{
			menu.AddOption( "Remove Last Option", "remove", () =>
			{
				Options.RemoveAt( Options.Count - 1 );
				SyncConditions();
				Update();
			} );
		}

		return menu;
	}

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var title = this is AnimationClipSelectorNode ? "Clip Selector" : "Selector";

		if ( Options.Count == 0 )
		{
			compiler.Warn( $"{title} has no options", this );
			return compiler.ReferencePose();
		}

		SyncConditions();

		var options = new List<AnimGraphBuilder.Node>();
		var conditions = new List<AnimGraphBuilder.Node>();
		var isClip = this is AnimationClipSelectorNode;

		for ( var i = 0; i < Options.Count; i++ )
		{
			var option = compiler.EmitPose( this, Options[i].Pose, title );
			options.Add( option );
			isClip &= option.IsClip();

			var condition = compiler.EmitValue( Conditions, Conditions.ConditionFor( i ) );
			if ( condition is null )
			{
				compiler.Warn( $"{title} option {i + 1} has no condition", Conditions );
				condition = compiler.ConstBool( false );
			}

			conditions.Add( condition );
		}

		if ( this is AnimationClipSelectorNode && !isClip )
		{
			compiler.Warn( "Clip Selector options must be animation clips" );
			isClip = false;
		}

		if ( isClip )
		{
			var cases = options.Select( ( option, i ) => (option, conditions[i]) ).ToArray();
			return compiler.Builder.ClipSelector( cases );
		}

		var selector = compiler.Builder.Selector();
		for ( var i = 0; i < options.Count; i++ )
			selector.Add( options[i], conditions[i] );

		return selector;
	}

	private SelectorConditionNode Conditions
	{
		get
		{
			ConditionGraph ??= Data.CreateSubGraph<SelectorConditionNode>( GraphKind.SelectorConditions );

			if ( ConditionGraph.Nodes.OfType<SelectorConditionNode>().FirstOrDefault() is { } conditions )
				return conditions;

			conditions = new SelectorConditionNode { Position = new Vector2( 400, 0 ) };
			ConditionGraph.AddNode( conditions );
			return conditions;
		}
	}

	private void SyncConditions()
	{
		Conditions.SyncOptions( Options.Select( x => x.Label ).ToList() );
	}
}

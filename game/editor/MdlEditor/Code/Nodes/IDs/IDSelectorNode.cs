using Editor.NodeEditor;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Outputs the ID of the first true option condition, or Default ID</summary>
[Title( "ID Selector" ), Group( "Values" ), Icon( "list" )]
public class IDSelectorNode : Node
{
	public class Option
	{
		[Property, KeyProperty]
		public string Value { get; set; } = "";

		[Hide]
		public NodeInput Condition { get; set; }
	}

	[Property]
	public List<Option> Options { get; set; } =
	[
		new(),
		new(),
	];

	[Property]
	public string DefaultId { get; set; } = "";

	[Output( typeof( IDLink ) )]
	[JsonIgnore]
	public object Result => default;

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
				var info = new AnimPlugInfo( $"Option{i}", () => OptionTitle( i ),
					_ => i < Options.Count ? Options[i].Condition : default,
					( _, value ) => { if ( i < Options.Count ) Options[i].Condition = value; } );

				_optionPlugs.Add( new AnimPlugIn( this, info, typeof( BoolLink ) ) );
			}

			return _optionPlugs;
		}
	}

	private string OptionTitle( int i ) =>
		i < Options.Count && !string.IsNullOrWhiteSpace( Options[i].Value ) ? Options[i].Value : $"Option {i + 1}";

	internal override AnimGraphBuilder.Node Compile( Compiler compiler )
	{
		var options = Options ?? [];
		if ( options.Count == 0 )
		{
			compiler.Warn( "ID Selector has no options" );
			return compiler.ConstID( DefaultId );
		}

		var cases = new List<(AnimGraphBuilder.Node Condition, string Value)>();
		foreach ( var option in options )
		{
			var condition = compiler.EmitValue( this, option.Condition );
			if ( condition is null )
			{
				compiler.Warn( "ID Selector has a disconnected condition" );
				return compiler.ConstID( DefaultId );
			}

			cases.Add( ( condition, option.Value) );
		}

		return compiler.Builder.IdSelect( cases, DefaultId );
	}

	public override GraphMenu CreateContextMenu( NodeUI node )
	{
		var menu = new GraphMenu( "ID Selector" );
		menu.AddOption( "Add Option", "add", () => { Options.Add( new Option() ); Update(); } );

		if ( Options.Count > 0 )
			menu.AddOption( "Remove Last Option", "remove", () => { Options.RemoveAt( Options.Count - 1 ); Update(); } );

		return menu;
	}
}

using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Editor.MdlEditor.Nodes;

/// <summary>Reads a graph parameter by name and exposes its current value. <br/><br/><b>Use when:</b>&#160;Driving animation behavior from values supplied to the AnimGraph. <br/><b>Important:</b>&#160;The parameter name and type must match the graph parameter definition.</summary>
public abstract class ParameterNode : Node
{
	[Hide]
	public string ParameterIdentifier { get; set; } = "";

	[Property, AnimGraphParameterName]
	public string ParameterName { get; set; } = "";

	[JsonIgnore, Hide, Browsable( false )]
	public abstract AnimParameterType ParameterType { get; }

	public override Color PrimaryColor => Theme.Yellow;

	internal override string LiveOutputLabel( string identifier )
		=> HasLiveDebugValue ? ValueDebug.ShortValue( OutputValueType, LiveDebugValue ) : null;

	internal override AnimGraphBuilder.Node Compile( Compiler compiler ) => compiler.EmitParameterReference( this );

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			if ( !string.IsNullOrWhiteSpace( ParameterName ) )
				info.Name = ParameterName;
			return info;
		}
	}
}

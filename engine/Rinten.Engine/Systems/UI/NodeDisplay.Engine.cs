namespace Rinten.UI.NodeGraph;

//The engine's half: a node of the editor's names itself with the type library's DisplayInfo
public partial record struct NodeDisplay
{
	public static implicit operator NodeDisplay( DisplayInfo info ) => new( info.Name, info.Description, info.Icon, info.Group );
}

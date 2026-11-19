namespace Editor.MdlEditor.Nodes;

/// <summary>A shared transition from every other state to one target state</summary>
[Title( "Global Transition" ), Group( "State Machine" ), Icon( "route" )]
[AvailableIn]
public class GlobalTransitionNode : TransitionNode
{
	/// <summary>The target state this global transition leads to.</summary>
	[Hide]
	public string StateName { get; set; } = "";

	public override bool CanClone => false;
	public override bool CanRemove => false;

	public override NodeDisplay DisplayInfo
	{
		get
		{
			var info = Rinten.DisplayInfo.For( this );
			info.Name = $"Global → {StateName}";
			return info;
		}
	}
}

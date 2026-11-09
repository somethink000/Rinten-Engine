using Facepunch.ActionGraphs;

//What action graphs read off attributes RintenUI's core declares: their interfaces are Facepunch.ActionGraphs', which the core does without
public partial class TitleAttribute : ITitleAttribute { }
public partial class DescriptionAttribute : IDescriptionAttribute { }
public partial class CategoryAttribute : IGroupAttribute { }
public partial class GroupAttribute : IGroupAttribute { }
public partial class IconAttribute : IIconAttribute { }

namespace Rinten
{
	public partial class PureAttribute : IPureAttribute { }
	public partial class ImpureAttribute : IImpureAttribute { }
	public partial class ActionGraphTargetAttribute : ITargetAttribute { }
	public partial class ActionGraphPropertyAttribute : IPropertyAttribute { }
	public partial class ActionGraphExposeWhenCachedAttribute : IExposeWhenCachedAttribute { }
	public partial class ActionGraphNodeAttribute : INodeAttribute { }

	[Obsolete( "Please use [ActionGraphNode( id ), Pure]." )]
	[AttributeUsage( AttributeTargets.Method )]
	public sealed class ExpressionNodeAttribute : NodeAttribute, IPureAttribute
	{
		public ExpressionNodeAttribute( string identifier )
			: base( identifier )
		{
		}
	}

	[Obsolete( "Please use [ActionGraphNode( id )]." )]
	[AttributeUsage( AttributeTargets.Method )]
	public sealed class ActionNodeAttribute : NodeAttribute
	{
		public ActionNodeAttribute( string identifier )
			: base( identifier )
		{
		}
	}
}

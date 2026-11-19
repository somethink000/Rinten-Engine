using System;
using Editor.NodeEditor;
using Rinten.UI.NodeGraph;
using Facepunch.ActionGraphs;
using Rinten;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using DisplayInfo = Rinten.DisplayInfo;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static Editor.Label;

namespace Editor.ActionGraphs;

#nullable enable

/// <summary>Node creation menu item based on a <see cref="NodeDefinition"/> from a <see cref="NodeLibrary"/>.</summary>
public class LibraryNodeType : INodeType
{
	[Event( GetGlobalNodeTypesEvent.EventName )]
	public static void OnGetGlobalNodeTypes( GetGlobalNodeTypesEvent ev )
	{
		foreach ( var nodeDefinition in EditorNodeLibrary.All.Values )
		{
			try
			{
				if ( nodeDefinition.DisplayInfo.Hidden ?? false )
				{
					continue;
				}

				if ( nodeDefinition.Attributes.OfType<HideAttribute>().Any() )
				{
					continue;
				}

				if ( nodeDefinition.Attributes.OfType<ObsoleteAttribute>().Any() )
				{
					continue;
				}

				ev.Output.Add( new LibraryNodeType( nodeDefinition ) );
			}
			catch ( Exception e )
			{
				Log.Error( $"Error creating node type for library node \"{nodeDefinition.Identifier}\": {e}" );
			}
		}

		var types = TypeLibrary.GetTypes()
			.Where( x => !x.Name.StartsWith( "<" ) && !x.HasAttribute<CompilerGeneratedAttribute>() )
			.ToArray();

		var componentTypes = types
			.Where( x => x.TargetType.IsAssignableTo( typeof( Component ) ) )
			.ToArray();

		Parallel.ForEach( types, typeDesc =>
		{
			EditorEvent.Run( FindReflectionNodeTypesEvent.EventName,
				new FindReflectionNodeTypesEvent( typeDesc, componentTypes, ev.Output ) );
		} );
	}

	protected static IReadOnlyList<GraphMenu.PathElement> LibraryPath( NodeBinding binding, IReadOnlyDictionary<string, object?>? properties = null )
	{
		var path = new List<GraphMenu.PathElement>();

		if ( binding.Inputs.FirstOrDefault( x => x.IsTarget ) is { } targetInput && TypeLibrary.TryGetType( targetInput.Type, out var targetDesc ) )
		{
			path.AddRange( MemberPath( targetDesc,
				binding.Kind is NodeKind.Expression
					? new GraphMenu.PathElement( "Expressions",
						Order: MethodNodeType.ExpressionsOrder,
						IsHeading: true )
					: new GraphMenu.PathElement( "Actions",
						Order: MethodNodeType.ActionsOrder,
						IsHeading: true ) ) );
		}
		else
		{
			path.Add( new( "Common Nodes", Order: 100, IsHeading: true ) );
		}

		var display = binding.DisplayInfo.Format( x => properties?.GetValueOrDefault( x )
			?? binding.Properties.FirstOrDefault( y => y.Name == x )?.Default );

		path.AddRange( Menu.GetSplitPath( new DisplayInfo
		{
			Name = display.Title,
			Group = display.Group,
			Icon = display.Icon
		} ).ToGraphPath() );

		return path;
	}

	protected static IReadOnlyList<GraphMenu.PathElement> MemberPath( TypeDescription declaringType, params GraphMenu.PathElement[] subPath )
	{
		var path = new List<GraphMenu.PathElement>
		{
			new( "Type Library", Order: 200, IsHeading: true ),
			new( declaringType.Title, declaringType.Icon ?? "data_object", declaringType.Description, Order: GetTypeOrder( declaringType.TargetType ) )
		};

		path.AddRange( subPath );

		return path;
	}

	public InputDefinition? TargetInput { get; }
	public IReadOnlyList<InputDefinition> PrimaryInputs { get; }
	public IReadOnlyList<OutputDefinition> PrimaryOutputs { get; }

	public NodeDefinition Definition { get; }
	public NodeBinding Binding { get; }

	public IReadOnlyDictionary<string, object?> Properties { get; }
	public IReadOnlyDictionary<string, object?> Inputs { get; }

	public GraphMenu.PathElement[] Path { get; }

	public virtual bool IsCommon => TargetInput is null && IsCommonWithTarget;
	public virtual bool IsCommonWithTarget => true;

	public LibraryNodeType( NodeDefinition definition )
		: this( definition, null )
	{

	}

	protected LibraryNodeType( NodeDefinition definition, IReadOnlyList<GraphMenu.PathElement>? path = null,
		IReadOnlyDictionary<string, object?>? properties = null,
		IReadOnlyDictionary<string, object?>? inputs = null )
	{
		Definition = definition;
		Properties = properties ?? ImmutableDictionary<string, object?>.Empty;
		Inputs = inputs ?? ImmutableDictionary<string, object?>.Empty;

		var surface = BindingSurface.Empty with
		{
			Properties = Properties,
			InputTypes = Inputs
				.Where( x => x.Value is not null )
				.ToDictionary( x => x.Key, x => x.Value!.GetType() )!
		};

		Binding = definition.Bind( surface );
		TargetInput = Binding.Inputs.FirstOrDefault( x => x.IsTarget );
		Path = (path ?? LibraryPath( Binding, properties )).ToArray();

		if ( Path[^1].Icon is null )
		{
			var icon = Binding.DisplayInfo.Icon ?? Binding.Kind switch
			{
				NodeKind.Expression => "functions",
				_ => null
			};

			Path[^1] = Path[^1] with { Icon = icon };
		}

		Path[^1] = Path[^1] with { Description = FormatDescription( Path[^1].Name, Path[^1].Description ) };

		PrimaryInputs = Binding.Inputs
			.Where( x => x is { IsTarget: false, IsSignal: false } )
			.Take( 1 )
			.ToArray();

		PrimaryOutputs = Binding.Outputs
			.Where( x => x is { IsSignal: false } )
			.Take( 1 )
			.ToArray();
	}

	private string FormatDescription( string name, string? description )
	{
		var builder = new StringBuilder();

		if ( Binding.Inputs.FirstOrDefault( x => x.IsTarget ) is { } targetInput )
		{
			builder.Append( $"<span style=\"font-size: 14px; font-weight: 600;\"><i>{targetInput.Type.ToRichText()}</i> \u2192 </span>" );
		}

		if ( description?.StartsWith( "<br/>" ) is true )
		{
			description = description.Substring( "<br/>".Length );
		}

		if ( description?.EndsWith( "<br/>" ) is true )
		{
			description = description.Substring( 0, description.Length - "<br/>".Length );
		}

		builder.Append( $"<span style=\"font-size: 16px; font-weight: 600;\">{name}</span><br/>" );
		builder.Append( description ?? "<i>No description provided.</i><br/>" );

		//FormatParameterList( builder, "Properties", Binding.Properties.Where( x => x.Display.Hidden is false && !Properties.ContainsKey( x.Name ) ) );


		builder.Append( $"<table width=\"100%\" ><tr>" );

		FormatParameterList( builder, "Inputs", Binding.Inputs.Where( x => x.Display.Hidden is false && !Inputs.ContainsKey( x.Name ) && !x.IsTarget ) );
		FormatParameterList( builder, "Outputs", Binding.Outputs.Where( x => x.Display.Hidden is false ) );

		builder.Append( $"</tr></table>" );

		return builder.ToString();
	}

	private static void FormatParameterList( StringBuilder builder, string title, IEnumerable<IParameterDefinition> parameters )
	{
		var any = false;

		foreach ( var parameter in parameters )
		{
			if ( !any )
			{
				any = true;

				//builder.Append( $"<span style=\"font-size: 12px; font-weight: 600;\">{title}</span>" );
				//builder.Append( "<p><table cellpadding=\"4\" width=\"100%\" style=\"background-color: #111111\">" );
				//builder.Append( $"<tr style=\"background-color: #1C1C1C\"><th>Name</th><th>Type</th><th>Description</th></tr>" );

				builder.Append( "<td>" );
				builder.Append( $"<span style=\"font-size: 12px; font-weight: 600;\">{title}</span>" );
				//builder.Append( "<ul style=\"margin-left: 0; block-indent: 0;\">" );
			}

			//builder.Append( $"<tr style=\"background-color: #161616\">" );
			//builder.Append( $"<td>{WithColor( parameter.Display.Title, "#9CDCFE" )}</td>" );
			//builder.Append( $"<td>{typeDisplay.Name}</td>" );
			//builder.Append( $"<td>{parameter.Display.Description ?? "<i>No description provided.</i>"}</td>" );
			//builder.Append( "</tr>" );

			builder.Append( $"<br/>{(parameter.Type == typeof( Signal ) ? "&#8227;" : "&#8226;")} {parameter.Display.Title.WithColor( "#9CDCFE" )}: {parameter.Type.ToRichText()}" );
		}

		if ( any )
		{
			//builder.Append( "</table></p><br/>" );

			builder.Append( "</td>" );
		}
	}

	private static int GetTypeOrder( Type type )
	{
		if ( type.IsInterface )
		{
			return 0;
		}

		var order = -100;

		while ( type.BaseType is not null )
		{
			order -= 100;
			type = type.BaseType;
		}

		return order;
	}

	public virtual bool TryGetInput( Type valueType, [NotNullWhen( true )] out string? name )
	{
		return TryGetInput( valueType, true, out name );
	}

	public virtual bool TryGetOutput( Type valueType, [NotNullWhen( true )] out string? name )
	{
		if ( valueType == typeof( Signal ) )
		{
			name = Binding.Outputs.FirstOrDefault( x => x.IsSignal )?.Name;
			return name is not null;
		}

		name = PrimaryOutputs.FirstOrDefault( x => valueType.IsAssignableFromExtended( x.Type ) )?.Name;
		return name is not null;
	}

	public bool TryGetInput( Type valueType, bool includeTargetInput, [NotNullWhen( true )] out string? name )
	{
		if ( valueType == typeof( Signal ) )
		{
			name = Binding.Inputs.FirstOrDefault( x => x.IsSignal )?.Name;
			return name is not null;
		}

		if ( includeTargetInput && (TargetInput?.Type.IsAssignableFromExtended( valueType, false ) ?? false) )
		{
			name = TargetInput.Name;
			return true;
		}

		name = PrimaryInputs.FirstOrDefault( x => x.Type.IsAssignableFromExtended( valueType ) )?.Name;
		return name is not null;
	}

	public virtual bool AutoExpand => false;

	public EditorNode CreateNode( EditorActionGraph editorGraph, Node? parent )
	{
		var node = OnCreateNode( editorGraph.Graph, parent );
		var editorNode = OnCreateEditorNode( editorGraph, node );

		return editorNode;
	}

	public INode CreateNode( IGraph graph )
	{
		return CreateNode( (EditorActionGraph)graph, null );
	}

	public Node CreateNode( ActionGraph graph, Node? parent = null )
	{
		return OnCreateNode( graph, parent );
	}

	protected virtual Node OnCreateNode( ActionGraph graph, Node? parent = null )
	{
		var node = graph.AddNode( Definition, parent );

		node.SetParameters( Properties, Inputs );

		AutoConnectCancellationTokenInput( node );

		return node;
	}

	private void AutoConnectCancellationTokenInput( Node node )
	{
		var graph = node.ActionGraph;

		if ( graph.Inputs.Values.FirstOrDefault( x => x.Type == typeof( CancellationToken ) ) is not { } ctGraphInput )
		{
			return;
		}

		if ( !(graph.InputNode?.Outputs.TryGetValue( ctGraphInput.Name, out var ctOutput ) ?? false) )
		{
			return;
		}

		if ( node.Inputs.Values.FirstOrDefault( x => x.Type == typeof( CancellationToken ) || x.Type == typeof( CancellationToken? ) ) is not { } ctInput )
		{
			return;
		}

		ctInput.SetLink( ctOutput );
	}

	protected virtual EditorNode OnCreateEditorNode( EditorActionGraph editorGraph, Node node )
	{
		return CreateEditorNode( editorGraph, node );
	}

	public static EditorNode CreateEditorNode( EditorActionGraph editorGraph, Node node )
	{
		return node.Definition.Identifier switch
		{
			"comment" => new CommentEditorNode( editorGraph, node ),
			"nop" => new RerouteEditorNode( editorGraph, node ),
			_ => new EditorNode( editorGraph, node )
		};
	}

	public virtual bool Matches( NodeQuery query ) => INodeType.DefaultMatches( this, query );

	public virtual bool IsExpansionOption()
	{
		if ( Binding.Kind != NodeKind.Expression )
		{
			return false;
		}

		if ( Binding.Outputs.Count != 1 )
		{
			return false;
		}

		if ( Binding.Inputs.Any( x => x is { IsTarget: false, IsRequired: true } && !Inputs.ContainsKey( x.Name ) ) )
		{
			return false;
		}

		if ( !Binding.Inputs.Any( x => x.IsTarget ) )
		{
			return false;
		}

		if ( !Binding.Properties.All( x => Properties.ContainsKey( x.Name ) ) )
		{
			return false;
		}

		return true;
	}

	public override string ToString()
	{
		return $"{Definition.Identifier} {{ {string.Join( ", ", Properties.Concat( Inputs ).Select( x => $"{x.Key}: {x.Value}" ) )} }}";
	}
}

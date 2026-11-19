using Editor.NodeEditor;
using Rinten.UI.NodeGraph;
using Facepunch.ActionGraphs;
using Rinten.ActionGraphs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using static Rinten.UI.NodeGraph.GraphView;
using DisplayInfo = Facepunch.ActionGraphs.DisplayInfo;

namespace Editor.ActionGraphs;

public interface IActionPlug : IPlug
{
	Type LastType { get; set; }
	int Index { get; }
}

public abstract class ActionPlug<T> : IActionPlug where T : Node.IParameter
{
	private readonly T _defaultParam;

	public EditorNode Node { get; }
	public T Parameter => Source.TryGetValue( Identifier, out var param ) ? param : _defaultParam;

	public int Index { get; set; }
	public virtual bool IsArrayElement => false;

	public IReadOnlyDictionary<string, T> Source { get; }

	public Type LastType { get; set; }

	INode IPlug.Node => Node;

	public string Identifier { get; }

	public virtual Type Type => Parameter.Type;

	public string Title => Parameter.Node == Node.Node
		? Parameter.Display.Title
		: GetAccessorNodeTitle( Parameter.Node );

	private static string GetAccessorNodeTitle( Node node )
	{
		var title = node.DisplayInfo.Title;

		if ( title.LastIndexOf( '→' ) is var index and not -1 ) title = title[(index + 1)..].TrimStart();
		if ( title.StartsWith( "Get " ) || title.StartsWith( "Set " ) ) title = title[4..];

		return title;
	}

	public NodeDisplay DisplayInfo => new()
	{
		Name = !IsArrayElement && Index == 0
			? Title
			: $"{Title}[{Index}]",
		Description = Parameter.Node.Parent != null
			? Parameter.Node.DisplayInfo.Description
			: Parameter.Display.Description,
		Group = GetGroupName()
	};

	private string GetGroupName()
	{
		var node = Parameter.Node;
		string groupName = null;

		while ( node.Parent is { } parent && node != Node.Node )
		{
			var parentOutputTitle = parent == Node.Node
				? node.Inputs.Values.First( x => x.Link is not null )
					.Link!.Source.Display.Title
				: GetAccessorNodeTitle( parent );

			if ( groupName is null )
			{
				groupName = parentOutputTitle;
			}
			else
			{
				groupName = $"{parentOutputTitle} > {groupName}";
			}

			node = parent;
		}

		return groupName;
	}

	protected ActionPlug( EditorNode node, IReadOnlyDictionary<string, T> source, string identifier, int index )
	{
		Node = node;
		Source = source;
		Identifier = identifier;
		Index = index;

		LastType = Type;

		_defaultParam = Parameter;
	}

	public virtual ValueEditor CreateEditor( NodeUI node, Plug plug )
	{
		return null;
	}

	public virtual GraphMenu CreateContextMenu( NodeUI node, Plug plug )
	{
		return null;
	}

	protected GraphMenu CreateDisplayInfoMenu( GraphMenu menu, Action<DisplayInfo> changed )
	{
		var display = Parameter.Display;
		var edit = menu.AddMenu( "Edit", "edit" );

		edit.AddLineEdit( "Title", value: display.Title,
			onChange: value => changed( display with { Title = value } ) );
		edit.AddLineEdit( "Description", value: display.Description,
			onChange: value => changed( display with { Description = value } ) );

		return menu;
	}

	public virtual void OnDoubleClick( NodeUI node, Plug plug )
	{

	}

	public abstract bool ShowConnection { get; }
	public bool ShowLabel => Node.HasTitleBar && !InTitleBar;
	public bool AllowStretch => false;
	public bool InTitleBar => Node.HasTitleBar && (Parameter.Name == "_signal" || Node.Inputs.Count <= 1 && Node.Outputs.Count <= 1);
	public virtual bool IsReachable => Node.IsReachable;

	public string ErrorMessage => string.Join( Environment.NewLine, Parameter.GetMessages()
		.Where( x => x.IsError )
		.Select( x => x.Value ) );

	public override string ToString()
	{
		return $"{Node.Identifier}.{Identifier}";
	}
}

public static class NodeOutputExtensions
{
	public static string GetLabel( this Node.Output output )
	{
		if ( output.Node.Definition == EditorNodeLibrary.Input
			&& output.Node.ActionGraph.Inputs.TryGetValue( output.Name, out var inputDef ) )
		{
			//Hack so that references to special hidden inputs will stay connected on copy / paste.

			if ( inputDef.IsTarget )
			{
				return "__this";
			}

			if ( inputDef.Type == typeof( CancellationToken ) )
			{
				return "__ct";
			}
		}

		return output.Node.UserData[ActionOutputPlug.LabelsKey]?[output.Name]?.GetValue<string>();
	}

	public static string GetNiceLabel( this Node.Output output )
	{
		if ( output.Node.Definition == EditorNodeLibrary.Input
			&& output.Node.ActionGraph.Inputs.TryGetValue( output.Name, out var inputDef ) )
		{
			if ( inputDef.IsTarget )
			{
				return "This";
			}

			if ( inputDef.Type == typeof( CancellationToken ) )
			{
				return "Cancellation Token";
			}
		}

		return output.GetLabel();
	}
}

public class ActionOutputPlug : ActionPlug<Node.Output>, IPlugOut
{
	public const string LabelsKey = "Labels";

	public string Label
	{
		get => Parameter.GetLabel();
		set
		{
			var labels = Parameter.Node.UserData[LabelsKey]?.AsObject();

			if ( value is null )
			{
				labels?.Remove( Identifier );

				if ( labels is { Count: 0 } )
				{
					Parameter.Node.UserData.Remove( LabelsKey );
				}

				return;
			}

			if ( labels is null )
			{
				Parameter.Node.UserData[LabelsKey] = labels = new JsonObject();
			}

			labels[Identifier] = value;
		}
	}

	public override bool ShowConnection => Label is null;
	public override bool IsReachable => base.IsReachable && (Parameter.Node == Node.Node || Parameter.IsLinked);

	public ActionOutputPlug( EditorNode node, IReadOnlyDictionary<string, Node.Output> source, string identifier, int index ) : base( node, source, identifier, index )
	{
	}

	public override ValueEditor CreateEditor( NodeUI node, Plug plug )
	{
		if ( Parameter.Node.Definition == EditorNodeLibrary.NoOperation )
		{
			return null;
		}

		return new DefaultOutputEditor( node, (PlugOut)plug );
	}

	public override GraphMenu CreateContextMenu( NodeUI node, Plug plug )
	{
		if ( Parameter.Node.Definition == EditorNodeLibrary.NoOperation )
		{
			return null;
		}

		var editorActionGraph = (EditorActionGraph)node.Graph.Graph;
		var actionGraph = editorActionGraph!.Graph;

		var type = Type;
		var menu = new GraphMenu( plug.Title );

		menu.AddLineEdit( "Label", Label,
			autoFocus: true,
			onSubmit: value =>
			{
				node.Graph?.PushUndo( "Set Label" );

				Label = string.IsNullOrWhiteSpace( value ) ? null : value;

				(node.Node as EditorNode)?.MarkDirty();

				node.Graph?.PushRedo();
			} );

		var agNode = Parameter.Node;

		if ( agNode.Definition == EditorNodeLibrary.Input )
		{
			CreateDisplayInfoMenu( menu, value =>
			{
				agNode.ActionGraph.SetParameters(
					agNode.ActionGraph.Inputs.Values
						.Select( x => x.Name == Parameter.Name ? x with { Display = value } : x ).ToArray(),
					agNode.ActionGraph.Outputs.Values.ToArray() );

				agNode.MarkDirty();
				node.MarkNodeChanged();
			} );
		}

		if ( Parameter.Node.Parent is not null && !Parameter.IsUsed() )
		{
			menu.AddOption( "Hide", "visibility_off", () =>
			{
				node.Graph?.PushUndo( "Hide Expanded output" );

				if ( !Parameter.IsAnyExpandedOutputVisible() )
				{
					Parameter.Node.Remove();
				}
				else
				{
					Parameter.Node.SetVisible( false );
				}

				(node.Node as EditorNode)?.MarkDirty();

				node.Graph?.PushRedo();
			} );
		}

		if ( Parameter.IsLinked && Parameter.IsUsed() )
		{
			menu.AddOption( "Clear", "clear", () =>
			{
				node.Graph?.PushUndo( "Clear Links" );

				foreach ( var link in Parameter.Links.ToArray() )
				{
					if ( link.Target.Node.Parent != null )
					{
						continue;
					}

					link.Remove();
				}

				(node.Node as EditorNode)?.MarkDirty();

				node.Graph?.PushRedo();
			} );
		}

		menu.AddSeparator();

		if ( !Parameter.IsSignal && editorActionGraph.CanModifyParameters )
		{
			var createOutputMenu = menu.AddMenu( "Create Graph Output", "add_box" );

			createOutputMenu.AboutToShow += () =>
			{
				createOutputMenu.Clear();

				createOutputMenu.AddLineEdit( "Name", autoFocus: true, onSubmit: value =>
				{
					if ( string.IsNullOrEmpty( value ) ) return;

					node.Graph.PushUndo( "Create Graph Output" );

					var name = actionGraph.GetNextOutputName();
					var output = new OutputDefinition( name, Parameter.Type, 0, new DisplayInfo( value ) );
					var hadOutputNde = actionGraph.PrimaryOutputNode is not null;

					actionGraph.SetParameters( actionGraph.Inputs.Values.ToArray(), actionGraph.Outputs.Values.With( output ) );
					actionGraph.AddRequiredNodes();
					actionGraph.PrimaryOutputNode!.UpdateParameters();

					actionGraph.PrimaryOutputNode.Inputs[name].SetLink( Parameter );

					if ( !hadOutputNde )
					{
						var editorOutputNode = new EditorNode( editorActionGraph, actionGraph.PrimaryOutputNode );

						editorActionGraph.AddNode( editorOutputNode );

						node.Graph.BuildFromNodes( new[] { editorOutputNode }, true );
					}

					editorActionGraph.MarkDirty( Parameter.Node );
					editorActionGraph.MarkDirty( actionGraph.PrimaryOutputNode );

					node.Graph.PushRedo();
				} );
			};
		}

		if ( editorActionGraph.CanModifyParameters && Parameter.Node.Definition == EditorNodeLibrary.Input && Parameter.Name.StartsWith( "_in" ) )
		{
			menu.AddOption( "Remove Graph Input", "delete", () =>
			{
				foreach ( var link in Parameter.Links.ToArray() )
				{
					if ( link.IsArrayElement )
					{
						link.Target.SetLink( new Constant( null ), link.ArrayIndex );
					}
					else
					{
						link.Target.ClearLinks();
					}
				}

				var input = actionGraph.Inputs.Values.First( x => x.Name == Parameter.Name );

				actionGraph.SetParameters(
					actionGraph.Inputs.Values.Without( input ),
					actionGraph.Outputs.Values.ToArray() );

				actionGraph.InputNode!.UpdateParameters();

				editorActionGraph.MarkDirty( actionGraph.InputNode );
			} );
		}

		EditorEvent.Run( PopulateOutputPlugMenuEvent.EventName,
			new PopulateOutputPlugMenuEvent( (ActionGraphView)node.Graph, this, menu ) );

		if ( !Parameter.IsSignal )
		{
			menu.AddSeparator();
			PopulateExpandedOutputMenu( this, node, menu );
		}

		return menu;
	}

	public override void OnDoubleClick( NodeUI node, Plug plug )
	{
		var visibleChildren = Parameter
			.GetExpandedOutputs( true )
			.Where( x => x != Parameter )
			.Where( x => !x.IsUsed() )
			.ToArray();

		if ( visibleChildren.Length > 0 )
		{
			foreach ( var visibleChild in visibleChildren )
			{
				if ( !visibleChild.IsAnyExpandedOutputVisible() )
				{
					visibleChild.Node.Remove();
				}
				else
				{
					visibleChild.Node.SetVisible( false );
				}
			}
		}
		else
		{
			var nodeTypes = ((ActionGraphView)node.Graph).GetExpansionOptions( this )
				.ToArray();

			var autoExpanded = nodeTypes
				.Where( x => x.Inner.AutoExpand == true )
				.ToArray();

			if ( autoExpanded.Length > 0 )
			{
				nodeTypes = autoExpanded;
			}

			foreach ( var nodeType in nodeTypes )
			{
				Expand( nodeType.Inner, node );
			}
		}
	}

	private static Node FindExpandedOutput( Node.Output output, LibraryNodeType nodeType )
	{
		return output.Node.Children
			.Where( x => x.Definition == nodeType.Definition )
			.Where( x => x.Inputs.Values.FirstOrDefault( y => y.IsTarget )?.Link?.Source is { } source && source == output )
			.Where( x =>
				nodeType.Properties.All( y =>
					x.Properties.TryGetValue( y.Key, out var property ) && property.Value == y.Value ) )
			.FirstOrDefault( x =>
				nodeType.Inputs.All( y =>
					x.Inputs.TryGetValue( y.Key, out var input ) && input.Value == y.Value ) );
	}

	private void Expand( LibraryNodeType type, NodeUI nodeUi )
	{
		var child = FindExpandedOutput( Parameter, type );

		nodeUi.Graph?.PushUndo( "Show Expanded output" );

		if ( child != null )
		{
			child.SetVisible( true );
		}
		else
		{
			child = type.CreateNode( (EditorActionGraph)nodeUi.Graph!.Graph, Parameter.Node ).Node;
			child.Inputs.Values.FirstOrDefault( x => x.IsTarget )?.SetLink( Parameter );
		}

		Node.MarkDirty();

		nodeUi.Graph?.PushRedo();
	}

	private static void PopulateExpandedOutputMenu( ActionOutputPlug plug, NodeUI nodeUi, GraphMenu menu )
	{
		var nodeTypes = ((ActionGraphView)nodeUi.Graph)
			.GetExpansionOptions( plug )
			.ToArray();

		PopulateNodeMenu( menu, nodeTypes, null, type =>
		{
			plug.Expand( ((SelectedOutputNodeType)type).Inner, nodeUi );
		} );
	}
}

public class ActionInputPlug : ActionPlug<Node.Input>, IPlugIn
{
	public ActionInputPlug( EditorNode node, IReadOnlyDictionary<string, Node.Input> source, string identifier, int index ) : base( node, source, identifier, index )
	{
	}

	public Link InputLink => IsArrayElement
		? Index >= 0 && Index < Parameter.LinkArray!.Count ? Parameter.LinkArray[Index] : null
		: Parameter.Link;

	public Node InputNestedNode
	{
		get
		{
			var link = InputLink;

			if ( link is not { Source.Node: { Parent: { } parent } node } )
			{
				return null;
			}

			if ( parent != Node.Node )
			{
				return null;
			}

			return node;
		}
	}

	public override Type Type => InputLink?.Source.Type ?? (Parameter.LinkArray is not null ? Parameter.ElementType : Parameter.Type);

	public IPlugOut ConnectedOutput
	{
		get
		{
			if ( IsArrayElement )
			{
				if ( Parameter.LinkArray is not { } links )
				{
					return null;
				}

				if ( Index < 0 || Index >= links.Count )
				{
					return null;
				}

				return links[Index] is { Source: { } output }
					? Node.Graph.FindNode( output.Node )?.Outputs[output]
					: null;
			}
			else
			{
				if ( Index is 0 && Parameter.Link is { Source: { } output } )
				{
					return Node.Graph.FindNode( output.Node )?.Outputs[output];
				}
			}

			return null;
		}
		set
		{
			if ( value is null )
			{
				if ( !Parameter.IsLinked )
				{
					return;
				}

				if ( Index == 0 && Parameter.LinkArray?.Count == 1 || !Parameter.IsArray )
				{
					Parameter.ClearLinks();
				}
				else
				{
					var defaultValue = Parameter.ElementType.IsValueType
						? Activator.CreateInstance( Parameter.ElementType )
						: null;

					Parameter.SetLink( new Constant( defaultValue ), Index );
				}

				Node.MarkDirty();
				return;
			}

			if ( value is not ActionPlug<Node.Output> { Parameter: { } output } )
			{
				return;
			}

			if ( !Parameter.IsArray || value.Type.IsArray && Index is 0 && Parameter.LinkArray is null )
			{
				Parameter.SetLink( output );
				Node.MarkDirty();
				return;
			}

			if ( Index < (Parameter.LinkArray?.Count ?? 0) )
			{
				Parameter.SetLink( output, Index );
			}
			else
			{
				Parameter.InsertLink( output, Index );
				Node.MarkDirty();
			}
		}
	}

	public override bool ShowConnection => true;

	public override bool IsArrayElement => Parameter is { LinkArray: not null };

	public override ValueEditor CreateEditor( NodeUI node, Plug plug )
	{
		if ( Node.Definition == EditorNodeLibrary.NoOperation )
		{
			return null;
		}

		return new DefaultInputEditor( node, (PlugIn)plug );
	}

	public override GraphMenu CreateContextMenu( NodeUI node, Plug plug )
	{
		if ( Node.Definition == EditorNodeLibrary.NoOperation )
		{
			return null;
		}

		var editorActionGraph = (EditorActionGraph)node.Graph.Graph;
		var actionGraph = editorActionGraph!.Graph;

		var type = Parameter.LinkArray is not null ? Parameter.ElementType : Parameter.Type;
		var matchingVariables = Parameter.Node.ActionGraph.Variables.Values
			.Where( x => !Parameter.IsSignal && x.Type.IsAssignableToExtended( type ) )
			.ToArray();

		var matchingLabeledOutputs = Parameter.Node.ActionGraph.Nodes.Values
			.SelectMany( x => x.Outputs.Values )
			.Where( x => x.GetLabel() is { } )
			.Where( x => x.IsSignal == Parameter.IsSignal && (Parameter.IsSignal || x.Type.IsAssignableToExtended( type )) )
			.ToArray();

		var matchingComponents = editorActionGraph.AvailableComponentTypes
			.Where( x => !Parameter.IsSignal && x.IsAssignableToExtended( type ) )
			.ToArray();

		var menu = new GraphMenu( plug.Title );

		var agNode = Parameter.Node;

		if ( agNode.Definition == EditorNodeLibrary.Output )
		{
			CreateDisplayInfoMenu( menu, value =>
			{
				agNode.ActionGraph.SetParameters(
					agNode.ActionGraph.Inputs.Values.ToArray(),
					agNode.ActionGraph.Outputs.Values
						.Select( x => x.Name == Parameter.Name ? x with { Display = value } : x ).ToArray() );

				agNode.MarkDirty();
			} );
		}

		if ( Parameter.IsLinked && (!IsArrayElement || Index < Parameter.LinkArray!.Count) )
		{
			menu.AddOption( "Clear", "clear", () =>
			{
				node.Graph?.PushUndo( "Clear Link" );

				if ( IsArrayElement && Index <= Parameter.LinkArray!.Count )
				{
					var defaultValue = Parameter.ElementType.IsValueType
						? Activator.CreateInstance( Parameter.ElementType )
						: null;

					Parameter.SetLink( new Constant( defaultValue ), Index );
				}
				else
				{
					Parameter.ClearLinks();
				}

				(node.Node as EditorNode)?.MarkDirty();

				node.Graph?.PushRedo();
			} );
		}

		if ( IsArrayElement && Index < Parameter.LinkArray!.Count )
		{
			menu.AddOption( "Remove", "remove", () =>
			{
				node.Graph?.PushUndo( "Remove Link" );

				Parameter.LinkArray[Index].Remove();
				(node.Node as EditorNode)?.MarkDirty();

				node.Graph?.PushRedo();
			} );
		}

		if ( IsArrayElement )
		{
			menu.AddOption( "Remove All", "playlist_remove", () =>
			{
				node.Graph?.PushUndo( "Remove All Links" );

				Parameter.ClearLinks();
				(node.Node as EditorNode)?.MarkDirty();

				node.Graph?.PushRedo();
			} );
		}

		menu.AddSeparator();

		if ( !Parameter.IsSignal )
		{
			var createVariableMenu = menu.AddMenu( "Create Variable", "add_box" );

			createVariableMenu.AboutToShow += () =>
			{
				createVariableMenu.Clear();

				createVariableMenu.AddLineEdit( "Name", autoFocus: true, onSubmit: value =>
				{
					if ( string.IsNullOrEmpty( value ) ) return;

					node.Graph?.PushUndo( "Create Variable" );

					var variable = actionGraph.AddVariable( value, type );

					Parameter.SetLink( variable );

					node.Graph?.PushRedo();
				} );
			};

			if ( editorActionGraph.CanModifyParameters )
			{
				var createInputMenu = menu.AddMenu( "Create Graph Input", "add_box" );

				createInputMenu.AboutToShow += () =>
				{
					createInputMenu.Clear();

					createInputMenu.AddLineEdit( "Name", autoFocus: true, onSubmit: value =>
					{
						if ( string.IsNullOrEmpty( value ) ) return;

						node.Graph.PushUndo( "Create Graph Input" );

						var name = actionGraph.GetNextInputName();
						var input = new InputDefinition( name, Parameter.Type, InputFlags.Required, new DisplayInfo( value ) );

						actionGraph.SetParameters( actionGraph.Inputs.Values.With( input ), actionGraph.Outputs.Values.ToArray() );

						actionGraph.InputNode!.UpdateParameters();

						Parameter.SetLink( actionGraph.InputNode.Outputs[name] );

						editorActionGraph.MarkDirty( actionGraph.InputNode );

						node.Graph.PushRedo();
					} );
				};
			}
		}

		if ( editorActionGraph.CanModifyParameters && Parameter.Node.Definition == EditorNodeLibrary.Output && Parameter.Name.StartsWith( "_out" ) )
		{
			menu.AddOption( "Remove Graph Output", "delete", () =>
			{
				Parameter.ClearLinks();

				var output = actionGraph.Outputs.Values.First( x => x.Name == Parameter.Name );

				actionGraph.SetParameters( actionGraph.Inputs.Values.ToArray(),
					actionGraph.Outputs.Values.Without( output ) );

				actionGraph.PrimaryOutputNode!.UpdateParameters();

				editorActionGraph.MarkDirty( actionGraph.PrimaryOutputNode );
			} );
		}

		if ( matchingVariables.Length > 0 )
		{
			var varMenu = menu.AddMenu( "Use Variable", "inbox" );

			foreach ( var variable in matchingVariables )
			{
				varMenu.AddOption( variable.Name, "inbox", () =>
				{
					node.Graph?.PushUndo( "Create Variable Link" );
					SetLink( node, variable );
					node.Graph?.PushRedo();
				} );
			}
		}

		if ( matchingLabeledOutputs.Length > 0 )
		{
			var labelMenu = menu.AddMenu( "Use Labeled Output", "link" );

			foreach ( var output in matchingLabeledOutputs )
			{
				labelMenu.AddOption( output.GetNiceLabel(), "link", () =>
				{
					node.Graph?.PushUndo( "Create Labeled Output Link" );
					SetLink( node, output );
					node.Graph?.PushRedo();
				} );
			}
		}

		if ( matchingComponents.Length > 0 )
		{
			var componentMenu = menu.AddMenu( "Use Component", "category" );

			foreach ( var componentType in matchingComponents )
			{
				var typeDesc = TypeLibrary.GetType( componentType );

				componentMenu.AddOption( typeDesc.Title, typeDesc.Icon ?? "category", () =>
				{
					node.Graph?.PushUndo( "Create Component Link" );

					var nodeType = new LocalTargetNodeType( new ComponentNodeType( typeDesc ) );
					var getNode = nodeType.CreateNode( actionGraph, agNode );

					SetLink( node, getNode.Outputs.Result );

					node.Graph?.PushRedo();
				} );
			}
		}

		EditorEvent.Run( PopulateInputPlugMenuEvent.EventName,
			new PopulateInputPlugMenuEvent( (ActionGraphView)node.Graph, this, menu ) );

		return menu;
	}

	private void SetLink( NodeUI node, ILinkSource source )
	{
		if ( IsArrayElement )
		{
			if ( Index < Parameter.LinkArray!.Count )
			{
				Parameter.SetLink( source, Index );
			}
			else
			{
				Parameter.InsertLink( source, Index );
			}
		}
		else
		{
			Parameter.SetLink( source );
		}

		(node.Node as EditorNode)?.MarkDirty();
	}

	public void GoToSource( NodeUI node, Plug plug )
	{
		if ( ConnectedOutput is ActionOutputPlug connectedOutput )
		{
			node.Graph.SelectNode( connectedOutput.Node );
			node.Graph.CenterOnSelection();
			return;
		}

		var eventArgs = new GoToPlugSourceEvent( (ActionGraphView)node.Graph, this );

		EditorEvent.Run( GoToPlugSourceEvent.EventName, eventArgs );

		if ( eventArgs.Handled )
		{
			return;
		}

		node.Graph.SelectNode( Node );

		if ( node.Graph is ActionGraphView actionGraphView )
		{
			actionGraphView.FocusOnInput( Parameter, IsArrayElement ? Index : null );
		}
	}

	public override void OnDoubleClick( NodeUI node, Plug plug )
	{
		GoToSource( node, plug );
	}

	public float? GetHandleOffset( string name )
	{
		return InputLink?.UserData[name]?.GetValue<float>();
	}

	public void SetHandleOffset( string name, float? value )
	{
		if ( InputLink is { } link )
		{
			if ( value is null ) link.UserData.Remove( name );
			else link.UserData[name] = value;
		}
	}
}

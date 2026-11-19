using Editor.Inspectors;
using Editor.NodeEditor;
using Rinten.UI.NodeGraph;
using Facepunch.ActionGraphs;
using Rinten;
using Rinten.Internal;
using Rinten.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace Editor.ActionGraphs;

/// <summary>The dock slot the node inspector is shown in.</summary>
public class PropertiesDock : PanelDockHost
{
	public Properties Properties { get; }

	public PropertiesDock( MainWindow mainWindow ) : base( null )
	{
		Name = "Inspector";
		WindowTitle = "Inspector";

		SetWindowIcon( "manage_search" );

		Properties = new Properties( mainWindow );

		SetContent( Properties );
	}
}

/// <summary>What the selected node has to edit: its own properties, and the inputs nothing is wired into.</summary>
[StyleSheet( "Properties.cs.scss" )]
public class Properties : Panel
{
	private static HashSet<Type> DefaultNumericTypes { get; } = new()
	{
		typeof(int),
		typeof(float),
		typeof(Vector2),
		typeof(Vector3),
		typeof(Vector4)
	};

	private static HashSet<Type> DefaultTypes { get; } = new()
	{
		typeof(int),
		typeof(float),
		typeof(Vector2),
		typeof(Vector3),
		typeof(Vector4),
		typeof(Color),
		typeof(string),
		typeof(bool),
		typeof(object)
	};

	public static Type DefaultType { get; } = Either.CreateType( DefaultTypes );

	public static Type DefaultNumericType { get; } = Either.CreateType( DefaultNumericTypes );

	public static Type PredictBestType( Node.IParameter target )
	{
		if ( target is not Node.Input )
		{
			return DefaultType;
		}

		if ( !target.Node.Definition.IsOperator() )
		{
			return DefaultType;
		}

		var otherInput = target.Name switch
		{
			"a" => target.Node.Inputs.TryGetValue( "b", out var b ) ? b : null,
			"b" => target.Node.Inputs.TryGetValue( "a", out var a ) ? a : null,
			_ => null
		};

		switch ( target.Node.Definition.Identifier )
		{
			case "op.conditional":
			case "op.coalesce":
			case "op.equal":
			case "op.notequal":
				return otherInput?.SourceType ?? DefaultType;

			default:
				return otherInput?.SourceType is { } otherType ? typeof( Either<,> ).MakeGenericType( otherType, DefaultNumericType ) : DefaultNumericType;
		}
	}

	public MainWindow MainWindow { get; }

	private readonly Panel _content;

	private object _target;
	private bool _contentInvalid;

	//What to put the caret in once the rows are up.
	private (Node.Input Input, int? Index)? _pendingFocus;

	public object Target
	{
		get => _target;
		set
		{
			if ( _target == value ) return;

			_target = value;
			_contentInvalid = true;
		}
	}

	public Properties( MainWindow mainWindow )
	{
		MainWindow = mainWindow;

		AddClass( "action-properties" );

		_content = AddChild<Panel>( "content" );

		MainWindow.SelectionChanged += OnSelectionChanged;
		MainWindow.FocusedOnInput += FocusOnInput;
	}

	public override void OnDeleted()
	{
		base.OnDeleted();

		MainWindow.SelectionChanged -= OnSelectionChanged;
		MainWindow.FocusedOnInput -= FocusOnInput;
	}

	private void OnSelectionChanged( object target ) => Target = target;

	public override void Tick()
	{
		base.Tick();

		var target = Target;

		if ( target is NodeUI nodeUi ) target = nodeUi.Node;

		if ( target is EditorNode node )
		{
			if ( !node.Node.IsValid ) Target = null;
			else node.MarkDirty();
		}

		if ( _contentInvalid )
		{
			_contentInvalid = false;

			RebuildContent();
		}

		ApplyPendingFocus();
	}

	//An edit is about to be made, so the state before it is what an undo goes back to. The focus event reaches here
	//from whichever control has just taken it, so this runs once as the caret lands rather than on every keystroke.
	protected override void OnFocus( PanelEvent e )
	{
		MainWindow.FocusedView?.PushUndo( "Edit Properties" );
	}

	/// <summary>Puts the caret in the row for an input the graph is asking about.</summary>
	public void FocusOnInput( Node.Input input, int? index )
	{
		if ( _contentInvalid )
		{
			_contentInvalid = false;

			RebuildContent();
		}

		_pendingFocus = (input, index);
	}

	//Focuses the row that was asked for, once there is a row to focus. A frame later than the ask, because the
	//sheet builds its rows on its own tick - at the moment of the ask there is nothing there to put a caret in.
	private void ApplyPendingFocus()
	{
		if ( _pendingFocus is not { } wanted ) return;

		var match = FindControl( wanted.Input, wanted.Index ) ?? FindControl( wanted.Input, null );

		if ( match is null ) return;

		_pendingFocus = null;

		match.Focus();
	}

	//The control editing one input of the node, if it is on the sheet.
	private BaseControl FindControl( Node.Input input, int? index )
	{
		foreach ( var control in Descendants.OfType<BaseControl>() )
		{
			if ( control.Property is not ISerializedNodeParameter parameter ) continue;
			if ( !ReferenceEquals( parameter.Parameter, input ) ) continue;

			var elementIndex = control.Property is SerializedNodeInputElement element ? element.Index : (int?)null;

			if ( index is not null && elementIndex != index ) continue;

			return control;
		}

		return null;
	}

	private void RebuildContent()
	{
		_content.DeleteChildren();

		if ( _target is null ) return;

		var target = Target;

		if ( target is NodeUI nodeUi ) target = nodeUi.Node;
		else nodeUi = null;

		switch ( target )
		{
			case CommentEditorNode commentNode:
				AddObject( EditorTypeLibrary.GetSerializedObject( commentNode ) ).OnPropertyChanged += _ => commentNode.MarkDirty();
				break;

			case EditorActionGraph actionGraph:
			{
				var obj = AddObject( EditorTypeLibrary.GetSerializedObject( actionGraph ) );
				var inputNode = actionGraph.FindNode( actionGraph.Graph.InputNode );

				obj.OnPropertyChanged += _ => inputNode?.MarkDirty();
				break;
			}

			case EditorNode node:
			{
				var obj = AddObject( EditorTypeLibrary.GetSerializedObject( node ) );

				obj.OnPropertyChanged += _ => nodeUi!.Position = node.Position;

				obj = AddObject( new SerializedActionNode( this, node ) );
				obj.OnPropertyChanged += _ => node.MarkDirty();
				break;
			}
		}
	}

	//One sheet of rows for one object, under whatever is already there.
	private SerializedObject AddObject( SerializedObject obj )
	{
		_content.AddChild( new Editor.Inspectors.ControlSheet { Target = obj } );

		return obj;
	}
}

internal class SerializedActionNode : SerializedObject
{
	private static bool CanEditInputType( Type type )
	{
		if ( type is null ) return false;
		if ( Either.IsEitherType( type ) ) return true;

		if ( type.IsAbstract || type.IsInterface || type.IsArray || type.ContainsGenericParameters )
		{
			return false;
		}

		if ( type == typeof( Task ) || type == typeof( Task[] ) ) return false;
		if ( type == typeof( Variable ) ) return false;

		//Should use scene ref / resource ref nodes instead
		if ( type == typeof( GameObject ) ) return false;
		if ( type.IsAssignableTo( typeof( Component ) ) ) return false;
		if ( type.IsAssignableTo( typeof( Resource ) ) ) return false;

		return true;
	}

	private static HashSet<string> HiddenProperties { get; } = new()
	{
		"graph/graph",
		"input.value/_name",
		"var/_var",
		"property/_name", "property/_type",
		"call/_name", "call/_type", "call/_isStatic",
		"new/_type",
		"switch/cases"
	};

	private static HashSet<string> HiddenInputs { get; } = new()
	{

	};

	public SerializedActionNode( Properties properties, EditorNode node )
	{
		PropertyList = new();

		foreach ( var (name, property) in node.Node.Properties )
		{
			if ( property.Display.Hidden is true )
			{
				continue;
			}

			if ( HiddenProperties.Contains( $"{node.Definition.Identifier}/{name}" ) )
			{
				continue;
			}

			PropertyList.Add( new SerializedNodeProperty( properties, node, property ) );
		}

		if ( node.Definition.Identifier == "switch" )
		{
			AddSwitchNodeProperties( properties, node );
		}

		foreach ( var (name, input) in node.Node.Inputs )
		{
			if ( HiddenInputs.Contains( $"{node.Definition.Identifier}/{name}" ) )
			{
				continue;
			}

			if ( !input.IsArray )
			{
				if ( input.IsLinked && input.Link?.TryGetConstant( out _ ) is not true )
				{
					continue;
				}

				if ( !CanEditInputType( input.Type ) )
				{
					continue;
				}

				PropertyList.Add( new SerializedNodeInput( properties, node, input ) );
			}
			else
			{
				if ( input.Link is not null )
				{
					continue;
				}

				if ( !CanEditInputType( input.ElementType ) )
				{
					continue;
				}

				var count = input.LinkArray?.Count ?? 0;

				for ( var i = 0; i <= count; ++i )
				{
					if ( i < count && input.LinkArray?[i].TryGetConstant( out _ ) is not true )
					{
						continue;
					}

					PropertyList.Add( new SerializedNodeInputElement( properties, node, input, i ) );
				}
			}
		}
	}

	private void AddSwitchNodeProperties( Properties properties, EditorNode node )
	{
		var valueType = GetSwitchValueType( node.Node ) ?? typeof( string );
		var sPropType = typeof( SerializedSwitchCasesProperty<> ).MakeGenericType( valueType );

		PropertyList.Add( (SerializedProperty)Activator.CreateInstance( sPropType, properties, node ) );
	}

	private Type GetSwitchValueType( Node node )
	{
		if ( node.Inputs["value"].SourceType is { } valueType )
		{
			return valueType;
		}

		if ( node.Properties["cases"].Value is not { } casesObj )
		{
			return null;
		}

		foreach ( var iFace in casesObj.GetType().GetInterfaces() )
		{
			if ( !iFace.IsConstructedGenericType ) continue;
			if ( iFace.GetGenericTypeDefinition() != typeof( IReadOnlyList<> ) ) continue;

			return iFace.GetGenericArguments()[0];
		}

		return null;
	}
}

public interface ISerializedNodeParameter
{
	Node.IParameter Parameter { get; }
}

internal abstract class SerializedNodeParameter<T> : SerializedProperty, ISerializedNodeParameter
	where T : Node.IParameter
{
	public Properties Properties { get; }
	public EditorNode Node { get; }
	public T Target { get; }

	public override string Name => Target.Name;
	public override string DisplayName => Target.Display.Title;
	public override string Description => Target.Display.Description;
	public override Type PropertyType => Target.Type == typeof( object )
		? Properties.PredictBestType( Target )
		: Nullable.GetUnderlyingType( Target.Type ) ?? Target.Type;

	public abstract object Value { get; set; }
	public abstract object DefaultValue { get; }
	public abstract bool IsRequired { get; }
	public abstract bool HasValue { get; }

	Node.IParameter ISerializedNodeParameter.Parameter => Target;

	public override IEnumerable<Attribute> GetAttributes() => Target.Attributes.Union( new[] { new AllowNullAttribute() } );

	protected SerializedNodeParameter( Properties properties, EditorNode node, T target )
	{
		Properties = properties;
		Node = node;
		Target = target;
	}

	public override bool TryGetAsObject( out SerializedObject obj )
	{
		obj = null;

		var description = EditorTypeLibrary.GetType( PropertyType );

		if ( description == null )
		{
			return false;
		}

		try
		{
			if ( !PropertyType.IsValueType )
			{
				var curVal = GetValue<object>();

				if ( curVal == null )
				{
					return false;
				}

				obj = EditorTypeLibrary.GetSerializedObject( curVal );
				return true;
			}

			obj = EditorTypeLibrary.GetSerializedObject( () => HasValue && Value is not null ? Value : IsRequired || DefaultValue is null
					? Activator.CreateInstance( PropertyType )
					: DefaultValue,
				description, this );
			return true;
		}
		catch ( Exception e )
		{
			Log.Warning( e );
			obj = null;
			return false;
		}
	}

	private static object ConvertTo( object value, Type sourceType, Type targetType )
	{
		if ( sourceType == targetType || sourceType.IsAssignableTo( targetType ) )
		{
			return value;
		}

		if ( Nullable.GetUnderlyingType( targetType ) is { } underlyingTargetType && Nullable.GetUnderlyingType( sourceType ) is null )
		{
			if ( value is null )
			{
				return Activator.CreateInstance( targetType, null );
			}

			return Activator.CreateInstance( targetType, ConvertTo( value, sourceType, underlyingTargetType ) );
		}

		if ( sourceType == typeof( long ) && targetType.IsEnum )
		{
			//Special case for EnumControlWidget :S

			return Enum.ToObject( targetType, (long)value );
		}

		if ( sourceType.IsEnum && targetType == typeof( long ) )
		{
			//Special case for EnumControlWidget :S

			return Convert.ChangeType(
				Convert.ChangeType(
					value,
					Enum.GetUnderlyingType( sourceType ) ),
				targetType );
		}

		return Convert.ChangeType( value, targetType );
	}

	public override void SetValue<TVal>( TVal value )
	{
		if ( Either.IsEitherType( PropertyType ) )
		{
			Value = value;
		}
		else
		{
			try
			{
				Value = ConvertTo( value, typeof( TVal ), PropertyType );
			}
			catch
			{
				Value = value;
			}
		}

		Node.MarkDirty();
	}

	public override TVal GetValue<TVal>( TVal defaultValue = default )
	{
		var rawValue = HasValue ? Value : Target.Definition.IsRequired ? defaultValue : DefaultValue;

		if ( rawValue is TVal value )
		{
			return value;
		}

		if ( rawValue is null )
		{
			return defaultValue;
		}

		try
		{
			return (TVal)ConvertTo( rawValue, rawValue.GetType(), typeof( TVal ) );
		}
		catch
		{
			return defaultValue;
		}
	}
}

internal class SerializedNodeProperty : SerializedNodeParameter<Node.Property>, Editor.Inspectors.ITypeConstraintSource
{
	public override string GroupName => "Properties";

	//A property that picks a type is filling in the node's generic parameter, so what the node will take is what the picker may offer.
	Editor.Inspectors.TypeConstraint Editor.Inspectors.ITypeConstraintSource.TypeConstraint =>
		new() { GenericParameter = Target.Definition?.GenericParameter };

	public SerializedNodeProperty( Properties properties, EditorNode node, Node.Property target ) : base( properties, node, target ) { }

	public override object Value
	{
		get => Target.Value;
		set => Target.Value = value;
	}

	public override Type PropertyType => Target.Type;
	public override object DefaultValue => Target.Definition.IsRequired ? null : Target.Definition.Default;

	public override bool HasValue => Target.Value is not null;
	public override bool IsRequired => Target.Definition.IsRequired;
}

internal class SerializedSwitchCasesProperty<T> : SerializedNodeProperty
{
	public override Type PropertyType { get; }

	private List<T> _value;

	public override object Value
	{
		get
		{
			if ( _value is not null ) return _value;

			if ( base.Value is not IReadOnlyList<T> baseValue )
			{
				return _value = new List<T>( (IReadOnlyList<T>)base.DefaultValue );
			}

			if ( baseValue is List<T> list )
			{
				return _value = list;
			}

			return _value = new List<T>( baseValue );
		}
		set
		{
			_value = (List<T>)value;
			base.Value = _value.ToArray();
		}
	}

	public override bool HasValue => true;

	public SerializedSwitchCasesProperty( Properties properties, EditorNode node )
		: base( properties, node, node.Node.Properties["cases"] )
	{
		PropertyType = typeof( List<> ).MakeGenericType( typeof( T ) );
	}

	public override bool TryGetAsObject( out SerializedObject obj )
	{
		obj = EditorTypeLibrary.GetSerializedObject( Value );
		obj.ParentProperty = this;
		obj.OnPropertyChanged += prop =>
		{
			Node.Node.Properties["cases"].Value = null;
			Node.Node.Properties["cases"].Value = ((List<T>)Value).ToArray();
			Node.MarkDirty();
		};

		return true;
	}
}

internal class SerializedNodeInput : SerializedNodeParameter<Node.Input>
{
	public override string GroupName => "Inputs";

	public SerializedNodeInput( Properties properties, EditorNode node, Node.Input target ) : base( properties, node, target ) { }

	public override object Value
	{
		get => Target.Value;
		set => Target.Value = value;
	}

	public override object DefaultValue => Target.Definition.IsRequired ? null : Target.Definition.Default;

	public override bool HasValue => Target.Link?.TryGetConstant( out _ ) is true;
	public override bool IsRequired => Target.Definition.IsRequired;
}

internal class SerializedNodeInputElement : SerializedNodeParameter<Node.Input>
{
	public int Index { get; }

	public override string DisplayName => $"{Target.Display.Title}[{Index}]";
	public override string GroupName => "Inputs";

	public SerializedNodeInputElement( Properties properties, EditorNode node, Node.Input target, int index ) : base( properties, node, target )
	{
		Index = index;
	}

	public override object Value
	{
		get => Target.LinkArray is { } linkArray && Index >= 0 && Index < linkArray.Count
			&& linkArray[Index].TryGetConstant( out var value ) ? value : null;
		set
		{
			if ( Target.LinkArray is { } linkArray && Index >= 0 && Index < linkArray.Count )
			{
				Target.SetLink( new Constant( value ), Index );
			}
			else
			{
				Target.InsertLink( new Constant( value ), Index );
			}
		}
	}

	public override Type PropertyType => Target.ElementType == typeof( object ) ? Properties.PredictBestType( Target ) : Target.ElementType;
	public override object DefaultValue => Target.ElementType.IsValueType
		? Activator.CreateInstance( Target.ElementType )
		: null;

	public override bool HasValue => Target.LinkArray is { } linkArray && Index >= 0 && Index < linkArray.Count
		&& linkArray[Index].TryGetConstant( out _ );
	public override bool IsRequired => false;
}

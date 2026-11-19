using Editor.NodeEditor;
using Rinten.UI.NodeGraph;
using System;
using Facepunch.ActionGraphs;
using Rinten;
using Rinten.UI;
using DisplayInfo = Rinten.DisplayInfo;
using UILabel = Rinten.UI.Label;

namespace Editor.ActionGraphs;

public interface IPulseTarget
{
	void UpdatePulse( float scale, Color colorTint );
}

//A colour gets a swatch, a variable or a labelled output gets an icon, and everything else is its own text. The border is the plug's colour, and it swells when a value passes through.
/// <summary>The value a plug carries, shown as a chip beside it.</summary>
public sealed class ValueChip : Panel
{
	private readonly Panel _swatch;
	private readonly IconPanel _icon;
	private readonly UILabel _text;

	public ValueChip()
	{
		AddClass( "value-chip" );

		_swatch = AddChild<Panel>( "swatch" );

		_icon = AddChild<IconPanel>( "icon" );

		_text = AddChild<UILabel>( "text" );
		_text.Tokenize = false;
	}

	public void Apply( HandleConfig config, string text, string icon, object rawValue, float pulseScale )
	{
		var border = config.Color.Desaturate( 0.2f ).Darken( 0.3f );
		var background = Theme.ControlBackground;

		if ( pulseScale > 1.0f )
		{
			background = Color.Lerp( background, border, (pulseScale - 1.0f) * 0.25f );
		}

		Style.BackgroundColor = background;
		Style.BorderColor = border;
		Style.BorderWidth = 2.0f * (pulseScale * 0.5f + 0.5f);

		var swatch = rawValue as Color?;

		_swatch.SetClass( "hidden", swatch is null );
		if ( swatch is { } colour ) _swatch.Style.BackgroundColor = colour;

		_icon.SetClass( "hidden", string.IsNullOrEmpty( icon ) );
		_icon.Text = icon ?? string.Empty;

		_text.Text = text ?? string.Empty;
	}
}

/// <summary>What a plug shows while nothing is wired into it.</summary>
public abstract class DefaultEditor<TPlugUI, TActionPlug, TParam> : ValueEditor, IPulseTarget
	where TPlugUI : Plug
	where TActionPlug : ActionPlug<TParam>
	where TParam : Node.IParameter
{
	public NodeUI Node { get; }
	public TPlugUI PlugUI { get; }
	public TActionPlug ActionPlug { get; }
	public TParam Param { get; }

	public override bool HideLabel => false;

	protected float PulseScale { get; private set; } = 1.0f;
	protected Color ColorTint { get; private set; }

	protected ValueChip Chip { get; }

	private int _lastHash;

	protected DefaultEditor( NodeUI node, TPlugUI parent )
	{
		Node = node;
		PlugUI = parent;
		ActionPlug = (TActionPlug)parent.Inner;
		Param = ActionPlug.Parameter;

		Chip = AddChild<ValueChip>();
	}

	public void UpdatePulse( float scale, Color colorTint )
	{
		PulseScale = scale;
		ColorTint = colorTint;

		Update();
	}

	public override void Tick()
	{
		base.Tick();

		Read();
	}

	//Re-reads what the plug is carrying, and shows it if anything changed.
	protected abstract void Read();

	//The plug's colour, with whatever is pulsing through it mixed in.
	protected HandleConfig TintedConfig
	{
		get
		{
			var config = PlugUI.HandleConfig;

			config.Color = Color.Lerp( config.Color, ColorTint.WithAlpha( 1.0f ), ColorTint.a );

			return config;
		}
	}

	//Applies the chip, but only when something about it has moved on.
	protected void Show( string text, string icon, object rawValue )
	{
		var hash = HashCode.Combine( text, icon, rawValue, PulseScale, ColorTint );

		if ( hash == _lastHash ) return;

		_lastHash = hash;

		Chip.Apply( TintedConfig, text, icon, rawValue, PulseScale );
	}

	//Whether the chip is shown at all.
	protected void SetShown( bool shown )
	{
		Enabled = shown;

		SetClass( "hidden", !shown );
	}
}

/// <summary>The value an input is fed while nothing is wired into it.</summary>
public class DefaultInputEditor : DefaultEditor<PlugIn, ActionInputPlug, Node.Input>
{
	public DefaultInputEditor( NodeUI node, PlugIn parent ) : base( node, parent )
	{
		AddClass( "input-value" );
	}

	protected override void Read()
	{
		var eventArgs = new BuildInputLabelEvent( (ActionGraphView)Node.Graph, ActionPlug );

		EditorEvent.Run( BuildInputLabelEvent.EventName, eventArgs );

		var shown = eventArgs.Handled
			&& (eventArgs.Value is not null || eventArgs.Text is not null && eventArgs.Icon is not null);

		SetShown( shown );

		if ( !shown ) return;

		//A label somebody else wrote is text and nothing else; only a value read off the link has a colour worth a swatch.
		if ( eventArgs.Text is { } label )
		{
			Show( label, eventArgs.Icon, null );
			return;
		}

		var text = ValueText.FormatValue( ActionPlug.Type, eventArgs.Value, out var rawValue );

		Show( text, eventArgs.Icon, rawValue );
	}
}

/// <summary>The name an output carries, where it has been given one.</summary>
public class DefaultOutputEditor : DefaultEditor<PlugOut, ActionOutputPlug, Node.Output>
{
	public DefaultOutputEditor( NodeUI node, PlugOut parent ) : base( node, parent )
	{
		AddClass( "output-value" );
	}

	protected override void Read()
	{
		var label = ActionPlug.Label;

		SetShown( label is not null );

		if ( label is null ) return;

		Show( label, "link", null );
	}
}

//Each of these answers a kind of link - a constant, a variable, a component read off the scene - and the first one that recognises it wins.
/// <summary>What an input says it is carrying, decided by whoever knows best.</summary>
public static class InputLabels
{
	[Event( BuildInputLabelEvent.EventName )]
	public static void BuildDefaultInputLabel( BuildInputLabelEvent eventArgs )
	{
		if ( eventArgs.Handled ) return;
		if ( eventArgs.Input.IsLinked ) return;
		if ( eventArgs.Input.Definition.IsRequired ) return;
		if ( eventArgs.Input.Definition.Default is not { } defaultValue || defaultValue is Array ) return;

		eventArgs.Value = defaultValue;
		eventArgs.Handled = true;
	}

	[Event( BuildInputLabelEvent.EventName )]
	public static void BuildConstantInputLabel( BuildInputLabelEvent eventArgs )
	{
		if ( eventArgs.Handled ) return;
		if ( eventArgs.Link?.TryGetConstant( out var constValue ) is not true ) return;

		eventArgs.Value = constValue;
		eventArgs.Handled = true;
	}

	[Event( BuildInputLabelEvent.EventName )]
	public static void BuildVariableInputLabel( BuildInputLabelEvent eventArgs )
	{
		if ( eventArgs.Handled ) return;
		if ( eventArgs.Link?.TryGetVariable( out var variable ) is not true ) return;

		eventArgs.Text = variable.Name;
		eventArgs.Icon = "inbox";
		eventArgs.Handled = true;
	}

	[Event( BuildInputLabelEvent.EventName )]
	public static void BuildComponentInputLabel( BuildInputLabelEvent eventArgs )
	{
		if ( eventArgs.Handled ) return;
		if ( eventArgs.Link is null ) return;
		if ( eventArgs.Link.Source.Node.Parent != eventArgs.Plug.Node.Node ) return;
		if ( eventArgs.Link.Source.Node.Definition.Identifier != "scene.get" ) return;

		var displayInfo = Rinten.DisplayInfo.ForType( eventArgs.Link.Source.Type );

		eventArgs.Text = displayInfo.Name;
		eventArgs.Icon = displayInfo.Icon ?? "category";
		eventArgs.Handled = true;
	}

	[Event( BuildInputLabelEvent.EventName )]
	public static void BuildLabelledInputLabel( BuildInputLabelEvent eventArgs )
	{
		if ( eventArgs.Handled ) return;
		if ( eventArgs.Link?.Source.GetNiceLabel() is not { } label ) return;

		eventArgs.Text = label;
		eventArgs.Icon = "link";
		eventArgs.Handled = true;
	}
}

using Microsoft.AspNetCore.Components.Rendering;
using Rinten.UI;
namespace Rinten;

[Category( "UI Panels" )]
[Icon( "widgets" )]
public abstract partial class PanelComponent : Component, IPanelComponent, Component.DontExecuteOnServer
{
	Panel panel;

	/// <summary>The panel. Can be null if the panel doesn't exist yet.</summary>
	public Panel Panel => panel;

	string loadedStyleSheet;

	internal override void OnEnabledInternal()
	{
		EnsurePanelCreated();

		base.OnEnabledInternal();
	}

	internal override void OnDisabledInternal()
	{
		base.OnDisabledInternal();

		DestroyPanel();
	}

	private void EnsurePanelCreated()
	{
		loadedStyleSheet = null;
		panel = new CustomBuildPanel( this, BuildRenderTree, OnTreeRenderedInternal, GetRenderTreeChecksum, BuildRenderHash );
		panel.ElementName = GetType().Name.ToLower();
		panel.GameObject = GameObject;

		LoadStyleSheet();
		UpdateParent();

		var type = Game.TypeLibrary?.GetType( GetType() );
		if ( type is not null )
		{
			panel.SourceFile = type.SourceFile;
			panel.SourceLine = type.SourceLine;
		}

	}

	internal void EnsureParentPanel()
	{
		if ( panel is not null && panel.Parent is null )
		{
			if ( panel.IsValid() )
			{
				UpdateParent();
			}
			else
			{
				//Make sure panel is created, it may have been deleted on parent disable.
				EnsurePanelCreated();
			}
		}
	}

	private void DestroyPanel()
	{
		if ( !panel.IsValid() )
			return;

		panel.Parent = null;
		panel.Delete();
		panel = null;
	}

	protected override void OnStart()
	{
		UpdateParent();
	}

	protected override void OnParentChanged( GameObject oldParent, GameObject newParent )
	{
		UpdateParent();
	}

	void UpdateParent()
	{
		if ( !panel.IsValid() ) return;

		panel.Parent = FindParentPanel();
	}

	Panel FindParentPanel()
	{
		//do we have any root panels with us?
		if ( Components.Get<IRootPanelComponent>() is IRootPanelComponent r )
		{
			return r.GetPanel();
		}

		//Do we have any parent panels we can become a child of?
		var parentPanel = GameObject.Components.Get<IPanelComponent>( FindMode.InAncestors | FindMode.Enabled );
		return parentPanel?.GetPanel();
	}

	Panel IPanelComponent.GetPanel()
	{
		return panel;
	}

	//Gets overridden by .razor file
	protected virtual void BuildRenderTree( RenderTreeBuilder v ) { }

	//Gets overridden by .razor file
	protected virtual string GetRenderTreeChecksum() => string.Empty;

	private int BuildRenderHash()
	{
		if ( !panel.IsValid() ) return 0;

		return HashCode.Combine( BuildHash(), panel.Parent );
	}

	//Called when the razor ui has been built.
	protected virtual void OnTreeFirstBuilt()
	{

	}

	//Called after the tree has been built. This can happen any time the contents change.
	protected virtual void OnTreeBuilt()
	{

	}

	private void OnTreeRenderedInternal( bool firstTime )
	{
		if ( firstTime )
		{
			OnTreeFirstBuilt();
		}

		OnTreeBuilt();
	}

	//When this has changes, we will re-render this panel. This is usually implemented
	//as a HashCode.Combine containing stuff that causes the panel's content to change.
	protected virtual int BuildHash() => 0;

	void LoadStyleSheet()
	{
		var type = Game.TypeLibrary?.GetType( GetType() );
		if ( type is null )
			return;

		//Get the shortest class file (incase we have MyPanel.SomeStuff.Blah)
		var location = type.GetAttributes<Rinten.Internal.ClassFileLocationAttribute>()
			.MinBy( x => x.Path.Length );

		if ( location is null )
			return;

		var path = BaseFileSystem.NormalizeFilename( location.Path + ".scss" );

		//Nothing to do
		if ( loadedStyleSheet == path ) return;

		//Remove old sheet
		if ( !string.IsNullOrWhiteSpace( loadedStyleSheet ) )
			panel.StyleSheet.Remove( loadedStyleSheet );

		//Add new one. Quietly: this is the sheet named after the class file rather than one anybody asked for,
		//and a razor component with no styles of its own - one that only hosts another - is not missing anything.
		loadedStyleSheet = path;
		panel.StyleSheet.Load( loadedStyleSheet, failSilently: true );
	}

	/// <summary>Should be called when you want the component to be re-rendered.</summary>
	public void StateHasChanged()
	{
		panel?.StateHasChanged();
	}

	internal protected virtual void OnMouseDown( MousePanelEvent e ) { }

	internal protected virtual void OnMouseMove( MousePanelEvent e ) { }

	internal protected virtual void OnMouseUp( MousePanelEvent e ) { }

	internal protected virtual void OnMouseOut( MousePanelEvent e ) { }

	internal protected virtual void OnMouseOver( MousePanelEvent e ) { }

	internal protected virtual void OnMouseWheel( Vector2 value ) { }
}

//A panel where we control the tree build.
//Named, not file-local: a web export keeps reflection metadata only for named types, and the tick reads its overrides by it
internal sealed class CustomBuildPanel : Panel
{
	PanelComponent component;
	Action<RenderTreeBuilder> treeBuilder;
	Action<bool> treeRendered;
	Func<string> treeChecksum;
	Func<int> buildHash;

	public CustomBuildPanel( PanelComponent component, Action<RenderTreeBuilder> treeBuilder, Action<bool> treeRendered, Func<string> treeChecksum, Func<int> buildHash )
	{
		this.component = component;
		this.treeBuilder = treeBuilder;
		this.treeRendered = treeRendered;
		this.treeChecksum = treeChecksum;
		this.buildHash = buildHash;
	}

	protected override void BuildRenderTree( RenderTreeBuilder v ) => treeBuilder?.Invoke( v );
	protected override void OnAfterTreeRender( bool firstTime ) => treeRendered?.Invoke( firstTime );
	protected override string GetRenderTreeChecksum() => treeChecksum?.Invoke() ?? "";
	protected override int BuildHash() => buildHash?.Invoke() ?? 0;

	protected override void OnMouseDown( MousePanelEvent e ) => component.OnMouseDown( e );
	protected override void OnMouseMove( MousePanelEvent e ) => component.OnMouseMove( e );
	protected override void OnMouseUp( MousePanelEvent e ) => component.OnMouseUp( e );
	protected override void OnMouseOut( MousePanelEvent e ) => component.OnMouseOut( e );
	protected override void OnMouseOver( MousePanelEvent e ) => component.OnMouseOver( e );
	public override void OnMouseWheel( Vector2 value ) => component.OnMouseWheel( value );
}

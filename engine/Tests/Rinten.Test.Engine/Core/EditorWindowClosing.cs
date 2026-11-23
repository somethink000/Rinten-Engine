using Editor;
using Rinten;
using Rinten.UI;
using Button = Editor.Button;

namespace EngineTests;

//Laying out a window after a click closed its content mid-frame must not throw
[TestClass]
[DoNotParallelize]
public class EditorWindowClosingTests
{
	[TestInitialize]
	public void OnPanels()
	{
		ThreadSafe.MarkMainThread();

		EditorBackend.Panels = true;

		Rinten.Engine.GlobalContext.Current.UISystem ??= new UISystem();
	}

	private static RootPanel Root( int width = 400, int height = 300 )
	{
		var root = new UiWindowRoot();

		root.PreLayout( new Rect( 0, 0, width, height ) );
		root.CalculateLayout();
		root.PostLayout();

		return root;
	}

	private static void Frame( RootPanel root, int width = 400, int height = 300 )
	{
		root.TickInternal();
		root.PreLayout( new Rect( 0, 0, width, height ) );
		root.CalculateLayout();
		root.PostLayout();
	}

	[TestMethod]
	public void AWidgetDestroyedInsideAFrameDoesNotTakeTheWindowWithIt()
	{
		var root = Root();

		var widget = new Widget( null );
		widget.Size = new Vector2( 200, 100 );

		root.AddChild( widget.Surface );

		Frame( root );

		//What Close does to a dialog: the widget goes, its front is left in the tree until the panel system gets round to it.
		widget.Destroy();

		Frame( root );
		Frame( root );

		Assert.IsTrue( root.IsValid, "the window's root survived the dialog closing" );
	}

	//And the same for a widget with children of its own, which is what a dialog actually is.
	[TestMethod]
	public void AWholeDialogClosingDoesNotTakeTheWindowWithIt()
	{
		var root = Root();

		var window = new Widget( null );
		window.Size = new Vector2( 300, 200 );
		window.Layout = Layout.Column();

		var field = window.Layout.Add( new LineEdit( window ) );
		var button = window.Layout.Add( new Button( "Create", window ) );

		root.AddChild( window.Surface );

		Frame( root );

		window.Destroy();

		Frame( root );
		Frame( root );

		Assert.IsTrue( root.IsValid );
	}
}

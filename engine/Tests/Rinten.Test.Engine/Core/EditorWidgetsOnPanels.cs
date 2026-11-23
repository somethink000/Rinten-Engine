using Editor;
using Rinten;
using Rinten.UI;
using System.Linq;
using Button = Editor.Button;
using Checkbox = Editor.Checkbox;
using Option = Editor.Option;

namespace EngineTests;

[TestClass]
[DoNotParallelize]
public class EditorWidgetsOnPanelsTests
{
	//A widget that draws something, and says when it was asked.
	private class Painted : Widget
	{
		public int Paints;

		public Painted() : base( null ) { }

		protected override void OnPaint()
		{
			Paints++;

			Paint.SetBrushAndPen( Color.Red );
			Paint.DrawRect( LocalRect );
		}
	}

	//A tab: the plate rounded along the top and square along the bottom.
	private class Tab : Widget
	{
		public Tab() : base( null ) { }

		protected override void OnPaint()
		{
			Paint.Antialiasing = false;

			Paint.ClearPen();
			Paint.SetBrush( Color.Red );
			Paint.DrawRect( LocalRect, Theme.TabRadius, Theme.TabRadius, 0.0f, 0.0f );
		}
	}

	//Flex settle on a root panel, like the running editor.
	private static void Arrange( Widget widget )
	{
		var root = widget.Surface?.FindRootPanel();

		if ( root is null )
		{
			var size = widget.Size;

			if ( size.x <= 0 ) size.x = 800;
			if ( size.y <= 0 ) size.y = 600;

			root = new RootPanel();
			root.PanelBounds = new Rect( 0, 0, size.x, size.y );
			root.AddChild( widget.Surface );
		}

		widget.SettleFlexSubtree();
	}

	[TestInitialize]
	public void OnPanels()
	{
		ThreadSafe.MarkMainThread();

		//What a test host would choose anyway - said out loud because the test is about the panel side.
		EditorBackend.Panels = true;
	}

	[TestMethod]
	public void AWidgetRemembersWhereItIsAndHowBigItIs()
	{
		var widget = new Widget( null );

		Assert.IsTrue( widget.IsValid, "a widget of ours is valid without anything native behind it" );

		widget.Position = new Vector2( 12, 34 );
		widget.Size = new Vector2( 200, 50 );

		Assert.AreEqual( new Vector2( 12, 34 ), widget.Position );
		Assert.AreEqual( new Vector2( 200, 50 ), widget.Size );

		Assert.AreEqual( new Rect( 0, 0, 200, 50 ), widget.LocalRect );

		//Hiding is the widget's own state; being visible also asks its parent.
		widget.Visible = false;

		Assert.IsFalse( widget.Visible );
		Assert.IsTrue( widget.Hidden );
	}

	//ToScreen walks parent positions; FromScreen undoes it.
	[TestMethod]
	public void FromScreenIsTheInverseOfToScreen()
	{
		var parent = new Widget( null );
		parent.Position = new Vector2( 10, 20 );

		var child = new Widget( parent );
		child.Position = new Vector2( 3, 4 );

		Assert.AreEqual( new Vector2( 13, 24 ), child.ToScreen( default ) );
		Assert.AreEqual( new Vector2( 18, 31 ), child.ToScreen( new Vector2( 5, 7 ) ) );
		Assert.AreEqual( new Vector2( 5, 7 ), child.FromScreen( child.ToScreen( new Vector2( 5, 7 ) ) ) );
	}

	[TestMethod]
	public void AChildIsHiddenWithItsParent()
	{
		var parent = new Widget( null );
		var child = new Widget( parent );

		Assert.AreEqual( parent, child.Parent );

		parent.Visible = false;

		Assert.IsFalse( child.Visible, "a child of a hidden widget is not on the screen either" );
	}

	//A widget paints itself, and what it painted is a picture.
	[TestMethod]
	public void AWidgetPaintsItselfIntoAPicture()
	{
		var widget = new Painted();
		widget.Size = new Vector2( 32, 16 );

		using var bitmap = new Bitmap( 32, 16 );
		bitmap.Clear( Color.Transparent );

		Assert.IsTrue( widget.PaintInto( bitmap, new Rect( 0, 0, 32, 16 ) ), "something should have been drawn" );
		Assert.AreEqual( 1, widget.Paints );

		var pixels = bitmap.GetPixels32();

		var middle = pixels[(8 * 32) + 16];

		Assert.AreEqual( 255, middle.r, 2, "the widget filled itself with red" );
		Assert.AreEqual( 0, middle.g, 2 );
		Assert.AreEqual( 255, middle.a, 2 );
	}

	//Tabs round only the top corners; margin sides map to corners easily transposed
	[TestMethod]
	public void ATabIsRoundedAlongTheTopOnly()
	{
		var widget = new Tab();
		widget.Size = new Vector2( 40, 20 );

		using var bitmap = new Bitmap( 40, 20 );
		bitmap.Clear( Color.Transparent );

		Assert.IsTrue( widget.PaintInto( bitmap, new Rect( 0, 0, 40, 20 ) ) );

		var pixels = bitmap.GetPixels32();

		byte At( int x, int y ) => pixels[(y * 40) + x].a;

		//A pixel inside each corner, far enough in to be cut away by a radius of this size and no further.
		Assert.AreEqual( 0, At( 0, 0 ), 8, "the top left corner is not cut away" );
		Assert.AreEqual( 0, At( 39, 0 ), 8, "the top right corner is not cut away" );

		Assert.AreEqual( 255, At( 0, 19 ), 8, "the bottom left corner is not square" );
		Assert.AreEqual( 255, At( 39, 19 ), 8, "the bottom right corner is not square" );

		Assert.AreEqual( 255, At( 20, 1 ), 8, "the tab is not filled" );
	}

	[TestMethod]
	public void ARowArrangesItsWidgetsAndSharesWhatIsLeft()
	{
		var window = new Widget( null );
		window.Size = new Vector2( 300, 40 );

		var row = Layout.Row();
		window.Layout = row;

		var fixedWidth = new Widget( null );
		fixedWidth.Size = new Vector2( 100, 40 );

		var stretched = new Widget( null );
		stretched.Size = new Vector2( 10, 40 );

		row.Spacing = 10;

		row.Add( fixedWidth );
		row.Add( stretched, 1 );

		Arrange( window );

		Assert.AreEqual( 0.0f, fixedWidth.Position.x, 0.5f, "the first widget starts at the left" );
		Assert.AreEqual( 100.0f, fixedWidth.Size.x, 0.5f, "and keeps the width it asked for" );

		Assert.AreEqual( 110.0f, stretched.Position.x, 0.5f, "the second starts after it and the gap" );
		Assert.AreEqual( 190.0f, stretched.Size.x, 0.5f, "and takes everything that was left" );

		Assert.AreEqual( 40.0f, stretched.Size.y, 0.5f, "both fill the row across" );
	}

	//A column does the same the other way, and margins are room nothing is put in.
	[TestMethod]
	public void AColumnLeavesItsMarginsEmpty()
	{
		var window = new Widget( null );
		window.Size = new Vector2( 200, 200 );

		var column = Layout.Column();
		window.Layout = column;

		column.Margin = 16;

		var top = new Widget( null );
		top.Size = new Vector2( 10, 10 );

		column.Add( top, 1 );

		Arrange( window );

		Assert.AreEqual( 16.0f, top.Position.x, 0.5f );
		Assert.AreEqual( 16.0f, top.Position.y, 0.5f );
		Assert.AreEqual( 168.0f, top.Size.x, 0.5f, "as wide as the column, less both margins" );
		Assert.AreEqual( 168.0f, top.Size.y, 0.5f, "and the only cell takes the height too" );
	}

	//An aligned layout keeps its widgets at their size instead of stretching cells to fill
	[TestMethod]
	public void AnAlignedLayoutDoesNotStretchWhatIsInIt()
	{
		var window = new Widget( null );
		window.Size = new Vector2( 300, 40 );

		var row = Layout.Row();
		window.Layout = row;
		row.Alignment = TextFlag.LeftCenter;

		var button = new Widget( null );
		button.Size = new Vector2( 60, 24 );

		row.Add( button );

		Arrange( window );

		Assert.AreEqual( 60.0f, button.Size.x, 0.5f, "an aligned row does not pull its controls out" );
		Assert.AreEqual( 0.0f, button.Position.x, 0.5f, "and keeps them at the end it was told" );
	}

	//A widget's content margins are room its layout does not use.
	[TestMethod]
	public void ContentMarginsAreRoomTheLayoutLeavesAlone()
	{
		var widget = new Widget( null );
		widget.Size = new Vector2( 100, 100 );
		widget.ContentMargins = 10;

		var column = Layout.Column();
		widget.Layout = column;

		var inside = new Widget( null );
		column.Add( inside, 1 );

		Arrange( widget );

		Assert.AreEqual( 10.0f, inside.Position.x, 0.5f );
		Assert.AreEqual( 10.0f, inside.Position.y, 0.5f );
		Assert.AreEqual( 80.0f, inside.Size.x, 0.5f, "the margins are taken off both sides" );
		Assert.AreEqual( 80.0f, inside.Size.y, 0.5f );
	}

	//Adding a widget deep inside must relayout every ancestor, not just its own parent
	[TestMethod]
	public void AddingSomethingDeepInsideRelaysOutEverythingAboveIt()
	{
		var window = new Widget( null );
		window.Size = new Vector2( 300, 40 );

		var row = Layout.Row();
		window.Layout = row;

		var holder = new Widget( null );
		holder.Layout = Layout.Row();

		//A size of its own, so that what it asks for later is plainly what came out of its contents rather than what it happened to be already.
		holder.Size = new Vector2( 10, 40 );

		row.Add( holder );

		var filler = new Widget( null );
		filler.Size = new Vector2( 100, 40 );

		row.Add( filler );

		//The leftover has somewhere to go, so what each widget gets is what it asked for and nothing else - which is the thing being measured here.
		row.AddStretchCell();

		Arrange( window );

		Assert.AreEqual( 10.0f, holder.Size.x, 0.5f, "nothing in it, so it keeps what it was" );

		//A frame later, the tool builds its own contents.
		var built = new Widget( null );
		built.FixedWidth = 120;

		holder.Layout.Add( built );

		Arrange( window );

		Assert.AreEqual( 120.0f, holder.Size.x, 0.5f,
			"the row above heard about it and gave it the room it now asks for" );
	}

	//The editor undoes a fixed size by setting it to this, which taken at face value is a widget
	//sixteen million pixels across with a minimum to match - and no layout can shrink it again.
	[TestMethod]
	public void TheLargestFixedSizeMeansNoFixedSize()
	{
		var widget = new Widget( null );
		widget.Size = new Vector2( 100, 30 );

		widget.FixedWidth = 60;

		Assert.AreEqual( 60.0f, widget.Size.x, 0.5f );
		Assert.AreEqual( 60.0f, widget.MinimumWidth, 0.5f );

		widget.FixedWidth = ( 1 << 24 ) - 1;

		Assert.AreEqual( 0.0f, widget.MinimumWidth, 0.5f, "the floor is gone" );
		Assert.IsTrue( widget.Size.x < 1000.0f, "and the widget was not made sixteen million pixels wide" );

		//Free again: a layout may now give it whatever it likes.
		var window = new Widget( null );
		window.Size = new Vector2( 200, 30 );

		var row = Layout.Row();
		window.Layout = row;

		row.Add( widget, 1 );

		Arrange( window );

		Assert.AreEqual( 200.0f, widget.Size.x, 0.5f, "it takes the room the row gives it" );
	}

	//Scene toolbar groups and fixed-width fields keep their size instead of stretching
	[TestMethod]
	public void TheSceneToolbarKeepsItsControlsTheSizeTheyAskedFor()
	{
		var bar = new Widget( null );
		bar.Size = new Vector2( 1200, 36 );

		var row = Layout.Row();
		bar.Layout = row;

		var left = row.AddRow( 1 );
		var centre = row.AddRow( 1 );
		var right = row.AddRow( 1 );

		left.Alignment = TextFlag.LeftCenter;
		centre.Alignment = TextFlag.Center;
		right.Alignment = TextFlag.RightCenter;

		//The tool dropdown, which says how wide it is.
		var mode = new Widget( null );
		mode.FixedWidth = 210;
		mode.FixedHeight = 28;
		left.Add( mode );

		//A group: a rounded box that is only as wide as what is in it.
		var group = new Widget( null );
		group.FixedHeight = 24;
		group.Layout = Layout.Row();
		group.Layout.Spacing = 2;

		var toggle = new Widget( null );
		toggle.FixedWidth = 24;
		toggle.FixedHeight = 24;
		group.Layout.Add( toggle );

		//The numeric field, fixed at fifty five like the snap steps are.
		var step = new Widget( null );
		step.FixedWidth = 55;
		step.Layout = Layout.Row();

		var edit = new LineEdit( step );
		edit.MinimumSize = 28;
		edit.MaximumSize = new Vector2( 4096, 24 );
		step.Layout.Add( edit );

		group.Layout.Add( step );

		left.Add( group );

		row.AddStretchCell();

		Arrange( bar );

		Assert.AreEqual( 210.0f, mode.Size.x, 0.5f, "the dropdown keeps its fixed width" );
		Assert.AreEqual( 55.0f, step.Size.x, 0.5f, "and the snap field keeps its fifty five" );

		Assert.AreEqual( 24.0f + 2.0f + 55.0f, group.Size.x, 1.0f,
			"the group is as wide as what is in it, not as wide as the third it sits in" );

		Assert.AreEqual( 0.0f, mode.Position.x, 0.5f, "and the run of them starts at the left" );
	}

	//Property grid gives leftover width to the stretch column, with a shared label column floor
	[TestMethod]
	public void APropertyGridGivesTheLeftoverToTheColumnThatAskedForIt()
	{
		var row = new Widget( null );
		row.Size = new Vector2( 400, 24 );

		var grid = Layout.Grid();
		row.Layout = grid;

		grid.HorizontalSpacing = 0;
		grid.VerticalSpacing = 0;

		var label = new Widget( null );
		label.MinimumWidth = 140;

		var control = new Widget( null );
		control.Size = new Vector2( 20, 20 );

		//The header is what declares the ten columns, six of which never hold anything.
		var header = new Widget( null );
		header.Size = new Vector2( 10, 4 );

		grid.AddCell( 0, 0, header, 10, 1 );
		grid.AddCell( 2, 1, label );
		grid.AddCell( 3, 1, control );

		grid.SetColumnStretch( 0, 0, 0, 1 );
		grid.SetMinimumColumnWidth( 2, 140 );

		Arrange( row );

		Assert.AreEqual( 140.0f, label.Size.x, 1.0f, "the label column keeps the floor it was given" );

		Assert.AreEqual( 140.0f, control.Position.x, 1.0f,
			"the control starts where the label column ends" );

		Assert.AreEqual( 260.0f, control.Size.x, 1.0f,
			"and takes everything left, rather than a tenth of it" );
	}

	//DoLayout still runs after arrange, so a field can park its own children.
	[TestMethod]
	public void DoLayoutPlacesChildrenAfterTheWidgetIsArranged()
	{
		var host = new Host();
		host.Size = new Vector2( 200, 24 );

		Arrange( host );

		Assert.AreEqual( 24.0f, host.Child.Position.x, 0.5f, "the child starts after the square" );
		Assert.AreEqual( 176.0f, host.Child.Size.x, 0.5f, "and fills the rest of the field" );
		Assert.AreEqual( 24.0f, host.Child.Size.y, 0.5f );
	}

	//A control that parks a child beside a square, the way a float field does.
	private sealed class Host : Widget
	{
		public readonly Widget Child;

		public Host() : base( null )
		{
			Child = new Widget( this );
		}

		protected override void DoLayout()
		{
			Child.Position = new Vector2( 24, 0 );
			Child.Size = new Vector2( Size.x - 24, Size.y );
		}
	}

	//A grid sizes child widgets by their content, not the default 100x30 of unarranged widgets
	[TestMethod]
	public void AGridMeasuresAWidgetByWhatIsInsideIt()
	{
		var sheet = new Widget( null );
		sheet.Size = new Vector2( 400, 200 );

		var outer = Layout.Grid();
		sheet.Layout = outer;

		outer.HorizontalSpacing = 0;
		outer.VerticalSpacing = 0;

		//A row of the sheet: a widget whose size is whatever its own layout works out, which is what every property row is.
		var row = new Widget( null );
		row.Layout = Layout.Row();

		var inside = new Widget( null );
		inside.FixedWidth = 250;
		inside.FixedHeight = 44;

		row.Layout.Add( inside );

		outer.AddCell( 0, 0, row );

		Arrange( sheet );

		Assert.AreEqual( 44.0f, inside.Size.y, 1.0f,
			"the row is as tall as what is in it, not the height a widget starts at" );

		Assert.AreEqual( 250.0f, inside.Size.x, 1.0f, "and what is in it keeps its width" );
	}

	//Text is measured before it is drawn, which is how every widget in the editor decides how much room it needs.
	[TestMethod]
	public void TextIsMeasuredOnTheCanvasItWillBeDrawnOn()
	{
		using var bitmap = new Bitmap( 128, 32 );
		var canvas = new PaintCanvas( bitmap );

		using ( Paint.Start( canvas ) )
		{
			Paint.SetDefaultFont( 12 );

			var narrow = Paint.MeasureText( "l" );
			var wide = Paint.MeasureText( "wwwwwwwwww" );

			Assert.IsTrue( narrow.x > 0, "a letter takes some room" );
			Assert.IsTrue( wide.x > narrow.x * 5, $"ten wide letters ({wide.x}) should be far wider than one thin one ({narrow.x})" );

			//And what does not fit is cut down until it does.
			var elided = Paint.GetElidedText( "a rather long piece of text", 40 );

			Assert.AreNotEqual( "a rather long piece of text", elided );
			Assert.IsTrue( Paint.MeasureText( elided ).x <= 40 );
		}
	}

	//A label is as wide as its words, and paints them.
	[TestMethod]
	public void ALabelIsAsBigAsWhatItSays()
	{
		var label = new Editor.Label( "Hello" );

		var narrow = label.Size;

		label.Text = "Hello, this is a much longer piece of text";

		Assert.IsTrue( label.Size.x > narrow.x * 2, $"a longer label is wider ({narrow.x} then {label.Size.x})" );
		Assert.IsTrue( label.Size.y > 0 );

		using var bitmap = new Bitmap( label.Size.x.CeilToInt(), label.Size.y.CeilToInt() );
		bitmap.Clear( Color.Transparent );

		Assert.IsTrue( label.PaintInto( bitmap, label.LocalRect ), "the label paints its words" );

		var ink = 0;
		foreach ( var pixel in bitmap.GetPixels32() ) if ( pixel.a > 8 ) ink++;

		Assert.IsTrue( ink > 20, $"only {ink} pixels have any letter in them" );
	}

	//A button answers a click, and a toggle keeps its state.
	[TestMethod]
	public void AButtonIsPressedAndClicked()
	{
		var clicks = 0;
		var presses = 0;

		var button = new Button( "Compile" ) { Clicked = () => clicks++, Pressed = () => presses++ };

		Assert.IsTrue( button.Size.x > 0 && button.Size.y > 0 );

		var at = new Vector2( 4, 4 );

		button.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );
		button.PanelMouseRelease( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.AreEqual( 1, presses );
		Assert.AreEqual( 1, clicks );

		//A toggle flips as it is clicked, and says so.
		button.IsToggle = true;

		button.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );
		button.PanelMouseRelease( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.IsTrue( button.IsChecked );
		Assert.AreEqual( 2, clicks );

		//And a disabled one says nothing at all.
		button.Enabled = false;

		button.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );
		button.PanelMouseRelease( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.AreEqual( 2, clicks );
	}

	//A row of real controls ends up with each one as wide as it needs.
	[TestMethod]
	public void ARowOfControlsGivesEachOneWhatItAsksFor()
	{
		var window = new Widget( null );
		window.Size = new Vector2( 400, 30 );

		var row = Layout.Row();
		window.Layout = row;

		var label = new Editor.Label( "Name" );
		var button = new Button( "Browse" );

		row.Add( label );
		row.Add( button );

		Arrange( window );

		Assert.IsTrue( label.Size.x > 0 );
		Assert.AreEqual( label.Size.x, button.Position.x, 1.0f, "the button starts where the label ends" );
		Assert.IsTrue( button.Size.x >= EditorText.Measure( "Browse" ).x, "the button is at least as wide as its word" );
	}

	//A check box ticks, unticks, and draws its box either way.
	[TestMethod]
	public void ACheckBoxTicksAndPaintsItsBox()
	{
		var changes = 0;

		var box = new Checkbox( "Cast shadows" );
		box.StateChanged = _ => changes++;

		Assert.IsFalse( box.Value );
		Assert.IsTrue( box.Size.x > 14, "there is room for the box and the words" );

		var at = new Vector2( 4, 4 );
		var click = new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None );

		box.PanelMousePress( click );
		box.PanelMouseRelease( click );

		Assert.IsTrue( box.Value );
		Assert.AreEqual( CheckState.On, box.State );
		Assert.AreEqual( 1, changes );

		box.PanelMousePress( click );
		box.PanelMouseRelease( click );

		Assert.IsFalse( box.Value );
		Assert.AreEqual( 2, changes );

		//And it draws something either way.
		using var bitmap = new Bitmap( box.Size.x.CeilToInt(), box.Size.y.CeilToInt() );
		bitmap.Clear( Color.Transparent );

		Assert.IsTrue( box.PaintInto( bitmap, box.LocalRect ) );

		var ink = 0;
		foreach ( var pixel in bitmap.GetPixels32() ) if ( pixel.a > 8 ) ink++;

		Assert.IsTrue( ink > 50, $"only {ink} pixels were drawn" );
	}

	//A text field takes letters, moves its caret, selects and deletes.
	[TestMethod]
	public void ALineEditTakesWhatIsTypedIntoIt()
	{
		var edited = 0;
		var returned = 0;

		var field = new LineEdit();
		field.TextEdited += _ => edited++;
		field.ReturnPressed += () => returned++;

		Type( field, "hello" );

		Assert.AreEqual( "hello", field.Text );
		Assert.AreEqual( 5, field.CursorPosition );
		Assert.AreEqual( 5, edited );

		//Backspace takes the last letter; left and home move the caret.
		Key( field, KeyCode.Backspace );
		Assert.AreEqual( "hell", field.Text );

		Key( field, KeyCode.Home );
		Assert.AreEqual( 0, field.CursorPosition );

		Type( field, "s" );
		Assert.AreEqual( "shell", field.Text );

		//Selecting everything and typing over it replaces the lot.
		field.SelectAll();
		Assert.AreEqual( "shell", field.SelectedText );

		Type( field, "x" );
		Assert.AreEqual( "x", field.Text );

		Key( field, KeyCode.Return );
		Assert.AreEqual( 1, returned );
	}

	//A field that is read only shows what it has and takes nothing.
	[TestMethod]
	public void AReadOnlyFieldTakesNothing()
	{
		var field = new LineEdit( "locked" ) { ReadOnly = true };

		Type( field, "nope" );
		Key( field, KeyCode.Backspace );

		Assert.AreEqual( "locked", field.Text );
	}

	//A field draws its box, its words and its caret.
	[TestMethod]
	public void ALineEditPaintsItself()
	{
		var field = new LineEdit( "hello" );
		field.Size = new Vector2( 120, 20 );

		using var bitmap = new Bitmap( 120, 20 );
		bitmap.Clear( Color.Transparent );

		Assert.IsTrue( field.PaintInto( bitmap, field.LocalRect ) );

		var ink = 0;
		foreach ( var pixel in bitmap.GetPixels32() ) if ( pixel.a > 8 ) ink++;

		Assert.IsTrue( ink > 100, $"only {ink} pixels were drawn" );
	}

	//Types a word into a field, a letter at a time.
	private static void Type( LineEdit field, string text )
	{
		foreach ( var letter in text )
		{
			field.PanelKeyPress( new KeyEvent( (KeyCode)char.ToUpperInvariant( letter ), letter.ToString(), KeyboardModifiers.None ) );
		}
	}

	//Presses a key that is not a letter.
	private static void Key( LineEdit field, KeyCode key, KeyboardModifiers modifiers = KeyboardModifiers.None )
	{
		field.PanelKeyPress( new KeyEvent( key, string.Empty, modifiers ) );
	}

	//A scroll area's canvas is sized by its layout's content, so it can be taller than the view
	[TestMethod]
	public void AScrollAreaHoldsSomethingBiggerThanItself()
	{
		var scroller = new ScrollArea( null );
		scroller.Size = new Vector2( 200, 100 );

		var canvas = new Widget( null );
		canvas.Layout = Layout.Column();

		var tall = new Widget( null );
		tall.FixedHeight = 400;
		tall.FixedWidth = 150;

		canvas.Layout.Add( tall );

		scroller.Canvas = canvas;

		Arrange( scroller );

		Assert.AreEqual( 400.0f, canvas.Size.y, 1.0f, "there is something to scroll to" );
		Assert.AreEqual( 200.0f, canvas.Size.x, 1.0f, "and no gap across, since it asks for less" );
	}

	//A splitter gives each side a share, and dragging moves the share across.
	[TestMethod]
	public void ASplitterSharesItsRoomAndIsDragged()
	{
		var splitter = new Splitter( null ) { IsHorizontal = true };
		splitter.Size = new Vector2( 400, 100 );

		var left = new Widget( null );
		var right = new Widget( null );

		splitter.AddWidget( left );
		splitter.AddWidget( right );

		Arrange( splitter );

		//Half each, less the handle between them.
		Assert.AreEqual( 198.0f, left.Size.x, 1.0f );
		Assert.AreEqual( 198.0f, right.Size.x, 1.0f );
		Assert.AreEqual( 202.0f, right.Position.x, 1.0f );
		Assert.AreEqual( 100.0f, left.Size.y, 0.5f, "both sides fill the splitter across" );

		//Dragging the handle to a quarter along moves the room from one side to the other and leaves the total alone.
		var at = new Vector2( 198, 50 );
		splitter.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		var to = new Vector2( 100, 50 );
		splitter.PanelMouseMove( new MouseEvent( to, to, to, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Arrange( splitter );

		Assert.AreEqual( 100.0f, left.Size.x, 2.0f, "the left side gave up its room" );
		Assert.AreEqual( 296.0f, right.Size.x, 2.0f, "and the right side took it" );
	}

	//A menu is rows, and choosing one does what the option says.
	[TestMethod]
	public void AMenuIsRowsAndChoosingOneDoesIt()
	{
		var chosen = 0;

		var menu = new Menu( "File" );

		var option = menu.AddOption( "Save", null, () => chosen++ );
		var toggle = menu.AddOption( "Wireframe" );
		toggle.Checkable = true;

		Assert.AreEqual( 2, menu.OptionCount );

		//Choosing runs the action; a checkable one flips as well.
		option.Choose();

		Assert.AreEqual( 1, chosen );

		toggle.Choose();

		Assert.IsTrue( toggle.Checked );

		toggle.Choose();

		Assert.IsFalse( toggle.Checked );

		//A disabled option answers to nothing.
		option.Enabled = false;
		option.Choose();

		Assert.AreEqual( 1, chosen );
	}

	//A searchable context menu puts its field above the buttons, as wide as they are.
	[TestMethod]
	public void ASearchableMenuPutsTheFieldAboveTheButtonsAndAsWide()
	{
		var menu = new ContextMenu { Searchable = true };

		menu.AddOption( "Cut" );
		menu.AddOption( "Duplicate Selection" );
		menu.AddOption( "Open in Mapping" );

		menu.OnAboutToShowInternal();
		menu.Build();

		var search = menu.Widgets.OfType<LineEdit>().Single();
		var row = menu.Rows.First();

		Assert.IsTrue( search.Position.y < row.Top,
			$"the search is under the buttons: search y {search.Position.y}, row y {row.Top}" );

		Assert.AreEqual( menu.Size.x, search.Size.x + search.Position.x * 2.0f, 0.5f,
			$"the search is {search.Size.x} wide in a menu {menu.Size.x} wide" );

		Assert.IsTrue( search.Size.x >= row.Wide - 16.0f,
			$"the search is narrower than the buttons: search {search.Size.x}, row {row.Wide}" );
	}

	//A menu bar keeps its menus, and each one knows what is in it.
	[TestMethod]
	public void AMenuBarHoldsItsMenus()
	{
		var bar = new MenuBar( null );

		var file = bar.AddMenu( "File" );
		file.AddOption( "New" );
		file.AddOption( "Open" );

		var view = bar.AddMenu( "View" );
		view.AddOption( "Wireframe" );

		Assert.AreEqual( file, bar.FindOrCreateMenu( "File" ), "asking again finds the same menu" );
		Assert.AreEqual( 2, file.OptionCount );
		Assert.AreEqual( 1, view.OptionCount );

		//The bar is a strip along the top, whatever is in it.
		Assert.IsTrue( bar.WantedSize().y > 0 );
	}

	//The title bar is a row across the window: icon, menus that take the leftover, extra buttons against the right edge.
	[TestMethod]
	public void TheTitleBarSpreadsAcrossTheWindow()
	{
		var host = new Widget( null );
		host.Size = new Vector2( 800, 34 );

		var bar = new TitleBar( host );
		bar.Size = new Vector2( 800, 34 );

		Arrange( bar );

		Assert.AreEqual( 800.0f, bar.Size.x, 0.5f, "the bar is as wide as the window" );

		Assert.IsTrue( bar.MenuBar.Size.x > 200.0f,
			$"the menu takes the room between the icon and the buttons, not a packed strip ({bar.MenuBar.Size.x})" );

		Assert.IsTrue( bar.TitleBarButtons.Position.x > 400.0f,
			$"the extra buttons sit on the right, not next to the menus ({bar.TitleBarButtons.Position.x})" );
	}

	//A drop-down keeps its list and what was chosen from it.
	[TestMethod]
	public void AComboBoxRemembersWhatWasChosen()
	{
		var chosen = 0;

		var combo = new ComboBox( null );

		combo.AddItem( "Vulkan", null, () => chosen++, selected: true );
		combo.AddItem( "Direct3D" );

		Assert.AreEqual( 2, combo.Count );
		Assert.AreEqual( "Vulkan", combo.CurrentText );
		Assert.AreEqual( 0, combo.CurrentIndex );

		Assert.IsTrue( combo.TrySelectNamed( "Direct3D" ) );
		Assert.AreEqual( "Direct3D", combo.CurrentText );

		Assert.IsFalse( combo.TrySelectNamed( "Metal" ), "a name that is not in the list is not chosen" );

		//Selecting a line runs whatever was added with it - once when it was picked out by name, and again when it is asked for on purpose.
		combo.TrySelectNamed( "Vulkan" );

		Assert.AreEqual( 1, chosen );

		combo.InvokeSelected();

		Assert.AreEqual( 2, chosen );
	}

	//A multi-line box takes lines, appends to them, and types into them.
	[TestMethod]
	public void ATextEditHoldsLines()
	{
		var edit = new TextEdit( null );
		edit.Size = new Vector2( 200, 100 );

		edit.PlainText = "one\ntwo";

		Assert.AreEqual( "one\ntwo", edit.PlainText );

		edit.AppendPlainText( "three" );

		Assert.AreEqual( "one\ntwo\nthree", edit.PlainText );

		//Typing goes in at the caret, which is at the end after setting text.
		edit.PanelKeyPress( new KeyEvent( KeyCode.End, string.Empty, KeyboardModifiers.None ) );
		edit.PanelKeyPress( new KeyEvent( KeyCode.Num1, "!", KeyboardModifiers.None ) );

		Assert.AreEqual( "one\ntwo\nthree!", edit.PlainText );

		//And markup is shown as the words it is made of.
		edit.Html = "<b>bold</b> and <i>italic</i>";

		Assert.AreEqual( "bold and italic", edit.PlainText );

		edit.Clear();

		Assert.AreEqual( string.Empty, edit.PlainText );
	}

	//Docks go where they were put, share an edge as tabs, and can be closed. This is the shape of every editor
	//window: something in the middle, docks around it, and two that share an edge showing one at a time.
	[TestMethod]
	public void DocksSitAroundTheMiddleAndShareEdgesAsTabs()
	{
		var docks = new DockManager( null );
		docks.Size = new Vector2( 800, 600 );

		var middle = docks.SetCentralWidget( new Widget( null ) );

		var left = docks.AddDock( "Scene", null, new Widget( null ), DockArea.Left );
		var bottom = docks.AddDock( "Console", null, new Widget( null ), DockArea.Bottom );
		var alsoBottom = docks.AddDock( "Output", null, new Widget( null ), DockArea.Bottom );

		Arrange( docks );

		Assert.IsNotNull( middle );
		Assert.AreEqual( 0.0f, left.Position.x, 0.5f, "the left dock starts at the left" );
		Assert.IsTrue( left.Size.x < docks.Size.x * 0.5f, "and takes a share rather than the lot" );

		Assert.IsTrue( bottom.Position.y > docks.Size.y * 0.5f, "the bottom docks are at the bottom" );

		//Two on one edge is a pair of tabs: the last one added is the one showing, and raising the other swaps them.
		Assert.IsFalse( bottom.Visible );
		Assert.IsTrue( alsoBottom.Visible );

		docks.RaiseDock( "Console" );

		Arrange( docks );

		Assert.IsTrue( bottom.Visible );
		Assert.IsFalse( alsoBottom.Visible );

		//Closing one leaves the other with the whole edge.
		Assert.IsTrue( docks.IsDockOpen( "Console" ) );

		docks.SetDockState( "Console", false );

		Arrange( docks );

		Assert.IsFalse( docks.IsDockOpen( "Console" ) );
		Assert.IsTrue( alsoBottom.Visible );

		//And what is where survives being written down and read back - what is showing, that is. A dock
		//the user closed is not written down, or it would be open again the next time the editor started.
		var state = docks.SaveStateManaged();

		Assert.IsTrue( state.Contains( "Scene" ) );
		Assert.IsTrue( state.Contains( "Output" ) );
		Assert.IsFalse( state.Contains( "Console" ), "a closed dock is not part of the layout" );

		//Read back into a window that has none of them: the layout says which docks there are, so they are made rather than looked for.
		var second = new DockManager( null );
		second.Size = new Vector2( 800, 600 );

		second.RegisterDockType( new DockManager.DockInfo { Title = "Scene", Area = DockArea.Left, CreateAction = () => new Widget( null ) } );
		second.RegisterDockType( new DockManager.DockInfo { Title = "Output", Area = DockArea.Bottom, CreateAction = () => new Widget( null ) } );

		Assert.IsTrue( second.RestoreState( state ) );

		Arrange( second );

		Assert.IsTrue( second.IsDockOpen( "Scene" ) );
		Assert.IsTrue( second.IsDockOpen( "Output" ) );
	}

	//A toolbar whose icons are missing files still paints.
	[TestMethod]
	public void AToolBarWithMissingPicturesStillPaints()
	{
		var bar = new ToolBar( null, "Tools" );
		bar.Size = new Vector2( 300, 30 );
		bar.AddOption( new Option( "New", "does-not-exist/new.png", () => { } ) );
		bar.AddOption( new Option( "Save", "does-not-exist/save.png", () => { } ) );
		Arrange( bar );

		using var bitmap = new Bitmap( 300, 30 );

		bar.PaintInto( bitmap, bar.LocalRect );
	}

	[TestMethod]
	public void AMissingPictureDoesNotThrowWhenDrawn()
	{
		using var bitmap = new Bitmap( 32, 32 );
		var canvas = new PaintCanvas( bitmap );

		using ( Paint.Start( canvas ) )
		{
			Paint.DrawIcon( new Rect( 0, 0, 24, 24 ), "does-not-exist/new.png", 24 );
			Paint.Draw( new Rect( 0, 0, 16, 16 ), "does-not-exist.png" );
		}
	}

	//A tool bar puts its options in a row and answers when one is clicked.
	[TestMethod]
	public void AToolBarAnswersItsButtons()
	{
		var clicked = 0;

		var bar = new ToolBar( null, "Tools" );
		bar.Size = new Vector2( 300, 30 );

		bar.AddOption( "Play", "play_arrow", () => clicked++ );
		bar.AddOption( "Stop", "stop" );

		Arrange( bar );

		//The first button is at the left-hand end of the bar.
		var at = new Vector2( 12, 14 );

		bar.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );
		bar.PanelMouseRelease( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.AreEqual( 1, clicked );
	}

	//An item that says when it was painted, moved or clicked.
	private class Node : GraphicsItem
	{
		public int Paints;
		public int Presses;

		public Node( GraphicsItem parent = null ) : base( parent )
		{
			Size = new Vector2( 100, 60 );
		}

		protected override void OnPaint()
		{
			Paints++;

			Paint.SetBrushAndPen( Color.Green );
			Paint.DrawRect( BoundingRect );
		}

		protected override void OnMousePressed( GraphicsMouseEvent e )
		{
			Presses++;
			LastScene = e.ScenePosition;
			LastLeft = e.LeftMouseButton;
			LastHasCtrl = e.HasCtrl;
		}

		public Vector2 LastScene;
		public bool LastLeft;
		public bool LastHasCtrl;
	}

	private static GraphicsView Graph()
	{
		var view = new GraphicsView( null );
		view.Size = new Vector2( 400, 300 );

		Arrange( view );

		return view;
	}

	//A view maps between its own corner and the scene, both ways round.
	[TestMethod]
	public void AGraphicsViewMapsToAndFromTheScene()
	{
		var view = Graph();

		//Nothing has moved it, so the middle of the view is the scene's origin.
		Assert.AreEqual( new Vector2( 0, 0 ), view.ToScene( new Vector2( 200, 150 ) ) );
		Assert.AreEqual( new Vector2( 200, 150 ), view.FromScene( new Vector2( 0, 0 ) ) );

		Assert.AreEqual( new Vector2( -200, -150 ), view.ToScene( new Vector2( 0, 0 ) ) );

		//And what it is showing is the whole of that.
		var showing = view.SceneRect;

		Assert.AreEqual( -200, showing.Left, 0.01f );
		Assert.AreEqual( -150, showing.Top, 0.01f );
		Assert.AreEqual( 400, showing.Width, 0.01f );
	}

	//Setting SceneRect looks at that part of the scene, which is what a timeline asks for. A graph must not do this with a world-sized rect.
	[TestMethod]
	public void SettingSceneRectFitsThatRectInTheView()
	{
		var view = Graph();

		view.SceneRect = new Rect( 0, 0, 200, 150 );

		Assert.AreEqual( 2.0f, view.Scale.x, 0.01f );
		Assert.AreEqual( new Vector2( 100, 75 ), view.Center );
	}

	//A click on an item carries the button and where it landed in the scene.
	[TestMethod]
	public void AGraphicsItemSeesTheButtonAndScenePosition()
	{
		var view = Graph();

		var node = new Node { Position = new Vector2( 0, 0 ), Selectable = true };
		view.Add( node );

		var at = new Vector2( 210, 160 );

		view.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.Ctrl ) );

		Assert.AreEqual( 1, node.Presses );
		Assert.IsTrue( node.LastLeft );
		Assert.IsTrue( node.LastHasCtrl );
		Assert.AreEqual( 10.0f, node.LastScene.x, 0.01f );
		Assert.AreEqual( 10.0f, node.LastScene.y, 0.01f );
	}

	//A child that takes the press is not a reason to drag the node behind it.
	[TestMethod]
	public void AnItemThatTakesThePressIsNotDraggedByItsParent()
	{
		var view = Graph();

		var node = new Node { Position = new Vector2( 0, 0 ), Selectable = true, Movable = true };
		view.Add( node );

		var plug = new Node( node )
		{
			Position = new Vector2( 90, 20 ),
			Size = new Vector2( 20, 20 )
		};

		var at = new Vector2( 295, 175 );

		view.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.AreEqual( 1, plug.Presses );
		Assert.IsTrue( node.Selected );

		var to = new Vector2( 345, 215 );

		view.PanelMouseMove( new MouseEvent( to, to, to, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );
		view.PanelMouseRelease( new MouseEvent( to, to, to, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.AreEqual( new Vector2( 0, 0 ), node.Position );
	}

	private class MenuHost : Widget
	{
		public int Menus;

		public MenuHost() : base( null ) { }

		protected override void OnContextMenu( ContextMenuEvent e )
		{
			Menus++;
			e.Accepted = true;
		}
	}

	//A right click on a widget is also a context menu, which is how a graph opens the node list.
	[TestMethod]
	public void ARightClickRaisesTheContextMenu()
	{
		var host = new MenuHost();
		host.Size = new Vector2( 80, 40 );

		var at = new Vector2( 12, 12 );

		host.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Right, MouseButtons.Right, KeyboardModifiers.None ) );
		host.PanelMouseRelease( new MouseEvent( at, at, at, MouseButtons.Right, MouseButtons.Right, KeyboardModifiers.None ) );

		Assert.AreEqual( 1, host.Menus );

		host.Destroy();
	}

	//Items are held by the view, found by where they are, and painted.
	[TestMethod]
	public void AGraphicsViewFindsAndPaintsItsItems()
	{
		var view = Graph();

		var one = new Node { Position = new Vector2( 0, 0 ) };
		var two = new Node { Position = new Vector2( 200, 0 ) };

		view.Add( one );
		view.Add( two );

		Assert.AreEqual( 2, view.Items.Count() );

		//A point inside the first item's box finds the first item.
		Assert.AreEqual( one, view.GetItemAt( new Vector2( 50, 30 ) ) );
		Assert.AreEqual( two, view.GetItemAt( new Vector2( 250, 30 ) ) );

		//And a gap between them finds nothing.
		Assert.IsNull( view.GetItemAt( new Vector2( 150, 30 ) ) );

		using var bitmap = new Bitmap( 400, 300 );

		view.PaintInto( bitmap, new Rect( 0, 0, 400, 300 ) );

		Assert.AreEqual( 1, one.Paints );
		Assert.AreEqual( 1, two.Paints );
	}

	//A child sits inside its parent, and is the one a click on it finds.
	[TestMethod]
	public void AChildItemIsFoundBeforeItsParent()
	{
		var view = Graph();

		var node = new Node { Position = new Vector2( 0, 0 ) };
		view.Add( node );

		//A plug on the edge of the node, which is how the graphs are built.
		var plug = new Node( node ) { Position = new Vector2( 90, 20 ), Size = new Vector2( 20, 20 ) };

		//The plug is placed against its parent, so the scene sees it at 90,20.
		Assert.AreEqual( new Vector2( 90, 20 ), plug.ToScene( new Vector2( 0, 0 ) ) );

		Assert.AreEqual( plug, view.GetItemAt( new Vector2( 95, 25 ) ) );
		Assert.AreEqual( node, view.GetItemAt( new Vector2( 50, 30 ) ) );
	}

	//Clicking an item selects it and tells it; clicking away clears it.
	[TestMethod]
	public void ClickingAnItemSelectsIt()
	{
		var view = Graph();

		var node = new Node { Position = new Vector2( 0, 0 ), Selectable = true, Movable = true };
		view.Add( node );

		var changed = 0;
		view.OnSelectionChanged += () => changed++;

		//The item's top left corner is the middle of the view.
		var at = new Vector2( 210, 160 );

		view.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.IsTrue( node.Selected );
		Assert.AreEqual( 1, node.Presses );
		Assert.AreEqual( node, view.SelectedItems.Single() );

		//Dragging it moves it by as much as the mouse moved.
		var to = new Vector2( 260, 200 );

		view.PanelMouseMove( new MouseEvent( to, to, to, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );
		view.PanelMouseRelease( new MouseEvent( to, to, to, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.AreEqual( new Vector2( 50, 40 ), node.Position );

		//And clicking the empty part of the scene lets go of it.
		var away = new Vector2( 380, 20 );

		view.PanelMousePress( new MouseEvent( away, away, away, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.IsFalse( node.Selected );
		Assert.AreEqual( 0, view.SelectedItems.Count() );
		Assert.IsTrue( changed >= 2 );
	}

	//The wheel zooms about the pointer: what was under it stays under it.
	[TestMethod]
	public void TheWheelZoomsAboutThePointer()
	{
		var view = Graph();

		var at = new Vector2( 300, 100 );
		var before = view.ToScene( at );

		var wheel = new WheelEvent( 1, at, KeyboardModifiers.None );
		view.PanelWheel( ref wheel );

		Assert.AreNotEqual( 1.0f, view.Scale.x );

		var after = view.ToScene( at );

		Assert.AreEqual( before.x, after.x, 0.01f );
		Assert.AreEqual( before.y, after.y, 0.01f );
	}

	//Destroying an item takes it and its children out of the view.
	[TestMethod]
	public void DestroyingAnItemRemovesItFromTheView()
	{
		var view = Graph();

		var node = new Node();
		view.Add( node );

		var plug = new Node( node );

		node.Destroy();

		Assert.IsFalse( node.IsValid );
		Assert.IsFalse( plug.IsValid );
		Assert.AreEqual( 0, view.Items.Count() );
		Assert.IsNull( view.GetItemAt( new Vector2( 10, 10 ) ) );
	}

	[TestMethod]
	public void ADisabledSegmentStaysOffAndDoesNotStealTheSelection()
	{
		var control = new SegmentedControl();
		control.AddOption( "Animation", "account_tree" );
		control.AddOption( "Model", "view_in_ar" );
		control.Size = new Vector2( 220, 24 );

		Arrange( control );

		var selected = "Animation";
		control.OnSelectedChanged = name => selected = name;

		control.SetOptionEnabled( "Animation", false, "no graph" );

		Assert.IsFalse( control.IsOptionEnabled( "Animation" ) );
		Assert.IsTrue( control.IsOptionEnabled( "Model" ) );

		var animation = control.Children.First( x => x.Name == "Animation" );
		Assert.IsFalse( animation.Enabled, "the Animation segment itself is disabled" );
		Assert.AreEqual( CursorShape.Forbidden, animation.Cursor );

		var at = new Vector2( 4, 4 );
		animation.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );
		animation.PanelMouseRelease( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.AreEqual( "Animation", selected, "a click on a disabled segment does not change the selection" );
		Assert.AreEqual( "Animation", control.Selected );

		control.Selected = "Model";
		Assert.AreEqual( "Model", control.Selected );

		control.Destroy();
	}

	[TestMethod]
	public void APressAndReleaseRaisesMouseClickOnce()
	{
		var widget = new Widget( null );
		widget.Size = new Vector2( 40, 24 );

		var clicks = 0;
		var presses = 0;
		widget.MouseClick = () => clicks++;
		widget.MouseLeftPress = () => presses++;

		var at = new Vector2( 8, 8 );
		widget.PanelMousePress( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );
		widget.PanelMouseRelease( new MouseEvent( at, at, at, MouseButtons.Left, MouseButtons.Left, KeyboardModifiers.None ) );

		Assert.AreEqual( 1, presses, "the press is one event" );
		Assert.AreEqual( 1, clicks, "and so is the click that follows it" );

		widget.Destroy();
	}

	[TestMethod]
	public void DestroyingAWidgetDestroysItsChildren()
	{
		var parent = new Widget( null );
		parent.Layout = Layout.Column();

		var child = new Widget( null );
		child.Layout = Layout.Column();
		parent.Layout.Add( child );

		var grandchild = new Widget( null );
		child.Layout.Add( grandchild );

		parent.Destroy();

		Assert.IsFalse( parent.IsValid );
		Assert.IsFalse( child.IsValid, "a child is destroyed with its parent" );
		Assert.IsFalse( grandchild.IsValid, "so is everything under it" );
	}

	[TestMethod]
	public void AddingARowAfterTheParentWasDestroyedDoesNotThrow()
	{
		var parent = new Widget( null );
		parent.Layout = Layout.Column();

		var body = new Widget( null );
		body.Layout = Layout.Column();
		parent.Layout.Add( body );

		parent.Destroy();

		var row = new Widget( null );
		body.Layout.Add( row );

		row.Destroy();
	}
}

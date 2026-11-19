using System;

namespace Editor.NodeEditor;

public static class MenuExtensions
{
	/// <summary>An editor menu path as a graph menu path.</summary>
	public static Rinten.UI.NodeGraph.GraphMenu.PathElement[] ToGraphPath( this Menu.PathElement[] path ) =>
		path.Select( x => new Rinten.UI.NodeGraph.GraphMenu.PathElement( x.Name, x.Icon, x.Description, x.Order, x.IsHeading ) ).ToArray();

	public static LineEdit AddLineEdit( this Menu menu, string label,
		string value = null, string placeholder = null, bool autoFocus = false,
		Action<string> onChange = null, Action<string> onSubmit = null )
	{
		var w = new Widget( menu );
		w.Layout = Layout.Row();
		w.Layout.Margin = 6;
		w.Layout.Spacing = 4;

		var lineEdit = new MenuLineEdit( w );

		lineEdit.PlaceholderText = placeholder ?? $"Enter {label}..";
		lineEdit.Text = value ?? "";

		if ( onChange is not null )
		{
			lineEdit.TextChanged += onChange;
		}

		if ( onSubmit is not null )
		{
			var firstTime = true;

			lineEdit.ReturnPressed += () =>
			{
				if ( !firstTime ) return;
				firstTime = false;

				onSubmit( lineEdit.Value );
				menu.RootMenu.Close();
			};
		}

		w.Layout.Add( new Label( $"{label}:", w ) );
		w.Layout.Add( lineEdit );

		menu.AddWidget( w );

		if ( autoFocus )
		{
			lineEdit.Focus();
		}

		return lineEdit;
	}
}

file class MenuLineEdit : LineEdit
{
	public MenuLineEdit( Widget parent ) : base( parent )
	{
	}

	//Stops the context menu from closing!!
	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );

		e.Accepted = true;
	}
}

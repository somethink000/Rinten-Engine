using NativeEngine;

namespace Rinten;

//Button tables from SDL scancodes/keycodes and button names, which must match bind strings
internal static partial class CoreInput
{
	//SDL's keycodes have this bit set where the key is a named one rather than a character - the rest of the number is then its scancode.
	private const uint ScancodeMask = 1u << 30;

	//SDL's scancodes, as far as we name them. They are the USB HID usage numbers, so
	//this table is the keyboard as hardware sees it, before any layout is applied.
	private static readonly ButtonCode[] scancodes = BuildScancodes();

	private static ButtonCode[] BuildScancodes()
	{
		var map = new ButtonCode[512];

		//Letters, in alphabetical order from 4.
		for ( var i = 0; i < 26; i++ )
			map[4 + i] = ButtonCode.KEY_A + i;

		//Digits, 1 through 9 and then 0 - the order they are on the row.
		for ( var i = 0; i < 9; i++ )
			map[30 + i] = ButtonCode.KEY_1 + i;

		map[39] = ButtonCode.KEY_0;

		map[40] = ButtonCode.KEY_ENTER;
		map[41] = ButtonCode.KEY_ESCAPE;
		map[42] = ButtonCode.KEY_BACKSPACE;
		map[43] = ButtonCode.KEY_TAB;
		map[44] = ButtonCode.KEY_SPACE;
		map[45] = ButtonCode.KEY_MINUS;
		map[46] = ButtonCode.KEY_EQUAL;
		map[47] = ButtonCode.KEY_LBRACKET;
		map[48] = ButtonCode.KEY_RBRACKET;
		map[49] = ButtonCode.KEY_BACKSLASH;

		//The extra key on a non-US keyboard where the US one has none.
		map[50] = ButtonCode.KEY_BACKSLASH;

		map[51] = ButtonCode.KEY_SEMICOLON;
		map[52] = ButtonCode.KEY_APOSTROPHE;
		map[53] = ButtonCode.KEY_BACKQUOTE;
		map[54] = ButtonCode.KEY_COMMA;
		map[55] = ButtonCode.KEY_PERIOD;
		map[56] = ButtonCode.KEY_SLASH;
		map[57] = ButtonCode.KEY_CAPSLOCK;

		for ( var i = 0; i < 12; i++ )
			map[58 + i] = ButtonCode.KEY_F1 + i;

		map[70] = ButtonCode.KEY_PRINTSCREEN;
		map[71] = ButtonCode.KEY_SCROLLLOCK;
		map[72] = ButtonCode.KEY_BREAK;
		map[73] = ButtonCode.KEY_INSERT;
		map[74] = ButtonCode.KEY_HOME;
		map[75] = ButtonCode.KEY_PAGEUP;
		map[76] = ButtonCode.KEY_DELETE;
		map[77] = ButtonCode.KEY_END;
		map[78] = ButtonCode.KEY_PAGEDOWN;
		map[79] = ButtonCode.KEY_RIGHT;
		map[80] = ButtonCode.KEY_LEFT;
		map[81] = ButtonCode.KEY_DOWN;
		map[82] = ButtonCode.KEY_UP;
		map[83] = ButtonCode.KEY_NUMLOCK;

		map[84] = ButtonCode.KEY_PAD_DIVIDE;
		map[85] = ButtonCode.KEY_PAD_MULTIPLY;
		map[86] = ButtonCode.KEY_PAD_MINUS;
		map[87] = ButtonCode.KEY_PAD_PLUS;
		map[88] = ButtonCode.KEY_PAD_ENTER;

		//The keypad digits run 1 to 9 and then 0, like the row above the letters rather than like the pad looks.
		for ( var i = 0; i < 9; i++ )
			map[89 + i] = ButtonCode.KEY_PAD_1 + i;

		map[98] = ButtonCode.KEY_PAD_0;
		map[99] = ButtonCode.KEY_PAD_DECIMAL;

		//The key between left shift and Z on a non-US keyboard.
		map[100] = ButtonCode.KEY_LESS;

		map[101] = ButtonCode.KEY_APP;
		map[103] = ButtonCode.KEY_KP_EQUALS;

		for ( var i = 0; i < 12; i++ )
			map[104 + i] = ButtonCode.KEY_F13 + i;

		map[224] = ButtonCode.KEY_LCONTROL;
		map[225] = ButtonCode.KEY_LSHIFT;
		map[226] = ButtonCode.KEY_LALT;
		map[227] = ButtonCode.KEY_LWIN;
		map[228] = ButtonCode.KEY_RCONTROL;
		map[229] = ButtonCode.KEY_RSHIFT;
		map[230] = ButtonCode.KEY_RALT;
		map[231] = ButtonCode.KEY_RWIN;

		return map;
	}

	//The key the window system says was pressed, by where it is on the keyboard.
	internal static ButtonCode FromScancode( uint scancode )
	{
		if ( scancode >= (uint)scancodes.Length ) return ButtonCode.BUTTON_CODE_INVALID;

		var code = scancodes[scancode];
		return code == 0 ? ButtonCode.BUTTON_CODE_INVALID : code;
	}

	//The key the window system says was pressed, by what it types under the layout
	//in use - so the same physical key is a different one on a French keyboard.
	internal static ButtonCode FromKeycode( uint keycode )
	{
		//A named key: everything below the mask is its scancode.
		if ( (keycode & ScancodeMask) != 0 )
			return FromScancode( keycode & ~ScancodeMask );

		return FromCharacter( keycode );
	}

	//The button for a character the keyboard types, where there is one.
	private static ButtonCode FromCharacter( uint character )
	{
		if ( character >= 'a' && character <= 'z' ) return ButtonCode.KEY_A + (int)(character - 'a');
		if ( character >= 'A' && character <= 'Z' ) return ButtonCode.KEY_A + (int)(character - 'A');
		if ( character >= '1' && character <= '9' ) return ButtonCode.KEY_1 + (int)(character - '1');

		return character switch
		{
			'0' => ButtonCode.KEY_0,
			' ' => ButtonCode.KEY_SPACE,
			'-' => ButtonCode.KEY_MINUS,
			'=' => ButtonCode.KEY_EQUAL,
			'[' => ButtonCode.KEY_LBRACKET,
			']' => ButtonCode.KEY_RBRACKET,
			'\\' => ButtonCode.KEY_BACKSLASH,
			';' => ButtonCode.KEY_SEMICOLON,
			'\'' => ButtonCode.KEY_APOSTROPHE,
			'`' => ButtonCode.KEY_BACKQUOTE,
			',' => ButtonCode.KEY_COMMA,
			'.' => ButtonCode.KEY_PERIOD,
			'/' => ButtonCode.KEY_SLASH,
			'<' => ButtonCode.KEY_LESS,
			'>' => ButtonCode.KEY_GREATER,
			'~' => ButtonCode.KEY_TILDE,
			'!' => ButtonCode.KEY_EXCLAIM,
			'"' => ButtonCode.KEY_QUOTEDBL,
			'#' => ButtonCode.KEY_HASH,
			'$' => ButtonCode.KEY_DOLLAR,
			'%' => ButtonCode.KEY_PERCENT,
			'&' => ButtonCode.KEY_AMPERSAND,
			'(' => ButtonCode.KEY_LEFTPAREN,
			')' => ButtonCode.KEY_RIGHTPAREN,
			'*' => ButtonCode.KEY_ASTERISK,
			'+' => ButtonCode.KEY_PLUS,
			':' => ButtonCode.KEY_COLON,
			'?' => ButtonCode.KEY_QUESTION,
			'@' => ButtonCode.KEY_AT,
			'^' => ButtonCode.KEY_CARET,
			'_' => ButtonCode.KEY_UNDERSCORE,
			'{' => ButtonCode.KEY_LEFT_CURLY_BRACKET,
			'|' => ButtonCode.KEY_VERTICAL_BAR,
			'}' => ButtonCode.KEY_RIGHT_CURLY_BRACKET,
			'\r' => ButtonCode.KEY_ENTER,
			'\t' => ButtonCode.KEY_TAB,
			'\b' => ButtonCode.KEY_BACKSPACE,
			0x1B => ButtonCode.KEY_ESCAPE,
			0x7F => ButtonCode.KEY_DELETE,
			_ => ButtonCode.BUTTON_CODE_INVALID,
		};
	}

	//The mouse button SDL numbers from one. Anything past the fifth is a button we have no name for.
	internal static ButtonCode FromMouseButton( byte button ) => button switch
	{
		1 => ButtonCode.MouseLeft,
		2 => ButtonCode.MouseMiddle,
		3 => ButtonCode.MouseRight,
		4 => ButtonCode.MouseBack,
		5 => ButtonCode.MouseForward,
		_ => ButtonCode.BUTTON_CODE_INVALID,
	};

	//Button names as binds spell them; left and right shift/ctrl/alt share a name so either matches
	private static readonly Dictionary<ButtonCode, string> overrides = new()
	{
		{ ButtonCode.KEY_LSHIFT, "SHIFT" },
		{ ButtonCode.KEY_RSHIFT, "SHIFT" },
		{ ButtonCode.KEY_LCONTROL, "CTRL" },
		{ ButtonCode.KEY_RCONTROL, "CTRL" },
		{ ButtonCode.KEY_LALT, "ALT" },
		{ ButtonCode.KEY_RALT, "ALT" },
		{ ButtonCode.KEY_LWIN, "WIN" },
		{ ButtonCode.KEY_RWIN, "WIN" },

		{ ButtonCode.MouseLeft, "MOUSE1" },
		{ ButtonCode.MouseRight, "MOUSE2" },
		{ ButtonCode.MouseMiddle, "MOUSE3" },
		{ ButtonCode.MouseBack, "MOUSE4" },
		{ ButtonCode.MouseForward, "MOUSE5" },
		{ ButtonCode.MouseWheelUp, "MWHEELUP" },
		{ ButtonCode.MouseWheelDown, "MWHEELDOWN" },
	};

	//Names that mean a button but are not the name it is given back as - what somebody
	//might reasonably write in a bind, or what an older config on disk was written with.
	private static readonly Dictionary<string, ButtonCode> aliases = new( StringComparer.OrdinalIgnoreCase )
	{
		{ "lshift", ButtonCode.KEY_LSHIFT },
		{ "rshift", ButtonCode.KEY_RSHIFT },
		{ "lctrl", ButtonCode.KEY_LCONTROL },
		{ "rctrl", ButtonCode.KEY_RCONTROL },
		{ "control", ButtonCode.KEY_LCONTROL },
		{ "lalt", ButtonCode.KEY_LALT },
		{ "ralt", ButtonCode.KEY_RALT },
		{ "return", ButtonCode.KEY_ENTER },
		{ "esc", ButtonCode.KEY_ESCAPE },
		{ "del", ButtonCode.KEY_DELETE },
		{ "pgup", ButtonCode.KEY_PAGEUP },
		{ "pgdn", ButtonCode.KEY_PAGEDOWN },
		{ "ins", ButtonCode.KEY_INSERT },
		{ "capslock", ButtonCode.KEY_CAPSLOCK },

		//The keypad and the punctuation as the editor's shortcuts spell them. A bind is matched by name, and
		//a name nothing answers to is a shortcut that can never fire - which is what every one of these was.
		{ "kp_0", ButtonCode.KEY_PAD_0 },
		{ "kp_1", ButtonCode.KEY_PAD_1 },
		{ "kp_2", ButtonCode.KEY_PAD_2 },
		{ "kp_3", ButtonCode.KEY_PAD_3 },
		{ "kp_4", ButtonCode.KEY_PAD_4 },
		{ "kp_5", ButtonCode.KEY_PAD_5 },
		{ "kp_6", ButtonCode.KEY_PAD_6 },
		{ "kp_7", ButtonCode.KEY_PAD_7 },
		{ "kp_8", ButtonCode.KEY_PAD_8 },
		{ "kp_9", ButtonCode.KEY_PAD_9 },
		{ "kp_add", ButtonCode.KEY_PAD_PLUS },
		{ "kp_plus", ButtonCode.KEY_PAD_PLUS },
		{ "kp_minus", ButtonCode.KEY_PAD_MINUS },
		{ "kp_multiply", ButtonCode.KEY_PAD_MULTIPLY },
		{ "kp_divide", ButtonCode.KEY_PAD_DIVIDE },
		{ "kp_decimal", ButtonCode.KEY_PAD_DECIMAL },
		{ "kp_enter", ButtonCode.KEY_PAD_ENTER },

		{ "[", ButtonCode.KEY_LBRACKET },
		{ "]", ButtonCode.KEY_RBRACKET },
		{ ";", ButtonCode.KEY_SEMICOLON },
		{ "'", ButtonCode.KEY_APOSTROPHE },
		{ "`", ButtonCode.KEY_BACKQUOTE },
		{ ",", ButtonCode.KEY_COMMA },
		{ ".", ButtonCode.KEY_PERIOD },
		{ "/", ButtonCode.KEY_SLASH },
		{ "\\", ButtonCode.KEY_BACKSLASH },
		{ "-", ButtonCode.KEY_MINUS },
		{ "=", ButtonCode.KEY_EQUAL },
		{ "mouseleft", ButtonCode.MouseLeft },
		{ "mouseright", ButtonCode.MouseRight },
		{ "mousemiddle", ButtonCode.MouseMiddle },
	};

	private static Dictionary<ButtonCode, string> names;
	private static Dictionary<string, ButtonCode> buttons;

	private static void BuildNames()
	{
		if ( names is not null ) return;

		var byCode = new Dictionary<ButtonCode, string>();
		var byName = new Dictionary<string, ButtonCode>( StringComparer.OrdinalIgnoreCase );

		foreach ( ButtonCode code in Enum.GetValues<ButtonCode>() )
		{
			if ( code <= ButtonCode.BUTTON_CODE_NONE ) continue;
			if ( byCode.ContainsKey( code ) ) continue;

			var name = overrides.TryGetValue( code, out var over ) ? over : Derive( code );
			if ( string.IsNullOrEmpty( name ) ) continue;

			byCode[code] = name;

			//First one wins: where two buttons share a name - the two shifts - asking for the
			//name back gives the left one, which is the one a keyboard has when it only has one.
			if ( !byName.ContainsKey( name ) ) byName[name] = code;
		}

		foreach ( var (alias, code) in aliases )
			byName.TryAdd( alias, code );

		buttons = byName;
		names = byCode;
	}

	//The name of an enumerator, without the prefix it shares.
	private static string Derive( ButtonCode code )
	{
		var name = Enum.GetName( code );
		if ( string.IsNullOrEmpty( name ) ) return null;

		//The names that are not keys at all - the range markers - are not buttons anybody binds.
		if ( name.Contains( "_FIRST" ) || name.Contains( "_LAST" ) || name.Contains( "_COUNT" ) )
			return null;

		if ( name.StartsWith( "KEY_", StringComparison.Ordinal ) ) name = name[4..];

		return name;
	}

	//What to call this button, as a bind spells it.
	internal static string CodeToString( ButtonCode code )
	{
		BuildNames();

		return names.TryGetValue( code, out var name ) ? name : string.Empty;
	}

	//The button a name means, or invalid where it means none.
	internal static ButtonCode StringToButtonCode( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return ButtonCode.BUTTON_CODE_INVALID;

		BuildNames();

		return buttons.TryGetValue( name.Trim(), out var code ) ? code : ButtonCode.BUTTON_CODE_INVALID;
	}

	//What to call this button where a person is going to read it.
	internal static string GetKeyDisplayName( ButtonCode code )
	{
		var name = CodeToString( code );
		if ( string.IsNullOrEmpty( name ) ) return string.Empty;

		//A single letter or digit is already how it is written on the key.
		if ( name.Length == 1 ) return name;

		return string.Create( name.Length, name, static ( span, source ) =>
		{
			for ( var i = 0; i < source.Length; i++ )
				span[i] = i == 0 ? char.ToUpperInvariant( source[i] ) : char.ToLowerInvariant( source[i] );
		} );
	}

	//Layout-independent Windows virtual key for a button on every platform, or 0 if none
	internal static int ButtonCodeToVirtualKey( ButtonCode code ) => ButtonCodes.ToVirtualKey( code );

}

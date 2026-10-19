using System;
using System.Collections.Generic;
using Godot;

/// <summary>Validated Windows shortcut settings and Godot key capture conversion.</summary>
public static class MenuShortcut
{
	public const int Alt = 1, Control = 2, Shift = 4, Win = 8;
	public const int DefaultVirtualKey = 0x44, DefaultModifiers = Control | Alt;

	public static bool IsValid(int virtualKey, int modifiers) =>
		(modifiers & ~15) == 0 && (modifiers & (Alt | Control | Shift)) != 0
		&& KeyName(virtualKey) != null && virtualKey != 0x7B // F12 is reserved by Windows debugging.
		&& !(virtualKey == 0xC0 && modifiers == Alt); // Existing click-through shortcut.

	public static string Format(int virtualKey, int modifiers)
	{
		var parts = new List<string>();
		if ((modifiers & Control) != 0) parts.Add("Ctrl");
		if ((modifiers & Alt) != 0) parts.Add("Alt");
		if ((modifiers & Shift) != 0) parts.Add("Shift");
		if ((modifiers & Win) != 0) parts.Add("Win");
		parts.Add(KeyName(virtualKey) ?? "Unknown key");
		return string.Join("+", parts);
	}

	public static bool TryFromGodotKey(InputEventKey input, out int virtualKey, out int modifiers)
	{
		modifiers = (input.AltPressed ? Alt : 0) | (input.CtrlPressed ? Control : 0)
			| (input.ShiftPressed ? Shift : 0) | (input.MetaPressed ? Win : 0);
		var key = input.Keycode == Key.None ? input.PhysicalKeycode : input.Keycode;
		var value = (int)key;
		virtualKey = value is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? value
			: key >= Key.F1 && key <= Key.F24 ? 0x70 + value - (int)Key.F1
			: key >= Key.Kp0 && key <= Key.Kp9 ? 0x60 + value - (int)Key.Kp0
			: key switch
			{
				Key.Backspace => 0x08, Key.Tab => 0x09, Key.Enter or Key.KpEnter => 0x0D,
				Key.Space => 0x20, Key.Pageup => 0x21, Key.Pagedown => 0x22,
				Key.End => 0x23, Key.Home => 0x24, Key.Left => 0x25, Key.Up => 0x26,
				Key.Right => 0x27, Key.Down => 0x28, Key.Insert => 0x2D, Key.Delete => 0x2E,
				Key.KpMultiply => 0x6A, Key.KpAdd => 0x6B, Key.KpSubtract => 0x6D,
				Key.KpPeriod => 0x6E, Key.KpDivide => 0x6F,
				_ => value switch
				{
					';' or ':' => 0xBA, '=' or '+' => 0xBB, ',' or '<' => 0xBC,
					'-' or '_' => 0xBD, '.' or '>' => 0xBE, '/' or '?' => 0xBF,
					'`' or '~' => 0xC0, '[' or '{' => 0xDB, '\\' or '|' => 0xDC,
					']' or '}' => 0xDD, '\'' or '"' => 0xDE, _ => 0,
				},
			};
		return input.Pressed && !input.Echo && IsValid(virtualKey, modifiers);
	}

	private static string? KeyName(int key) => key switch
	{
		>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
		>= 0x70 and <= 0x87 => "F" + (key - 0x6F),
		>= 0x60 and <= 0x69 => "Numpad " + (key - 0x60),
		0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x20 => "Space",
		0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
		0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
		0x2D => "Insert", 0x2E => "Delete", 0x6A => "Numpad *", 0x6B => "Numpad +",
		0x6D => "Numpad -", 0x6E => "Numpad .", 0x6F => "Numpad /",
		0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/",
		0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'", _ => null,
	};
}

using System.Collections.Generic;
using Godot;

// Font overrides are local to a control. Keeping the theme resources untouched lets new
// cards resolve their authored size, and lets 100% restore the exact original appearance.
public partial class MenuFontScale : Node
{
	private const string HelperName = "MenuFontScale";
	private sealed record FontSize(Node Target, StringName Name, int Original, bool HadOverride);
	private readonly Dictionary<ulong, List<FontSize>> _originalSizes = new();
	private Window _window = null!;
	private float _scale = 1;
	private bool _refreshQueued;

	public static MenuFontScale Attach(Window window)
	{
		var helper = new MenuFontScale { Name = HelperName, _window = window };
		window.AddChild(helper);
		return helper;
	}

	public override void _Ready() => GetTree().NodeAdded += OnNodeAdded;
	public override void _ExitTree() => GetTree().NodeAdded -= OnNodeAdded;

	public void Apply(float scale)
	{
		if (Mathf.IsEqualApprox(_scale, scale)) return;
		_scale = scale;
		Refresh();
	}

	private void OnNodeAdded(Node node)
	{
		if (_refreshQueued || Mathf.IsEqualApprox(_scale, 1) || !_window.IsAncestorOf(node)) return;
		_refreshQueued = true;
		// Dynamic cards finish configuring their authored appearance before font sizes are read.
		CallDeferred(nameof(Refresh));
	}

	private void Refresh()
	{
		_refreshQueued = false;
		foreach (var id in new List<ulong>(_originalSizes.Keys))
			if (!GodotObject.IsInstanceValid(_originalSizes[id][0].Target)) _originalSizes.Remove(id);
		Visit(_window);
	}

	private void Visit(Node node)
	{
		// Separate menu windows manage their own fonts. Built-in popups are included here.
		if (node is Window nested && nested != _window && nested.GetNodeOrNull<MenuFontScale>(HelperName) != null) return;
		if (node is BaseButton or Label or LineEdit or TextEdit or ItemList or Tree or TabBar)
			ScaleFonts(node, new[] { "font_size" });
		else if (node is RichTextLabel)
			ScaleFonts(node, new[] { "normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size" });
		else if (node is PopupMenu)
			ScaleFonts(node, new[] { "font_size", "font_separator_size" });
		foreach (var child in node.GetChildren(true)) Visit(child);
	}

	private void ScaleFonts(Node node, string[] names)
	{
		var id = node.GetInstanceId();
		if (!_originalSizes.TryGetValue(id, out var sizes))
		{
			if (Mathf.IsEqualApprox(_scale, 1)) return;
			sizes = new List<FontSize>();
			foreach (var name in names)
			{
				var original = node is Control control ? control.GetThemeFontSize(name) : ((Window)node).GetThemeFontSize(name);
				var overridden = node is Control target ? target.HasThemeFontSizeOverride(name) : ((Window)node).HasThemeFontSizeOverride(name);
				sizes.Add(new FontSize(node, name, original, overridden));
			}
			_originalSizes.Add(id, sizes);
		}
		foreach (var entry in sizes)
		{
			var restoreInherited = Mathf.IsEqualApprox(_scale, 1) && !entry.HadOverride;
			var fontSize = Mathf.Max(1, Mathf.RoundToInt(entry.Original * _scale));
			if (node is Control control)
			{
				if (restoreInherited) control.RemoveThemeFontSizeOverride(entry.Name);
				else if (!control.HasThemeFontSizeOverride(entry.Name) || control.GetThemeFontSize(entry.Name) != fontSize)
					control.AddThemeFontSizeOverride(entry.Name, fontSize);
			}
			else if (node is Window window)
			{
				if (restoreInherited) window.RemoveThemeFontSizeOverride(entry.Name);
				else if (!window.HasThemeFontSizeOverride(entry.Name) || window.GetThemeFontSize(entry.Name) != fontSize)
					window.AddThemeFontSizeOverride(entry.Name, fontSize);
			}
		}
	}
}

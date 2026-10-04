using System;
using Godot;

/// <summary>Authored controls added below Godot's built-in color wheel.</summary>
[Tool]
public partial class AccessoryColorControls : VBoxContainer
{
	public Button AdvancedButton => GetNode<Button>("%AdvancedColor");
	public event Action<Color>? ColorSelected;
	public event Action<bool>? AdvancedToggled;
	public event Action? ResetRequested;
	public event Action? DoneRequested;

	public override void _Ready()
	{
		if (Engine.IsEditorHint()) return;
		// Swatch colors come from the editable StyleBox resources on each button.
		foreach (var child in GetNode<HBoxContainer>("%QuickColors").GetChildren())
		{
			if (child is not Button swatch) continue;
			swatch.Pressed += () =>
			{
				if (swatch.GetThemeStylebox("normal") is StyleBoxFlat skin) ColorSelected?.Invoke(skin.BgColor);
			};
		}
		AdvancedButton.Toggled += advanced => AdvancedToggled?.Invoke(advanced);
		GetNode<Button>("%PopupResetColor").Pressed += () => ResetRequested?.Invoke();
		GetNode<Button>("%DoneColor").Pressed += () => DoneRequested?.Invoke();
	}
}

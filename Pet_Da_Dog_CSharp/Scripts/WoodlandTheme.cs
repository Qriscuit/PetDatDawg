using Godot;

// Compatibility helpers load authored resources; the editor owns presentation.
public static class WoodlandTheme
{
	public const string ThemePath = "res://UI/Theme/DefaultTheme.tres";
	public static Theme Shared => ResourceLoader.Load<Theme>(ThemePath);
	public static Color Text => Shared.GetColor("text", "PetPalette");
	public static Color Muted => Shared.GetColor("muted", "PetPalette");
	public static Color Accent => Shared.GetColor("accent", "PetPalette");
	public static Color Border => Shared.GetColor("border", "PetPalette");
	public static Color Parchment => Shared.GetColor("parchment", "PetPalette");
	public static Color Moss => Shared.GetColor("moss", "PetPalette");
	public static Theme Build(float scale = 1)
	{
		if (Mathf.IsEqualApprox(scale, 1)) return Shared;
		var theme = (Theme)Shared.Duplicate(true);
		theme.DefaultBaseScale *= scale;
		theme.DefaultFontSize = Mathf.RoundToInt(theme.DefaultFontSize * scale);
		foreach (var type in theme.GetTypeList())
			foreach (var name in theme.GetFontSizeList(type))
				theme.SetFontSize(name, type, Mathf.RoundToInt(theme.GetFontSize(name, type) * scale));
		return theme;
	}
	public static void StyleButton(Button button, string variation = "Button") => button.ThemeTypeVariation = variation;
	public static Texture2D? Icon(string name) => Shared.HasIcon(name.Replace('-', '_'), "PetIcons")
		? Shared.GetIcon(name.Replace('-', '_'), "PetIcons") : null;
	public static StyleBox WindowStyle() => ResourceLoader.Load<StyleBox>("res://UI/Theme/Window.tres");
	public static StyleBox PanelStyle(bool woodFrame = false) => ResourceLoader.Load<StyleBox>(woodFrame
		? "res://UI/Theme/Panel.tres" : "res://UI/Theme/PlainPanel.tres");
	public static StyleBox CatalogStyle() => ResourceLoader.Load<StyleBox>("res://UI/Theme/Catalog.tres");
	public static void ApplyPopup(Window popup)
	{
		// Built-in picker buttons have engine-owned inline gray backgrounds.
		// Inherit the authored theme in their place, without overwriting the theme.
		if (popup.Theme == null)
		{
			for (var ancestor = popup.GetParent(); ancestor != null; ancestor = ancestor.GetParent())
			{
				var authored = ancestor is Control control ? control.Theme : ancestor is Window window ? window.Theme : null;
				if (authored == null) continue;
				popup.Theme = authored; break;
			}
			popup.Theme ??= Shared;
		}
		void Visit(Node node)
		{
			foreach (var child in node.GetChildren(true))
			{
				if (child is Button { Owner: null } button && button.Text is "RGB" or "HSV" or "Linear" or "Raw")
				{
					button.ThemeTypeVariation = "ColorPickerModeButton";
					foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
						button.RemoveThemeStyleboxOverride(state);
				}
				Visit(child);
			}
		}
		Visit(popup);
	}
}

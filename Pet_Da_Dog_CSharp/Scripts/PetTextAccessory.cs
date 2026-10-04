using Godot;

/// <summary>Code-drawn text artwork shared by the wardrobe preview and desktop pet.</summary>
public partial class PetTextAccessory : Node2D
{
	public static readonly Vector2 ReferenceSize = new(200, 84);
	private static readonly Font TextFont = new SystemFont { FontNames = new[] { "Segoe UI", "Noto Sans", "Segoe UI Emoji" }, FontWeight = 600 };
	private static readonly StyleBoxFlat Bubble = CreateBubble();
	private const TextServer.LineBreakFlag Breaks = TextServer.LineBreakFlag.Mandatory | TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.GraphemeBound;
	public string Text { get; set; } = AccessoryWardrobe.DefaultText;
	public Color TextColor { get; set; } = Colors.White;
	public bool BackgroundVisible { get; set; } = true;
	public override void _Draw() => DrawTextBox(this, Text, TextColor, BackgroundVisible);
	public static void DrawTextBox(CanvasItem canvas, string text, Color color, bool backgroundVisible = true)
	{
		if (backgroundVisible) canvas.DrawStyleBox(Bubble, new Rect2(-ReferenceSize * 0.5f, ReferenceSize));
		var fontSize = 24;
		var area = ReferenceSize - new Vector2(24, 18);
		Vector2 textSize;
		do
		{
			textSize = TextFont.GetMultilineStringSize(text, HorizontalAlignment.Center, area.X, fontSize, -1, Breaks);
			if (textSize.Y <= area.Y || fontSize <= 8) break;
			fontSize--;
		} while (true);
		var origin = new Vector2(12, (ReferenceSize.Y - textSize.Y) * 0.5f + TextFont.GetAscent(fontSize)) - ReferenceSize * 0.5f;
		if (!backgroundVisible)
		{
			// Keep free-floating letters readable against both light and dark desktops.
			var lightText = color.R * 0.2126f + color.G * 0.7152f + color.B * 0.0722f > 0.45f;
			var outline = new Color(lightText ? Colors.Black : Colors.White, color.A * 0.85f);
			canvas.DrawMultilineStringOutline(TextFont, origin, text, HorizontalAlignment.Center, area.X, fontSize, -1, 2, outline, Breaks);
		}
		canvas.DrawMultilineString(TextFont, origin, text, HorizontalAlignment.Center, area.X, fontSize, -1, color, Breaks);
	}
	private static StyleBoxFlat CreateBubble()
	{
		var bubble = new StyleBoxFlat { BgColor = WoodlandTheme.Moss.Darkened(0.2f), BorderColor = WoodlandTheme.Border };
		bubble.SetCornerRadiusAll(14); bubble.SetBorderWidthAll(2); return bubble;
	}
}

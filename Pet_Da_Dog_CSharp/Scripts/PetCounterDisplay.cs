using System.Globalization;
using Godot;

/// <summary>A decorative readout of the latest Steam-confirmed Pets total.</summary>
[Tool]
public partial class PetCounterDisplay : PanelContainer
{
	public const float FootGap = 4;
	public string CounterText => GetNode<Label>("Value").Text;
	public Rect2? GetRenderBounds() => Visible ? new Rect2(GlobalPosition, Size) : null;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		if (!Engine.IsEditorHint()) SetConfirmedPets(null);
	}

	public void SetConfirmedPets(long? confirmedPets)
	{
		var label = GetNode<Label>("Value");
		var text = confirmedPets is >= 0 ? $"Pets: {confirmedPets.Value.ToString("N0", CultureInfo.InvariantCulture)}" : "Pets: —";
		if (label.Text != text) label.Text = text;
		// Resizing both ways prevents an old, longer total leaving an oversized badge.
		Size = GetCombinedMinimumSize();
	}

	public void Place(Vector2 viewportSize, Vector2 feet, float outfitBottom, PetCounterPlacement placement)
	{
		var inset = Vector2.One * 8;
		var maximum = (viewportSize - Size).Max(Vector2.Zero);
		var cornerMaximum = (maximum - inset).Max(Vector2.Zero);
		var desired = placement switch
		{
			PetCounterPlacement.TopLeft => inset,
			PetCounterPlacement.TopRight => new Vector2(cornerMaximum.X, inset.Y),
			PetCounterPlacement.BottomLeft => new Vector2(inset.X, cornerMaximum.Y),
			PetCounterPlacement.BottomRight => cornerMaximum,
			_ => new Vector2(feet.X - Size.X * 0.5f, feet.Y + outfitBottom + FootGap),
		};
		Position = desired.Clamp(Vector2.Zero, maximum);
	}
}

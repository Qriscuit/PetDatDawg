using Godot;

/// <summary>Editable catalog card template. Its exported sample data is safe in the editor.</summary>
[Tool]
public partial class AccessoryCard : Button
{
	private string _displayName = "Accessory", _previewStatus = "Click to place";
	private Texture2D? _previewTexture;
	private Color _previewTint = Colors.White;
	[ExportGroup("Editor sample")]
	[Export] public string DisplayName { get => _displayName; set { _displayName = value; RefreshIfReady(); } }
	[Export] public Texture2D? PreviewTexture { get => _previewTexture; set { _previewTexture = value; RefreshIfReady(); } }
	[Export] public string PreviewStatus { get => _previewStatus; set { _previewStatus = value; RefreshIfReady(); } }
	[Export] public Color PreviewTint { get => _previewTint; set { _previewTint = value; RefreshIfReady(); } }
	[ExportGroup("State captions")]
	[Export] public string AvailableStatusText { get; set; } = "Click to place";
	[Export] public string EquippedStatusText { get; set; } = "Equipped";
	[Export] public string ItemPlacementStatusText { get; set; } = "Click the dog";
	[Export] public string TextPlacementStatusText { get; set; } = "Place near dog";
	/// <summary>Named tokens: {name}, {id}, and {category}.</summary>
	[Export(PropertyHint.MultilineText)] public string ChooseTooltipFormat { get; set; } = "Choose {name}. Click the desktop dog to place it, or drag an equipped item directly.";

	public TextureRect Preview => GetNode<TextureRect>("%CardTexture");
	public Label Status => GetNode<Label>("%CardStatus");

	public override void _Ready() => RefreshSample();
	private void RefreshIfReady() { if (IsNodeReady()) RefreshSample(); }

	public void Configure(AccessoryDefinition accessory, Color tint)
	{
		DisplayName = accessory.Name; PreviewTexture = accessory.Texture; PreviewTint = tint;
		TooltipText = (ChooseTooltipFormat ?? string.Empty).Replace("{name}", accessory.Name)
			.Replace("{id}", accessory.Id).Replace("{category}", AccessoryCategories.For(accessory));
		RefreshSample();
	}

	public void RefreshStatus(bool placing, bool equipped, bool isText)
	{
		Status.Text = placing ? isText ? TextPlacementStatusText : ItemPlacementStatusText
			: equipped ? EquippedStatusText : AvailableStatusText;
	}

	private void RefreshSample()
	{
		Preview.Texture = PreviewTexture;
		Preview.Modulate = PreviewTint;
		GetNode<Label>("%CardName").Text = DisplayName;
		Status.Text = PreviewStatus;
	}
}

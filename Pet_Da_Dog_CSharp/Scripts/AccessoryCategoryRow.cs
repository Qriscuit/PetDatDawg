using System;
using Godot;

/// <summary>A scene-authored collapsible accessory shelf with a horizontal card row.</summary>
[Tool]
public partial class AccessoryCategoryRow : VBoxContainer
{
	private string _category = "Accessories";
	private int _count;
	private int _cardsAdded;
	private int _previewCount;
	private bool _expanded = true;
	private Label? _emptyLabel;
	private string _displayTitle = string.Empty;
	private string _headerFormat = "{glyph}  {title} · {count}";
	private string _expandedGlyph = "▾", _collapsedGlyph = "▸";
	private string _expandedTooltipFormat = "Collapse {title_lower} accessories.";
	private string _collapsedTooltipFormat = "Expand {title_lower} accessories.";

	/// <summary>Catalog identity used to populate this shelf. Use DisplayTitle to change its caption.</summary>
	[ExportGroup("Category")]
	[Export] public string Category
	{
		get => _category;
		set { _category = value; RefreshHeader(); }
	}
	[Export] public int PreviewCount
	{
		get => _previewCount;
		set
		{
			_previewCount = Math.Max(0, value);
			if (!IsNodeReady() || !Engine.IsEditorHint()) return;
			_count = _previewCount; _emptyLabel!.Visible = _count == 0; RefreshHeader();
		}
	}

	[Export] public bool Expanded
	{
		get => _expanded;
		set
		{
			_expanded = value;
			RefreshHeader();
			if (Scroll != null) Scroll.Visible = value;
		}
	}

	/// <summary>Optional visible title. An empty title displays the Category identity.</summary>
	[ExportGroup("Header copy")]
	[Export] public string DisplayTitle { get => _displayTitle; set { _displayTitle = value; RefreshHeader(); } }
	/// <summary>Named tokens: {glyph}, {title}, {title_lower}, {category}, and {count}.</summary>
	[Export] public string HeaderFormat { get => _headerFormat; set { _headerFormat = value; RefreshHeader(); } }
	[Export] public string ExpandedGlyph { get => _expandedGlyph; set { _expandedGlyph = value; RefreshHeader(); } }
	[Export] public string CollapsedGlyph { get => _collapsedGlyph; set { _collapsedGlyph = value; RefreshHeader(); } }
	[Export] public string ExpandedTooltipFormat { get => _expandedTooltipFormat; set { _expandedTooltipFormat = value; RefreshHeader(); } }
	[Export] public string CollapsedTooltipFormat { get => _collapsedTooltipFormat; set { _collapsedTooltipFormat = value; RefreshHeader(); } }

	public Button HeaderButton { get; private set; } = null!;
	public ScrollContainer Scroll { get; private set; } = null!;
	public HBoxContainer Cards { get; private set; } = null!;

	public override void _Ready()
	{
		BindNodes();
		_count = PreviewCount;
		_emptyLabel!.Visible = _count == 0;
		HeaderButton.Toggled += expanded => Expanded = expanded;
		Expanded = _expanded;
	}

	private void BindNodes()
	{
		HeaderButton = GetNode<Button>("%CategoryHeader");
		Scroll = GetNode<ScrollContainer>("%CategoryScroll");
		Cards = GetNode<HBoxContainer>("%CategoryCards");
		_emptyLabel = GetNode<Label>("%EmptyCategoryLabel");
	}

	public void Configure(string category, int count)
	{
		if (Cards == null) BindNodes();
		// Replace the sample catalog data, preserving every authored layout node.
		foreach (var child in Cards!.GetChildren())
		{
			if (child is not AccessoryCard) continue;
			Cards.RemoveChild(child); child.QueueFree();
		}
		_category = category; _count = Math.Max(0, count); _cardsAdded = 0;
		_emptyLabel!.Visible = _count == 0;
		Expanded = _expanded;
	}

	public void AddCard(Control card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (Cards == null) throw new InvalidOperationException("Configure the category before adding cards.");
		_emptyLabel!.Visible = false;
		Cards.AddChild(card);
		_count = Math.Max(_count, ++_cardsAdded);
		RefreshHeader();
	}

	private void RefreshHeader()
	{
		if (HeaderButton == null) return;
		HeaderButton.Text = FormatHeader(HeaderFormat);
		HeaderButton.TooltipText = FormatHeader(_expanded ? ExpandedTooltipFormat : CollapsedTooltipFormat);
		HeaderButton.SetPressedNoSignal(_expanded);
	}
	private string FormatHeader(string format)
	{
		var title = string.IsNullOrEmpty(DisplayTitle) ? _category : DisplayTitle;
		return (format ?? string.Empty).Replace("{glyph}", _expanded ? ExpandedGlyph : CollapsedGlyph)
			.Replace("{title}", title).Replace("{title_lower}", title.ToLowerInvariant())
			.Replace("{category}", _category).Replace("{count}", _count.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}
}

public static class AccessoryCategories
{
	public static readonly string[] OrderedNames = { "Wings", "Collars", "Glasses", "Decorations", "Text" };

	public static string For(AccessoryDefinition accessory)
	{
		if (accessory.IsText) return "Text";
		var name = $"{accessory.Id} {accessory.Name}";
		if (name.Contains("wing", StringComparison.OrdinalIgnoreCase)) return "Wings";
		if (name.Contains("collar", StringComparison.OrdinalIgnoreCase)) return "Collars";
		if (name.Contains("glasses", StringComparison.OrdinalIgnoreCase) || name.Contains("goggle", StringComparison.OrdinalIgnoreCase))
			return "Glasses";
		return "Decorations";
	}
}

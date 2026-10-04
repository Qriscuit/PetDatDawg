using System;
using Godot;

/// <summary>A collapsible accessory shelf with one horizontally scrolling row.</summary>
public partial class AccessoryCategoryRow : VBoxContainer
{
	private string _category = string.Empty;
	private int _count;
	private int _cardsAdded;
	private bool _expanded = true;
	private Label? _emptyLabel;

	public Button HeaderButton { get; private set; } = null!;
	public ScrollContainer Scroll { get; private set; } = null!;
	public HBoxContainer Cards { get; private set; } = null!;

	public bool Expanded
	{
		get => _expanded;
		set
		{
			_expanded = value;
			RefreshHeader();
			if (Scroll != null) Scroll.Visible = value;
		}
	}

	public void Configure(string category, int count)
	{
		foreach (var child in GetChildren())
		{
			RemoveChild(child);
			child.QueueFree();
		}
		_category = category;
		_count = Math.Max(0, count);
		_cardsAdded = 0;
		_emptyLabel = null;
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		SizeFlagsVertical = SizeFlags.ShrinkBegin;
		AddThemeConstantOverride("separation", 6);

		HeaderButton = new Button
		{
			CustomMinimumSize = new Vector2(0, 34),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			Alignment = HorizontalAlignment.Left,
			ToggleMode = true
		};
		WoodlandTheme.StyleButton(HeaderButton, "SecondaryButton");
		HeaderButton.AddThemeFontSizeOverride("font_size", 14);
		HeaderButton.Toggled += expanded => Expanded = expanded;
		AddChild(HeaderButton);

		Scroll = new ScrollContainer
		{
			CustomMinimumSize = new Vector2(0, 126),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ShrinkBegin,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
			VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
			FollowFocus = true
		};
		Scroll.AddThemeStyleboxOverride("panel", WoodlandTheme.CatalogStyle());
		Cards = new HBoxContainer
		{
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
			SizeFlagsVertical = SizeFlags.Fill
		};
		Cards.AddThemeConstantOverride("separation", 8);
		Scroll.AddChild(Cards);
		AddChild(Scroll);

		if (_count == 0)
		{
			_emptyLabel = new Label
			{
				Text = "No accessories in this category yet.",
				CustomMinimumSize = new Vector2(0, 112),
				VerticalAlignment = VerticalAlignment.Center,
				MouseFilter = MouseFilterEnum.Ignore
			};
			_emptyLabel.AddThemeColorOverride("font_color", WoodlandTheme.Muted);
			_emptyLabel.AddThemeFontSizeOverride("font_size", 13);
			Cards.AddChild(_emptyLabel);
		}
		Expanded = _expanded;
	}

	public void AddCard(Control card)
	{
		ArgumentNullException.ThrowIfNull(card);
		if (Cards == null) throw new InvalidOperationException("Configure the category before adding cards.");
		if (_emptyLabel != null)
		{
			Cards.RemoveChild(_emptyLabel);
			_emptyLabel.QueueFree();
			_emptyLabel = null;
		}
		Cards.AddChild(card);
		_count = Math.Max(_count, ++_cardsAdded);
		RefreshHeader();
	}

	private void RefreshHeader()
	{
		if (HeaderButton == null) return;
		HeaderButton.Text = $"{(_expanded ? "▾" : "▸")}  {_category} · {_count}";
		HeaderButton.TooltipText = $"{(_expanded ? "Collapse" : "Expand")} {_category.ToLowerInvariant()} accessories.";
		HeaderButton.SetPressedNoSignal(_expanded);
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

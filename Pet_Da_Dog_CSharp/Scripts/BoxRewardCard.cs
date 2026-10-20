using Godot;
using System;

// Presentation data only: callers supply a confirmed server receipt. There is no
// balance or ownership storage in the card, including when previewing test art.
public sealed record BoxReward(int ItemDefId, int Quantity = 1, bool WasDuplicate = false,
	string? DisplayName = null, string? PreviewPath = null);

public partial class BoxRewardCard : Window
{
	public event Action? Dismissed;
	public event Action? EquipRequested;
	public event Action? SkipRequested;
	public bool ShowingReward { get; private set; }
	private Label _heading = null!, _detail = null!, _name = null!;
	private TextureRect _preview = null!;
	private Button _keep = null!, _equip = null!, _skip = null!;
	private ScrollContainer _bodyScroll = null!;
	private MenuFontScale _fontScale = null!;
	private float _requestedFontScale = 1;
	private bool _showingGoldenBones;
	private Rect2I? _usableRect;

	public override void _Ready()
	{
		_heading = GetNode<Label>("%RewardHeading"); _detail = GetNode<Label>("%RewardDetail");
		_name = GetNode<Label>("%RewardName"); _preview = GetNode<TextureRect>("%RewardPreview");
		_keep = GetNode<Button>("%KeepReward"); _equip = GetNode<Button>("%EquipReward"); _skip = GetNode<Button>("%SkipReveal");
		_bodyScroll = GetNode<ScrollContainer>("%RewardBodyScroll");
		_keep.Pressed += () => Dismissed?.Invoke(); _equip.Pressed += () => EquipRequested?.Invoke();
		_skip.Pressed += () => SkipRequested?.Invoke(); CloseRequested += () => Dismissed?.Invoke();
		_fontScale = MenuFontScale.Attach(this);
		// Native wrap-controls sizing can grow the window after deferred label and
		// container layout. The body scrolls inside a bounded window instead.
		WrapControls = false;
		MinSize = Vector2I.Zero;
		_preview.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
		_preview.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
	}

	public void ShowProgress(string text, float fontScale)
	{
		ShowingReward = false;
		_showingGoldenBones = false;
		_heading.Text = text; _detail.Text = "Your purchase will be confirmed by Steam.";
		_preview.Visible = false; _name.Visible = false; _equip.Visible = false;
		_keep.Text = "Back to Shop"; _skip.Visible = true;
		ApplyPresentationLayout(fontScale);
	}

	public void SetProgressText(string text) { if (!ShowingReward) _heading.Text = text; }

	public void ShowReward(BoxReward reward, bool canEquip, float fontScale)
	{
		ShowingReward = true;
		_showingGoldenBones = reward.ItemDefId == 101;
		var definition = SteamCosmeticCatalog.Find(reward.ItemDefId);
		_heading.Text = _showingGoldenBones ? "Golden Bones gained" : "Look what you found!";
		_name.Text = reward.DisplayName ?? definition?.Name ?? (reward.ItemDefId == 101 ? "Golden Bones" : "Item");
		_detail.Text = $"{(definition?.Kind == "dog" ? "Dog" : definition?.Kind == "accessory" ? "Accessory" : "Reward")} · ×{reward.Quantity}"
			+ (reward.WasDuplicate ? " · Another copy" : "");
		var path = reward.PreviewPath ?? definition?.UiAssetPath ?? (reward.ItemDefId == 101 ? "res://Art/UI/Shop/GoldenBones.svg" : null);
		_preview.Texture = path == null ? null : SteamCosmeticCatalog.CroppedTexture(path);
		_preview.Visible = true; _name.Visible = true;
		_equip.Visible = canEquip; _skip.Visible = false; _keep.Text = _showingGoldenBones ? "Done" : "Keep";
		ApplyPresentationLayout(fontScale);
	}

	private void ApplyPresentationLayout(float fontScale)
	{
		_requestedFontScale = float.IsFinite(fontScale) ? Mathf.Clamp(fontScale, 0.75f, 1.5f) : 1;
		_fontScale.Apply(_requestedFontScale);
		// Preview dimensions depend on the card, never the source texture or atlas.
		_preview.CustomMinimumSize = new Vector2(0, (_showingGoldenBones ? 72 : 150) * Math.Max(1, _requestedFontScale));
		_bodyScroll.ScrollVertical = 0;
		var screen = CurrentScreen;
		if (screen < 0 || screen >= DisplayServer.GetScreenCount()) screen = (int)DisplayServer.ScreenPrimary;
		FitToUsableRect(_usableRect ?? DisplayServer.ScreenGetUsableRect(screen));
		Show();
	}

	// The caller supplies the dog's monitor work area before positioning the card.
	// Remember it across progress/reward changes instead of fitting to another
	// monitor when deferred layout runs or a previous reward is still visible.
	public void FitToUsableRect(Rect2I usable)
	{
		if (usable.Size.X <= 0 || usable.Size.Y <= 0) return;
		_usableRect = usable;
		var inset = Math.Min(12, Math.Max(0, (Math.Min(usable.Size.X, usable.Size.Y) - 1) / 2));
		var available = new Vector2I(Math.Max(1, usable.Size.X - inset * 2), Math.Max(1, usable.Size.Y - inset * 2));
		var scaleExtra = Math.Max(0, _requestedFontScale - 1);
		var preferred = new Vector2I(Mathf.RoundToInt(360 + 120 * scaleExtra),
			Mathf.RoundToInt((ShowingReward ? 360 : 180) + 120 * scaleExtra));
		WrapControls = false;
		MinSize = Vector2I.Zero;
		MaxSize = available;
		Size = new Vector2I(Math.Min(preferred.X, available.X), Math.Min(preferred.Y, available.Y));
		var start = usable.Position + new Vector2I(inset, inset);
		Position = new Vector2I(Math.Clamp(Position.X, start.X, start.X + available.X - Size.X),
			Math.Clamp(Position.Y, start.Y, start.Y + available.Y - Size.Y));
	}
}

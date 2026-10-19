using System;
using Godot;

/// <summary>Shared local selection and undo transaction for editing the real desktop dog.</summary>
public partial class AccessoryEditingSession : Node
{
	private AccessoryWardrobe? _wardrobe;

	public bool Active { get; private set; }
	public string? SelectedId { get; private set; }
	public bool IsPlacing { get; private set; }
	public bool IsDragging { get; private set; }
	public bool IsCatalogDragging { get; private set; }
	public bool IsManipulating => IsDragging || IsCatalogDragging;
	public event Action? StateChanged;
	public event Action<bool>? OutfitSaved;
	public event Action? EditStarting;
	public event Action? InteractionCanceled;
	public event Action<bool>? ActiveChanged;
	public void ReportOutfitSave(bool success) => OutfitSaved?.Invoke(success);

	public override void _Ready()
	{
		_wardrobe = GetNodeOrNull<AccessoryWardrobe>("/root/AccessoryWardrobe");
		if (_wardrobe != null) _wardrobe.Changed += OnWardrobeChanged;
	}

	public override void _ExitTree()
	{
		CancelInteraction();
		if (_wardrobe != null) _wardrobe.Changed -= OnWardrobeChanged;
	}

	public void SetActive(bool active)
	{
		if (Active == active) return;
		if (!active)
		{
			// Flush inspector-owned edits before cancelling any desktop gesture.
			EditStarting?.Invoke();
			CancelInteraction();
		}
		Active = active;
		ActiveChanged?.Invoke(active);
		StateChanged?.Invoke();
	}

	public void ChooseAccessory(string id)
	{
		if (!Active || _wardrobe?.CanEquip(id) != true) return;
		CompleteGesture();
		EditStarting?.Invoke();
		IsCatalogDragging = false;
		// A catalog choice offers the next unused copy. Equipped copies are selected
		// directly on the dog; when the quantity is exhausted the card selects one.
		var definition = _wardrobe.Find(id);
		var existing = _wardrobe.GetPlacement(id) ?? System.Linq.Enumerable.FirstOrDefault(_wardrobe.Equipped,
			placement => _wardrobe.Find(placement.Id)?.Id == definition?.Id);
		var nextCopy = _wardrobe.CreatePlacementId(id);
		SelectedId = nextCopy ?? existing?.Id;
		IsPlacing = nextCopy != null;
		StateChanged?.Invoke();
	}

	public void ChooseAdditionalCopy(string id)
	{
		if (!Active || _wardrobe?.CanAddCopy(id) != true) return;
		CompleteGesture();
		EditStarting?.Invoke();
		var copy = _wardrobe.CreatePlacementId(id);
		if (copy == null) return;
		IsCatalogDragging = false;
		SelectedId = copy;
		IsPlacing = true;
		StateChanged?.Invoke();
	}

	public bool BeginCatalogDrag(string id)
	{
		// Do not let dragging an exhausted card move or replace an equipped copy.
		if (!Active) return false;
		if (_wardrobe?.CanAddCopy(id) != true) { CancelInteraction(); return false; }
		ChooseAdditionalCopy(id);
		if (!IsPlacing || SelectedId == null) return false;
		IsCatalogDragging = true;
		StateChanged?.Invoke();
		return true;
	}

	public bool CompleteCatalogDrag(Vector2 normalizedPosition)
	{
		if (!IsCatalogDragging) return false;
		IsCatalogDragging = false;
		if (!PlaceAt(normalizedPosition))
		{
			CancelInteraction();
			return false;
		}
		return true;
	}

	public void SelectAccessory(string id)
	{
		if (!Active || _wardrobe?.GetPlacement(id) == null) return;
		CompleteGesture();
		EditStarting?.Invoke();
		SelectedId = id;
		IsPlacing = false;
		IsCatalogDragging = false;
		StateChanged?.Invoke();
	}

	public void ClearSelection()
	{
		CancelInteraction();
		EditStarting?.Invoke();
		SelectedId = null;
		IsPlacing = false;
		StateChanged?.Invoke();
	}

	public bool BeginGesture(string label)
	{
		if (!Active || _wardrobe == null) return false;
		CompleteGesture();
		// The menu and desktop keep separate focus; never share their undo transactions.
		EditStarting?.Invoke();
		if (!Active) return false;
		_wardrobe.BeginEdit(label);
		IsDragging = true;
		StateChanged?.Invoke();
		return true;
	}

	public void CompleteGesture()
	{
		if (!IsDragging || _wardrobe == null) return;
		IsDragging = false;
		if (SelectedId != null && _wardrobe.GetPlacement(SelectedId) != null) IsPlacing = false;
		_wardrobe.CommitEdit();
		OutfitSaved?.Invoke(_wardrobe.Save());
		StateChanged?.Invoke();
	}

	public bool PlaceAt(Vector2 normalizedPosition)
	{
		if (!Active || SelectedId == null || _wardrobe?.CanEquip(SelectedId) != true || !normalizedPosition.IsFinite()) return false;
		if (!IsDragging && !BeginGesture("place accessory")) return false;
		_wardrobe.Equip(SelectedId, normalizedPosition);
		if (_wardrobe.GetPlacement(SelectedId) == null)
		{
			CancelInteraction();
			return false;
		}
		IsPlacing = false;
		CompleteGesture();
		return true;
	}

	public void CancelInteraction()
	{
		IsCatalogDragging = false;
		if (IsDragging && _wardrobe != null)
		{
			IsDragging = false;
			_wardrobe.CancelEdit();
			OutfitSaved?.Invoke(_wardrobe.Save());
		}
		IsPlacing = false;
		if (SelectedId != null && _wardrobe?.GetPlacement(SelectedId) == null) SelectedId = null;
		InteractionCanceled?.Invoke();
		StateChanged?.Invoke();
	}

	private void OnWardrobeChanged()
	{
		if (SelectedId != null && (_wardrobe?.CanEquip(SelectedId) != true
			|| (!IsPlacing && _wardrobe.GetPlacement(SelectedId) == null)
			|| (IsPlacing && _wardrobe.GetPlacement(SelectedId) == null && !_wardrobe.CanAddCopy(SelectedId))))
		{
			CancelInteraction(); SelectedId = null; StateChanged?.Invoke(); return;
		}
		if (SelectedId == null || IsPlacing || IsDragging || _wardrobe?.GetPlacement(SelectedId) != null) return;
		SelectedId = null;
		StateChanged?.Invoke();
	}
}

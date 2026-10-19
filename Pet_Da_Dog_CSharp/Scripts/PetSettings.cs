using System;
using System.Globalization;
using Godot;

public enum PetCounterPlacement { DogFeet = 0, TopLeft = 1, TopRight = 2, BottomLeft = 3, BottomRight = 4 }

public partial class PetSettings : Node
{
    [Signal]
    public delegate void SettingsChangedEventHandler();

    public const bool DefaultAlwaysOnTop = true;
    public const bool DefaultDogClickThrough = false;
    public const bool DefaultStationary = false;
    public const bool DefaultBounceEnabled = true;
    public const bool DefaultFlipHorizontally = false;
    public const bool DefaultShowPetCounter = false;
    public const PetCounterPlacement DefaultPetCounterPosition = PetCounterPlacement.DogFeet;
    public const bool DefaultOpenMenuOnRightClick = true;
    public const int DefaultMenuHotkeyVirtualKey = 0x44;
    public const int DefaultMenuHotkeyModifiers = 3;
    public const float DefaultDogTransparency = 1.0f;
    public const float DefaultDogScale = 1.0f;
    public const float DefaultUiScale = 1.0f;
    public const float DefaultFontScale = 1.0f;

    public const float MinDogTransparency = 0.0f;
    public const float MaxDogTransparency = 1.0f;
    public const float MinDogScale = 0.5f;
    public const float MaxDogScale = 2.0f;
    public const float MinUiScale = 0.75f;
    public const float MaxUiScale = 1.75f;
    public const float MinFontScale = 0.75f;
    public const float MaxFontScale = 1.5f;

    private const string SettingsPath = "user://pet_settings.cfg";
    private const string Section = "dog";

    private bool _alwaysOnTop = DefaultAlwaysOnTop;
    private bool _dogClickThrough = DefaultDogClickThrough;
    private bool _stationary = DefaultStationary;
    private bool _bounceEnabled = DefaultBounceEnabled;
    private bool _flipHorizontally = DefaultFlipHorizontally;
    private bool _showPetCounter = DefaultShowPetCounter;
    private PetCounterPlacement _petCounterPosition = DefaultPetCounterPosition;
    private bool _openMenuOnRightClick = DefaultOpenMenuOnRightClick;
    private int _menuHotkeyVirtualKey = DefaultMenuHotkeyVirtualKey;
    private int _menuHotkeyModifiers = DefaultMenuHotkeyModifiers;
    private float _dogTransparency = DefaultDogTransparency;
    private float _dogScale = DefaultDogScale;
    private float _uiScale = DefaultUiScale;
    private float _fontScale = DefaultFontScale;
    private bool _hasSeenWelcome;

    public bool AlwaysOnTop => _alwaysOnTop;
    public bool DogClickThrough => _dogClickThrough;
    public bool Stationary => _stationary;
    public bool BounceEnabled => _bounceEnabled;
    public bool FlipHorizontally => _flipHorizontally;
    public bool ShowPetCounter => _showPetCounter;
    public PetCounterPlacement PetCounterPosition => _petCounterPosition;
    public bool OpenMenuOnRightClick => _openMenuOnRightClick;
    public int MenuHotkeyVirtualKey => _menuHotkeyVirtualKey;
    public int MenuHotkeyModifiers => _menuHotkeyModifiers;
    public float DogTransparency => _dogTransparency;
    public float DogScale => _dogScale;
    public float UiScale => _uiScale;
    public float FontScale => _fontScale;
    public bool HasSeenWelcome => _hasSeenWelcome;
    public void DismissWelcome() => SetBool(ref _hasSeenWelcome, true);

    public override void _Ready()
    {
        Load();
    }

    public void SetAlwaysOnTop(bool value)
    {
        SetBool(ref _alwaysOnTop, value);
    }

    public void SetDogClickThrough(bool value)
    {
        SetBool(ref _dogClickThrough, value);
    }

    public void ToggleDogClickThrough()
    {
        SetDogClickThrough(!DogClickThrough);
    }

    public void SetStationary(bool value)
    {
        SetBool(ref _stationary, value);
    }

    public void SetBounceEnabled(bool value)
    {
        SetBool(ref _bounceEnabled, value);
    }

    public void SetFlipHorizontally(bool value)
    {
        SetBool(ref _flipHorizontally, value);
    }

    public void SetShowPetCounter(bool value)
    {
        SetBool(ref _showPetCounter, value);
    }

    public void SetPetCounterPosition(PetCounterPlacement value)
    {
        if (!Enum.IsDefined(typeof(PetCounterPlacement), value) || _petCounterPosition == value) return;
        _petCounterPosition = value;
        SaveAndNotify();
    }

    public void SetOpenMenuOnRightClick(bool value)
    {
        SetBool(ref _openMenuOnRightClick, value);
    }

    public void SetMenuHotkey(int virtualKey, int modifiers)
    {
        if (!MenuShortcut.IsValid(virtualKey, modifiers)
            || (_menuHotkeyVirtualKey == virtualKey && _menuHotkeyModifiers == modifiers)) return;
        _menuHotkeyVirtualKey = virtualKey;
        _menuHotkeyModifiers = modifiers;
        SaveAndNotify();
    }

    public void SetDogTransparency(float value)
    {
        SetFloat(ref _dogTransparency, Mathf.Clamp(value, MinDogTransparency, MaxDogTransparency));
    }

    public void SetDogScale(float value)
    {
        SetFloat(ref _dogScale, Mathf.Clamp(value, MinDogScale, MaxDogScale));
    }

    public void SetUiScale(float value)
    {
        SetFloat(ref _uiScale, Mathf.Clamp(value, MinUiScale, MaxUiScale));
    }

    public void SetFontScale(float value)
    {
        if (float.IsFinite(value)) SetFloat(ref _fontScale, Mathf.Clamp(value, MinFontScale, MaxFontScale));
    }

    private void Load()
    {
        var config = new ConfigFile();
        var error = config.Load(SettingsPath);
        if (error != Error.Ok)
        {
            Save();
            return;
        }

        _alwaysOnTop = ReadBool(config, "always_on_top", DefaultAlwaysOnTop);
        _dogClickThrough = ReadBool(config, "dog_click_through", DefaultDogClickThrough);
        _stationary = ReadBool(config, "stationary", DefaultStationary);
        _bounceEnabled = ReadBool(config, "bounce_enabled", DefaultBounceEnabled);
        _flipHorizontally = ReadBool(config, "flip_horizontally", DefaultFlipHorizontally);
        _showPetCounter = ReadBool(config, "show_pet_counter", DefaultShowPetCounter);
        var counterPosition = ReadInt(config, "pet_counter_position", (int)DefaultPetCounterPosition);
        _petCounterPosition = Enum.IsDefined(typeof(PetCounterPlacement), counterPosition)
            ? (PetCounterPlacement)counterPosition : DefaultPetCounterPosition;
        _openMenuOnRightClick = ReadBool(config, "open_menu_on_right_click", DefaultOpenMenuOnRightClick);
        var menuKey = ReadInt(config, "menu_hotkey_virtual_key", DefaultMenuHotkeyVirtualKey);
        var menuModifiers = ReadInt(config, "menu_hotkey_modifiers", DefaultMenuHotkeyModifiers);
        var validShortcut = MenuShortcut.IsValid(menuKey, menuModifiers);
        _menuHotkeyVirtualKey = validShortcut ? menuKey : DefaultMenuHotkeyVirtualKey;
        _menuHotkeyModifiers = validShortcut ? menuModifiers : DefaultMenuHotkeyModifiers;
        _dogTransparency = Mathf.Clamp(ReadFloat(config, "dog_transparency", DefaultDogTransparency), MinDogTransparency, MaxDogTransparency);
        _dogScale = Mathf.Clamp(ReadFloat(config, "dog_scale", DefaultDogScale), MinDogScale, MaxDogScale);
        _uiScale = Mathf.Clamp(ReadFloat(config, "ui_scale", DefaultUiScale), MinUiScale, MaxUiScale);
        var fontScale = ReadFloat(config, "font_scale", DefaultFontScale);
        _fontScale = float.IsFinite(fontScale) ? Mathf.Clamp(fontScale, MinFontScale, MaxFontScale) : DefaultFontScale;
        _hasSeenWelcome = ReadBool(config, "welcome_seen", false);
    }

    private void Save()
    {
        var config = new ConfigFile();
        config.SetValue(Section, "always_on_top", AlwaysOnTop);
        config.SetValue(Section, "dog_click_through", DogClickThrough);
        config.SetValue(Section, "stationary", Stationary);
        config.SetValue(Section, "bounce_enabled", BounceEnabled);
        config.SetValue(Section, "flip_horizontally", FlipHorizontally);
        config.SetValue(Section, "show_pet_counter", ShowPetCounter);
        config.SetValue(Section, "pet_counter_position", (int)PetCounterPosition);
        config.SetValue(Section, "open_menu_on_right_click", OpenMenuOnRightClick);
        config.SetValue(Section, "menu_hotkey_virtual_key", MenuHotkeyVirtualKey);
        config.SetValue(Section, "menu_hotkey_modifiers", MenuHotkeyModifiers);
        config.SetValue(Section, "dog_transparency", DogTransparency);
        config.SetValue(Section, "dog_scale", DogScale);
        config.SetValue(Section, "ui_scale", UiScale);
        config.SetValue(Section, "font_scale", FontScale);
        config.SetValue(Section, "welcome_seen", HasSeenWelcome);

        var error = config.Save(SettingsPath);
        if (error != Error.Ok)
        {
            GD.PushWarning($"Could not save pet settings to {SettingsPath}: {error}");
        }
    }

    private void SaveAndNotify()
    {
        Save();
        EmitSignal(SignalName.SettingsChanged);
    }

    private void SetBool(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        SaveAndNotify();
    }

    private void SetFloat(ref float field, float value)
    {
        if (Mathf.IsEqualApprox(field, value))
        {
            return;
        }

        field = value;
        SaveAndNotify();
    }

    private static bool ReadBool(ConfigFile config, string key, bool fallback)
    {
        var valueText = config.GetValue(Section, key, fallback).ToString();
        return bool.TryParse(valueText, out var parsed) ? parsed : fallback;
    }

    private static float ReadFloat(ConfigFile config, string key, float fallback)
    {
        var valueText = config.GetValue(Section, key, fallback).ToString();
        return float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static int ReadInt(ConfigFile config, string key, int fallback)
    {
        var valueText = config.GetValue(Section, key, fallback).ToString();
        return int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : fallback;
    }
}

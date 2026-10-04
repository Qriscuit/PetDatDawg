using System;
using System.Globalization;
using Godot;

public partial class PetSettings : Node
{
    [Signal]
    public delegate void SettingsChangedEventHandler();

    public const bool DefaultAlwaysOnTop = true;
    public const bool DefaultDogClickThrough = false;
    public const float DefaultDogTransparency = 1.0f;
    public const float DefaultDogScale = 1.0f;
    public const float DefaultUiScale = 1.0f;

    public const float MinDogTransparency = 0.0f;
    public const float MaxDogTransparency = 1.0f;
    public const float MinDogScale = 0.5f;
    public const float MaxDogScale = 2.0f;
    public const float MinUiScale = 0.75f;
    public const float MaxUiScale = 1.75f;

    private const string SettingsPath = "user://pet_settings.cfg";
    private const string Section = "dog";

    private bool _alwaysOnTop = DefaultAlwaysOnTop;
    private bool _dogClickThrough = DefaultDogClickThrough;
    private float _dogTransparency = DefaultDogTransparency;
    private float _dogScale = DefaultDogScale;
    private float _uiScale = DefaultUiScale;
    private bool _hasSeenWelcome;

    public bool AlwaysOnTop => _alwaysOnTop;
    public bool DogClickThrough => _dogClickThrough;
    public float DogTransparency => _dogTransparency;
    public float DogScale => _dogScale;
    public float UiScale => _uiScale;
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
        _dogTransparency = Mathf.Clamp(ReadFloat(config, "dog_transparency", DefaultDogTransparency), MinDogTransparency, MaxDogTransparency);
        _dogScale = Mathf.Clamp(ReadFloat(config, "dog_scale", DefaultDogScale), MinDogScale, MaxDogScale);
        _uiScale = Mathf.Clamp(ReadFloat(config, "ui_scale", DefaultUiScale), MinUiScale, MaxUiScale);
        _hasSeenWelcome = ReadBool(config, "welcome_seen", false);
    }

    private void Save()
    {
        var config = new ConfigFile();
        config.SetValue(Section, "always_on_top", AlwaysOnTop);
        config.SetValue(Section, "dog_click_through", DogClickThrough);
        config.SetValue(Section, "dog_transparency", DogTransparency);
        config.SetValue(Section, "dog_scale", DogScale);
        config.SetValue(Section, "ui_scale", UiScale);
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
}

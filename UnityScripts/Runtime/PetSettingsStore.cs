using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace PetDaDog.Unity
{
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class PetSettingsStore : MonoBehaviour
    {
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

        private const string SettingsFileName = "pet_settings.json";
        private const string GodotSettingsFileName = "pet_settings.cfg";

        [Serializable]
        private sealed class PersistedSettings
        {
            public bool alwaysOnTop = DefaultAlwaysOnTop;
            public bool dogClickThrough = DefaultDogClickThrough;
            public float dogTransparency = DefaultDogTransparency;
            public float dogScale = DefaultDogScale;
            public float uiScale = DefaultUiScale;
        }

        private bool _initialized;
        private bool _alwaysOnTop = DefaultAlwaysOnTop;
        private bool _dogClickThrough = DefaultDogClickThrough;
        private float _dogTransparency = DefaultDogTransparency;
        private float _dogScale = DefaultDogScale;
        private float _uiScale = DefaultUiScale;

        public event Action Changed;

        public bool AlwaysOnTop => _alwaysOnTop;
        public bool DogClickThrough => _dogClickThrough;
        public float DogTransparency => _dogTransparency;
        public float DogScale => _dogScale;
        public float UiScale => _uiScale;

        private string SettingsPath => Path.Combine(Application.persistentDataPath, SettingsFileName);

        private void Awake()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Load();
        }

        public void SetAlwaysOnTop(bool value) => SetBool(ref _alwaysOnTop, value);

        public void SetDogClickThrough(bool value) => SetBool(ref _dogClickThrough, value);

        public void ToggleDogClickThrough() => SetDogClickThrough(!DogClickThrough);

        public void SetDogTransparency(float value) => SetFloat(ref _dogTransparency, ClampDogTransparency(value));

        public void SetDogScale(float value) => SetFloat(ref _dogScale, ClampDogScale(value));

        public void SetUiScale(float value) => SetFloat(ref _uiScale, ClampUiScale(value));

        public static float ClampDogTransparency(float value) => Mathf.Clamp(value, MinDogTransparency, MaxDogTransparency);

        public static float ClampDogScale(float value) => Mathf.Clamp(value, MinDogScale, MaxDogScale);

        public static float ClampUiScale(float value) => Mathf.Clamp(value, MinUiScale, MaxUiScale);

        private void Load()
        {
            PersistedSettings loaded = null;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    loaded = JsonUtility.FromJson<PersistedSettings>(File.ReadAllText(SettingsPath));
                }
                else
                {
                    loaded = TryImportGodotSettings();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Pet Da Dog could not load local settings: {exception.Message}");
            }

            loaded ??= new PersistedSettings();
            _alwaysOnTop = loaded.alwaysOnTop;
            _dogClickThrough = loaded.dogClickThrough;
            _dogTransparency = ClampDogTransparency(loaded.dogTransparency);
            _dogScale = ClampDogScale(loaded.dogScale);
            _uiScale = ClampUiScale(loaded.uiScale);
            Save();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Application.persistentDataPath);
                var settings = new PersistedSettings
                {
                    alwaysOnTop = AlwaysOnTop,
                    dogClickThrough = DogClickThrough,
                    dogTransparency = DogTransparency,
                    dogScale = DogScale,
                    uiScale = UiScale,
                };
                File.WriteAllText(SettingsPath, JsonUtility.ToJson(settings, true));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Pet Da Dog could not save local settings: {exception.Message}");
            }
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
            if (Mathf.Approximately(field, value))
            {
                return;
            }

            field = value;
            SaveAndNotify();
        }

        private void SaveAndNotify()
        {
            Save();
            Changed?.Invoke();
        }

        private static PersistedSettings TryImportGodotSettings()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)
            {
                return null;
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = Path.Combine(appData, "Godot", "app_userdata", "PetDaDogCSharp", GodotSettingsFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var imported = new PersistedSettings();
            var inDogSection = false;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    inDogSection = string.Equals(line, "[dog]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inDogSection || string.IsNullOrWhiteSpace(line) || line.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                switch (key)
                {
                    case "always_on_top" when bool.TryParse(value, out var alwaysOnTop):
                        imported.alwaysOnTop = alwaysOnTop;
                        break;
                    case "dog_click_through" when bool.TryParse(value, out var clickThrough):
                        imported.dogClickThrough = clickThrough;
                        break;
                    case "dog_transparency" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var transparency):
                        imported.dogTransparency = transparency;
                        break;
                    case "dog_scale" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dogScale):
                        imported.dogScale = dogScale;
                        break;
                    case "ui_scale" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var uiScale):
                        imported.uiScale = uiScale;
                        break;
                }
            }

            return imported;
        }
    }
}

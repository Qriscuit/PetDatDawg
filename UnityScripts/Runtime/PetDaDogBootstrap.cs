using System;
using UnityEngine;

namespace PetDaDog.Unity
{
    [DefaultExecutionOrder(-900)]
    [DisallowMultipleComponent]
    public sealed class PetDaDogBootstrap : MonoBehaviour
    {
        [SerializeField] private PetSettingsStore settings;
        [SerializeField] private SteamIntegrationBehaviour steam;
        [SerializeField] private BackendPetClientBehaviour backend;
        [SerializeField] private WindowsOverlayBridge overlay;
        [SerializeField] private NativeStatusWindow statusWindow;
        [SerializeField] private DesktopPetController desktopPet;

        private bool _initialized;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            settings ??= GetComponent<PetSettingsStore>();
            steam ??= GetComponent<SteamIntegrationBehaviour>();
            backend ??= GetComponent<BackendPetClientBehaviour>();
            overlay ??= GetComponent<WindowsOverlayBridge>();
            statusWindow ??= GetComponent<NativeStatusWindow>();
            desktopPet ??= GetComponent<DesktopPetController>();
        }

        private void Start()
        {
            if (_initialized)
            {
                return;
            }

            if (settings == null || steam == null || backend == null || overlay == null || statusWindow == null || desktopPet == null)
            {
                throw new InvalidOperationException("PetDaDogBootstrap needs every Pet Da Dog runtime component on the root object.");
            }

            _initialized = true;
            overlay.Initialize(desktopPet, settings);
            backend.Initialize(steam);
            statusWindow.Initialize(settings);
            desktopPet.Initialize(settings, backend, overlay, statusWindow.ShowStatusWindow);

            overlay.DogClicked += desktopPet.HandleNativeDogClick;
            overlay.StatusRequested += statusWindow.ShowStatusWindow;
            overlay.ExitRequested += QuitApplication;
            steam.Initialize();
        }

        private void Update()
        {
            if (!_initialized)
            {
                return;
            }

            statusWindow.UpdateStatus(new StatusSnapshot(
                backend.ConfirmedPets,
                backend.PendingGrantCount,
                backend.Status,
                steam.Status));
        }

        private static void QuitApplication()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void OnDestroy()
        {
            if (overlay == null || desktopPet == null)
            {
                return;
            }

            overlay.DogClicked -= desktopPet.HandleNativeDogClick;
            overlay.StatusRequested -= statusWindow.ShowStatusWindow;
            overlay.ExitRequested -= QuitApplication;
        }
    }
}

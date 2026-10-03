using System;
using System.Collections.Generic;
using UnityEngine;

namespace PetDaDog.Unity
{
    [DisallowMultipleComponent]
    public sealed class DesktopPetController : MonoBehaviour
    {
        private const float DogHeight = 128.0f;
        private const float WalkSpeed = 120.0f;
        private const float HopHeight = 10.0f;
        private const float StepFrequency = 8.0f;
        private const float GroundMargin = 2.0f;
        private const int OverlayPadding = 28;
        private const float LayoutRefreshSeconds = 1.0f;
        private const float InvisibleDogAlphaThreshold = 0.01f;
        private const float HeartLifetimeSeconds = 0.8f;
        private const float HeartRiseDistance = 34.0f;
        private const int MaxActiveHearts = 8;

        // The exact RGB is also passed to SetLayeredWindowAttributes. Keep it out of artwork.
        public static readonly Color32 ChromaKeyColor = new Color32(0, 255, 1, 255);

        [Header("Scene references")]
        [SerializeField] private Camera pixelCamera;
        [SerializeField] private Transform footAnchor;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private SpriteRenderer dogSprite;
        [SerializeField] private FloatingHeartBehaviour heartPrefab;

        private readonly List<FloatingHeartBehaviour> _floatingHearts = new List<FloatingHeartBehaviour>();
        private PetSettingsStore _settings;
        private BackendPetClientBehaviour _backend;
        private WindowsOverlayBridge _overlay;
        private Action _statusRequested;
        private RectInt _visibleDogRect;
        private bool[] _opaquePixels;
        private int _spriteWidth;
        private int _spriteHeight;
        private Vector2 _visibleDogSize;
        private float _baseDogScale = 1.0f;
        private float _dogScale = 1.0f;
        private float _walkX;
        private float _direction = 1.0f;
        private float _stepPhase;
        private float _layoutTimer;
        private bool _initialized;

        public event Action StatusRequested;

        public bool IsInitialized => _initialized;
        public float VisibleDogHeight => _visibleDogSize.y;

        public void Initialize(
            PetSettingsStore settings,
            BackendPetClientBehaviour backend,
            WindowsOverlayBridge overlay,
            Action statusRequested)
        {
            if (_initialized)
            {
                return;
            }

            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _statusRequested = statusRequested;
            ValidateReferences();
            ConfigureDogSprite();
            _settings.Changed += ApplySettings;
            ApplySettings();
            _overlay.RefreshOverlayLayout(VisibleDogHeight, force: true);
            ConfigurePixelCamera();
            _walkX = _overlay.OverlaySize.x * 0.5f;
            _walkX = PetMath.ClampWalkPosition(_walkX, _overlay.OverlaySize.x, _visibleDogSize.x);
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized)
            {
                return;
            }

            var delta = Time.unscaledDeltaTime;
            _layoutTimer += delta;
            if (_layoutTimer >= LayoutRefreshSeconds)
            {
                _layoutTimer = 0.0f;
                if (_overlay.RefreshOverlayLayout(VisibleDogHeight, force: false))
                {
                    ConfigurePixelCamera();
                    _walkX = PetMath.ClampWalkPosition(_walkX, _overlay.OverlaySize.x, _visibleDogSize.x);
                }
            }

            StepDog(delta);
            AnimateDog();
            TrimDestroyedHearts();
        }

        public void HandleNativeDogClick(DogMouseButton button)
        {
            if (ShouldDogPassThrough())
            {
                return;
            }

            if (button == DogMouseButton.Left)
            {
                _backend.EnqueuePetGrant(Guid.NewGuid());
                SpawnFloatingHeart();
                return;
            }

            _statusRequested?.Invoke();
            StatusRequested?.Invoke();
        }

        /// <summary>
        /// Tests a pixel in the Unity player window, using bottom-left origin.
        /// It is called by the Win32 WndProc before Windows dispatches a click.
        /// </summary>
        public bool IsOpaqueDogWindowPixel(Vector2Int windowPixel)
        {
            if (!_initialized || ShouldDogPassThrough() || _opaquePixels == null || dogSprite == null)
            {
                return false;
            }

            if (windowPixel.x < 0 || windowPixel.y < 0
                || windowPixel.x >= _overlay.OverlaySize.x || windowPixel.y >= _overlay.OverlaySize.y)
            {
                return false;
            }

            var worldPoint = new Vector3(windowPixel.x + 0.5f, windowPixel.y + 0.5f, dogSprite.transform.position.z);
            var localPoint = dogSprite.transform.InverseTransformPoint(worldPoint);
            var sprite = dogSprite.sprite;
            var pixelsPerUnit = sprite.pixelsPerUnit;
            var x = Mathf.FloorToInt(localPoint.x * pixelsPerUnit + sprite.pivot.x);
            var y = Mathf.FloorToInt(localPoint.y * pixelsPerUnit + sprite.pivot.y);
            return x >= 0 && y >= 0 && x < _spriteWidth && y < _spriteHeight && _opaquePixels[y * _spriteWidth + x];
        }

        private void ValidateReferences()
        {
            if (pixelCamera == null || footAnchor == null || visualRoot == null || dogSprite == null)
            {
                throw new InvalidOperationException(
                    "DesktopPetController needs a Camera, FootAnchor, VisualRoot, and Dog Sprite Renderer.");
            }
        }

        private void ConfigureDogSprite()
        {
            var sprite = dogSprite.sprite;
            if (sprite == null)
            {
                throw new InvalidOperationException("DesktopPetController needs Doggo.png assigned as its SpriteRenderer sprite.");
            }

            var texture = sprite.texture;
            if (!texture.isReadable)
            {
                throw new InvalidOperationException(
                    "Doggo.png must have Read/Write Enabled so transparent dog pixels can pass clicks through.");
            }

            var textureRect = sprite.textureRect;
            _spriteWidth = Mathf.RoundToInt(textureRect.width);
            _spriteHeight = Mathf.RoundToInt(textureRect.height);
            var sourcePixels = texture.GetPixels32(
                Mathf.RoundToInt(textureRect.x),
                Mathf.RoundToInt(textureRect.y),
                _spriteWidth,
                _spriteHeight);

            _visibleDogRect = PetMath.FindVisibleBounds(sourcePixels, _spriteWidth, _spriteHeight);
            if (_visibleDogRect.width == 0 || _visibleDogRect.height == 0)
            {
                _visibleDogRect = new RectInt(0, 0, _spriteWidth, _spriteHeight);
            }

            _opaquePixels = new bool[_spriteWidth * _spriteHeight];
            for (var index = 0; index < sourcePixels.Length; index++)
            {
                _opaquePixels[index] = sourcePixels[index].a > PetMath.OpaqueAlphaThreshold;
            }

            var pixelsPerUnit = sprite.pixelsPerUnit;
            var visibleCenter = new Vector2(
                _visibleDogRect.x + _visibleDogRect.width * 0.5f,
                _visibleDogRect.y + _visibleDogRect.height * 0.5f);
            var visibleBottom = _visibleDogRect.y;
            var spriteOffset = new Vector3(
                -(visibleCenter.x - sprite.pivot.x) / pixelsPerUnit,
                -(visibleBottom - sprite.pivot.y) / pixelsPerUnit,
                dogSprite.transform.localPosition.z);
            dogSprite.transform.localPosition = spriteOffset;
            dogSprite.enabled = true;

            _baseDogScale = DogHeight * pixelsPerUnit / _visibleDogRect.height;
        }

        private void ApplySettings()
        {
            _dogScale = _baseDogScale * _settings.DogScale;
            _visibleDogSize = new Vector2(_visibleDogRect.width * _dogScale, _visibleDogRect.height * _dogScale);
            var color = dogSprite.color;
            color.a = _settings.DogTransparency;
            dogSprite.color = color;

            _overlay.ApplyWindowSettings(_settings.AlwaysOnTop, ShouldDogPassThrough());
            if (_overlay.RefreshOverlayLayout(VisibleDogHeight, force: true))
            {
                ConfigurePixelCamera();
            }

            _walkX = PetMath.ClampWalkPosition(_walkX, _overlay.OverlaySize.x, _visibleDogSize.x);
        }

        private void ConfigurePixelCamera()
        {
            var size = _overlay.OverlaySize;
            if (size.x <= 0 || size.y <= 0)
            {
                return;
            }

            pixelCamera.orthographic = true;
            pixelCamera.orthographicSize = size.y * 0.5f;
            pixelCamera.transform.position = new Vector3(size.x * 0.5f, size.y * 0.5f, pixelCamera.transform.position.z);
            pixelCamera.clearFlags = CameraClearFlags.SolidColor;
            pixelCamera.backgroundColor = ChromaKeyColor;
            pixelCamera.allowHDR = false;
        }

        private void StepDog(float delta)
        {
            _stepPhase += delta * StepFrequency;
            _walkX += _direction * WalkSpeed * delta;
            var bounds = PetMath.GetWalkBounds(_overlay.OverlaySize.x, _visibleDogSize.x);
            if (_walkX <= bounds.x)
            {
                _walkX = bounds.x;
                _direction = 1.0f;
            }
            else if (_walkX >= bounds.y)
            {
                _walkX = bounds.y;
                _direction = -1.0f;
            }
        }

        private void AnimateDog()
        {
            var hop = Mathf.Abs(Mathf.Sin(_stepPhase));
            var squash = 1.0f + (1.0f - hop) * 0.07f;
            var stretch = 1.0f + hop * 0.05f;
            footAnchor.position = new Vector3(
                _walkX,
                _overlay.OverlaySize.y - GroundMargin - hop * HopHeight,
                footAnchor.position.z);
            visualRoot.localScale = new Vector3(
                _direction * _dogScale * squash,
                _dogScale * stretch,
                1.0f);
            visualRoot.localRotation = Quaternion.Euler(0.0f, 0.0f, Mathf.Sin(_stepPhase * 0.5f) * 2.0f * _direction);
        }

        private void SpawnFloatingHeart()
        {
            if (heartPrefab == null)
            {
                return;
            }

            TrimDestroyedHearts();
            while (_floatingHearts.Count >= MaxActiveHearts)
            {
                var oldest = _floatingHearts[0];
                _floatingHearts.RemoveAt(0);
                if (oldest != null)
                {
                    Destroy(oldest.gameObject);
                }
            }

            var xOffset = Mathf.Sin(_stepPhase * 1.37f) * 12.0f;
            var start = new Vector3(xOffset, _visibleDogSize.y + 16.0f, 0.0f);
            var end = start + new Vector3(0.0f, HeartRiseDistance, 0.0f);
            var heart = Instantiate(heartPrefab, footAnchor);
            heart.Initialize(start, end, HeartLifetimeSeconds);
            _floatingHearts.Add(heart);
        }

        private bool ShouldDogPassThrough()
        {
            return _settings.DogClickThrough || _settings.DogTransparency <= InvisibleDogAlphaThreshold;
        }

        private void TrimDestroyedHearts()
        {
            _floatingHearts.RemoveAll(heart => heart == null);
        }

        private void OnDestroy()
        {
            if (_settings != null)
            {
                _settings.Changed -= ApplySettings;
            }
        }
    }
}

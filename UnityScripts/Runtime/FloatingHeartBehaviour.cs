using UnityEngine;

namespace PetDaDog.Unity
{
    [DisallowMultipleComponent]
    public sealed class FloatingHeartBehaviour : MonoBehaviour
    {
        private SpriteRenderer _spriteRenderer;
        private Vector3 _startPosition;
        private Vector3 _endPosition;
        private Vector3 _startScale;
        private float _lifetime;
        private float _age;

        public void Initialize(Vector3 startPosition, Vector3 endPosition, float lifetimeSeconds)
        {
            _spriteRenderer = GetComponent<SpriteRenderer>();
            _startPosition = startPosition;
            _endPosition = endPosition;
            _startScale = transform.localScale;
            _lifetime = Mathf.Max(0.01f, lifetimeSeconds);
            transform.localPosition = _startPosition;
        }

        private void Update()
        {
            _age += Time.unscaledDeltaTime;
            var progress = Mathf.Clamp01(_age / _lifetime);
            var eased = 1.0f - Mathf.Pow(1.0f - progress, 2.0f);
            transform.localPosition = Vector3.Lerp(_startPosition, _endPosition, eased);
            transform.localScale = _startScale * Mathf.Lerp(1.0f, 1.24f, eased);

            if (_spriteRenderer != null)
            {
                var color = _spriteRenderer.color;
                color.a = 1.0f - progress;
                _spriteRenderer.color = color;
            }

            if (progress >= 1.0f)
            {
                Destroy(gameObject);
            }
        }
    }
}

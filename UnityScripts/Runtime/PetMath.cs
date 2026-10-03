using UnityEngine;

namespace PetDaDog.Unity
{
    /// <summary>
    /// Stateless pet calculations kept separate so their edge cases can be
    /// covered by Unity EditMode tests without creating a desktop window.
    /// </summary>
    public static class PetMath
    {
        public const byte OpaqueAlphaThreshold = 8; // About 0.03 on Unity's 0-255 alpha scale.

        public static Vector2 GetWalkBounds(float windowWidth, float visibleDogWidth)
        {
            var halfDogWidth = visibleDogWidth * 0.5f;
            var minX = Mathf.Max(halfDogWidth + 6.0f, 16.0f);
            return new Vector2(minX, Mathf.Max(minX, windowWidth - minX));
        }

        public static float ClampWalkPosition(float value, float windowWidth, float visibleDogWidth)
        {
            var bounds = GetWalkBounds(windowWidth, visibleDogWidth);
            return Mathf.Clamp(value, bounds.x, bounds.y);
        }

        public static RectInt FindVisibleBounds(Color32[] pixels, int width, int height)
        {
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length < width * height)
            {
                return new RectInt();
            }

            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (pixels[y * width + x].a <= OpaqueAlphaThreshold)
                    {
                        continue;
                    }

                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            return maxX < minX || maxY < minY
                ? new RectInt()
                : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
    }
}

using System;
using System.Collections.Generic;

namespace FarmerScala;

/// <summary>Place beside the news row without overlapping visible native DLC buttons.</summary>
internal static class NewsButtonPlacement
{
    internal static float NativeGap((float Left, float Bottom, float Right, float Top) news,
        IReadOnlyList<(float Left, float Bottom, float Right, float Top)> buttons, float fallback)
    {
        float nearest = float.PositiveInfinity;
        foreach (var button in buttons)
        {
            if (button.Right <= button.Left || button.Top <= button.Bottom || button.Top <= news.Bottom || button.Bottom >= news.Top) continue;
            float gap = button.Right <= news.Left ? news.Left - button.Right :
                button.Left >= news.Right ? button.Left - news.Right : -1f;
            // A native icon in another corner is not a neighboring spacing reference.
            if (gap >= 0f && gap <= Math.Max(news.Right - news.Left, button.Right - button.Left))
                nearest = Math.Min(nearest, gap);
        }
        return float.IsPositiveInfinity(nearest) ? fallback : nearest;
    }
    internal static float Offset((float Left, float Bottom, float Right, float Top) news,
        IReadOnlyList<(float Left, float Bottom, float Right, float Top)> buttons, float gap)
    {
        float width = news.Right - news.Left;
        float left = news.Left - width - gap;
        // A move only goes left, past an occupied button. Revisit earlier
        // buttons because native hierarchy order need not match visual order.
        for (int pass = 0; pass < buttons.Count; pass++)
        {
            float before = left;
            foreach (var button in buttons)
                if (button.Right > button.Left && button.Top > button.Bottom &&
                    button.Top > news.Bottom && button.Bottom < news.Top &&
                    left + width + gap > button.Left + .001f && left - gap < button.Right - .001f)
                    left = Math.Min(left, button.Left - width - gap);
            if (left == before)
                break;
        }
        return left - news.Left;
    }
}

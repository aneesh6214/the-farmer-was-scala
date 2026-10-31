using UnityEngine;
using UnityEngine.UI;

namespace FarmerScala;

internal enum OutputSeverity
{
    None, Warning, Error
}

/// <summary>Thread-safe notification state; logging can run on native simulation threads.</summary>
internal sealed class OutputAttention
{
    private readonly object gate = new object();
    private OutputSeverity severity;
    internal OutputSeverity Severity
    {
        get
        {
            lock (gate)
                return severity;
        }
    }
    internal void Notify(OutputSeverity next)
    {
        lock (gate)
            if (next > severity)
                severity = next;
    }
    internal void Acknowledge()
    {
        lock (gate)
            severity = OutputSeverity.None;
    }

    // Retain each native interaction state's saturation, brightness, opacity,
    // multiplier and transition duration; borrow only the diagnostic hue.
    internal static ColorBlock Tint(ColorBlock native, Color accent)
    {
        Color.RGBToHSV(accent, out float hue, out _, out _);
        Color Recolor(Color color)
        {
            Color.RGBToHSV(color, out _, out float saturation, out float value);
            var result = Color.HSVToRGB(hue, saturation, value);
            result.a = color.a;
            return result;
        }
        native.normalColor = Recolor(native.normalColor);
        native.highlightedColor = Recolor(native.highlightedColor);
        native.pressedColor = Recolor(native.pressedColor);
        native.selectedColor = Recolor(native.selectedColor);
        return native;
    }
}

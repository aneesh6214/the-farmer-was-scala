using System;

namespace FarmerScala;

/// <summary>A compact header, message and equal language choices.</summary>
internal readonly struct SaveLanguagePopupGeometry
{
    internal readonly struct Box
    {
        public readonly float X, Y, Width, Height;
        public float Left => X - Width / 2f;
        public float Right => X + Width / 2f;
        public float Bottom => Y - Height / 2f;
        public float Top => Y + Height / 2f;
        public Box(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    public readonly float Width, Height;
    public readonly Box Back, Header, Body, Message, Default, Native;

    public SaveLanguagePopupGeometry(float width, float height, float textHeight,
        float buttonWidth, float buttonHeight, float backWidth, float padding, float gap,
        float frameInset = 6f, float titleHeight = 56f, float closeWidth = 40f,
        float closeHeight = 40f, float closeRight = 8f, float closeTop = 8f,
        float titleLeft = 16f)
    {
        bool Positive(float value) => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        if (!Positive(width) || !Positive(height) || !Positive(textHeight) || !Positive(buttonWidth) ||
            !Positive(buttonHeight) || !Positive(backWidth) || !Positive(padding) || !Positive(gap))
            throw new InvalidOperationException("The language chooser needs positive bounds, padding and gaps.");
        Width = Math.Max(width, 2f * buttonWidth + gap + 2f * padding);
        float frame = frameInset;
        float bodyHeight = 2f * padding + buttonHeight + textHeight + gap;
        Height = titleHeight + bodyHeight + frame;
        if (!Positive(Width) || !Positive(Height))
            throw new InvalidOperationException("Invalid language chooser dimensions.");
        float optionWidth = (Width - 2f * padding - gap) / 2f;
        float bottomY = -Height / 2f + frame + padding + buttonHeight / 2f;
        float headerY = Height / 2f - titleHeight / 2f;
        Back = new Box(Width / 2f - closeRight - closeWidth / 2f,
            Height / 2f - closeTop - closeHeight / 2f, closeWidth, closeHeight);
        titleLeft += -Width / 2f;
        float titleRight = Back.Left - gap;
        Header = new Box((titleLeft + titleRight) / 2f, headerY,
            titleRight - titleLeft, titleHeight);
        float bodyTop = Height / 2f - titleHeight;
        Body = new Box(0f, bodyTop - bodyHeight / 2f, Width - 2f * frame, bodyHeight);
        Message = new Box(0f, bodyTop - padding - textHeight / 2f,
            Width - 2f * padding, textHeight);
        Default = new Box(-(optionWidth + gap) / 2f, bottomY, optionWidth, buttonHeight);
        Native = new Box((optionWidth + gap) / 2f, bottomY, optionWidth, buttonHeight);
    }
}

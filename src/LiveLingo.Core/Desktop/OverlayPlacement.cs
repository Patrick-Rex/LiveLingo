namespace LiveLingo.Core.Desktop;

/// <summary>以物理像素工作区钳制 DIP 尺寸，适用于负坐标和混合缩放。</summary>
/// <param name="X">物理横坐标。</param><param name="Y">物理纵坐标。</param><param name="Width">DIP 宽度。</param><param name="Height">DIP 高度。</param>
public readonly record struct OverlayPlacement(int X, int Y, double Width, double Height)
{
    /// <summary>约束到一个当前有效工作区；缩放仅转换一次。</summary>
    /// <param name="left">工作区像素左边。</param><param name="top">工作区像素顶部。</param><param name="pixelWidth">工作区像素宽。</param><param name="pixelHeight">工作区像素高。</param><param name="scaling">当前缩放。</param><returns>可见位置和尺寸。</returns>
    public OverlayPlacement Clamp(int left, int top, int pixelWidth, int pixelHeight, double scaling)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || !double.IsFinite(scaling) || scaling <= 0) throw new ArgumentException("无效的屏幕工作区。");
        var width = Math.Min(Width, pixelWidth / scaling); var height = Math.Min(Height, pixelHeight / scaling);
        return new(Math.Clamp(X, left, left + pixelWidth - (int)Math.Ceiling(width * scaling)),
            Math.Clamp(Y, top, top + pixelHeight - (int)Math.Ceiling(height * scaling)), width, height);
    }
}

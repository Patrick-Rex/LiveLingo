using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace LiveLingo.App.Views;

/// <summary>将主窗与浮窗的字幕阅读位置对齐到目标句末，随虚拟容器的实际高度重新计算。</summary>
internal static class SubtitleScrollPosition
{
    /// <summary>在布局完成后对齐当前目标，视口外的中间项先由虚拟面板生成。</summary>
    /// <param name="items">按播出顺序排列的字幕列表。</param><param name="scroll">列表使用的纵向滚动视口。</param>
    /// <param name="index">当前阅读目标在列表中的位置。</param>
    public static void Align(ItemsControl items, IScrollable scroll, int index)
    {
        if (index < 0 || index >= items.ItemCount || scroll.Viewport.Height <= 0) return;
        var end = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var target = end;
        if (index < items.ItemCount - 1)
        {
            if (items.ContainerFromIndex(index) is not { } pair)
            {
                items.ScrollIntoView(index);
                return;
            }
            if (items.ItemsPanelRoot is not { } panel || pair.TranslatePoint(new Point(0, pair.Bounds.Height), panel) is not { } bottom) return;
            target = Math.Clamp(bottom.Y - scroll.Viewport.Height, 0, end);
        }
        if (Math.Abs(target - scroll.Offset.Y) > 0.1) scroll.Offset = scroll.Offset.WithY(target);
    }
}

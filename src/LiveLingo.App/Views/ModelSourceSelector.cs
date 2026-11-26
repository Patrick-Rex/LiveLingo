using Avalonia.Controls;
using Avalonia.Input;

namespace LiveLingo.App.Views;

/// <summary>供来源及词库选择器使用，收起时让滚轮继续滚动设置页，点击或键盘可切换选项。</summary>
public sealed class ModelSourceSelector : ComboBox
{
    /// <summary>沿用标准下拉框的主题、模板及共享样式。</summary>
    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <summary>展开时保留弹层滚轮行为，收起时让事件冒泡至页面滚动容器。</summary>
    /// <param name="e">携带滚动方向与路由处理状态的指针事件。</param>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (IsDropDownOpen) base.OnPointerWheelChanged(e);
    }
}

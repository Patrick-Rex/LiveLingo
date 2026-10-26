using LiveLingo.Core.History;

namespace LiveLingo.App.Services;

/// <summary>隔离档案视图模型与窗口确认、剪贴板及系统保存对话框。</summary>
public interface IHistoryInteraction
{
    /// <summary>显示明确的删除范围，默认保持取消。</summary><param name="message">待确认范围。</param><param name="cancellationToken">窗口生命周期。</param><returns>用户是否确认。</returns>
    Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken);
    /// <summary>复制已冻结的文本。</summary><param name="text">完整文本。</param><param name="cancellationToken">窗口生命周期。</param><returns>复制任务。</returns>
    Task CopyAsync(string text, CancellationToken cancellationToken);
    /// <summary>选择目标并写入已冻结的文本，取消不生成文件。</summary><param name="text">完整文本。</param><param name="suggestedName">建议文件名。</param><param name="format">导出格式。</param><param name="cancellationToken">窗口生命周期。</param><returns>是否保存成功。</returns>
    Task<bool> SaveAsync(string text, string suggestedName, HistoryExportFormat format, CancellationToken cancellationToken);
}

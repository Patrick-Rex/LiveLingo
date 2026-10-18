namespace LiveLingo.Runtime.Storage;

/// <summary>为有界音频缓存提供会话专属临时存储，所有键均由实现创建。</summary>
public interface IAudioSpillStore : IDisposable
{
    /// <summary>独占写入一块 PCM，不覆盖已有文件。</summary>
    /// <param name="samples">完整有效采样。</param><returns>后续读取和删除使用的不透明键。</returns>
    string Write(ReadOnlySpan<float> samples);
    /// <summary>完整读取登记采样，长度不符必须失败。</summary>
    /// <param name="key">本存储建立的键。</param><param name="samples">大小匹配的目标。</param>
    void Read(string key, Span<float> samples);
    /// <summary>删除本存储拥有的一个块。</summary>
    /// <param name="key">本存储建立的键。</param>
    void Delete(string key);
}

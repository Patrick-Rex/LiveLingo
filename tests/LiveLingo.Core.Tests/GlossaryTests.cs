using LiveLingo.Core.Desktop;

namespace LiveLingo.Core.Tests;

/// <summary>验证日中词库的编辑格式和有界匹配顺序。</summary>
public sealed class GlossaryTests
{
    /// <summary>忽略空行及字段首尾空格，整句命中时保留译文中间的等号。</summary>
    [Fact]
    public void ExactMatchIgnoresFieldWhitespaceAndPreservesLiteralValue()
    {
        var glossary = TranslationGlossary.Parse(" 神ゲー = 神作 \r\n \r\nクリア！ = 结算=通关！\r\n");
        Assert.Equal("神作", glossary.Match("神ゲー").ExactTranslation);
        Assert.Equal("结算=通关！", glossary.Match("クリア！").ExactTranslation);
        Assert.Null(glossary.Match("クリア").ExactTranslation);
        Assert.Empty(glossary.Match("クリア").Terms);
    }

    /// <summary>重叠位置只使用较长词；原文另有独立短词时亦纳入提示，各词只出现一次。</summary>
    [Fact]
    public void OverlapsPreferLongTermsAndPreserveStandaloneMatches()
    {
        var glossary = TranslationGlossary.Parse("初見=初见\n初見プレイ=初见游玩\n推し=我推");
        Assert.Equal(["初見プレイ"], glossary.Match("初見プレイです。初見プレイ。").Terms.Select(term => term.Key));
        Assert.Equal(["初見プレイ", "初見"], glossary.Match("初見プレイ／初見").Terms.Select(term => term.Key));
    }

    /// <summary>只提示优先级最高的八种词条且匹配区分英文大小写。</summary>
    [Fact]
    public void MatchCapsTermCountAndUsesOrdinalText()
    {
        var glossary = TranslationGlossary.Parse(string.Join('\n', Enumerable.Range(0, 9).Select(i => $"A{i}=译{i}")));
        Assert.Equal(8, glossary.Match("A0A1A2A3A4A5A6A7A8").Terms.Length);
        Assert.Empty(glossary.Match("a0").Terms);
    }

    /// <summary>无效配置整体拒绝，标明出错行且不泄漏正文。</summary>
    /// <param name="text">包含空字段、重复源词或缺少分隔符的草稿。</param><param name="line">预期出错行。</param>
    [Theory, InlineData("秘密=", 1), InlineData("=秘密", 1), InlineData("\n秘密", 2), InlineData("秘密=甲\n秘密=乙", 2)]
    public void InvalidRowsReportLocation(string text, int line)
    {
        var error = Assert.Throws<ArgumentException>(() => TranslationGlossary.Parse(text));
        Assert.Contains($"第 {line} 行", error.Message);
        Assert.DoesNotContain("秘密", error.Message);
    }

    /// <summary>限制文本、行数和字段长度，全部空行可恢复为共享的空词表。</summary>
    [Fact]
    public void LimitsAreAppliedBeforeMatching()
    {
        Assert.Same(TranslationGlossary.Empty, TranslationGlossary.Parse("\r\n  "));
        Assert.Throws<ArgumentException>(() => TranslationGlossary.Parse(new string(' ', 4097)));
        Assert.Throws<ArgumentException>(() => TranslationGlossary.Parse("詞=" + new string('词', 121)));
        Assert.Throws<ArgumentException>(() => TranslationGlossary.Parse(string.Join('\n', Enumerable.Range(0, 65).Select(i => $"{i}=词"))));
    }
}

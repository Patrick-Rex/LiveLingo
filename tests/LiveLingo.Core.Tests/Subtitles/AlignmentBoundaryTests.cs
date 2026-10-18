using LiveLingo.Core.Segmentation;
using LiveLingo.Core.Transcription;

namespace LiveLingo.Core.Tests.Subtitles;

/// <summary>证明来源重叠与语义证据不能被单纯字符串去重或标点替代。</summary>
public sealed class AlignmentBoundaryTests
{
    /// <summary>增长窗口返回完整替换原文，真实重复和新增否定都保留。</summary>
    [Fact]
    public void GrowingHypothesisPreservesRepeatedSourceAndNegation()
    {
        var previous = new TranscriptionHypothesis(new(0, 64000), "はい、はい。今日中に作業が");
        var current = new TranscriptionHypothesis(new(0, 128000), "はい、はい。今日中に作業が終わりません。");
        var decision = HypothesisAlignment.Compare(previous, current);
        Assert.True(decision.Accepted);
        Assert.Equal(current, decision.Hypothesis);
        Assert.Equal(previous.Text.Length, decision.StablePrefixLength);
    }

    /// <summary>不同音频上的相同文本不得通过字符串相等被合并。</summary>
    [Fact]
    public void EqualTextWithoutAudioOverlapIsNotDeduplicated()
    {
        var decision = HypothesisAlignment.Compare(new(new(0, 16000), "確認して、確認してください。"),
            new(new(16000, 32000), "確認して、確認してください。"));
        Assert.False(decision.Accepted);
        Assert.Equal("audio_overlap_required", decision.Reason);
    }

    /// <summary>有多个可能重复位置时保留歧义，不能任选一次删除。</summary>
    [Fact]
    public void RepeatedOverlapIsExplicitlyAmbiguous()
    {
        var decision = HypothesisAlignment.Compare(new(new(0, 32000), "はいはいはいはい"),
            new(new(16000, 48000), "はいはいはいはい次に進みます"), 2);
        Assert.False(decision.Accepted);
        Assert.Equal("ambiguous_text_overlap", decision.Reason);
    }

    /// <summary>唯一且有音频交叠的长前后缀可组成同一逻辑块。</summary>
    [Fact]
    public void UniqueOrderedOverlapKeepsFullLogicalBlock()
    {
        var decision = HypothesisAlignment.Compare(new(new(0, 32000), "最初に、大学の最寄り駅の駐輪場"),
            new(new(16000, 48000), "大学の最寄り駅の駐輪場を借ります。"));
        Assert.True(decision.Accepted);
        Assert.Equal("最初に、大学の最寄り駅の駐輪場を借ります。", decision.Hypothesis!.Text);
        Assert.Equal(0, decision.Hypothesis.Range.Start);
        Assert.Equal(48000, decision.Hypothesis.Range.End);
    }

    /// <summary>真实回放中的断句修订可对齐，但合并输出采用新窗口的原始标点和旧窗口独有前文。</summary>
    [Fact]
    public void ShiftedWindowMatchesPunctuationRevisionsWithoutRewritingSource()
    {
        const string oldText = "そうなんですか。ただスタッフには人気があったので、お客様にまずは食べてみてもらいたいと。無料のドリンクをつけようとか、いろいろ意見は出たんですが、インパクトがある広告じゃなきゃ注文もしてもらえないと思いまして。それで消滅の危機、売り上げが5位以内じゃなかったらもう売りませんと広告に入れたんですね。そうなんです。";
        const string newText = "スタッフには人気があったので、お客様にまずは食べてみてもらいたいと、無料のドリンクをつけようとかいろいろ意見は出たんですが、インパクトがある広告じゃなきゃ注文もしてもらえないと思いまして、それで消滅の危機、売り上げが5位以内じゃなかったらもう売りませんと広告に入れたんですね。そうなんです。それで注文してくださる方が一気に増えたんです。";
        var decision = HypothesisAlignment.Compare(new(new(202240, 649280), oldText), new(new(229120, 709120), newText));
        Assert.True(decision.Accepted);
        Assert.Equal("そうなんですか。ただ" + newText, decision.Hypothesis!.Text);
        Assert.Equal(202240, decision.Hypothesis.Range.Start);
        Assert.Equal(709120, decision.Hypothesis.Range.End);
    }

    /// <summary>跨窗口标点辅助匹配不得吸收真实重复差异或更改数值、人名和否定。</summary>
    /// <param name="previous">旧原文。</param><param name="current">不能证明归属一致的新原文。</param>
    [Theory]
    [InlineData("前文。はい、はい、確認してください。", "はい、確認してください。続きです。")]
    [InlineData("前文。円周率は3.14です。", "円周率は314です。続きです。")]
    [InlineData("前文。中野さんは行きません。", "中田さんは行きません。続きです。")]
    [InlineData("前文。今日は現場へ行きます。", "今日は現場へ行きません。続きです。")]
    public void ShiftedWindowRejectsChangedLexicalContent(string previous, string current)
    {
        var decision = HypothesisAlignment.Compare(new(new(0, 480000), previous), new(new(16000, 496000), current));
        // 重复省略若仍存在唯一后缀匹配，旧前文必须完整保留，不能删掉真实呼叫。
        if (previous.Contains("はい、はい", StringComparison.Ordinal) && decision.Accepted)
            Assert.Equal("前文。はい、" + current, decision.Hypothesis!.Text);
        else Assert.False(decision.Accepted);
    }

    /// <summary>仅计数记号的等值写法可辅助对齐，输出必须完整采用新窗口原始写法。</summary>
    /// <param name="oldNumber">旧计数文本。</param><param name="newNumber">等值新计数文本。</param>
    [Theory]
    [InlineData("五位以内", "5位以内")]
    [InlineData("十五種類", "15種類")]
    [InlineData("二百五十枚", "250枚")]
    [InlineData("千二百三十四個", "1234個")]
    [InlineData("〇五回", "05回")]
    public void CounterSpellingChangesPreserveRawOutput(string oldNumber, string newNumber)
    {
        var current = "売り上げが" + newNumber + "じゃなかったらもう売りません。";
        var decision = HypothesisAlignment.Compare(new(new(0, 480000), "前文。売り上げが" + oldNumber + "じゃなかったら。"),
            new(new(16000, 496000), current));
        Assert.True(decision.Accepted);
        Assert.Equal("前文。" + current, decision.Hypothesis!.Text);
    }

    /// <summary>计数辅助比较不能抹除数值变化、前导零、人名或非法数字结构。</summary>
    /// <param name="oldNumber">旧文本。</param><param name="newNumber">不等价的新文本。</param>
    [Theory]
    [InlineData("五位以内", "6位以内")]
    [InlineData("〇五回", "5回")]
    [InlineData("五十嵐さん", "50嵐さん")]
    [InlineData("三・一四個", "314個")]
    [InlineData("二三十個", "230個")]
    [InlineData("十百個", "110個")]
    public void CounterComparisonRejectsChangedMeaning(string oldNumber, string newNumber)
    {
        var decision = HypothesisAlignment.Compare(new(new(0, 480000), "前文。確認したのは" + oldNumber + "です。"),
            new(new(16000, 496000), "確認したのは" + newNumber + "です。続きです。"));
        Assert.False(decision.Accepted);
    }

    /// <summary>唯一文本后缀也不能从完整数值内部开始匹配，将十五错误归属为五。</summary>
    /// <param name="number">旧窗口完整数值记号。</param>
    [Theory]
    [InlineData("15種類")]
    [InlineData("十五種類")]
    public void ShiftedWindowCannotAnchorInsideNumber(string number)
    {
        var decision = HypothesisAlignment.Compare(new(new(0, 480000), "前文。" + number + "を確認してください。"),
            new(new(16000, 496000), "5種類を確認してください。次に進みます。"));
        Assert.False(decision.Accepted);
    }

    /// <summary>表层标点变化不破坏同一内容的稳定证据，否定、字词与重复差异仍必须拒绝。</summary>
    [Fact]
    public void StabilityIgnoresOnlyPunctuationWithoutChangingSource()
    {
        Assert.True(HypothesisAlignment.HasStableContent("例、宿題を確認しますか？", "例。宿題を確認しますか。はい。", true));
        Assert.True(HypothesisAlignment.HasStableContent("はい、はい。行きません？", "はいはい。行きません。", false));
        Assert.False(HypothesisAlignment.HasStableContent("はい、はい。行きません。", "はい。行きません。", true));
        Assert.False(HypothesisAlignment.HasStableContent("今日は行きます。", "今日は行きません。", true));
        Assert.False(HypothesisAlignment.HasStableContent("中野さんです。", "中田さんです。", true));
        Assert.False(HypothesisAlignment.HasStableContent("行かない。", "行かないわけではありません。", true));
        Assert.False(HypothesisAlignment.HasStableContent("3.14です。", "314です。", false));
        Assert.False(HypothesisAlignment.HasStableContent("1、2です。", "12です。", false));
        Assert.False(HypothesisAlignment.HasStableContent("？", "？", false));
    }

    /// <summary>不完整依赖即使有标点、稳定和静音也不定稿。</summary>
    /// <param name="text">尚未闭合的日语假设。</param>
    [Theory]
    [InlineData("午前中にその作業が入ると。")]
    [InlineData("今やってる調査結果の入力が。")]
    [InlineData("行くわけでは。")]
    [InlineData("「今日は行きません。")]
    [InlineData("まず田中さん、次に中野さん、")]
    [InlineData("終わりますが。")]
    public void FluentPrefixCannotBecomeFinal(string text)
    {
        Assert.False(JapaneseBoundaryPolicy.Evaluate(text, 3, true, true).CanClose);
    }

    /// <summary>完整否定/双重否定只有同时获得稳定和右侧证据才允许闭合。</summary>
    /// <param name="text">完整否定命题。</param>
    [Theory]
    [InlineData("今日中には終わらなくなってしまいます。")]
    [InlineData("行かないわけではありません。")]
    [InlineData("確認して、確認してください。")]
    [InlineData("どのように宿題を確認しますか？")]
    [InlineData("では練習しましょう。")]
    [InlineData("今日ではありませんでしたか？")]
    public void CompletePredicateStillNeedsIndependentEvidence(string text)
    {
        Assert.False(JapaneseBoundaryPolicy.Evaluate(text, 1, true, true).CanClose);
        Assert.False(JapaneseBoundaryPolicy.Evaluate(text, 2, false, true).CanClose);
        Assert.False(JapaneseBoundaryPolicy.Evaluate(text, 2, true, false).CanClose);
        Assert.True(JapaneseBoundaryPolicy.Evaluate(text, 2, true, true).CanClose);
    }
}

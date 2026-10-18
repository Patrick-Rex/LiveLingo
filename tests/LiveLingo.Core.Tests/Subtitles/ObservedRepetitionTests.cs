using LiveLingo.Core.Transcription;

namespace LiveLingo.Core.Tests.Subtitles;

/// <summary>验证扩窗候选的已观察重复计数，明确保守拒收与不可检出范围。</summary>
public sealed class ObservedRepetitionTests
{
    /// <summary>减少已观察的长片段或有标点的短重复时拒收，新增重复及普通改写不受影响。</summary>
    /// <param name="previous">已接受原文。</param><param name="candidate">完整候选。</param>
    /// <param name="reduced">是否发现计数减少。</param>
    [Theory]
    [InlineData("この仕事は明日までに終わらないと思います。この仕事は明日までに終わらないと思います。", "この仕事は明日までに終わらないと思います。", true)]
    [InlineData("中野さん、中野さんって", "中野さんって", true)]
    [InlineData("はい、はい", "はい", true)]
    [InlineData("はい、はい", "はい、はい、はい", false)]
    [InlineData("どきどきしています", "緊張しています", false)]
    [InlineData("今日は行きます", "今日は行きません", false)]
    [InlineData("五位以内、五位以内", "5位以内。５位以内", false)]
    [InlineData("今日は中野さんが来ます。明日は中野さんが帰ります", "今日は中野さんが来ます。明日は山本さんが帰ります", false)]
    [InlineData("はい はい", "はい、はい", false)]
    [InlineData("😀さん、😀さん", "😀さん", true)]
    public void CountsObservedFragments(string previous, string candidate, bool reduced) =>
        Assert.Equal(reduced, HypothesisAlignment.HasReducedObservedRepetition(previous, candidate));

    /// <summary>此前未观察到的新重复无法由字符串回归检查发现，不能把未拒收当质量通过。</summary>
    [Fact]
    public void CannotDetectPreviouslyUnobservedOccurrence()
    {
        const string text = "これは明日までに終わらないと思います。";
        Assert.False(HypothesisAlignment.HasReducedObservedRepetition(text + text, text + text));
    }

    /// <summary>重复片段整体纠正也会触发保守拒收，实验不宣称理解语义等价。</summary>
    [Fact]
    public void LexicalCorrectionInsideRepeatedFragmentIsConservativelyRejected()
    {
        const string previous = "この作業は明日の朝までに終わらないと思います。";
        const string corrected = "この仕事は明日の朝までに終わると思います。";
        Assert.True(HypothesisAlignment.HasReducedObservedRepetition(previous + previous, corrected + corrected));
    }
}

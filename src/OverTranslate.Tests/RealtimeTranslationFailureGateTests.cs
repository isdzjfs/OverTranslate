using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeTranslationFailureGateTests
{
    [Fact]
    public void Failure_IsNotRetriedForUnchangedText_ButNewContentCanBeRequested()
    {
        var gate = new RealtimeTranslationFailureGate();
        Assert.False(gate.ShouldSkip(["first", "second"], 0));
        gate.Record(["first", "second"], 0);
        Assert.True(gate.ShouldSkip(["first", "second"], 0));
        Assert.True(gate.ShouldSkip(["first", "second"], 0));
        Assert.False(gate.ShouldSkip(["new content"], 0));
        Assert.False(gate.ShouldSkip(["first", "second"], 0));
    }

    [Fact]
    public void ExplicitResume_CanRequestTheSameTextInANewGeneration()
    {
        var gate = new RealtimeTranslationFailureGate();
        gate.Record(["first"], 0);
        Assert.False(gate.ShouldSkip(["first"], 1));
    }
}

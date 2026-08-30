using Vistara.Application.Derivatives;

namespace Vistara.UnitTests.Derivatives;

public sealed class DerivativeStateTests
{
    [Theory]
    [InlineData(DerivativeState.Missing, DerivativeState.Queued)]
    [InlineData(DerivativeState.Queued, DerivativeState.Processing)]
    [InlineData(DerivativeState.Processing, DerivativeState.Ready)]
    [InlineData(DerivativeState.Processing, DerivativeState.Failed)]
    [InlineData(DerivativeState.Failed, DerivativeState.Queued)]
    public void State_machine_allows_only_documented_generation_transitions(
        DerivativeState current,
        DerivativeState next)
    {
        Assert.True(DerivativeStateMachine.CanTransition(current, next));
    }

    [Theory]
    [InlineData(DerivativeState.Missing, DerivativeState.Ready)]
    [InlineData(DerivativeState.Queued, DerivativeState.Ready)]
    [InlineData(DerivativeState.Ready, DerivativeState.Processing)]
    [InlineData(DerivativeState.Failed, DerivativeState.Ready)]
    public void State_machine_rejects_skipped_or_mutating_ready_transitions(
        DerivativeState current,
        DerivativeState next)
    {
        Assert.False(DerivativeStateMachine.CanTransition(current, next));
    }

    [Fact]
    public void State_machine_rejects_unknown_enum_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DerivativeStateMachine.CanTransition(
                (DerivativeState)99,
                DerivativeState.Queued));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DerivativeStateMachine.CanTransition(
                DerivativeState.Queued,
                (DerivativeState)99));
    }
}

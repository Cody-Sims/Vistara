namespace Vistara.Application.Derivatives;

public enum DerivativeState
{
    Missing,
    Queued,
    Processing,
    Ready,
    Failed,
}

public static class DerivativeStateMachine
{
    public static bool CanTransition(DerivativeState current, DerivativeState next)
    {
        EnsureDefined(current, nameof(current));
        EnsureDefined(next, nameof(next));
        return (current, next) switch
        {
            (DerivativeState.Missing, DerivativeState.Queued) => true,
            (DerivativeState.Queued, DerivativeState.Processing) => true,
            (DerivativeState.Processing, DerivativeState.Ready) => true,
            (DerivativeState.Processing, DerivativeState.Failed) => true,
            (DerivativeState.Failed, DerivativeState.Queued) => true,
            _ => false,
        };
    }

    private static void EnsureDefined(DerivativeState value, string parameterName)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

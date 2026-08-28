using Vistara.Domain.Uploads;

namespace Vistara.UnitTests.Uploads;

public sealed class UploadStateMachineTests
{
    public static TheoryData<UploadState, UploadState> ValidTransitions =>
        new()
        {
            { UploadState.Pending, UploadState.UploadIssued },
            { UploadState.UploadIssued, UploadState.CommitRequested },
            { UploadState.CommitRequested, UploadState.Verifying },
            { UploadState.Verifying, UploadState.Promoting },
            { UploadState.Promoting, UploadState.Accepted },
            { UploadState.UploadIssued, UploadState.OutcomeUnknown },
            { UploadState.CommitRequested, UploadState.OutcomeUnknown },
            { UploadState.Verifying, UploadState.OutcomeUnknown },
            { UploadState.Promoting, UploadState.OutcomeUnknown },
            { UploadState.OutcomeUnknown, UploadState.Reconciling },
        };

    [Theory]
    [MemberData(nameof(ValidTransitions))]
    public void Uploads_declared_forward_transitions_are_valid(
        UploadState current,
        UploadState target)
    {
        Assert.True(UploadStateMachine.CanTransition(current, target));
    }

    [Fact]
    public void Uploads_every_pre_accept_state_can_abort_expire_or_reject()
    {
        UploadState[] preAcceptStates =
        [
            UploadState.Pending,
            UploadState.UploadIssued,
            UploadState.CommitRequested,
            UploadState.Verifying,
            UploadState.Promoting,
            UploadState.OutcomeUnknown,
            UploadState.Reconciling,
        ];

        foreach (UploadState current in preAcceptStates)
        {
            Assert.True(UploadStateMachine.CanTransition(current, UploadState.Aborted));
            Assert.True(UploadStateMachine.CanTransition(current, UploadState.Expired));
            Assert.True(UploadStateMachine.CanTransition(current, UploadState.Rejected));
        }
    }

    [Fact]
    public void Uploads_all_other_transitions_are_invalid()
    {
        HashSet<(UploadState Current, UploadState Target)> valid =
            ValidTransitions
                .Select(row => ((UploadState)row[0], (UploadState)row[1]))
                .ToHashSet();
        UploadState[] preAcceptStates =
        [
            UploadState.Pending,
            UploadState.UploadIssued,
            UploadState.CommitRequested,
            UploadState.Verifying,
            UploadState.Promoting,
            UploadState.OutcomeUnknown,
            UploadState.Reconciling,
        ];
        foreach (UploadState current in preAcceptStates)
        {
            valid.Add((current, UploadState.Aborted));
            valid.Add((current, UploadState.Expired));
            valid.Add((current, UploadState.Rejected));
        }

        foreach (UploadState current in Enum.GetValues<UploadState>())
        {
            foreach (UploadState target in Enum.GetValues<UploadState>())
            {
                Assert.Equal(
                    valid.Contains((current, target)),
                    UploadStateMachine.CanTransition(current, target));
            }
        }
    }
}

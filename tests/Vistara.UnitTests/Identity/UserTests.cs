using Vistara.Domain.Common;
using Vistara.Domain.Identity;

namespace Vistara.UnitTests.Identity;

public sealed class UserTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [Fact]
    public void Create_normalizes_email_and_initializes_active_user()
    {
        UserId id = new(Guid.CreateVersion7(CreatedAt));

        Result<User> result = User.Create(id, " Alice@Example.COM ", " Alice ", CreatedAt);

        Assert.True(result.TryGetValue(out User? user));
        Assert.Equal("alice@example.com", user.Email.Value);
        Assert.Equal("Alice", user.DisplayName);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(CreatedAt, user.CreatedAt);
        Assert.Equal(CreatedAt, user.UpdatedAt);
        Assert.Equal(1, user.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-at.example.com")]
    [InlineData("@example.com")]
    [InlineData("alice@")]
    public void Create_rejects_invalid_email(string email)
    {
        Result<User> result = User.Create(
            new UserId(Guid.CreateVersion7(CreatedAt)),
            email,
            "Alice",
            CreatedAt);

        Assert.Equal("identity.invalid_email", result.Error?.Code);
    }

    [Fact]
    public void Local_identity_login_is_normalized_and_duplicates_are_rejected()
    {
        User user = CreateUser();
        LocalIdentityId firstId = new(Guid.CreateVersion7(CreatedAt.AddMilliseconds(1)));
        LocalIdentityId secondId = new(Guid.CreateVersion7(CreatedAt.AddMilliseconds(2)));

        Assert.True(user.LinkLocalIdentity(firstId, " Alice@Example.com ", CreatedAt.AddMinutes(1)).IsSuccess);
        Assert.Equal("alice@example.com", Assert.Single(user.LocalIdentities).Login.Value);

        Result duplicate = user.LinkLocalIdentity(
            secondId,
            "ALICE@example.COM",
            CreatedAt.AddMinutes(2));

        Assert.Equal("identity.local_identity_exists", duplicate.Error?.Code);
        Assert.Single(user.LocalIdentities);
        Assert.Equal(2, user.Version);
    }

    [Fact]
    public void External_identity_normalizes_issuer_but_preserves_subject_case()
    {
        User user = CreateUser();
        ExternalIdentityId firstId = new(Guid.CreateVersion7(CreatedAt.AddMilliseconds(1)));
        ExternalIdentityId secondId = new(Guid.CreateVersion7(CreatedAt.AddMilliseconds(2)));

        Assert.True(user.LinkExternalIdentity(
            firstId,
            " HTTPS://Login.Example.COM/ ",
            " Subject-A ",
            CreatedAt.AddMinutes(1)).IsSuccess);

        ExternalIdentityLink link = Assert.Single(user.ExternalIdentities);
        Assert.Equal("https://login.example.com", link.Issuer.Value);
        Assert.Equal("Subject-A", link.Subject);

        Result duplicate = user.LinkExternalIdentity(
            secondId,
            "https://login.example.com",
            "Subject-A",
            CreatedAt.AddMinutes(2));
        Assert.Equal("identity.external_identity_exists", duplicate.Error?.Code);

        Assert.True(user.LinkExternalIdentity(
            secondId,
            "https://login.example.com",
            "subject-a",
            CreatedAt.AddMinutes(2)).IsSuccess);
        Assert.Equal(2, user.ExternalIdentities.Count);
    }

    [Fact]
    public void User_status_transitions_are_versioned_and_disabled_is_terminal()
    {
        User user = CreateUser();

        Assert.True(user.Suspend(CreatedAt.AddMinutes(1)).IsSuccess);
        Assert.Equal(UserStatus.Suspended, user.Status);

        Result duplicate = user.Suspend(CreatedAt.AddMinutes(2));
        Assert.Equal("identity.status_unchanged", duplicate.Error?.Code);

        Assert.True(user.Activate(CreatedAt.AddMinutes(2)).IsSuccess);
        Assert.True(user.Disable(CreatedAt.AddMinutes(3)).IsSuccess);
        Result terminal = user.Activate(CreatedAt.AddMinutes(4));

        Assert.Equal("identity.invalid_status_transition", terminal.Error?.Code);
        Assert.Equal(UserStatus.Disabled, user.Status);
        Assert.Equal(4, user.Version);
    }

    private static User CreateUser()
    {
        Result<User> result = User.Create(
            new UserId(Guid.CreateVersion7(CreatedAt)),
            "alice@example.com",
            "Alice",
            CreatedAt);
        Assert.True(result.TryGetValue(out User? user));
        return user;
    }
}

using Vistara.Domain.Identity;

namespace Vistara.Application.Identity;

public interface IAuthSessionRepository
{
    ValueTask<AuthSession?> FindByDigestAsync(
        SessionDigest digest,
        CancellationToken cancellationToken);

    ValueTask AddAsync(AuthSession session, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        AuthSession session,
        long expectedVersion,
        CancellationToken cancellationToken);
}

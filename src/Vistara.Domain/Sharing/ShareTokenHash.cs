using Vistara.Domain.Common;

namespace Vistara.Domain.Sharing;

public sealed record ShareTokenHash
{
    private ShareTokenHash(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<ShareTokenHash> FromHex(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            return Result.Failure<ShareTokenHash>(SharingErrors.TokenHashInvalid());
        }

        return Result.Success(new ShareTokenHash(value.ToLowerInvariant()));
    }

    public override string ToString() => "[hashed share token]";
}

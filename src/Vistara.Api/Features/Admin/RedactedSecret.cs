using System.Security.Cryptography;
using System.Text;

namespace Vistara.Api.Features.Admin;

/// <summary>
/// Holds a submitted credential for the lifetime of one request. The value is
/// never exposed by <see cref="ToString"/>, so it cannot leak through string
/// interpolation, structured logging, activity tags, exception messages, or a
/// serialized DTO. Disposal zeroes the backing buffer.
/// </summary>
public sealed class RedactedSecret : IDisposable
{
    public const string Placeholder = "[REDACTED]";

    private readonly byte[] _value;
    private bool _disposed;

    private RedactedSecret(byte[] value)
    {
        _value = value;
    }

    public int Length => _value.Length;

    public static RedactedSecret? From(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : new RedactedSecret(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Materializes the value for the single purpose of constructing a
    /// provider client. Callers must not retain, log, or copy the result.
    /// </summary>
    public string Reveal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Encoding.UTF8.GetString(_value);
    }

    /// <summary>Always the placeholder; the value can never be printed.</summary>
    public override string ToString() => Placeholder;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_value);
        _disposed = true;
    }
}

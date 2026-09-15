namespace Cryptex.Core.Interfaces;

/// <summary>
/// The single exception type every envelope-decryption failure produces.
/// </summary>
/// <remarks>
/// <para>
/// This type is deliberately <b>opaque</b>: a wrong key, a corrupt/tampered ciphertext, a bad
/// padding block, a failed MAC check and a truncated blob all surface as this exact exception with
/// this exact message. Callers - and therefore API responses - must not be able to tell those cases
/// apart, because a distinguishable padding failure turns this service into a
/// <see href="https://en.wikipedia.org/wiki/Padding_oracle_attack">padding oracle</see> that lets an
/// attacker who can submit chosen blobs decrypt real documents byte by byte.
/// </para>
/// <para>
/// Implementations log the specific internal reason at <c>Debug</c> level, server-side only.
/// </para>
/// </remarks>
public sealed class EnvelopeDecryptionException : Exception
{
    /// <summary>The one and only message any envelope failure is allowed to carry.</summary>
    public const string UniformMessage = "Unable to decrypt envelope.";

    public EnvelopeDecryptionException() : base(UniformMessage) { }

    /// <summary>
    /// Creates the exception with the uniform message. <paramref name="message"/> is ignored on
    /// purpose so that no call site can accidentally leak a distinguishing reason to a caller.
    /// </summary>
    public EnvelopeDecryptionException(string? message) : base(UniformMessage) { }

    /// <summary>
    /// Creates the exception with the uniform message. The inner exception is retained for
    /// server-side logging only and must never be surfaced in an API response.
    /// </summary>
    public EnvelopeDecryptionException(string? message, Exception? innerException)
        : base(UniformMessage, innerException) { }
}

/// <summary>
/// Decrypts an opaque "envelope" blob - a source file that the customer's own code encrypted in
/// bulk, byte-for-byte, before it ever reached this service - back into the original file bytes.
/// </summary>
/// <remarks>
/// This is distinct from both password-protected Office documents (MS-OFFCRYPTO, handled by
/// <c>OfficeCryptoDecryptor</c>) and from at-rest encryption of data this service itself persists
/// (<c>IDocumentCipher</c>). See the README section "Three distinct kinds of encryption".
/// </remarks>
public interface IEnvelopeDecryptor
{
    /// <summary>
    /// Decrypts <paramref name="envelope"/> into the original plaintext file bytes.
    /// </summary>
    /// <param name="envelope">The full encrypted blob as produced by the customer's encryptor.</param>
    /// <param name="keyId">
    /// Optional key identifier, for corpora encrypted under rotating keys. When null or empty the
    /// configured default key id is used.
    /// </param>
    /// <exception cref="EnvelopeDecryptionException">
    /// Thrown for <i>every</i> failure mode, with an identical message in all cases. Do not attempt
    /// to distinguish causes from it - see the remarks on <see cref="EnvelopeDecryptionException"/>.
    /// </exception>
    byte[] Decrypt(ReadOnlySpan<byte> envelope, string? keyId = null);
}

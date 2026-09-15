namespace Cryptex.Security;

/// <summary>Thrown when an OOXML file is encrypted with a scheme/version this decryptor cannot handle.</summary>
public sealed class UnsupportedOfficeEncryptionException : Exception
{
    public UnsupportedOfficeEncryptionException(string message) : base(message) { }
}

/// <summary>Thrown when the supplied password does not match the document's password verifier.</summary>
public sealed class InvalidDocumentPasswordException : Exception
{
    public InvalidDocumentPasswordException(string message) : base(message) { }
}

internal enum OfficeEncryptionScheme
{
    Standard,
    Agile
}

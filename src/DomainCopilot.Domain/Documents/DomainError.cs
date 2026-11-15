namespace DomainCopilot.Domain.Documents;

/// <summary>Base exception for errors understood by the domain and application.</summary>
public class DomainError : Exception
{
    public DomainError() : base(string.Empty) { }

    public DomainError(string? message) : base(message ?? string.Empty) { }

    public DomainError(string? message, Exception? innerException)
        : base(message ?? string.Empty, innerException) { }
}

/// <summary>A source document could not be read or had an unexpected structure.</summary>
public class ExtractionError : DomainError
{
    public ExtractionError() : base() { }

    public ExtractionError(string? message) : base(message) { }

    public ExtractionError(string? message, Exception? innerException)
        : base(message, innerException) { }
}

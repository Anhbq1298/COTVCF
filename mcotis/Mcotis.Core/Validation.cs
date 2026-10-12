namespace Mcotis.Core;

public enum ValidationSeverity { Error, Warning }

/// <summary>One finding about a document. <paramref name="Code"/> is stable (tests and callers match on it);
/// <paramref name="Message"/> is for the user and names the object.</summary>
public sealed record ValidationIssue(ValidationSeverity Severity, string Code, ObjectKind Kind, ObjectId ObjectId, string Message)
{
    public override string ToString() => $"{Severity}: {Message}";
}

public enum DocumentError
{
    /// <summary>A name is empty, blank or holds control characters.</summary>
    InvalidName,
    /// <summary>Another object in the same name scope already has the name.</summary>
    DuplicateName,
    DuplicateId,
    NotFound,
    /// <summary>The object cannot be removed while something else refers to it.</summary>
    InUse,
    /// <summary>The edit would leave the document invalid; <see cref="DocumentException.Issues"/> lists why.</summary>
    Invalid,
}

/// <summary>An edit was refused. The document the edit started from is unchanged.</summary>
public sealed class DocumentException(DocumentError error, string message, IReadOnlyList<ValidationIssue>? issues = null)
    : Exception(message)
{
    public DocumentError Error { get; } = error;
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues ?? [];
}

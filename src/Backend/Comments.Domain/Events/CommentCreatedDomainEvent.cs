namespace Comments.Domain.Events;

/// <summary>
/// Domain fact: a comment (or answer) was persisted. Carries the aggregate so application
/// services can project it into an integration event without re-reading the database.
/// Pure domain type — no infrastructure dependencies.
/// </summary>
public sealed record CommentCreatedDomainEvent(
    Comment Comment,
    DateTime OccurredAt);

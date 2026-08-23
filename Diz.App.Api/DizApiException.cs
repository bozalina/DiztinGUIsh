namespace Diz.App.Api;

public enum DizApiErrorKind { NoProjectLoaded, NotFound, InvalidArgument, Conflict }

public class DizApiException(DizApiErrorKind kind, string message)
    : Exception(message)
{
    public DizApiErrorKind Kind { get; } = kind;
}

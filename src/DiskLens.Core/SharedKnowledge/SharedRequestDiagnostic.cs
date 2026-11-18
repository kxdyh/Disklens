namespace DiskLens.Core.SharedKnowledge;

/// <summary>Closed, content-free request diagnostics. No addresses, identities or request contents.</summary>
public sealed record SharedRequestDiagnostic(
    string Operation,
    string Phase,
    string Outcome,
    int? HttpStatus,
    TimeSpan Elapsed,
    TimeSpan Timeout);

/// <summary>Identifies a request's own deadline separately from caller or transport cancellation.</summary>
public sealed class SharedRequestTimeoutException : OperationCanceledException
{
    public string Operation { get; }
    public string Phase { get; }
    public TimeSpan Timeout { get; }
    public TimeSpan Elapsed { get; }

    internal SharedRequestTimeoutException(string operation, string phase, TimeSpan timeout,
        TimeSpan elapsed, OperationCanceledException innerException, CancellationToken cancellationToken)
        : base("共享经验请求超时。", innerException, cancellationToken)
    {
        Operation = operation;
        Phase = phase;
        Timeout = timeout;
        Elapsed = elapsed;
    }
}

namespace Ledger.Worker.State;

internal sealed class WorkerState
{
    public DateTimeOffset? PublisherLastSuccessAt { get; set; }

    public DateTimeOffset? ConsumerLastSuccessAt { get; set; }

    public string? FatalError { get; set; }
}

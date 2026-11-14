namespace Ledger.Application;

public interface IPostMovementUseCase
{
    Task<MovementResult> PostAsync(PostMovementCommand command, CancellationToken cancellationToken);
}

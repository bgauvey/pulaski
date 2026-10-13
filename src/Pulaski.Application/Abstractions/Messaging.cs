namespace Pulaski.Application.Abstractions;

/// <summary>A request that changes state. Handled by exactly one <see cref="ICommandHandler{TCommand,TResult}"/>.</summary>
public interface ICommand<TResult>;

/// <summary>A request that reads state and changes nothing.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

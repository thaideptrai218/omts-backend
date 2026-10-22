namespace OMTS.Application.Common.Results;

/// <summary>Outcome of a use case: a value, or an <see cref="AppError"/> to show the user.</summary>
public sealed class Result<T>
{
    private Result(T? value, AppError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public AppError? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(AppError error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(AppError error) => Failure(error);
}

/// <summary>Result of a use case that returns nothing but may fail.</summary>
public sealed record Done
{
    public static readonly Done Value = new();
}

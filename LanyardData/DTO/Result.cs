namespace LanyardData.DTO;

public record Result<T>
{
    public bool IsSuccess { get; init; }
    public bool Success => IsSuccess;
    public T? Data { get; init; }
    public string? Error { get; init; }

    public static Result<T> Ok(T data) => new() { IsSuccess = true, Data = data };
    
    public static Result<T> Fail(string error) => new() { IsSuccess = false, Error = error };
}

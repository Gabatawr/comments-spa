using Comments.Application.Dtos;

namespace Comments.Application.Validation;

/// <summary>
/// Accumulates field errors in the exact shape required by docs/API-v2.md §0:
/// <c>{ "title": "Validation failed", "status": 400, "errors": { "field": ["..."] } }</c>.
/// </summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsValid => _errors.Count == 0;

    public IReadOnlyDictionary<string, List<string>> Errors => _errors;

    public void Add(string key, string message)
    {
        if (!_errors.TryGetValue(key, out var list))
        {
            list = new List<string>();
            _errors[key] = list;
        }

        if (!list.Contains(message))
        {
            list.Add(message);
        }
    }

    public void AddRange(string key, IEnumerable<string> messages)
    {
        foreach (var message in messages)
        {
            Add(key, message);
        }
    }

    public bool HasErrors(string key) => _errors.ContainsKey(key);

    public ErrorResponse ToResponse(int status = 400) => new()
    {
        Title = "Validation failed",
        Status = status,
        Errors = _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal),
    };
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;

namespace Pulaski.Application.Security;

/// <summary>
/// A user's own view settings (#131, D-131): <c>home</c> (a page), <c>favorites</c> (pages) and <c>filters:&lt;page&gt;</c> (what a list page
/// opens with). Kept per user so they follow them anywhere; personal view state, so no events. Each value is JSON, at most 16 KB.
/// </summary>
public static partial class UserSettingKeys
{
    public const string Home = "home";
    public const string Favorites = "favorites";
    public const int MaxBytes = 16384;

    public static string Filters(string page) => $"filters:{page}";

    public static bool IsValid(string? key) => key is not null && Pattern().IsMatch(key);

    [GeneratedRegex("^(home|favorites|filters:[a-z0-9/_-]{1,80})$")]
    private static partial Regex Pattern();
}

public interface IUserSettingStore
{
    /// <summary>The user's settings that aren't cleared, as JSON by key.</summary>
    Task<IReadOnlyDictionary<string, string>> ListAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IUserSettingRepository
{
    /// <summary>Sets one setting (JSON <c>null</c> clears it); false when there is no such user.</summary>
    Task<bool> SetAsync(Guid userId, string key, string json, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Sets one of the caller's own view settings (#131); a null value clears it.</summary>
public sealed record SetUserSettingCommand(Guid UserId, string? Key, JsonElement? Value) : ICommand<VersionResponse>;

internal sealed class SetUserSettingHandler(IUnitOfWork unitOfWork, IUserSettingRepository settings, TimeProvider time) : ICommandHandler<SetUserSettingCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetUserSettingCommand command, CancellationToken cancellationToken)
    {
        if (!UserSettingKeys.IsValid(command.Key))
        {
            throw ValidationException.Field("Key", $"That isn't a setting Pulaski keeps.");
        }

        var json = command.Value is { ValueKind: not JsonValueKind.Undefined } value ? value.GetRawText() : "null";
        if (System.Text.Encoding.UTF8.GetByteCount(json) > UserSettingKeys.MaxBytes)
        {
            throw ValidationException.Field("Value", $"That setting is too large to keep.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await settings.SetAsync(command.UserId, command.Key!, json, time.GetUtcNow(), cancellationToken))
        {
            throw new NotFoundException($"That user wasn't found.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

using System.Security.Cryptography;

namespace Pulaski.Domain.Labor;

/// <summary>How a person proves who they are at a terminal (D-049). Values match <c>sec.terminal.auth_mode</c>.</summary>
public static class TerminalAuthModes
{
    /// <summary>Their badge alone.</summary>
    public const string Badge = "badge";

    /// <summary>Their badge and a PIN, for terminals anyone could pick a dropped badge up at.</summary>
    public const string BadgePlusPin = "badge_plus_pin";

    public static IReadOnlyList<string> All { get; } = [Badge, BadgePlusPin];
}

/// <summary>
/// Rules for badges, PINs and terminal secrets (D-049). Secrets and enrolment codes are random and only their SHA-256 is
/// kept; badge values are hashed with a server key (they are short and guessable) and PINs like passwords.
/// </summary>
public static class TerminalRules
{
    public const int MaxFailedPins = 5;
    public static readonly TimeSpan PinLockout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EnrolmentCodeLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long a badge scan identifies its person at the terminal: long enough to choose what to do.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <returns>What is wrong with a badge value as scanned; null when it can be used.</returns>
    public static string? BadgeProblem(string value) =>
        value.Length is < 4 or > 64 ? "A badge value is 4 to 64 characters." : null;

    /// <returns>What is wrong with a PIN; null when it can be used.</returns>
    public static string? PinProblem(string pin) =>
        pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit) ? "A PIN is 4 to 8 digits." : null;

    /// <summary>The badge value as stored for matching: trimmed, upper case, so readers that differ in case agree.</summary>
    public static string NormalizeBadge(string value) => value.Trim().ToUpperInvariant();

    /// <summary>A one-time enrolment code such as <c>K7QM2-XW9PD</c>: ten characters with no 0/O or 1/I to misread.</summary>
    public static string NewEnrolmentCode()
    {
        var chars = Enumerable.Range(0, 10).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray();
        return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
    }

    /// <summary>A code as typed: upper case, without spaces or the dash.</summary>
    public static string NormalizeCode(string code) => new([.. code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]);

    /// <summary>A device secret: 32 random bytes, URL-safe.</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Sha256(string value) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>The lockout after this many wrong PINs in a row, or null while there are tries left.</summary>
    public static DateTimeOffset? LockAfter(int failedPins, DateTimeOffset now) => failedPins >= MaxFailedPins ? now + PinLockout : null;
}

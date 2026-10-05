using System.Globalization;
using System.Text;

namespace GenHTTP.Modules.Git;

/// <summary>
/// Identifies who authored or committed a change and when.
/// </summary>
/// <remarks>
/// Git stores time stamps with a precision of seconds, so fractions
/// of a second are dropped when a commit is created.
/// </remarks>
public sealed class GitSignature
{

    #region Get-/Setters

    /// <summary>
    /// The name of the person (e.g. "Jane Doe").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The email address of the person (may be empty).
    /// </summary>
    public string Email { get; }

    /// <summary>
    /// The point in time, including the time zone offset of the person.
    /// </summary>
    public DateTimeOffset When { get; }

    #endregion

    #region Initialization

    /// <summary>
    /// Creates a new signature.
    /// </summary>
    /// <param name="name">The name of the person (must not be empty)</param>
    /// <param name="email">The email address of the person</param>
    /// <param name="when">The point in time</param>
    public GitSignature(string name, string email, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(email);

        name = name.Trim();
        email = email.Trim();

        if (name.Length == 0)
        {
            throw new ArgumentException("The name of a signature must not be empty", nameof(name));
        }

        Validate(name, nameof(name));
        Validate(email, nameof(email));

        Name = name;
        Email = email;
        When = new DateTimeOffset(when.Ticks - when.Ticks % TimeSpan.TicksPerSecond, when.Offset);
    }

    private static void Validate(string value, string parameter)
    {
        if (value.AsSpan().IndexOfAny("<>\n\r\0") >= 0)
        {
            throw new ArgumentException("Names and email addresses must not contain angle brackets, line breaks or NUL characters", parameter);
        }
    }

    #endregion

    #region Functionality

    /// <summary>
    /// Formats this signature the way git stores it in a commit
    /// (e.g. "Jane Doe &lt;jane@example.com&gt; 1700000000 +0100").
    /// </summary>
    public override string ToString()
    {
        var offset = When.Offset;

        var sign = offset < TimeSpan.Zero ? '-' : '+';

        offset = offset.Duration();

        return string.Create(CultureInfo.InvariantCulture, $"{Name} <{Email}> {When.ToUnixTimeSeconds()} {sign}{offset.Hours:00}{offset.Minutes:00}");
    }

    /// <summary>
    /// Parses a signature as stored by git.
    /// </summary>
    /// <exception cref="FormatException">Thrown if the signature is malformed</exception>
    internal static GitSignature Parse(ReadOnlySpan<byte> value)
    {
        var open = value.IndexOf((byte)'<');
        var close = value.LastIndexOf((byte)'>');

        if (open < 0 || close < open)
        {
            throw new FormatException("Malformed signature: missing email address");
        }

        var name = Encoding.UTF8.GetString(value[..open]).Trim();
        var email = Encoding.UTF8.GetString(value[(open + 1)..close]).Trim();

        var date = Encoding.ASCII.GetString(value[(close + 1)..]).Trim();

        var parts = date.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (name.Length == 0)
        {
            throw new FormatException("Malformed signature: missing name");
        }

        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new FormatException("Malformed signature: invalid date");
        }

        var zone = parts[1];

        if (zone.Length != 5 || zone[0] is not ('+' or '-') || !int.TryParse(zone.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var hhmm))
        {
            throw new FormatException("Malformed signature: invalid time zone");
        }

        var offset = new TimeSpan(hhmm / 100, hhmm % 100, 0);

        if (zone[0] == '-')
        {
            offset = -offset;
        }

        // git accepts values .NET cannot represent, which are rare enough
        // to be shown in UTC without affecting the stored commit
        if (offset.Duration() > TimeSpan.FromHours(14))
        {
            offset = TimeSpan.Zero;
        }

        seconds = Math.Clamp(seconds, DateTimeOffset.MinValue.ToUnixTimeSeconds() + 86400, DateTimeOffset.MaxValue.ToUnixTimeSeconds() - 86400);

        var when = DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(offset);

        try
        {
            return new GitSignature(name, email, when);
        }
        catch (ArgumentException e)
        {
            throw new FormatException($"Malformed signature: {e.Message}", e);
        }
    }

    #endregion

}

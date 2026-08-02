using System.Reflection;

namespace MarketPulse.Application.Authentication;

/// <summary>
/// NIST SP 800-63B: length plus a blocklist, no composition rules, no forced rotation.
/// Composition rules ("one uppercase, one symbol") are what the guidance advises
/// against — they push users toward predictable mutations like `Password1!`.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    private static readonly HashSet<string> Blocklist = LoadBlocklist();

    public static int BlocklistSize => Blocklist.Count;

    public static bool IsAcceptable(string password) =>
        !string.IsNullOrWhiteSpace(password)
        && password.Length >= MinimumLength
        && !Blocklist.Contains(password);

    private static HashSet<string> LoadBlocklist()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("CommonPasswords.txt", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                set.Add(line.Trim());
            }
        }

        return set;
    }
}

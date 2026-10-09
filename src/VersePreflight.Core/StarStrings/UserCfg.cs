using System.Text;
using System.Text.RegularExpressions;

namespace VersePreflight.Core.StarStrings;

public enum UserCfgChange { AlreadySet, Added, Replaced }

/// <summary>Edits the game's user.cfg one setting at a time, preserving every other line.</summary>
public static partial class UserCfg
{
    public static UserCfgChange EnsureLanguage(string userCfgPath, string language = "english")
    {
        var wanted = $"g_language = {language}";
        var text = File.Exists(userCfgPath) ? File.ReadAllText(userCfgPath) : "";
        var newline = text.Contains("\r\n", StringComparison.Ordinal) || text.Length == 0 ? "\r\n" : "\n";
        var lines = text.Length == 0 ? [] : text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1); // trailing newline

        var index = lines.FindIndex(l => LanguageLine().IsMatch(l));
        UserCfgChange change;
        if (index >= 0)
        {
            var value = LanguageLine().Match(lines[index]).Groups["value"].Value.Trim();
            if (string.Equals(value, language, StringComparison.OrdinalIgnoreCase)) return UserCfgChange.AlreadySet;
            lines[index] = wanted;
            change = UserCfgChange.Replaced;
        }
        else
        {
            lines.Add(wanted);
            change = UserCfgChange.Added;
        }

        File.WriteAllText(userCfgPath, string.Join(newline, lines) + newline, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return change;
    }

    [GeneratedRegex(@"^\s*g_language\s*=(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageLine();
}

using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZLauncher;

/// <summary>
/// A dotenv file edited in place: KEY=VALUE lines become editable entries, every other line
/// (comments, blanks, anything unparsed) is written back exactly as it was, in the same order.
/// Values are never logged.
/// </summary>
sealed partial class EnvFile
{
    public sealed class Entry
    {
        public required string Key { get; init; }
        public string Value { get; set; } = "";
        internal string OriginalValue = "";
        internal string Prefix = "";   // "KEY=" or "export KEY=" exactly as written
        internal char Quote;           // '"' or '\'' if the value was quoted, else '\0'
        internal int Line = -1;        // index into _lines, -1 for a new entry
        public bool IsSecret => SecretName().IsMatch(Key);
        public bool Changed => Value != OriginalValue;
    }

    readonly List<string> _lines = new();
    string _newline = "\n";
    bool _trailingNewline = true;
    public List<Entry> Entries { get; } = new();

    public static EnvFile Load(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false));
        var env = new EnvFile
        {
            _newline = text.Contains("\r\n") ? "\r\n" : "\n",
            _trailingNewline = text.Length == 0 || text.EndsWith('\n'),
        };
        var lines = text.Split('\n');
        int count = env._trailingNewline && text.Length > 0 ? lines.Length - 1 : lines.Length;
        for (int i = 0; i < count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            env._lines.Add(line);
            var m = Assignment().Match(line);
            if (!m.Success) continue;
            string raw = m.Groups["value"].Value;
            char quote = raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0] ? raw[0] : '\0';
            string value = quote != '\0' ? raw[1..^1] : raw;
            env.Entries.Add(new Entry { Key = m.Groups["key"].Value, Value = value, OriginalValue = value, Prefix = m.Groups["prefix"].Value, Quote = quote, Line = i });
        }
        return env;
    }

    public Entry Add(string key) { var e = new Entry { Key = key, Prefix = key + "=" }; Entries.Add(e); return e; }

    public bool HasChanges => Entries.Any(e => e.Changed || e.Line < 0);

    /// <summary>Rewrites only the lines whose value changed (plus new keys at the end). Overwrites in place, so the file keeps its owner and mode.</summary>
    public void Save(string path)
    {
        var lines = new List<string>(_lines);
        foreach (var e in Entries.Where(e => e.Line >= 0 && e.Changed)) lines[e.Line] = Format(e);
        foreach (var e in Entries.Where(e => e.Line < 0)) lines.Add(Format(e));
        var text = string.Join(_newline, lines) + (_trailingNewline && lines.Count > 0 ? _newline : "");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        foreach (var e in Entries) e.OriginalValue = e.Value;
    }

    static string Format(Entry e)
    {
        char q = e.Quote;
        // Quote values that would otherwise change meaning (leading/trailing spaces, '#').
        if (q == '\0' && e.Value.Length > 0 && (e.Value != e.Value.Trim() || e.Value.Contains('#'))) q = '"';
        if (q == '\'' && e.Value.Contains('\'')) q = '"';
        if (q == '"' && e.Value.Contains('"')) q = '\'';
        return q == '\0' ? e.Prefix + e.Value : $"{e.Prefix}{q}{e.Value}{q}";
    }

    [GeneratedRegex(@"^(?<prefix>\s*(?:export\s+)?(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*)(?<value>.*)$")]
    private static partial Regex Assignment();

    [GeneratedRegex("SECRET|TOKEN|PASSWORD|PASSPHRASE|API_?KEY", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
}

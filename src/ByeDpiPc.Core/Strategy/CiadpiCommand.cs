using System.Text;

namespace ByeDpiPc.Core.Strategy;

public static class CiadpiCommand
{
    /// <summary>
    /// Разбивает строку аргументов как shell.
    /// Порт shellSplit из ByeByeDPI:
    /// https://github.com/romanvht/ByeByeDPI/blob/master/app/src/main/java/io/github/romanvht/byedpi/utility/ArgumentsUtils.kt
    /// </summary>
    public static List<string> Split(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quoteChar = ' ';
        bool escaping = false, quoting = false;
        int lastCloseQuote = int.MinValue;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (escaping)
            {
                current.Append(c);
                escaping = false;
            }
            else if (c == '\\' && quoting)
            {
                if (i + 1 < text.Length && text[i + 1] == quoteChar) escaping = true;
                else current.Append(c);
            }
            else if (quoting && c == quoteChar)
            {
                quoting = false;
                lastCloseQuote = i;
            }
            else if (!quoting && (c == '\'' || c == '"'))
            {
                quoting = true;
                quoteChar = c;
            }
            else if (!quoting && char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || lastCloseQuote == i - 1)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0 || lastCloseQuote == text.Length - 1)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static readonly HashSet<string> ListenOptions = new(StringComparer.Ordinal) { "-i", "--ip", "-p", "--port", "-D", "--daemon", "-w", "--pidfile", "-E", "--transparent" };
    private static readonly HashSet<string> FlagOnly = new(StringComparer.Ordinal) { "-D", "--daemon", "-E", "--transparent" };

    /// <summary>
    /// Готовый список аргументов ciadpi: слушать только 127.0.0.1:port, затем стратегия.
    /// Свои -i/-p из стратегии вырезаются, {sni} заменяется на <paramref name="fakeSni"/>.
    /// </summary>
    public static List<string> BuildArguments(string strategy, int port, string fakeSni)
    {
        var result = new List<string> { "-i", "127.0.0.1", "-p", port.ToString() };
        var tokens = Split(strategy.Replace("{sni}", string.IsNullOrWhiteSpace(fakeSni) ? "www.iana.org" : fakeSni.Trim()));
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Length == 0) continue;
            var name = t.Contains('=') && t.StartsWith("--") ? t[..t.IndexOf('=')] : t;
            if (ListenOptions.Contains(name))
            {
                // Пропускаем значение опции (если оно не было записано через «=» и опция не флаг).
                if (!FlagOnly.Contains(name) && name == t) i++;
                continue;
            }
            if (t.Length > 2 && (t.StartsWith("-i") || t.StartsWith("-p")) && t[1] != '-' && char.IsDigit(t[2]))
                continue; // слитная форма -p1080 / -i127…
            result.Add(t);
        }
        return result;
    }

    /// <summary>Аргументы для отображения пользователю.</summary>
    public static string Join(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
}

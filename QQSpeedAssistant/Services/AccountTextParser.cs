using QQSpeedAssistant.Models;

namespace QQSpeedAssistant.Services;

/// <summary>账号文本解析结果。</summary>
public sealed record AccountParseResult(IReadOnlyList<AccountEntry> Accounts, IReadOnlyList<string> Errors);

/// <summary>
/// 解析批量账号文本，格式为 <c>账号----密码----区服</c>，每行一个账号。
/// 兼容制表符与逗号分隔，忽略空行与 # 开头的注释行。
/// </summary>
public static class AccountTextParser
{
    /// <summary>解析文本。</summary>
    public static AccountParseResult Parse(string? text)
    {
        var accounts = new List<AccountEntry>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AccountParseResult(accounts, errors);
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = SplitLine(line);
            if (parts.Length < 2)
            {
                errors.Add($"第 {index + 1} 行格式不正确：{line}");
                continue;
            }

            var account = parts[0];
            var password = parts[1];
            var server = parts.Length > 2 ? parts[2] : "";
            if (account.Length == 0 || password.Length == 0)
            {
                errors.Add($"第 {index + 1} 行账号或密码为空：{line}");
                continue;
            }

            accounts.Add(new AccountEntry
            {
                Account = account,
                Password = password,
                Server = server,
                Enabled = true
            });
        }

        return new AccountParseResult(accounts, errors);
    }

    /// <summary>按优先级拆分一行为若干字段。</summary>
    private static string[] SplitLine(string line)
    {
        if (line.Contains("----", StringComparison.Ordinal))
        {
            return line.Split("----", StringSplitOptions.None).Select(p => p.Trim()).ToArray();
        }

        if (line.Contains('\t'))
        {
            return line.Split('\t', StringSplitOptions.None).Select(p => p.Trim()).ToArray();
        }

        if (line.Contains(',', StringComparison.Ordinal) || line.Contains('，'))
        {
            return line.Replace('，', ',').Split(',', StringSplitOptions.None).Select(p => p.Trim()).ToArray();
        }

        return [line];
    }
}

using System.Text.RegularExpressions;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Engine;

/// <summary>フォアグラウンドのアプリの情報。</summary>
public sealed record ForegroundInfo(string ExeName, string ExePath, string Title, int ProcessId)
{
    public static readonly ForegroundInfo Empty = new("", "", "", 0);
}

/// <summary>
/// アプリ → プロファイルの選択。ルールを上から評価し、最初に一致したものを使う。一致しなければデフォルト。
/// 1つのルールに複数の条件があるときは、全て一致する必要がある。
/// </summary>
public static class ProfileResolver
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static string Resolve(ForegroundInfo fg, IEnumerable<AppRule> rules, string defaultProfileId, ISet<string>? existingProfiles = null)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.ProfileId)) continue;
            if (existingProfiles != null && !existingProfiles.Contains(rule.ProfileId)) continue;
            if (Matches(rule.Match, fg)) return rule.ProfileId;
        }
        return defaultProfileId;
    }

    public static bool Matches(AppMatch m, ForegroundInfo fg)
    {
        if (m.IsEmpty) return false;
        if (!string.IsNullOrWhiteSpace(m.Exe))
        {
            var exe = m.Exe.Trim();
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !exe.Contains('.')) exe += ".exe";
            if (!string.Equals(exe, fg.ExeName, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(m.Path) && !GlobMatch(m.Path.Trim(), fg.ExePath)) return false;
        if (!string.IsNullOrWhiteSpace(m.TitleRegex))
        {
            try
            {
                if (!Regex.IsMatch(fg.Title, m.TitleRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                    return false;
            }
            catch (ArgumentException)
            {
                return false; // 正規表現の誤り（検証で警告済み）
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>* と ? だけのワイルドカード一致（大文字小文字・区切り文字 / \ を区別しない）。</summary>
    public static bool GlobMatch(string pattern, string text)
    {
        static string Norm(string s) => s.Replace('/', '\\');
        var regex = "^" + Regex.Escape(Norm(pattern)).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(Norm(text), regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

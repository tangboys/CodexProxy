using System.Text;
using System.Text.RegularExpressions;

namespace CodexProxy.Core;

public static class NodeCountry
{
    private static readonly (string Code, string Names)[] Countries = [
        ("HK", "香港|Hong Kong"), ("TW", "台湾|台灣|Taiwan"), ("JP", "日本|东京|大阪|Japan|Tokyo"),
        ("US", "美国|美國|洛杉矶|硅谷|United States|USA|Los Angeles"), ("SG", "新加坡|狮城|Singapore"),
        ("KR", "韩国|韓國|首尔|Korea|Seoul"), ("GB", "英国|英國|伦敦|United Kingdom|Britain|London|UK"),
        ("DE", "德国|德國|法兰克福|Germany|Frankfurt"), ("FR", "法国|法國|巴黎|France|Paris"),
        ("CA", "加拿大|Canada"), ("AU", "澳大利亚|澳洲|Australia"), ("RU", "俄罗斯|俄羅斯|Russia"),
        ("NL", "荷兰|荷蘭|Netherlands"), ("IN", "印度|India"), ("CN", "中国|中國|China"),
        ("TR", "土耳其|Turkey"), ("BR", "巴西|Brazil") ];

    public static string Find(string name)
    {
        // Existing regional-indicator flags are more explicit than an alias in the name.
        var runes = name.EnumerateRunes().ToArray();
        for (var i = 0; i + 1 < runes.Length; i++)
            if (runes[i].Value is >= 0x1F1E6 and <= 0x1F1FF && runes[i + 1].Value is >= 0x1F1E6 and <= 0x1F1FF)
                return string.Concat((char)('A' + runes[i].Value - 0x1F1E6), (char)('A' + runes[i + 1].Value - 0x1F1E6));
        foreach (var (code, names) in Countries)
            if (Regex.IsMatch(name, $"(?<![A-Za-z])(?:{code}|{names})(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return code;
        return "";
    }

    public static string DisplayName(string name)
    {
        var plain = string.Concat(name.EnumerateRunes().Where(r => r.Value is < 0x1F1E6 or > 0x1F1FF).Select(r => r.ToString())).Trim();
        return plain.Length == 0 ? name : plain;
    }
}

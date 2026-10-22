using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kiroku.Core;

public static class Names
{
    /// <summary>表示用: 前後の空白除去、連続空白を1つに。</summary>
    public static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Regex.Replace(s.Normalize(NormalizationForm.FormC).Trim(), @"[\s　]+", " ");
    }

    /// <summary>照合用キー: 全角半角統一・空白全除去・小文字化。</summary>
    public static string Key(string s) =>
        Regex.Replace(s.Normalize(NormalizationForm.FormKC), @"[\s　]+", "").ToLowerInvariant();
}

public static class TimeText
{
    /// <summary>"9:30" "930" "09時30分" "９：３０" などを "09:30" に。空は null。不正は例外。</summary>
    public static string? Normalize(string? input, string label)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Normalize(NormalizationForm.FormKC).Trim().Replace("時", ":").Replace("分", "").Replace("：", ":");
        Match m;
        if ((m = Regex.Match(s, @"^(\d{1,2}):(\d{1,2})$")).Success) { }
        else if ((m = Regex.Match(s, @"^(\d{1,2})(\d{2})$")).Success) { }
        else if ((m = Regex.Match(s, @"^(\d{1,2})$")).Success) { return Fmt(label, int.Parse(m.Groups[1].Value), 0); }
        else throw Bad(label);
        return Fmt(label, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    private static string Fmt(string label, int h, int m)
    {
        if (h is < 0 or > 23 || m is < 0 or > 59) throw Bad(label);
        return $"{h:00}:{m:00}";
    }

    private static KirokuException Bad(string label) =>
        new(400, "bad_time", $"{label}の形式が正しくありません。「9:30」や「0930」のように入力してください。");

    public static string? Date(string? s, string label = "日付")
    {
        if (!DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            || d.Year < 2000 || d.Year > 2100)
            throw new KirokuException(400, "bad_date", $"{label}が正しくありません。");
        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

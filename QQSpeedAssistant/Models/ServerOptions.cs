namespace QQSpeedAssistant.Models;

/// <summary>
/// 区服的可选值与写法归一化。
/// 账号页的「区服」列是下拉框，只提供 <see cref="All"/> 里的三项；
/// 历史数据里的旧写法（电信／电一／电二／网通…）在读取时统一折成标准值。
/// </summary>
public static class ServerOptions
{
    /// <summary>电信区。</summary>
    public const string Dianxin = "电信区";

    /// <summary>联通区。</summary>
    public const string Liantong = "联通区";

    /// <summary>电信2区。</summary>
    public const string Dianxin2 = "电信2区";

    /// <summary>下拉列表按此顺序显示。</summary>
    public static readonly IReadOnlyList<string> All = [Dianxin, Liantong, Dianxin2];

    /// <summary>新建账号时的默认区服。</summary>
    public const string Default = Dianxin;

    /// <summary>
    /// 把历史写法折成标准值：<br/>
    /// 电信2区／电信二区／电二／电2 → 电信2区；<br/>
    /// 联通区／联通／网通 → 联通区；<br/>
    /// 电信区／电信／电一／电1 → 电信区。<br/>
    /// 空值保持为空（留空表示改用「默认大区」设置），认不出的写法原样返回，由调用方决定是否提示。
    /// </summary>
    public static string Normalize(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return "";
        }

        // 「2」必须排在「1」前面判断：先认二区，再落到一区。
        if (text.Contains('二') || text.Contains('2'))
        {
            return Dianxin2;
        }

        if (text.Contains("联通") || text.Contains("网通"))
        {
            return Liantong;
        }

        if (text.Contains("电信") || text.Contains("电一") || text.Contains('1'))
        {
            return Dianxin;
        }

        return text;
    }

    /// <summary>这个值是否是下拉里的标准项。</summary>
    public static bool IsKnown(string? value) =>
        !string.IsNullOrEmpty(value) && All.Contains(value);
}

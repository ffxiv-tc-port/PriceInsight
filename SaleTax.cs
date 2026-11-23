using System;

namespace PriceInsight;

/// <summary>
/// 寄售稅：成交後賣方實拿＝成交總額×(100−稅率)/100,整數無條件捨去。
/// 稅率依僱員所在城市每週變動、上限 5%;提示視窗不知道會由哪位僱員賣,
/// 所以有遊戲送來的稅率時取各城最高(估出來只會偏少),沒有就用 5%。
/// </summary>
public sealed class SaleTax {
    public const int FallbackPercent = 5;
    private const int MaxPlausiblePercent = 25;

    private sealed record Snapshot(int Percent, DateTime ValidUntilUtc);

    // 稅率事件不保證在框架執行緒觸發 ⇒ 整份換掉的不可變快照,讀端不加鎖。
    private volatile Snapshot? live;

    /// <summary>目前用的稅率(%)與它是不是遊戲送來的即時值。</summary>
    public (int Percent, bool Live) Current(DateTime nowUtc) {
        var snap = live;
        return snap != null && snap.ValidUntilUtc > nowUtc ? (snap.Percent, true) : (FallbackPercent, false);
    }

    /// <summary>收下一份各城稅率;任何一城不合理或有效期不合理就整份不採用、回 false。</summary>
    public bool Accept(ReadOnlySpan<uint> townPercents, DateTime validUntilUtc, DateTime nowUtc) {
        if (townPercents.Length == 0)
            return false;
        var max = 0u;
        foreach (var p in townPercents) {
            if (p > MaxPlausiblePercent)
                return false;
            max = Math.Max(max, p);
        }
        // 稅率每週換一次;有效期在過去或超過兩週＝解析對不上,不採用。
        if (validUntilUtc <= nowUtc || validUntilUtc > nowUtc.AddDays(14))
            return false;
        live = new Snapshot((int)max, validUntilUtc);
        return true;
    }

    public static long NetProceeds(long gross, int percent) =>
        gross <= 0 ? 0 : gross * (100 - Math.Clamp(percent, 0, 100)) / 100;
}

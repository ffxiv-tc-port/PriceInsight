using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace PriceInsight;

/// <summary>
/// Marketbuddy's <c>MarketDataCache</c> mirror type, member-for-member.
/// </summary>
/// <remarks>
/// 🔴 <b>成員名必須與 Marketbuddy 端逐字相同。</b>兩邊是不同組件裡的不同型別,所以
/// Dalamud 的 CallGate 會把物件做一次 JSON 來回轉(<c>CallGateChannel.ConvertObject</c>)。
/// 名字打錯<b>不會報錯</b>,只會靜默拿到預設值(0／false)——看起來就像「這件沒資料」。
/// <para>
/// 來源:<c>Marketbuddy/Marketbuddy/MarketDataCache.cs</c> 的
/// <c>MarketDataCache.PublicSnapshot</c>(2026-09-10 實讀)。
/// </para>
/// <para>
/// 🔴 <b>價格 0 代表「這個品質沒有掛單」</b>,不是免費——真實掛單不可能是 0 gil。
/// 提供端刻意用 0 當哨兵而不是可空值型別:CallGate 的 <c>InvokeFunc</c> 對 null 走
/// <c>(TRet)result</c>,可空**值**型別會擲一個看起來與 IPC 完全無關的 NRE。
/// </para>
/// </remarks>
internal class MarketbuddyListings {
    public uint ItemId { get; set; }

    /// <summary>這份資料屬於哪一個世界。Marketbuddy 一換世界就把整份快取清掉。</summary>
    public uint WorldId { get; set; }

    /// <summary>Marketbuddy 看到這筆掛單的時間(Unix 毫秒,UTC)。</summary>
    public long ObservedAtUnixMs { get; set; }

    /// <summary>⚠️ 第一頁的筆數,不是「總共幾件在賣」。要當「至少 N 件」讀。</summary>
    public int ListingCountNq { get; set; }

    /// <summary>⚠️ 同上,第一頁的筆數。</summary>
    public int ListingCountHq { get; set; }

    /// <summary>普通品最低單價;0 代表沒有普通品掛單。</summary>
    public uint LowestPriceNq { get; set; }

    /// <summary>高品質最低單價;0 代表沒有高品質掛單。</summary>
    public uint LowestPriceHq { get; set; }

    /// <summary>
    /// true 代表 Marketbuddy <b>確認過</b>這件道具沒人在賣(它自己走完查詢流程才寫得出來),
    /// 而不是「還沒查過」。這是 Universalis 推不出來的資訊。
    /// </summary>
    public bool ConfirmedEmpty { get; set; }
}

/// <summary>
/// 讀取 Marketbuddy 的被動市場快取:本世界、最多 1 小時內、真實看到過的掛單。
/// </summary>
/// <remarks>
/// <para>
/// 這是 Universalis 之外的**第二個**價格來源,兩者語意完全不同:Universalis 是跨資料中心、
/// 群眾上傳、可能好幾天前的資料;這裡是本世界、被動收集、真的在市場佈告板上看到過的掛單。
/// ⇒ 呈現時必須讓使用者分得出是哪一種。
/// </para>
/// <para>
/// 🔴 <b>呼叫端可能是任何執行緒。</b><c>ItemPriceTooltip.ParseMbData</c> 既會在 framework
/// 執行緒(每次提示視窗更新)被呼叫,也會在背景的查詢工作執行緒上(<c>Refresh</c>／
/// <c>FetchFailed</c>)被呼叫。所以這個類別的所有共用狀態都在 <see cref="gate"/> 之下。
/// </para>
/// <para>
/// 🔴 <b>鎖內不做任何外部呼叫</b>:兩次 <c>InvokeFunc</c> 都在鎖外面。鎖內只碰字典,
/// 不畫 ImGui、不做檔案 I/O、不呼叫別的外掛。競爭時最多多打一次 IPC(對方那邊只是一次
/// ConcurrentDictionary 查表),沒有正確性問題。
/// </para>
/// <para>
/// 🔴 <b>提示視窗是每幀重畫的</b>,所以這裡自帶節流:同一件道具在
/// <see cref="EntryThrottle"/> 之內只會真的打一次 IPC,其餘直接回上一次的結果。
/// 刻意<b>不</b>用 ECommons 的 EzThrottler——它不是執行緒安全的,而且是整個外掛共用的
/// 靜態實例(PriceInsight 也根本沒有引用 ECommons)。
/// </para>
/// </remarks>
internal sealed class MarketbuddyBridge {
    /// <summary>
    /// 我們看得懂的最低契約版本。🔑 用 <c>&gt;=</c> 比對:對方合法地遞增版本號時
    /// 不應該讓這條路徑靜默失效。
    /// </summary>
    private const int RequiredApiVersion = 1;

    /// <summary>同一件道具兩次真正的 IPC 呼叫之間至少隔這麼久。</summary>
    private static readonly TimeSpan EntryThrottle = TimeSpan.FromSeconds(2);

    /// <summary>問不到之後,這麼久之內不再試(Marketbuddy 沒裝時就是這條路)。</summary>
    private static readonly TimeSpan RetryAfterMissing = TimeSpan.FromSeconds(30);

    /// <summary>對方擲了預期外的例外時退避得久一點,免得每次提示都重來一次。</summary>
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 比這更舊的快照一律不用。Marketbuddy 自己的硬性上限也是 1 小時,這裡再擋一次,
    /// 這樣就算對方哪天放寬了保存時間,我們顯示出去的東西仍然有明確的上界。
    /// </summary>
    private static readonly TimeSpan MaxUsableAge = TimeSpan.FromHours(1);

    /// <summary>節流表的上限;超過就整份丟掉重來(它只是節流,丟掉沒有正確性代價)。</summary>
    private const int MaxThrottleEntries = 512;

    private readonly ICallGateSubscriber<int> versionGate;
    private readonly ICallGateSubscriber<uint, MarketbuddyListings?> listingsGate;

    private readonly object gate = new();
    private readonly Dictionary<uint, (DateTime At, MarketbuddyListings? Value)> throttle = new();
    private DateTime unavailableUntil = DateTime.MinValue;
    private bool versionChecked;
    private bool loggedUnavailable;

    public MarketbuddyBridge(IDalamudPluginInterface pluginInterface) {
        // 🔴 端點名逐字取自 Marketbuddy/Marketbuddy/IPCManager.cs 的 TagMarketCache*
        //    (2026-09-10 實讀)。CallGate 是純字串比對:名字打錯不會有任何錯誤訊息。
        versionGate = pluginInterface.GetIpcSubscriber<int>("Marketbuddy.MarketCache.Version");
        listingsGate = pluginInterface.GetIpcSubscriber<uint, MarketbuddyListings?>("Marketbuddy.MarketCache.Get");
    }

    /// <summary>
    /// 這件道具在本世界最近一次看到的真實掛單。沒有可用資料時回 <c>null</c>——
    /// Marketbuddy 沒裝、端點不存在、資料太舊、或它就是沒看過這件道具,一律走這條。
    /// </summary>
    public MarketbuddyListings? Query(uint itemId) {
        var now = DateTime.UtcNow;
        bool needVersionCheck;
        lock (gate) {
            if (throttle.TryGetValue(itemId, out var cached) && now - cached.At < EntryThrottle)
                return Usable(cached.Value, now);
            if (now < unavailableUntil)
                return null;
            needVersionCheck = !versionChecked;
        }

        MarketbuddyListings? result;
        try {
            if (needVersionCheck) {
                var version = versionGate.InvokeFunc();
                if (version < RequiredApiVersion) {
                    // 對方比我們舊:安靜地整個關掉這條路徑,不要每次提示都重問。
                    MarkUnavailable(TimeSpan.FromHours(1));
                    Service.PluginLog.Information(
                        $"Marketbuddy market cache IPC is v{version}, need >= {RequiredApiVersion}; live listings disabled.");
                    return null;
                }

                lock (gate) {
                    versionChecked = true;
                }
            }

            result = listingsGate.InvokeFunc(itemId);
        } catch (IpcError) {
            // Marketbuddy 沒裝,或端點還沒註冊好。這是最常見的一條路 -> 完全靜默,
            // 提示視窗照樣走 Universalis。
            MarkUnavailable(RetryAfterMissing);
            return null;
        } catch (TargetInvocationException e) {
            // 🔴 提供端(同步端點)自己擲的例外會被 Func.DynamicInvoke 包成這個型別,
            //    catch (IpcError) 攔不到它。攔不住的話這裡每幀都會擲一次。
            LogUnavailableOnce(e);
            MarkUnavailable(RetryAfterError);
            return null;
        } catch (Exception e) {
            // 這是外掛之間的邊界:對方的任何失敗都不可以讓我們的提示半死。
            // 退避 + 只記一次,避免洗 log。
            LogUnavailableOnce(e);
            MarkUnavailable(RetryAfterError);
            return null;
        }

        lock (gate) {
            if (throttle.Count >= MaxThrottleEntries)
                throttle.Clear();
            throttle[itemId] = (now, result);
            unavailableUntil = DateTime.MinValue;
            loggedUnavailable = false;
        }

        return Usable(result, now);
    }

    /// <summary>太舊的快照當成沒有。<c>ObservedAtUnixMs</c> 是 UTC 的 Unix 毫秒。</summary>
    private static MarketbuddyListings? Usable(MarketbuddyListings? snapshot, DateTime nowUtc) {
        if (snapshot == null)
            return null;
        var observed = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.ObservedAtUnixMs).UtcDateTime;
        return nowUtc - observed > MaxUsableAge ? null : snapshot;
    }

    private void MarkUnavailable(TimeSpan retryAfter) {
        lock (gate) {
            unavailableUntil = DateTime.UtcNow + retryAfter;
            versionChecked = false;
        }
    }

    private void LogUnavailableOnce(Exception e) {
        bool shouldLog;
        lock (gate) {
            shouldLog = !loggedUnavailable;
            loggedUnavailable = true;
        }

        if (shouldLog)
            Service.PluginLog.Information(e, "Marketbuddy market cache IPC failed; falling back to Universalis only.");
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace PriceInsight;

// 本檔假設與契約：不引用任何 Dalamud 型別（離線探針直接編這個檔）；時間一律由呼叫端傳入；
// 每個類別都可從任意執行緒呼叫。

/// <summary>哪些情況觸發了查價；只用於每小時彙總。</summary>
internal enum FetchSource { Login, Inventory, Saddlebag, Retainer, Hover }

internal static class PrefetchPolicy {
    /// <summary>
    /// 預取要不要做。<paramref name="multiModeEnabled"/> 為 null＝問不到 AutoRetainer（沒載入、端點或型別不符），
    /// 當成「沒開」照舊預取：問不到時寧可多查，也不要讓手動登入的使用者少了預取。
    /// </summary>
    public static bool ShouldPrefetch(bool prefetchEnabled, bool skipDuringMultiMode, bool? multiModeEnabled) =>
        prefetchEnabled && !(skipDuringMultiMode && multiModeEnabled == true);
}

/// <summary>單一道具查詢失敗後的冷卻：冷卻期內不再自動重查（滑鼠移上與預取都不送），使用者按 Alt 不受限。</summary>
internal sealed class ItemFailureCooldown {
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<uint, DateTime> failedAt = new();

    public void MarkFailed(uint itemId, DateTime now) => failedAt[itemId] = now;

    public void Clear(uint itemId) => failedAt.TryRemove(itemId, out _);

    public bool IsCoolingDown(uint itemId, DateTime now) {
        if (!failedAt.TryGetValue(itemId, out var at))
            return false;
        if (now - at < Duration)
            return true;
        failedAt.TryRemove(new KeyValuePair<uint, DateTime>(itemId, at));
        return false;
    }
}

/// <summary>
/// 連續 <see cref="Threshold"/> 次請求失敗就停 <see cref="OpenDuration"/>。停完放行；
/// 只有成功才把計數歸零，所以停完後第一個請求又失敗會立刻再停。
/// </summary>
internal sealed class CircuitBreaker {
    public const int Threshold = 5;
    public static readonly TimeSpan OpenDuration = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private int consecutiveFailures;
    private DateTime openUntil = DateTime.MinValue;

    public bool IsOpen(DateTime now) {
        lock (gate)
            return now < openUntil;
    }

    /// <returns>這次失敗讓斷路器打開時為 true（呼叫端據此只記一行）。</returns>
    public bool RecordFailure(DateTime now) {
        lock (gate) {
            consecutiveFailures++;
            if (consecutiveFailures < Threshold || now < openUntil)
                return false;
            openUntil = now + OpenDuration;
            return true;
        }
    }

    /// <returns>成功前累積的連續失敗數。</returns>
    public int RecordSuccess() {
        lock (gate) {
            var previous = consecutiveFailures;
            consecutiveFailures = 0;
            return previous;
        }
    }
}

/// <summary>按世界分開保留的狀態，最多 <c>capacity</c> 個世界，超過就丟最久沒用到的那個。</summary>
internal sealed class WorldStoreSet<T> where T : class {
    private readonly object gate = new();
    private readonly int capacity;
    private readonly Func<uint, T> factory;
    private readonly LinkedList<(uint WorldId, T Store)> order = new();

    public WorldStoreSet(int capacity, Func<uint, T> factory) {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        this.factory = factory;
    }

    /// <param name="evictedWorldId">這次因為超過上限被丟掉的世界；沒有則 null。</param>
    public T GetOrCreate(uint worldId, out uint? evictedWorldId) {
        evictedWorldId = null;
        lock (gate) {
            for (var node = order.First; node != null; node = node.Next) {
                if (node.Value.WorldId != worldId)
                    continue;
                order.Remove(node);
                order.AddFirst(node);
                return node.Value.Store;
            }

            var store = factory(worldId);
            order.AddFirst((worldId, store));
            if (order.Count > capacity) {
                evictedWorldId = order.Last!.Value.WorldId;
                order.RemoveLast();
            }

            return store;
        }
    }

    public uint[] WorldIds() {
        lock (gate) {
            var ids = new uint[order.Count];
            var i = 0;
            foreach (var entry in order)
                ids[i++] = entry.WorldId;
            return ids;
        }
    }
}

/// <summary>
/// 目前查價用哪個世界的狀態。<c>follow</c>＝「使用目前世界」；關著時第一次選定後就固定，行為與舊版相同。
/// 換世界只換 <see cref="Store"/> 指向的狀態：呼叫端已經抓在手上的舊狀態不受影響，所以進行中的查詢結果仍存回舊世界。
/// </summary>
internal sealed class WorldFollower<T> where T : class {
    internal readonly record struct Change(uint? From, uint To, uint? Evicted);

    private sealed record Tracked(uint WorldId, T Store);

    private readonly WorldStoreSet<T> stores;
    private volatile Tracked? tracked;

    public WorldFollower(WorldStoreSet<T> stores) => this.stores = stores;

    public T? Store => tracked?.Store;

    public uint? WorldId => tracked?.WorldId;

    /// <summary>只在單一執行緒（框架執行緒）呼叫。世界 id 0＝讀不到，一律不動。</summary>
    /// <returns>這次真的換了世界（含第一次選定）才回傳；否則 null。</returns>
    public Change? Update(bool follow, uint currentWorldId, uint homeWorldId) {
        var now = tracked;
        var target = now == null ? (follow ? currentWorldId : homeWorldId) : (follow ? currentWorldId : now.WorldId);
        if (target == 0 || target == now?.WorldId)
            return null;
        var store = stores.GetOrCreate(target, out var evicted);
        tracked = new Tracked(target, store);
        return new Change(now?.WorldId, target, evicted);
    }
}

/// <summary>對 Universalis 的請求量計數，每小時吐一行彙總（整小時沒有任何動靜就不吐）。</summary>
internal sealed class UniversalisStats {
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly object flushGate = new();
    private DateTime windowStart;

    private int aggregatedRequests, overviewRequests, itemsRequested, failures;
    private int hoverCacheHits, prefetchCacheHits, cooldownSkips, breakerOpens, multiModeSkips;
    private readonly int[] sourceItems = new int[Enum.GetValues<FetchSource>().Length];

    public UniversalisStats(DateTime now) => windowStart = now;

    public void AggregatedRequest(int items) {
        Interlocked.Increment(ref aggregatedRequests);
        Interlocked.Add(ref itemsRequested, items);
    }

    public void OverviewRequest() => Interlocked.Increment(ref overviewRequests);
    public void Failure() => Interlocked.Increment(ref failures);
    public void HoverCacheHit() => Interlocked.Increment(ref hoverCacheHits);
    public void PrefetchCacheHit() => Interlocked.Increment(ref prefetchCacheHits);
    public void CooldownSkip() => Interlocked.Increment(ref cooldownSkips);
    public void BreakerOpened() => Interlocked.Increment(ref breakerOpens);
    public void MultiModeSkip() => Interlocked.Increment(ref multiModeSkips);
    public void Queued(FetchSource source, int items) => Interlocked.Add(ref sourceItems[(int)source], items);

    /// <summary>滿一小時（或 <paramref name="force"/>）就回傳彙總行並歸零；否則回 null。</summary>
    public string? TryFlush(DateTime now, bool force = false) {
        int agg, ov, items, fail, hoverHit, prefetchHit, cooldown, opens, multi;
        int[] sources;
        TimeSpan elapsed;
        lock (flushGate) {
            elapsed = now - windowStart;
            if (!force && elapsed < Interval)
                return null;
            windowStart = now;
            agg = Interlocked.Exchange(ref aggregatedRequests, 0);
            ov = Interlocked.Exchange(ref overviewRequests, 0);
            items = Interlocked.Exchange(ref itemsRequested, 0);
            fail = Interlocked.Exchange(ref failures, 0);
            hoverHit = Interlocked.Exchange(ref hoverCacheHits, 0);
            prefetchHit = Interlocked.Exchange(ref prefetchCacheHits, 0);
            cooldown = Interlocked.Exchange(ref cooldownSkips, 0);
            opens = Interlocked.Exchange(ref breakerOpens, 0);
            multi = Interlocked.Exchange(ref multiModeSkips, 0);
            sources = new int[sourceItems.Length];
            for (var i = 0; i < sources.Length; i++)
                sources[i] = Interlocked.Exchange(ref sourceItems[i], 0);
        }

        var anySource = false;
        foreach (var s in sources)
            anySource |= s != 0;
        if (agg + ov + fail + hoverHit + prefetchHit + cooldown + opens + multi == 0 && !anySource)
            return null;

        var sb = new StringBuilder("[PriceInsight] Universalis ");
        sb.Append(elapsed >= Interval ? "本小時" : $"近 {Math.Max(1, (int)elapsed.TotalMinutes)} 分");
        sb.Append($"：aggregated {agg} 次／v3 {ov} 次、道具 {items} 件、快取命中 滑鼠 {hoverHit}／預取 {prefetchHit} 件");
        sb.Append($"、觸發來源 登入 {sources[(int)FetchSource.Login]}／背包 {sources[(int)FetchSource.Inventory]}");
        sb.Append($"／鞍囊 {sources[(int)FetchSource.Saddlebag]}／僱員 {sources[(int)FetchSource.Retainer]}／滑鼠 {sources[(int)FetchSource.Hover]} 件");
        sb.Append($"、失敗 {fail}、失敗冷卻中略過 {cooldown} 件、連續失敗暫停 {opens} 次、因多角色模式跳過預取 {multi} 次");
        return sb.ToString();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Network.Structures;
using EasyCaching.InMemory;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace PriceInsight;

public class ItemPriceLookup : IDisposable {
    private readonly InMemoryCaching cache = new("prices", new InMemoryCachingOptions { EnableReadDeepClone = false });
    private readonly ConcurrentQueue<uint> requestedItems = new();
    private readonly ConcurrentDictionary<uint, (Task Task, CancellationTokenSource Token)> activeTasks = new();
    // Live data seen on the in-game market board for the tracked world - fresher than anything Universalis has.
    private readonly ConcurrentDictionary<uint, LiveWorldData> liveWorldData = new();
    // Items whose cached entry is still shown while a forced (alt) refresh is in flight.
    private readonly ConcurrentDictionary<uint, byte> refreshingItems = new();
    // When each cached entry was last written (Universalis fetch, live board merge or synthesis).
    private readonly ConcurrentDictionary<uint, DateTime> lastUpdated = new();
    // An entry younger than this is not worth re-fetching on a forced refresh.
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(30);
    private readonly PriceInsightPlugin plugin;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private uint? homeWorldId;

    private sealed record LiveWorldData {
        public DateTime OfferingsSeen { get; init; }
        public Listing? MinNq { get; init; }
        public Listing? MinHq { get; init; }
        public DateTime HistorySeen { get; init; }
        public Listing? RecentNq { get; init; }
        public Listing? RecentHq { get; init; }
    }

    public ItemPriceLookup(PriceInsightPlugin plugin) {
        this.plugin = plugin;
        Task.Run(ProcessQueue, cancellationTokenSource.Token)
            // Silently ignore cancel
            .ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled);
    }

    public bool CheckReady() {
        var localPlayer = Service.ClientState.LocalPlayer;
        if (localPlayer == null) return false;
        if (plugin.Configuration.UseCurrentWorld) {
            homeWorldId ??= localPlayer.CurrentWorld.RowId;
        } else {
            homeWorldId ??= localPlayer.HomeWorld.RowId;
        }

        return homeWorldId != null;
    }

    public (MarketBoardData? MarketBoardData, LookupState State) Get(ulong fullItemId, bool refresh) {
        if (!ToMarketableItemId(fullItemId, out var itemId))
            return (null, LookupState.NonMarketable);

        var cached = cache.Get<MarketBoardData>(itemId.ToString()) is { IsNull: false, Value: var mbData } ? mbData : null;
        if (refresh && cached != null) {
            // Freshness guard: an entry fetched/merged moments ago has nothing to gain from a re-fetch.
            if (lastUpdated.TryGetValue(itemId, out var updated) && DateTime.Now - updated < FreshnessWindow)
                return (cached, LookupState.Marketable);
            // Keep showing the old entry while the refresh lookup runs; it gets swapped out once
            // the fetch completes. Don't cancel an in-flight fetch - its result is fresh enough.
            refreshingItems[itemId] = 0;
            if (!activeTasks.ContainsKey(itemId) && !requestedItems.Contains(itemId))
                requestedItems.Enqueue(itemId);
            return (cached, LookupState.Refreshing);
        }

        if (cached != null)
            return (cached, refreshingItems.ContainsKey(itemId) ? LookupState.Refreshing : LookupState.Marketable);
        if (activeTasks.TryGetValue(itemId, out var t))
            return (null, t.Task.IsFaulted ? LookupState.Faulted : LookupState.Marketable);

        requestedItems.Enqueue(itemId);

        return (null, LookupState.Marketable);
    }

    // Cache peek without lookup side effects (no enqueue, no state changes).
    public MarketBoardData? GetCached(ulong fullItemId) {
        if (!ToMarketableItemId(fullItemId, out var itemId))
            return null;
        return cache.Get<MarketBoardData>(itemId.ToString()) is { IsNull: false, Value: var mbData } ? mbData : null;
    }

    private static bool ToMarketableItemId(ulong fullItemId, out uint itemId, ExcelSheet<Item>? sheet = null) {
        itemId = (uint)(fullItemId % 500000);
        if (fullItemId is >= 2000000 or >= 500000 and < 1000000)
            return false;
        sheet ??= Service.DataManager.Excel.GetSheet<Item>();
        return sheet.GetRowOrDefault(itemId) is not null and not { ItemSearchCategory.RowId: 0 };
    }

    public void Fetch(IEnumerable<uint> items) {
        var itemSheet = Service.DataManager.Excel.GetSheet<Item>();
        foreach (var id in items) {
            if (!ToMarketableItemId(id, out var itemId, itemSheet))
                continue;
            if (cache.Get(itemId.ToString()) != null || (activeTasks.TryGetValue(itemId, out var t) && !t.Task.IsFaulted))
                continue;
            if (!requestedItems.Contains(itemId))
                requestedItems.Enqueue(itemId);
        }
    }

    private async Task ProcessQueue() {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(cancellationTokenSource.Token)) {
            if (requestedItems.IsEmpty)
                continue;
            var items = new HashSet<uint>();
            while (items.Count < 50 && requestedItems.TryDequeue(out var item))
                items.Add(item);
            await FetchInternal(items);
        }

        timer.Dispose();
    }

    // Market board packets arrive on the framework thread (hooked via InfoProxyItemSearch).
    // The board only ever shows listings of the world the player is on, so only accept them
    // when that world is the one this cache is tracking.
    private bool IsTrackedWorldCurrent() {
        return CheckReady() && Service.PlayerState.IsLoaded && Service.PlayerState.CurrentWorld.RowId == homeWorldId;
    }

    public void ApplyMarketBoardOfferings(IMarketBoardCurrentOfferings currentOfferings) {
        if (!IsTrackedWorldCurrent())
            return;
        var now = DateTime.Now;
        foreach (var listings in currentOfferings.ItemListings.GroupBy(l => l.ItemId)) {
            Listing? minNq = null, minHq = null;
            foreach (var l in listings) {
                var listing = new Listing { Price = l.PricePerUnit, World = null, Datacenter = null, Time = now };
                if (l.IsHq)
                    minHq = Cheapest(minHq, listing);
                else
                    minNq = Cheapest(minNq, listing);
            }

            var itemId = listings.Key;
            liveWorldData.AddOrUpdate(itemId,
                _ => new LiveWorldData { OfferingsSeen = now, MinNq = minNq, MinHq = minHq },
                (_, old) => {
                    // Pages of the same browse arrive within seconds - combine them; a later browse replaces.
                    if ((now - old.OfferingsSeen).TotalSeconds < 30) {
                        minNq = Cheapest(old.MinNq, minNq);
                        minHq = Cheapest(old.MinHq, minHq);
                    }

                    return old with { OfferingsSeen = now, MinNq = minNq, MinHq = minHq };
                });
            ApplyLiveDataToCachedEntry(itemId);
        }
    }

    public void ApplyMarketBoardHistory(IMarketBoardHistory history) {
        if (!IsTrackedWorldCurrent())
            return;
        Listing? recentNq = null, recentHq = null;
        foreach (var l in history.HistoryListings) {
            var listing = new Listing { Price = l.SalePrice, World = null, Datacenter = null, Time = l.PurchaseTime };
            if (l.IsHq)
                recentHq = MostRecent(recentHq, listing);
            else
                recentNq = MostRecent(recentNq, listing);
        }

        if (recentNq == null && recentHq == null)
            return;
        var now = DateTime.Now;
        liveWorldData.AddOrUpdate(history.ItemId,
            _ => new LiveWorldData { HistorySeen = now, RecentNq = recentNq, RecentHq = recentHq },
            (_, old) => old with { HistorySeen = now, RecentNq = recentNq ?? old.RecentNq, RecentHq = recentHq ?? old.RecentHq });
        ApplyLiveDataToCachedEntry(history.ItemId);
    }

    private static Listing? Cheapest(Listing? a, Listing? b) => a == null ? b : b == null ? a : b.Price < a.Price ? b : a;

    private static Listing? MostRecent(Listing? a, Listing? b) => a == null ? b : b == null ? a : b.Time > a.Time ? b : a;

    private void ApplyLiveDataToCachedEntry(uint itemId) {
        if (cache.Get<MarketBoardData>(itemId.ToString()) is not { IsNull: false, Value: var mbData })
            return;
        var merged = MergeLiveWorldData(itemId, mbData);
        if (!ReferenceEquals(merged, mbData)) {
            cache.Set(itemId.ToString(), merged, TimeSpan.FromMinutes(90));
            lastUpdated[itemId] = DateTime.Now;
        }
    }

    // Overlay data the player just saw on the market board over the (often much older) Universalis
    // own-world data. Cross-world/datacenter scopes stay untouched - the board can't see those.
    private MarketBoardData MergeLiveWorldData(uint itemId, MarketBoardData data) {
        if (!liveWorldData.TryGetValue(itemId, out var live))
            return data;
        var result = data;
        var universalisMinTime = MostRecent(result.MinimumPrice.World.Nq, result.MinimumPrice.World.Hq)?.Time ?? DateTime.MinValue;
        if ((live.MinNq != null || live.MinHq != null) && live.OfferingsSeen > universalisMinTime) {
            result = result with {
                MinimumPrice = result.MinimumPrice with {
                    World = new() {
                        Nq = live.MinNq ?? result.MinimumPrice.World.Nq,
                        Hq = live.MinHq ?? result.MinimumPrice.World.Hq,
                    }
                }
            };
        }

        var universalisRecentTime = MostRecent(result.MostRecentPurchase.World.Nq, result.MostRecentPurchase.World.Hq)?.Time ?? DateTime.MinValue;
        if ((live.RecentNq != null || live.RecentHq != null) && live.HistorySeen > universalisRecentTime) {
            result = result with {
                MostRecentPurchase = result.MostRecentPurchase with {
                    World = new() {
                        Nq = MostRecent(live.RecentNq, result.MostRecentPurchase.World.Nq),
                        Hq = MostRecent(live.RecentHq, result.MostRecentPurchase.World.Hq),
                    }
                }
            };
        }

        return result;
    }

    // When Universalis has nothing for an item (failed lookup or no data uploaded - common on TC)
    // but the player has seen it on the market board, build an own-world-only entry from that.
    private MarketBoardData? SynthesizeFromLiveData(uint itemId) {
        if (!homeWorldId.HasValue || !liveWorldData.TryGetValue(itemId, out var live))
            return null;
        if (live is { MinNq: null, MinHq: null, RecentNq: null, RecentHq: null })
            return null;
        var (worldName, dcName, region) = UniversalisClientV2.WorldLookup[homeWorldId.Value];
        var empty = new Quality<Listing> { Nq = null, Hq = null };
        var emptyValue = new Quality<double?> { Nq = null, Hq = null };
        return new MarketBoardData {
            HomeWorld = worldName,
            Datacenter = dcName,
            Region = region,
            MinimumPrice = new() { World = new() { Nq = live.MinNq, Hq = live.MinHq }, Datacenter = empty, Region = empty },
            MostRecentPurchase = new() { World = new() { Nq = live.RecentNq, Hq = live.RecentHq }, Datacenter = empty, Region = empty },
            AverageSalePrice = new() { World = emptyValue, Datacenter = emptyValue, Region = emptyValue },
            DailySaleVelocity = new() { World = emptyValue, Datacenter = emptyValue, Region = emptyValue },
        };
    }

    private Task<Dictionary<uint, MarketBoardData>?> FetchInternal(ICollection<uint> itemIds) {
        var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token);
        var itemTask = FetchItemTask();

        foreach (var id in itemIds) {
            var task = Task.Run(async () => {
                var items = await itemTask;
                if (items != null && items.TryGetValue(id, out var value)) {
                    cache.Set(id.ToString(), value, TimeSpan.FromMinutes(90));
                    lastUpdated[id] = DateTime.Now;
                } else if (SynthesizeFromLiveData(id) is { } fromGameData) {
                    // Shorter lifetime so Universalis still gets retried for the cross-world scopes.
                    cache.Set(id.ToString(), fromGameData, TimeSpan.FromMinutes(15));
                    lastUpdated[id] = DateTime.Now;
                }

                activeTasks.TryRemove(id, out _);
                refreshingItems.TryRemove(id, out _);
            }, token.Token);
            task.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled);
            activeTasks[id] = (task, token);
        }

        return itemTask;

        async Task<Dictionary<uint, MarketBoardData>?> FetchItemTask() {
            if (!homeWorldId.HasValue)
                return null;
            var fetchStart = DateTime.Now;
            var result = await plugin.UniversalisClientV2.GetMarketBoardDataList(homeWorldId.Value, itemIds, token.Token);
            if (result != null) {
                // Overlay fresher own-world data seen on the in-game market board before it hits the tooltip/cache.
                foreach (var id in itemIds) {
                    if (result.TryGetValue(id, out var value))
                        result[id] = MergeLiveWorldData(id, value);
                }

                plugin.ItemPriceTooltip.Refresh(result);
            } else if (!token.Token.IsCancellationRequested)
                // A cancelled lookup (alt-refresh requeues the item, logout/unload tears us down) is not a fetch failure.
                plugin.ItemPriceTooltip.FetchFailed(itemIds);
            Service.PluginLog.Debug($"Fetching {itemIds.Count} items took {(DateTime.Now - fetchStart).TotalMilliseconds:F0}ms");
            return result;
        }
    }

    public void Dispose() {
        cancellationTokenSource.Cancel();
    }
}

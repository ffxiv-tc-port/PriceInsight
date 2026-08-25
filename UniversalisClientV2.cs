using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Networking.Http;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace PriceInsight;

public class UniversalisClientV2 : IDisposable {
    private static readonly Dictionary<uint, string> Regions = new() { { 1, "Japan" }, { 2, "North-America" }, { 3, "Europe" }, { 4, "Oceania" } };
    internal static readonly Dictionary<uint, (string Name, string DcName, string Region)> WorldLookup = Service.DataManager.GetExcelSheet<World>()
        .ToDictionary(w => w.RowId, w => (w.Name.ExtractText(), w.DataCenter.Value.Name.ExtractText(), GetRegionName(w)));

    private readonly HappyEyeballsCallback happyEyeballsCallback;
    private readonly HttpClient httpClient;

    public UniversalisClientV2() {
        happyEyeballsCallback = new HappyEyeballsCallback();
        httpClient = new HttpClient(new SocketsHttpHandler {
            AutomaticDecompression = DecompressionMethods.All, ConnectCallback = happyEyeballsCallback.ConnectCallback
        });
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PriceInsight/{Assembly.GetExecutingAssembly().GetName().Version} ({Environment.OSVersion}) Dalamud/{Assembly.GetAssembly(typeof(Util))!.GetName().Version}");
    }

    public async Task<Dictionary<uint, MarketBoardData>?> GetMarketBoardDataList(
        uint homeWorldId, ICollection<uint> itemId, CancellationToken cancellationToken) {
        try {
            try {
                return await GetMarketBoardDataListOnce(homeWorldId, itemId, cancellationToken);
            } catch (HttpRequestException ex) when (IsTransient(ex.StatusCode)) {
                // Universalis is a public service; intermittent 408/429/5xx (esp. 504, and 429 under
                // burst load) is routine. Back off briefly and retry once.
                Service.PluginLog.Debug("Universalis returned {0} for itemIds {1}, retrying once.", ex.StatusCode, itemId);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return await GetMarketBoardDataListOnce(homeWorldId, itemId, cancellationToken);
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // Expected cancellation (alt-refresh, logout cache clear, plugin unload) - not an error.
            Service.PluginLog.Verbose("Universalis lookup for itemIds {0} was cancelled.", itemId);
            return null;
        } catch (HttpRequestException ex) when (IsTransient(ex.StatusCode)) {
            // Still transient after the retry: server-side/rate-limit issue, not something the user can act on.
            Service.PluginLog.Warning("Universalis is having issues (HTTP {0}) while fetching itemIds {1}.", ex.StatusCode, itemId);
            return null;
        } catch (Exception ex) {
            Service.PluginLog.Error(ex, "Failed to retrieve data from Universalis for itemIds {0}.", itemId);
            return null;
        }
    }

    private async Task<Dictionary<uint, MarketBoardData>?> GetMarketBoardDataListOnce(
        uint homeWorldId, ICollection<uint> itemId, CancellationToken cancellationToken) {
        using var result =
            await httpClient.GetAsync($"https://universalis.app/api/v2/aggregated/{homeWorldId}/{string.Join(',', itemId.Select(i => i.ToString()))}",
                cancellationToken);

        // 400 is not a failure here - see the else branch below. Every other non-OK status still
        // throws with its StatusCode attached so the caller's transient/permanent grading applies.
        if (result.StatusCode != HttpStatusCode.OK && result.StatusCode != HttpStatusCode.BadRequest) {
            throw new HttpRequestException("Invalid status code " + result.StatusCode, null, result.StatusCode);
        }

        var items = new Dictionary<uint, MarketBoardData>();
        var unresolvedItems = new HashSet<uint>();
        if (result.StatusCode == HttpStatusCode.OK) {
            await using var responseStream = await result.Content.ReadAsStreamAsync(cancellationToken);
            var json = await JsonSerializer.DeserializeAsync<AggregatedMarketBoardData>(responseStream, cancellationToken: cancellationToken);
            if (json == null) {
                throw new HttpRequestException("Universalis returned null response");
            }

            if (json.results != null) {
                foreach (var item in json.results) {
                    // Indexer rather than Add(): a duplicated itemId in the response would otherwise
                    // throw and lose the whole batch.
                    items[item.itemId] = item.ToMarketBoardData(homeWorldId);
                }
            }

            unresolvedItems.UnionWith(json.failedItems ?? []);
        } else {
            // The aggregated endpoint returns 400 when every requested item is absent from
            // Universalis' current marketable-item list. This happens for dyes that remain
            // marketable on the Traditional Chinese client but were consolidated in global 7.5.
            unresolvedItems.UnionWith(itemId);
        }

        if (unresolvedItems.Count > 0) {
            Service.PluginLog.Debug(
                "Universalis aggregated data did not resolve itemIds {0}; trying the v3 overview endpoint.",
                unresolvedItems);
            // Keep fallback concurrency deliberately small. An inventory prefetch can contain dozens of
            // legacy dyes, and each overview request already fans out across every world in the DC, so
            // run three items at a time rather than sequentially (slow) or all at once (rate-limited).
            foreach (var batch in unresolvedItems.Chunk(3)) {
                var fallbackResults = await Task.WhenAll(batch.Select(async unresolvedItem => {
                    try {
                        var data = await GetMarketBoardOverview(homeWorldId, unresolvedItem, cancellationToken);
                        return (ItemId: unresolvedItem, Data: data, Error: (Exception?)null);
                    } catch (Exception ex) when (!cancellationToken.IsCancellationRequested) {
                        // One item failing must not sink the rest of the batch; the user can re-hover to retry.
                        return (ItemId: unresolvedItem, Data: (MarketBoardData?)null, Error: ex);
                    }
                }));

                foreach (var fallbackResult in fallbackResults) {
                    if (fallbackResult.Data != null)
                        items[fallbackResult.ItemId] = fallbackResult.Data;
                    else if (fallbackResult.Error != null)
                        Service.PluginLog.Warning(fallbackResult.Error,
                            "Universalis overview fallback failed for itemId {0}.", fallbackResult.ItemId);
                }
            }
        }

        // Returning an empty dictionary would make the caller call Refresh() with nothing in it,
        // which is a no-op that leaves the tooltip stuck on "loading" forever. null routes to
        // FetchFailed() instead, which shows the failure hint over any retained cache entry.
        return items.Count > 0 ? items : null;
    }

    /// <summary>
    /// Per-item fallback for items the aggregated endpoint refuses to resolve. The v3 endpoint takes
    /// explicit world IDs and does not filter against the global marketable-item list, so it still
    /// answers for items that are only marketable on the Traditional Chinese service.
    /// </summary>
    private async Task<MarketBoardData?> GetMarketBoardOverview(
        uint homeWorldId, uint itemId, CancellationToken cancellationToken) {
        if (!WorldLookup.TryGetValue(homeWorldId, out var homeWorld))
            return null;

        // Keep the fallback bounded to the current data center; on the Traditional Chinese service
        // that data center is also the whole region, so nothing is lost by not widening it.
        var worldIds = WorldLookup
            .Where(w => w.Value.DcName == homeWorld.DcName)
            .Select(w => w.Key)
            .OrderBy(w => w)
            .ToArray();
        if (worldIds.Length == 0)
            return null;

        // Does the queried data center cover the whole region? On the Traditional Chinese service it
        // does; elsewhere it does not, and the region scope must then be reported as "no data" rather
        // than silently equated to the single data center we asked about.
        var regionWorldIds = WorldLookup
            .Where(w => w.Value.Region == homeWorld.Region)
            .Select(w => w.Key)
            .ToHashSet();
        var dcCoversRegion = regionWorldIds.SetEquals(worldIds);

        var (statusCode, overview) = await RequestMarketBoardOverview(worldIds, itemId, cancellationToken);
        if (statusCode == HttpStatusCode.NotFound) {
            // V3 normally returns 200 with empty arrays for an item nobody has listed. A 404 instead
            // means one of the supplied world IDs was not recognised. Retry each world on its own so a
            // single stale/invalid world cannot be mistaken for "this item has no market data at all".
            Service.PluginLog.Debug(
                "Universalis rejected the overview world list for itemId {0}; retrying individual worlds.",
                itemId);
            var successfulWorldIds = new List<uint>();
            var partialOverviews = new List<MarketOverview>();
            foreach (var batch in worldIds.Chunk(3)) {
                var worldResults = await Task.WhenAll(batch.Select(async worldId => {
                    var response = await RequestMarketBoardOverview([worldId], itemId, cancellationToken);
                    return (WorldId: worldId, response.StatusCode, response.Overview);
                }));

                foreach (var worldResult in worldResults) {
                    if (worldResult.StatusCode == HttpStatusCode.OK && worldResult.Overview != null) {
                        successfulWorldIds.Add(worldResult.WorldId);
                        partialOverviews.Add(worldResult.Overview);
                    } else if (worldResult.StatusCode == HttpStatusCode.NotFound) {
                        Service.PluginLog.Warning(
                            "Universalis does not recognize worldId {0} while retrieving itemId {1}.",
                            worldResult.WorldId, itemId);
                    } else {
                        throw new HttpRequestException(
                            "Invalid fallback status code " + worldResult.StatusCode, null, worldResult.StatusCode);
                    }
                }
            }

            if (partialOverviews.Count == 0)
                throw new HttpRequestException("Universalis did not recognize any fallback worlds", null, statusCode);

            var dcComplete = successfulWorldIds.Count == worldIds.Length;
            return MarketOverview.Merge(itemId, partialOverviews)
                .ToMarketBoardData(homeWorldId, successfulWorldIds, dcComplete, dcComplete && dcCoversRegion);
        }

        if (statusCode != HttpStatusCode.OK)
            throw new HttpRequestException("Invalid fallback status code " + statusCode, null, statusCode);

        return overview?.ToMarketBoardData(homeWorldId, worldIds, true, dcCoversRegion);
    }

    private async Task<(HttpStatusCode StatusCode, MarketOverview? Overview)> RequestMarketBoardOverview(
        IReadOnlyCollection<uint> worldIds, uint itemId, CancellationToken cancellationToken) {
        var requestUri = $"https://universalis.app/api/v3/market/overview/{string.Join(',', worldIds)}/{itemId}";
        using var result = await GetOverviewWithRetry(requestUri, cancellationToken);
        // Unlike the aggregated path this deliberately does not throw on a non-OK status: the caller
        // grades 404 (unknown world) separately from other failures.
        if (result.StatusCode != HttpStatusCode.OK)
            return (result.StatusCode, null);

        await using var responseStream = await result.Content.ReadAsStreamAsync(cancellationToken);
        var overview = await JsonSerializer.DeserializeAsync<MarketOverview>(responseStream, cancellationToken: cancellationToken);
        return (result.StatusCode, overview);
    }

    /// <remarks>
    /// The fallback runs one request per unresolved item, so it is the path most likely to trip the
    /// rate limiter. It gets the same policy as the aggregated path (back off 2s, retry once) rather
    /// than its own, so there is a single definition of "transient" in this file.
    /// </remarks>
    private async Task<HttpResponseMessage> GetOverviewWithRetry(string requestUri, CancellationToken cancellationToken) {
        var result = await httpClient.GetAsync(requestUri, cancellationToken);
        if (!IsTransient(result.StatusCode))
            return result;

        Service.PluginLog.Debug("Universalis overview returned {0} for {1}, retrying once.", result.StatusCode, requestUri);
        result.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        return await httpClient.GetAsync(requestUri, cancellationToken);
    }

    /// <summary>
    /// Statuses worth a single retry: 408/429 plus anything 5xx. Anything else is either a success
    /// or a permanent client-side error that retrying cannot fix.
    /// </summary>
    private static bool IsTransient(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

    /// <remarks>
    /// The Region column only enumerates the four global regions, so the Traditional Chinese,
    /// Chinese and Korean data centers all fall through to "unknown" and show up as such in the
    /// tooltip's region row. Map them by data center name instead.
    /// </remarks>
    private static string GetRegionName(World world) {
        var dcName = world.DataCenter.Value.Name.ExtractText();
        return dcName switch {
            "陸行鳥" => "繁中服",
            "陆行鸟" or "莫古力" or "猫小胖" or "豆豆柴" => "中国",
            "한국" => "한국",
            _ => Regions.GetValueOrDefault(world.DataCenter.Value.Region) ?? "unknown",
        };
    }

    public void Dispose() {
        httpClient.Dispose();
        happyEyeballsCallback.Dispose();
    }
}

// ReSharper disable all
file class AggregatedMarketBoardData {
    public List<Result>? results { get; set; }
    public List<uint>? failedItems { get; set; }
}

internal class MarketOverview {
    public uint item { get; set; }
    public Dictionary<uint, long?>? updatedAt { get; set; }
    public List<OverviewListing>? listings { get; set; }
    public List<OverviewSale>? sales { get; set; }

    /// <summary>
    /// Combines the per-world overviews collected during the 404 fallback into one so the aggregate
    /// (min price, recent sale, average, velocity) is computed over every world that answered.
    /// </summary>
    public static MarketOverview Merge(uint itemId, IEnumerable<MarketOverview> overviews) {
        var overviewList = overviews.ToList();
        return new MarketOverview {
            item = itemId,
            updatedAt = overviewList
                .SelectMany(o => o.updatedAt ?? [])
                .GroupBy(entry => entry.Key)
                .ToDictionary(group => group.Key, group => group.Last().Value),
            listings = overviewList.SelectMany(o => o.listings ?? []).ToList(),
            sales = overviewList.SelectMany(o => o.sales ?? []).ToList(),
        };
    }

    public MarketBoardData ToMarketBoardData(
        uint homeWorldId, IReadOnlyCollection<uint> dcWorldIds, bool dcComplete, bool regionComplete) {
        var (homeWorld, dcName, region) = UniversalisClientV2.WorldLookup[homeWorldId];
        var homeWorldIds = new HashSet<uint> { homeWorldId };
        var dcWorldIdSet = dcWorldIds.ToHashSet();

        // When the DC (or region) query only partially resolved, report that scope as "no data"
        // rather than a minimum drawn from an incomplete world set that would mislead the user.
        return new MarketBoardData {
            HomeWorld = homeWorld,
            Datacenter = dcName,
            Region = region,
            MinimumPrice = new() {
                World = GetMinimumPrice(homeWorldIds),
                Datacenter = dcComplete ? GetMinimumPrice(dcWorldIdSet) : NoData<PriceInsight.Listing>(),
                Region = regionComplete ? GetMinimumPrice(dcWorldIdSet) : NoData<PriceInsight.Listing>(),
            },
            MostRecentPurchase = new() {
                World = GetMostRecentPurchase(homeWorldIds),
                Datacenter = dcComplete ? GetMostRecentPurchase(dcWorldIdSet) : NoData<PriceInsight.Listing>(),
                Region = regionComplete ? GetMostRecentPurchase(dcWorldIdSet) : NoData<PriceInsight.Listing>(),
            },
            AverageSalePrice = new() {
                World = GetAverageSalePrice(homeWorldIds),
                Datacenter = dcComplete ? GetAverageSalePrice(dcWorldIdSet) : NoData<double?>(),
                Region = regionComplete ? GetAverageSalePrice(dcWorldIdSet) : NoData<double?>(),
            },
            DailySaleVelocity = new() {
                World = GetDailySaleVelocity(homeWorldIds),
                Datacenter = dcComplete ? GetDailySaleVelocity(dcWorldIdSet) : NoData<double?>(),
                Region = regionComplete ? GetDailySaleVelocity(dcWorldIdSet) : NoData<double?>(),
            },
        };
    }

    private static Quality<T> NoData<T>() => new() { Nq = default, Hq = default };

    private Quality<PriceInsight.Listing> GetMinimumPrice(IReadOnlySet<uint> worldIds) => new() {
        Nq = GetMinimumPrice(worldIds, false),
        Hq = GetMinimumPrice(worldIds, true),
    };

    private PriceInsight.Listing? GetMinimumPrice(IReadOnlySet<uint> worldIds, bool hq) {
        var listing = listings?
            .Where(l => worldIds.Contains(l.world) && l.hq == hq && l.quantity > 0)
            .MinBy(l => l.UntaxedPrice);
        return listing == null ? null : ToListing(listing.UntaxedPrice, listing.world, listing.reviewedAt);
    }

    private Quality<PriceInsight.Listing> GetMostRecentPurchase(IReadOnlySet<uint> worldIds) => new() {
        Nq = GetMostRecentPurchase(worldIds, false),
        Hq = GetMostRecentPurchase(worldIds, true),
    };

    private PriceInsight.Listing? GetMostRecentPurchase(IReadOnlySet<uint> worldIds, bool hq) {
        var sale = sales?
            .Where(s => worldIds.Contains(s.world) && s.hq == hq)
            .MaxBy(s => s.saleTime);
        return sale == null ? null : ToListing(sale.price, sale.world, sale.saleTime);
    }

    private Quality<double?> GetAverageSalePrice(IReadOnlySet<uint> worldIds) => new() {
        Nq = GetAverageSalePrice(worldIds, false),
        Hq = GetAverageSalePrice(worldIds, true),
    };

    private double? GetAverageSalePrice(IReadOnlySet<uint> worldIds, bool hq) {
        if (IsRecentSalesTruncated(worldIds))
            return null;

        var recentSales = GetRecentSales(worldIds, hq).Where(s => s.quantity is > 0).ToList();
        var quantity = recentSales.Sum(s => (long)s.quantity!.Value);
        return quantity > 0 ? recentSales.Sum(s => (double)s.price * s.quantity!.Value) / quantity : null;
    }

    private Quality<double?> GetDailySaleVelocity(IReadOnlySet<uint> worldIds) => new() {
        Nq = GetDailySaleVelocity(worldIds, false),
        Hq = GetDailySaleVelocity(worldIds, true),
    };

    private double? GetDailySaleVelocity(IReadOnlySet<uint> worldIds, bool hq) {
        if (IsRecentSalesTruncated(worldIds))
            return null;

        var quantity = GetRecentSales(worldIds, hq)
            .Where(s => s.quantity is > 0)
            .Sum(s => (long)s.quantity!.Value);
        return quantity > 0 ? quantity / 4d : null;
    }

    private IEnumerable<OverviewSale> GetRecentSales(IReadOnlySet<uint> worldIds, bool hq) {
        var startOfWindow = GetSaleWindowStart();
        return sales?.Where(s => worldIds.Contains(s.world) && s.hq == hq && s.saleTime >= startOfWindow)
               ?? Enumerable.Empty<OverviewSale>();
    }

    // V3 overview caps the sales list at 20 entries per world. If a world already fills that cap
    // entirely from within the 3-day averaging window, older sales in the window were dropped and any
    // average/velocity computed from it would be understated - report nothing rather than a wrong number.
    private bool IsRecentSalesTruncated(IReadOnlySet<uint> worldIds) {
        var startOfWindow = GetSaleWindowStart();
        return worldIds.Any(worldId => {
            var worldSales = sales?.Where(s => s.world == worldId).ToList() ?? [];
            return worldSales.Count >= 20 && worldSales.Min(s => s.saleTime) >= startOfWindow;
        });
    }

    private static long GetSaleWindowStart() =>
        new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-3), TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static PriceInsight.Listing ToListing(long price, uint worldId, long timestamp) {
        UniversalisClientV2.WorldLookup.TryGetValue(worldId, out var world);
        return new PriceInsight.Listing {
            Price = price,
            World = world.Name,
            Datacenter = world.DcName,
            Time = timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime : null,
        };
    }
}

internal class OverviewListing {
    public uint world { get; set; }
    public long reviewedAt { get; set; }
    public decimal price { get; set; }
    public int quantity { get; set; }
    public long total { get; set; }
    public bool hq { get; set; }

    // V3 overview returns a tax-inclusive total. Recover the original integer
    // per-unit listing price used by the aggregated endpoint and the game UI.
    public long UntaxedPrice => quantity > 0 && total > 0
        ? total * 20 / (21L * quantity)
        : (long)Math.Floor(price / 1.05m);
}

internal class OverviewSale {
    public uint world { get; set; }
    public bool hq { get; set; }
    public long price { get; set; }
    // Nullable: a malformed/absent quantity must not be silently read as 0 and skew the average.
    public int? quantity { get; set; }
    public long saleTime { get; set; }
}

file class Result {
    public uint itemId { get; set; }
    public Aggregate? nq { get; set; }
    public Aggregate? hq { get; set; }
    public List<WorldUploadTime>? worldUploadTimes { get; set; }

    public MarketBoardData ToMarketBoardData(uint worldId) {
        var (worldName, dcName, region) = UniversalisClientV2.WorldLookup[worldId];
        var worldUploadTimes = this.worldUploadTimes != null
            ? this.worldUploadTimes.ToDictionary(w => w.worldId, w => (DateTime)w.timestamp)
            : new Dictionary<uint, DateTime>();
        var worldUploadTime = worldUploadTimes.GetValueOrDefault(worldId, DateTime.Now);
        var marketBoardData = new MarketBoardData {
            HomeWorld = worldName,
            Datacenter = dcName,
            Region = region,
            MinimumPrice = new() {
                World = new() {
                    Nq = this.nq?.minListing?.world?.ToListing(worldUploadTime, worldUploadTimes),
                    Hq = this.hq?.minListing?.world?.ToListing(worldUploadTime, worldUploadTimes),
                },
                Datacenter = new() {
                    Nq = this.nq?.minListing?.dc?.ToListing(worldUploadTime, worldUploadTimes),
                    Hq = this.hq?.minListing?.dc?.ToListing(worldUploadTime, worldUploadTimes),
                },
                Region = new() {
                    Nq = this.nq?.minListing?.region?.ToListing(worldUploadTime, worldUploadTimes),
                    Hq = this.hq?.minListing?.region?.ToListing(worldUploadTime, worldUploadTimes),
                }
            },
            MostRecentPurchase = new() {
                World = new() {
                    Nq = this.nq?.recentPurchase?.world,
                    Hq = this.hq?.recentPurchase?.world,
                },
                Datacenter = new() {
                    Nq = this.nq?.recentPurchase?.dc,
                    Hq = this.hq?.recentPurchase?.dc,
                },
                Region = new() {
                    Nq = this.nq?.recentPurchase?.region,
                    Hq = this.hq?.recentPurchase?.region,
                }
            },
            AverageSalePrice = new() {
                World = new() {
                    Nq = this.nq?.averageSalePrice?.world?.price > 0 ? this.nq?.averageSalePrice?.world?.price : null,
                    Hq = this.hq?.averageSalePrice?.world?.price > 0 ? this.hq?.averageSalePrice?.world?.price : null,
                },
                Datacenter = new() {
                    Nq = this.nq?.averageSalePrice?.dc?.price > 0 ? this.nq?.averageSalePrice?.dc?.price : null,
                    Hq = this.hq?.averageSalePrice?.dc?.price > 0 ? this.hq?.averageSalePrice?.dc?.price : null,
                },
                Region = new() {
                    Nq = this.nq?.averageSalePrice?.region?.price > 0 ? this.nq?.averageSalePrice?.region?.price : null,
                    Hq = this.hq?.averageSalePrice?.region?.price > 0 ? this.hq?.averageSalePrice?.region?.price : null,
                }
            },
            DailySaleVelocity = new() {
                World = new() {
                    Nq = this.nq?.dailySaleVelocity?.world?.quantity > 0 ? this.nq?.dailySaleVelocity?.world?.quantity : null,
                    Hq = this.hq?.dailySaleVelocity?.world?.quantity > 0 ? this.hq?.dailySaleVelocity?.world?.quantity : null,
                },
                Datacenter = new() {
                    Nq = this.nq?.dailySaleVelocity?.dc?.quantity > 0 ? this.nq?.dailySaleVelocity?.dc?.quantity : null,
                    Hq = this.hq?.dailySaleVelocity?.dc?.quantity > 0 ? this.hq?.dailySaleVelocity?.dc?.quantity : null,
                },
                Region = new() {
                    Nq = this.nq?.dailySaleVelocity?.region?.quantity > 0 ? this.nq?.dailySaleVelocity?.region?.quantity : null,
                    Hq = this.hq?.dailySaleVelocity?.region?.quantity > 0 ? this.hq?.dailySaleVelocity?.region?.quantity : null,
                },
            }
        };
        return marketBoardData;
    }
}

file class Aggregate {
    public Value? minListing { get; set; }
    public Value? recentPurchase { get; set; }
    public SaleValue? averageSalePrice { get; set; }
    public Value? dailySaleVelocity { get; set; }
}

file class Value {
    public Entry? world { get; set; }
    public Entry? dc { get; set; }
    public Entry? region { get; set; }
}

file class Entry {
    public int? price { get; set; }
    public uint? worldId { get; set; }
    public UnixMilliDateTime? timestamp { get; set; }
    public double? quantity { get; set; }

    public static implicit operator Listing?(Entry? e) {
        if (e == null || e.price == null)
            return null;
        string? world = null, datacenter = null;
        if (e.worldId != null) {
            (world, datacenter, _) = UniversalisClientV2.WorldLookup[e.worldId.Value];
        }

        return new Listing { Price = e.price.Value, Time = e.timestamp, World = world, Datacenter = datacenter };
    }

    public Listing? ToListing(DateTime defaultTime, Dictionary<uint, DateTime> worldUploadTimes) {
        var listing = (Listing?)this;
        if (listing == null)
            return null;
        var time = worldId != null ? worldUploadTimes.GetValueOrDefault(worldId.Value, defaultTime) : defaultTime;
        return listing with { Time = time };
    }
}

file class SaleValue {
    public SaleEntry? world { get; set; }
    public SaleEntry? dc { get; set; }
    public SaleEntry? region { get; set; }
}

file class SaleEntry {
    public double? price { get; set; }
}

file class WorldUploadTime {
    public required uint worldId { get; set; }
    public required UnixMilliDateTime timestamp { get; set; }
}
// ReSharper restore all

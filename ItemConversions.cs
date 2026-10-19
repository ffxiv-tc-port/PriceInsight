using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace PriceInsight;

/// <summary>Lookup from an unsellable item to the marketable item it can be exchanged for. Built once on first use; safe from any thread.</summary>
public class ItemConversions {
    private readonly Lazy<Dictionary<uint, ItemConversion>> map = new(Build);

    public bool TryGet(uint itemId, [MaybeNullWhen(false)] out ItemConversion conversion) => map.Value.TryGetValue(itemId, out conversion);

    private static Dictionary<uint, ItemConversion> Build() {
        var result = new Dictionary<uint, ItemConversion>();
        try {
            foreach (var conversion in ItemConversionSource.LoadSkybuilderInspections(Service.DataManager.Excel)) {
                if (!result.TryAdd(conversion.SourceItemId, conversion))
                    Service.PluginLog.Debug($"Duplicate item conversion for {conversion.SourceItemId}, ignoring");
            }
        } catch (Exception e) {
            Service.PluginLog.Error(e, "Failed to load item conversions");
        }

        Service.PluginLog.Information($"Loaded {result.Count} item conversions");
        return result;
    }
}

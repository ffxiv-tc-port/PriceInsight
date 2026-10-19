using System.Collections.Generic;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace PriceInsight;

/// <summary>An unsellable item: <see cref="AmountRequired"/> of it are exchanged for one marketable <see cref="TargetItemId"/>.</summary>
public sealed record ItemConversion(uint SourceItemId, uint TargetItemId, uint AmountRequired, string TargetName);

/// <summary>Pure game-data reader, no plugin services, so it can be checked offline against an EXD dump.</summary>
public static class ItemConversionSource {
    /// <summary>Diadem Skybuilders' materials and the inspected ones they are exchanged for (HWDGathererInspection).</summary>
    public static IEnumerable<ItemConversion> LoadSkybuilderInspections(ExcelModule excel) {
        var items = excel.GetSheet<Item>();
        foreach (var row in excel.GetSheet<HWDGathererInspection>()) {
            foreach (var entry in row.HWDGathererInspectionData) {
                var targetId = entry.ItemReceived.RowId;
                if (targetId == 0)
                    continue;
                // Mining/botany entries link through GatheringItem, fish entries through FishParameter.
                var sourceId = entry.RequiredItem.ValueNullable?.Item.RowId ?? 0;
                if (sourceId == 0)
                    sourceId = entry.FishParameter.ValueNullable?.Item.RowId ?? 0;
                if (sourceId == 0)
                    continue;
                // Inspected fish are not marketable either; a marketable source already has its own price.
                if (items.GetRowOrDefault(targetId) is not { ItemSearchCategory.RowId: not 0 } target)
                    continue;
                if (items.GetRowOrDefault(sourceId) is not { ItemSearchCategory.RowId: 0 })
                    continue;
                yield return new ItemConversion(sourceId, targetId, entry.AmountRequired == 0 ? 1u : entry.AmountRequired, target.Name.ExtractText());
            }
        }
    }
}

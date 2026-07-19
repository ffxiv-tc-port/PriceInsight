using System;
using ImGuiNET;

namespace PriceInsight;

internal class ConfigUI(PriceInsightPlugin plugin) : IDisposable {
    private bool settingsVisible = false;

    public bool SettingsVisible {
        get => settingsVisible;
        set => settingsVisible = value;
    }

    public void Dispose() {
    }

    public void Draw() {
        if (!SettingsVisible) {
            return;
        }

        var conf = plugin.Configuration;
        if (ImGui.Begin("Price Insight 設定", ref settingsVisible,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) {
            var configValue = conf.RefreshWithAlt;
            if (ImGui.Checkbox("按住 Alt 以重新整理價格", ref configValue)) {
                conf.RefreshWithAlt = configValue;
                conf.Save();
            }

            configValue = conf.PrefetchInventory;
            if (ImGui.Checkbox("預先擷取背包內道具的價格", ref configValue)) {
                conf.PrefetchInventory = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("登入時預先擷取背包、陸行鳥背包與雇員身上所有道具的價格。");

            configValue = conf.UseCurrentWorld;
            if (ImGui.Checkbox("以目前所在的世界作為本服", ref configValue)) {
                conf.UseCurrentWorld = configValue;
                conf.Save();
                plugin.ClearCache();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("你目前所在的世界將被視為你的「本服」。\n適合在跨資料中心旅行時查看當地價格。");

            ImGui.Separator();
            ImGui.PushID(0);

            ImGui.Text("顯示最低價格的範圍：");

            configValue = conf.ShowRegion;
            if (ImGui.Checkbox("大區域", ref configValue)) {
                conf.ShowRegion = configValue;
                conf.Save();
            }
            TooltipRegion();

            configValue = conf.ShowDatacenter;
            if (ImGui.Checkbox("資料中心", ref configValue)) {
                conf.ShowDatacenter = configValue;
                conf.Save();
            }

            configValue = conf.ShowWorld;
            if (ImGui.Checkbox("本服", ref configValue)) {
                conf.ShowWorld = configValue;
                conf.Save();
            }

            ImGui.PopID();
            ImGui.Separator();
            ImGui.PushID(1);

            ImGui.Text("顯示最近成交紀錄的範圍：");

            configValue = conf.ShowMostRecentPurchaseRegion;
            if (ImGui.Checkbox("大區域", ref configValue)) {
                conf.ShowMostRecentPurchaseRegion = configValue;
                conf.Save();
            }
            TooltipRegion();

            configValue = conf.ShowMostRecentPurchase;
            if (ImGui.Checkbox("資料中心", ref configValue)) {
                conf.ShowMostRecentPurchase = configValue;
                conf.Save();
            }

            configValue = conf.ShowMostRecentPurchaseWorld;
            if (ImGui.Checkbox("本服", ref configValue)) {
                conf.ShowMostRecentPurchaseWorld = configValue;
                conf.Save();
            }

            ImGui.PopID();
            ImGui.Separator();

            var selectValue = conf.ShowDailySaleVelocityIn;
            if (ImGui.Combo("顯示每日銷售量", ref selectValue, new[] {"不顯示", "本服", "資料中心", "大區域"}, 4)) {
                conf.ShowDailySaleVelocityIn = selectValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("依據最近 4 天的銷售紀錄，顯示平均每日銷售量。");

            selectValue = conf.ShowAverageSalePriceIn;
            if (ImGui.Combo("顯示平均成交價格", ref selectValue, new[] {"不顯示", "本服", "資料中心", "大區域"}, 4)) {
                conf.ShowAverageSalePriceIn = selectValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("依據最近 4 天的銷售紀錄，顯示平均成交價格。");

            configValue = conf.ShowStackSalePrice;
            if (ImGui.Checkbox("顯示整組出售價格", ref configValue)) {
                conf.ShowStackSalePrice = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("顯示以目前單價計算，出售目前懸停堆疊數量的總價格。");

            configValue = conf.ShowAge;
            if (ImGui.Checkbox("顯示資料更新時間", ref configValue)) {
                conf.ShowAge = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("顯示價格資訊最後一次更新的時間。\n可關閉此選項以減少提示視窗內容過多的問題。");

            configValue = conf.ShowDatacenterOnCrossWorlds;
            if (ImGui.Checkbox("為外部世界顯示所屬資料中心", ref configValue)) {
                conf.ShowDatacenterOnCrossWorlds = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("在顯示整個大區域的價格時，為其他資料中心的世界顯示所屬資料中心。\n可關閉此選項以減少提示視窗內容過多的問題。");

            configValue = conf.ShowBothNqAndHq;
            if (ImGui.Checkbox("永遠同時顯示普通品與高品質價格", ref configValue)) {
                conf.ShowBothNqAndHq = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("同時顯示該道具的普通品與高品質價格。\n關閉後將只顯示目前品質的價格（可按住 Ctrl 切換普通品/高品質）。");
        }

        ImGui.End();
    }

    private static void TooltipRegion() {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("包含所有可透過資料中心旅行前往的資料中心。");
    }
}
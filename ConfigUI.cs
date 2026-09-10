using System;
using Dalamud.Bindings.ImGui;

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
        if (ImGui.Begin("Price Insight Config".Loc(), ref settingsVisible,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) {
            var configValue = conf.RefreshWithAlt;
            if (ImGui.Checkbox("Tap Alt to refresh prices".Loc(), ref configValue)) {
                conf.RefreshWithAlt = configValue;
                conf.Save();
            }

            configValue = conf.PrefetchInventory;
            if (ImGui.Checkbox("Prefetch prices for items in inventory".Loc(), ref configValue)) {
                conf.PrefetchInventory = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Prefetch prices for all items in inventory, chocobo saddlebag and retainer when logging in.".Loc());

            configValue = conf.UseCurrentWorld;
            if (ImGui.Checkbox("Use current world as home world".Loc(), ref configValue)) {
                conf.UseCurrentWorld = configValue;
                conf.Save();
                plugin.ClearCache();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The current world you're on will be considered your \"home world\".\nUseful if you're datacenter travelling and want to see prices there.".Loc());

            ImGui.Separator();
            ImGui.PushID(0);

            ImGui.Text("Show cheapest price in:".Loc());

            configValue = conf.ShowRegion;
            if (ImGui.Checkbox("Region".Loc(), ref configValue)) {
                conf.ShowRegion = configValue;
                conf.Save();
            }
            TooltipRegion();

            configValue = conf.ShowDatacenter;
            if (ImGui.Checkbox("Datacenter".Loc(), ref configValue)) {
                conf.ShowDatacenter = configValue;
                conf.Save();
            }

            configValue = conf.ShowWorld;
            if (ImGui.Checkbox("Home world".Loc(), ref configValue)) {
                conf.ShowWorld = configValue;
                conf.Save();
            }

            ImGui.PopID();
            ImGui.Separator();
            ImGui.PushID(1);

            ImGui.Text("Show most recent purchase in:".Loc());

            configValue = conf.ShowMostRecentPurchaseRegion;
            if (ImGui.Checkbox("Region".Loc(), ref configValue)) {
                conf.ShowMostRecentPurchaseRegion = configValue;
                conf.Save();
            }
            TooltipRegion();

            configValue = conf.ShowMostRecentPurchase;
            if (ImGui.Checkbox("Datacenter".Loc(), ref configValue)) {
                conf.ShowMostRecentPurchase = configValue;
                conf.Save();
            }

            configValue = conf.ShowMostRecentPurchaseWorld;
            if (ImGui.Checkbox("Home world".Loc(), ref configValue)) {
                conf.ShowMostRecentPurchaseWorld = configValue;
                conf.Save();
            }

            ImGui.PopID();
            ImGui.Separator();

            var selectValue = conf.ShowDailySaleVelocityIn;
            if (ImGui.Combo("Show sales per day".Loc(), ref selectValue, ["Do not show".Loc(), "World".Loc(), "Datacenter".Loc(), "Region".Loc()])) {
                conf.ShowDailySaleVelocityIn = selectValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show the average sales per day based on sales of the last 4 days.".Loc());

            selectValue = conf.ShowAverageSalePriceIn;
            if (ImGui.Combo("Show average sale price".Loc(), ref selectValue, ["Do not show".Loc(), "World".Loc(), "Datacenter".Loc(), "Region".Loc()])) {
                conf.ShowAverageSalePriceIn = selectValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show the average sale price based on sales of the last 4 days.".Loc());

            configValue = conf.ShowStackSalePrice;
            if (ImGui.Checkbox("Show stack sale price".Loc(), ref configValue)) {
                conf.ShowStackSalePrice = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show the price of the hovered stack if sold at the given unit price.".Loc());

            configValue = conf.ShowAge;
            if (ImGui.Checkbox("Show age of data".Loc(), ref configValue)) {
                conf.ShowAge = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show when the price info was last refreshed.\nCan be turned off to reduce tooltip bloat.".Loc());

            configValue = conf.ShowDatacenterOnCrossWorlds;
            if (ImGui.Checkbox("Show datacenter for foreign worlds".Loc(), ref configValue)) {
                conf.ShowDatacenterOnCrossWorlds = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show the datacenter for worlds from other datacenters when displaying prices for the entire region.\nCan be turned off to reduce tooltip bloat.".Loc());

            configValue = conf.ShowMarketbuddyListings;
            if (ImGui.Checkbox("Show live listings from Marketbuddy".Loc(), ref configValue)) {
                conf.ShowMarketbuddyListings = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Marketbuddy passively remembers the real listings it has seen on the market board for the world you are on (up to 1 hour).\nThose are real, world-local prices; Universalis is crowd-uploaded and can be days old.\nHas no effect when Marketbuddy is not installed."
                        .Loc());

            configValue = conf.ShowBothNqAndHq;
            if (ImGui.Checkbox("Always display NQ and HQ prices".Loc(), ref configValue)) {
                conf.ShowBothNqAndHq = configValue;
                conf.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show the prices for both NQ and HQ of an item.\nWhen turned off will only display price for the current quality (use Ctrl to switch between NQ and HQ).".Loc());
        }

        ImGui.End();
    }

    private static void TooltipRegion() {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include all datacenters available via datacenter traveling.".Loc());
    }
}

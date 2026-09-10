using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace PriceInsight;

public class PriceInsightPlugin : IDalamudPlugin {
    public Configuration Configuration { get; }
    public ItemPriceTooltip ItemPriceTooltip { get; }
    public Hooks Hooks { get; }
    public ItemPriceLookup ItemPriceLookup { get; private set; }
    public UniversalisClientV2 UniversalisClientV2 { get; }

    /// <summary>Marketbuddy 被動市場快取的讀取端(沒裝時每次查詢一律靜默回 null)。</summary>
    internal MarketbuddyBridge MarketbuddyBridge { get; }

    private readonly ConfigUI configUi;

    public PriceInsightPlugin(IDalamudPluginInterface pluginInterface) {
        Service.Initialize(pluginInterface);
        Localization.Init(pluginInterface.AssemblyLocation.DirectoryName);

        Configuration = Configuration.Get(pluginInterface);

        UniversalisClientV2 = new UniversalisClientV2();
        // 🔴 必須排在 ItemPriceTooltip 之前:提示視窗一被畫就會用到它。
        //    建構子只是把 CallGate 的訂閱物件拿到手,不會去呼叫 Marketbuddy。
        MarketbuddyBridge = new MarketbuddyBridge(pluginInterface);
        ItemPriceLookup = new ItemPriceLookup(this);
        ItemPriceTooltip = new ItemPriceTooltip(this);
        Hooks = new Hooks(this);
        configUi = new ConfigUI(this);

        Service.CommandManager.AddHandler("/priceinsight", new CommandInfo((_, _) => OpenConfigUI()) { HelpMessage = "Price Insight Configuration Menu".Loc() });

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, ["Inventory", "InventoryLarge", "InventoryExpansion"], HandleInventoryUpdate);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "InventoryBuddy", HandleSaddlebagOpen);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreSetup,  ["InventoryRetainer", "InventoryRetainerLarge"], HandleRetainerOpen);

        pluginInterface.UiBuilder.Draw += () => configUi.Draw();
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUI;
        Service.ClientState.Logout += ClearCache;
        Service.ClientState.Login += ClientOnLogin;
        Service.MarketBoard.OfferingsReceived += MarketBoardOnOfferingsReceived;
        Service.MarketBoard.HistoryReceived += MarketBoardOnHistoryReceived;
    }

    private void MarketBoardOnOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings) {
        try {
            ItemPriceLookup.ApplyMarketBoardOfferings(currentOfferings);
        } catch (Exception e) {
            Service.PluginLog.Error(e, "Failed to apply market board offerings to the price cache");
        }
    }

    private void MarketBoardOnHistoryReceived(IMarketBoardHistory history) {
        try {
            ItemPriceLookup.ApplyMarketBoardHistory(history);
        } catch (Exception e) {
            Service.PluginLog.Error(e, "Failed to apply market board history to the price cache");
        }
    }

    private void ClientOnLogin() {
        CheckInventories(InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4);
    }

    private DateTime lastCheckInventory = DateTime.MinValue;
    private void HandleInventoryUpdate(AddonEvent type, AddonArgs args) {
        if ((DateTime.Now - lastCheckInventory).TotalMinutes < 1) return;
        CheckInventories(InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4);
        lastCheckInventory = DateTime.Now;
    }

    private DateTime lastCheckSaddlebag = DateTime.MinValue;
    private void HandleSaddlebagOpen(AddonEvent type, AddonArgs args) {
        if ((DateTime.Now - lastCheckSaddlebag).TotalSeconds < 30) return;
        CheckInventories(InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2);
        lastCheckSaddlebag = DateTime.Now;
    }

    private DateTime lastCheckRetainer = DateTime.MinValue;
    private void HandleRetainerOpen(AddonEvent type, AddonArgs args) {
        if ((DateTime.Now - lastCheckRetainer).TotalSeconds < 5) return;
        CheckInventories(InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
            InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7);
        lastCheckRetainer = DateTime.Now;
    }

    public void ClearCache(int type = 0, int code = 0) {
        var ipl = ItemPriceLookup;
        ItemPriceLookup = new ItemPriceLookup(this);
        ipl.Dispose();
    }

    private void CheckInventories(params InventoryType[] inventoriesToScan) {
        if (Service.PlayerState.ContentId == 0 || !ItemPriceLookup.CheckReady())
            return;
        if (!Configuration.PrefetchInventory)
            return;
        Service.PluginLog.Debug($"Prefetch: checking {inventoriesToScan.Length} inventories");
        try {
            var items = new HashSet<uint>();
            unsafe {
                var manager = InventoryManager.Instance();
                foreach (var inv in inventoriesToScan) {
                    var container = manager->GetInventoryContainer(inv);
                    if (container == null || !container->IsLoaded)
                        continue;
                    for (var i = 0; i < container->Size; i++) {
                        var item = &container->Items[i];
                        var itemId = item->ItemId;
                        if (itemId != 0)
                            items.Add(itemId);
                    }
                }
            }

            if (items.Count > 0) {
                Service.PluginLog.Debug($"Prefetch: queueing {items.Count} items");
                ItemPriceLookup.Fetch(items);
            }
        } catch (Exception e) {
            Service.PluginLog.Error(e, "Failed to process update");
        }
    }

    private void OpenConfigUI() {
        configUi.SettingsVisible = true;
    }

    public void Dispose() {
        Service.CommandManager.RemoveHandler("/priceinsight");
        Service.ClientState.Logout -= ClearCache;
        Service.ClientState.Login -= ClientOnLogin;
        Service.MarketBoard.OfferingsReceived -= MarketBoardOnOfferingsReceived;
        Service.MarketBoard.HistoryReceived -= MarketBoardOnHistoryReceived;
        Hooks.Dispose();
        ItemPriceTooltip.Dispose();
        ItemPriceLookup.Dispose();
        UniversalisClientV2.Dispose();
        configUi.Dispose();
    }
}

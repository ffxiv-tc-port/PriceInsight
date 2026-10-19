using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PriceInsight;

public class Hooks : IDisposable {
    private readonly PriceInsightPlugin plugin;

    private unsafe delegate byte AgentItemDetailOnItemHovered(void* a1, void* a2, void* a3, void* a4, uint a5, uint a6, int* a7);

    // 兩條特徵碼都明寫 Fallible：遊戲改版後對不上時欄位留 null，只停掉用到它的那一項
    // （建構子各印一行 Warning），不讓整個插件載入失敗。
    [Signature("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 48 89 9C 24 ?? ?? ?? ?? 4C 89 A4 24", DetourName = nameof(AgentItemDetailOnItemHoveredDetour), Fallibility = Fallibility.Fallible)]
    private readonly Hook<AgentItemDetailOnItemHovered>? agentItemDetailOnItemHovered = null;

    /// <summary>特徵碼對不上時是 null。呼叫前一定要先判：呼叫 null 函式指標是攔不到的存取違規。</summary>
    [Signature("E8 ?? ?? ?? ?? 45 85 ED 4C 8B AC 24", Fallibility = Fallibility.Fallible)]
    public readonly unsafe delegate*unmanaged[Thiscall]<AtkUnitBase*, short, short, byte, void> ItemDetailSetPositionPreservingOriginal = null;

    /// <summary>false＝取不到滑鼠所指道具的堆疊數量，整組出售價格不會顯示。</summary>
    public bool StackQuantityAvailable => agentItemDetailOnItemHovered != null;

    public unsafe Hooks(PriceInsightPlugin plugin) {
        this.plugin = plugin;
        try {
            Service.GameInteropProvider.InitializeFromAttributes(this);
        } catch (Exception e) {
            Service.PluginLog.Error(e, "Hooks: 特徵碼初始化中途失敗，沒解到的項目見下面的警告");
        }

        if (agentItemDetailOnItemHovered == null)
            Service.PluginLog.Warning("Hooks: 特徵碼對不上 AgentItemDetailOnItemHovered（遊戲改版後需要更新）：取不到滑鼠所指道具的堆疊數量，「顯示整組出售價格」暫時不會顯示，其餘價格照常");
        if (ItemDetailSetPositionPreservingOriginal == null)
            Service.PluginLog.Warning("Hooks: 特徵碼對不上 ItemDetailSetPositionPreservingOriginal（遊戲改版後需要更新）：加上價格後的道具提示視窗超出畫面下緣時不會自動上移，價格照常顯示");

        agentItemDetailOnItemHovered?.Enable();
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "ItemDetail", (_, args) => ItemPriceTooltip.RestoreToNormal((AtkUnitBase*)args.Addon.Address));
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "ItemDetail", (_, args) => plugin.ItemPriceTooltip.OnItemTooltip((AtkUnitBase*)args.Addon.Address));
    }

    private unsafe byte AgentItemDetailOnItemHoveredDetour(void* a1, void* a2, void* a3, void* a4, uint a5, uint a6, int* a7) {
        var ret = agentItemDetailOnItemHovered!.OriginalDisposeSafe(a1, a2, a3, a4, a5, a6, a7);
        try {
            plugin.ItemPriceTooltip.LastItemQuantity = a7[5];
        } catch (Exception e) {
            plugin.ItemPriceTooltip.LastItemQuantity = null;
            Service.PluginLog.Error(e, "Failed to read last item quantity");
        }

        return ret;
    }

    public void Dispose() {
        agentItemDetailOnItemHovered?.Dispose();
    }

}

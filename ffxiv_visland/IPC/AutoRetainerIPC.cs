using AutoRetainerAPI.Configuration;
using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;

namespace visland.IPC;

// 契約：每個方法都不擲例外（呼叫端是沒有自己 try 的 GatherRouteExec.Update 與除錯分頁），失敗回「沒有資料」，每個端點只記一次 Information。
// OfflineCharacterData 經 CallGate 轉型時會跑 ECommons 的欄位初始式，而 visland 不初始化 ECommons ⇒ 必擲；
// 所以這支失敗後 60 秒內不再呼叫（每幀每角色擲一次例外很貴）。刻意不讓整合「真的能用」：那會接上事件觸發的多角色模式接手鏈，要使用者裁。
public class AutoRetainerIPC {
    private const long OfflineDataRetryMs = 60_000;
    private readonly HashSet<string> _reported = [];
    private long _offlineDataBlockedUntil;

    private readonly ICallGateSubscriber<bool> _isBusy;
    private readonly ICallGateSubscriber<bool> _getMultiModeEnabled;
    private readonly ICallGateSubscriber<bool, object> _setMultiModeEnabled;
    private readonly ICallGateSubscriber<List<ulong>> _getRegisteredCIDs;
    private readonly ICallGateSubscriber<ulong, OfflineCharacterData> _getOfflineCharacterData;

    public AutoRetainerIPC() {
        _isBusy = Service.Interface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy");
        _getMultiModeEnabled = Service.Interface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled");
        _setMultiModeEnabled = Service.Interface.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled");
        _getRegisteredCIDs = Service.Interface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs");
        _getOfflineCharacterData = Service.Interface.GetIpcSubscriber<ulong, OfflineCharacterData>("AutoRetainer.GetOfflineCharacterData");
    }

    public bool IsBusy() {
        try {
            return _isBusy.HasFunction && _isBusy.InvokeFunc();
        }
        catch (Exception ex) {
            ReportOnce("PluginState.IsBusy", ex);
            return false;
        }
    }

    public bool GetMultiEnabled() {
        try {
            return _getMultiModeEnabled.HasFunction && _getMultiModeEnabled.InvokeFunc();
        }
        catch (Exception ex) {
            ReportOnce("GetMultiModeEnabled", ex);
            return false;
        }
    }

    public void SetMultiEnabled(bool enabled) {
        try {
            if (_setMultiModeEnabled.HasAction)
                _setMultiModeEnabled.InvokeAction(enabled);
        }
        catch (Exception ex) {
            ReportOnce("SetMultiModeEnabled", ex);
        }
    }

    public List<ulong> GetRegisteredCIDs() {
        try {
            return _getRegisteredCIDs.HasFunction ? _getRegisteredCIDs.InvokeFunc() ?? [] : [];
        }
        catch (Exception ex) {
            ReportOnce("GetRegisteredCIDs", ex);
            return [];
        }
    }

    public OfflineCharacterData? GetOfflineCharacterData(ulong cid) {
        if (Environment.TickCount64 < _offlineDataBlockedUntil)
            return null;
        try {
            return _getOfflineCharacterData.HasFunction ? _getOfflineCharacterData.InvokeFunc(cid) : null;
        }
        catch (Exception ex) {
            _offlineDataBlockedUntil = Environment.TickCount64 + OfflineDataRetryMs;
            ReportOnce("GetOfflineCharacterData", ex);
            return null;
        }
    }

    private void ReportOnce(string endpoint, Exception ex) {
        if (_reported.Add(endpoint))
            Service.Log.Information($"[AutoRetainer 整合] 呼叫 AutoRetainer.{endpoint} 失敗（{ex.GetType().Name}: {ex.Message}），當作沒有資料、路線照常跑；同一個端點只記這一次。");
    }
}

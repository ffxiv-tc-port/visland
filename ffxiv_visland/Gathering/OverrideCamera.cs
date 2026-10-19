using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;
using visland.Helpers;

namespace visland.Gathering;

// NOTE: no hand-rolled camera struct on purpose. Hard-coded FieldOffsets silently read/write the wrong
// fields when the native layout shifts between game versions (it has: +0x10), so use
// FFXIVClientStructs.FFXIV.Client.Game.Camera, which carries the layout of the pin we build on.

public unsafe class OverrideCamera : IDisposable {
    public bool Enabled {
        get => _rmiCameraHook?.IsEnabled ?? false;
        set {
            if (_rmiCameraHook == null)
                return;
            if (value)
                _rmiCameraHook.Enable();
            else
                _rmiCameraHook.Disable();
        }
    }

    public bool IgnoreUserInput;
    public Angle DesiredAzimuth;
    public Angle DesiredAltitude;
    public Angle SpeedH = 360.Degrees();
    public Angle SpeedV = 360.Degrees();

    private delegate void RMICameraDelegate(Camera* self, int inputMode, float speedH, float speedV);
    private readonly Hook<RMICameraDelegate>? _rmiCameraHook;

    public OverrideCamera() {
        // Scan fallibly: this prologue signature has failed to match on an older client build, and a miss should
        // just disable camera auto-facing instead of throwing out of the ctor and taking down the whole plugin.
        var rmiCameraAddr = Service.SigScanner.TryScanText("48 8B C4 53 48 81 EC ?? ?? ?? ?? 44 0F 29 50 ??", out var addr) ? addr : IntPtr.Zero;
        if (rmiCameraAddr != IntPtr.Zero) {
            _rmiCameraHook = Service.Hook.HookFromAddress<RMICameraDelegate>(rmiCameraAddr, RMICameraDetour);
            Service.Log.Information($"RMICamera address: 0x{rmiCameraAddr:X}");
        } else {
            Service.Log.Warning("RMICamera signature not found - camera auto-facing disabled");
        }
    }

    public void Dispose() => _rmiCameraHook?.Dispose();

    // fail-closed: a detour is called directly by native code, so a managed exception escaping it unwinds
    // through native frames with no handler. Everything on top of Original() runs inside a try; the degraded
    // behaviour is "don't override" - Original has already run, so the game's own camera handling passes through.
    // NOTE: this catches managed exceptions (e.g. ClientStructs' InvalidOperationException when a signature
    // stops resolving after a patch), NOT AccessViolationException (corrupted-state, uncatchable).
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex) {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information because users are asked to report it.
        var now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        Service.Log.Information($"OverrideCamera: camera override threw, leaving the game's own camera input alone (total {_detourErrors}): {ex}");
    }

    private void RMICameraDetour(Camera* self, int inputMode, float speedH, float speedV) {
        _rmiCameraHook!.OriginalDisposeSafe(self, inputMode, speedH, speedV);
        try {
            if (self == null)
                return;
            if (IgnoreUserInput || inputMode == 0) {
                // Framework.Instance() 回的是指標槽裡的內容,遊戲還沒建好 / 正在拆掉 Framework 時合法為 null;裸解參考是攔不到的 AccessViolationException,而這個 detour 是原生程式碼直接呼叫的。
                // fail-closed:拿不到就當這一幀 dt = 0 ⇒ maxH/maxV = 0,等於這一幀不轉鏡頭(Original() 已經先跑過了)。每幀都會跑,刻意不寫 log。
                var framework = Framework.Instance();
                var dt = framework == null ? 0f : framework->FrameDeltaTime;
                var deltaH = (DesiredAzimuth - self->DirH.Radians()).Normalized();
                var deltaV = (DesiredAltitude - self->DirV.Radians()).Normalized();
                var maxH = SpeedH.Rad * dt;
                var maxV = SpeedV.Rad * dt;
                self->InputDeltaH = Math.Clamp(deltaH.Rad, -maxH, maxH);
                self->InputDeltaV = Math.Clamp(deltaV.Rad, -maxV, maxV);
            }
        }
        catch (Exception ex) {
            OnDetourError(ex);
        }
    }
}

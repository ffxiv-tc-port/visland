using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;
using visland.Helpers;

namespace visland.Gathering;

// NOTE: the old hand-rolled `CameraEx` struct is gone on purpose (same fix as vnavmesh/Lifestream on
// TC 7.20). Its 0x130-based FieldOffsets were correct for TC 7.15 but TC 7.20 shifted the native
// struct +0x10, so DirH at 0x130 now reads FoV and writing "InputDeltaH" at 0x140 actually clobbered
// DirH. FFXIVClientStructs.FFXIV.Client.Game.Camera carries the current layout and is verified
// against the API13 pin we build on, so use it directly.

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
        // This prologue signature didn't match TC 7.15's compiled shape of the function, but on TC
        // 7.20 it matches exactly once (verified via vnavmesh's port). Still scan fallibly so a miss
        // just disables camera auto-facing instead of throwing out of the ctor and taking down the
        // whole plugin.
        var rmiCameraAddr = Service.SigScanner.TryScanText("48 8B C4 53 48 81 EC ?? ?? ?? ?? 44 0F 29 50 ??", out var addr) ? addr : IntPtr.Zero;
        if (rmiCameraAddr != IntPtr.Zero) {
            _rmiCameraHook = Service.Hook.HookFromAddress<RMICameraDelegate>(rmiCameraAddr, RMICameraDetour);
            Service.Log.Information($"RMICamera address: 0x{rmiCameraAddr:X}");
        } else {
            Service.Log.Warning("RMICamera signature not found - camera auto-facing disabled");
        }
    }

    public void Dispose() => _rmiCameraHook?.Dispose();

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. Everything we
    // add on top of Original() therefore runs inside a try, and the degraded behaviour is "don't
    // override" - Original has already run, so the game's own camera handling passes through intact.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in
    // .NET Core). What it catches is managed exceptions - most importantly the
    // InvalidOperationException that ClientStructs' [StaticAddress]/[MemberFunction] members throw
    // when their signature stops resolving after a game patch.
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex) {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 1 - Debug is captured too, but drowned by the 100k+ Debug lines a single log file holds.
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
                // 🔴 上面那圈 try/catch 擋的是「特徵碼失配 -> ThrowNullAddress 丟 InvalidOperationException」,
                //    但那不是這裡唯一的失敗形式。Framework.Instance() 是
                //    [StaticAddress("48 8B 1D ?? ?? ?? ?? 8B 7C 24 64", 3, isPointer: true)],
                //    產生器對 isPointer:true 產出的是「if (ppInstance is null) Throw...; return *ppInstance;」
                //    —— 判空判的是**外層那個指標槽的位址**,回傳的卻是**槽裡的內容**,而那個內容
                //    在遊戲還沒建好 / 正在拆掉 Framework 時合法為 null,且完全沒被判過。
                //    裸解參考它 = AccessViolationException,在 .NET Core 是 corrupted-state exception,
                //    catch (Exception) 攔不到 —— 這個 detour 是原生程式碼直接呼叫的,沒有第二道防線。
                // fail-closed:拿不到就當這一幀 dt = 0。下面 maxH/maxV = 速度 * 0 = 0,
                //    Math.Clamp(delta, -0, 0) = 0,等於這一幀不轉鏡頭 —— Original() 已經先跑過了,
                //    遊戲自己的鏡頭處理原封不動。每幀都會跑,刻意不寫 log。
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

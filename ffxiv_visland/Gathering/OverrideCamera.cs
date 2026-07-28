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

    private void RMICameraDetour(Camera* self, int inputMode, float speedH, float speedV) {
        _rmiCameraHook!.Original(self, inputMode, speedH, speedV);
        if (IgnoreUserInput || inputMode == 0) {
            var dt = Framework.Instance()->FrameDeltaTime;
            var deltaH = (DesiredAzimuth - self->DirH.Radians()).Normalized();
            var deltaV = (DesiredAltitude - self->DirV.Radians()).Normalized();
            var maxH = SpeedH.Rad * dt;
            var maxV = SpeedV.Rad * dt;
            self->InputDeltaH = Math.Clamp(deltaH.Rad, -maxH, maxH);
            self->InputDeltaV = Math.Clamp(deltaV.Rad, -maxV, maxV);
        }
    }
}

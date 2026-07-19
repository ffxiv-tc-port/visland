using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;
using System.Runtime.InteropServices;
using visland.Helpers;

namespace visland.Gathering;

[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public unsafe struct CameraEx {
    // TC's client predates the game patch that shifted these offsets forward by 0x10 (same "api13"
    // struct-layout shift documented/fixed in the vnavmesh port) - use the pre-shift offsets.
    [FieldOffset(0x130)] public float DirH;
    [FieldOffset(0x134)] public float DirV;
    [FieldOffset(0x138)] public float InputDeltaHAdjusted;
    [FieldOffset(0x13C)] public float InputDeltaVAdjusted;
    [FieldOffset(0x140)] public float InputDeltaH;
    [FieldOffset(0x144)] public float InputDeltaV;
    [FieldOffset(0x148)] public float DirVMin;
    [FieldOffset(0x14C)] public float DirVMax;
}

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

    private delegate void RMICameraDelegate(CameraEx* self, int inputMode, float speedH, float speedV);
    private readonly Hook<RMICameraDelegate>? _rmiCameraHook;

    public OverrideCamera() {
        // Global's function-prologue signature ("48 8B C4 53 48 81 EC ?? ?? ?? ?? 44 0F 29 50 ??")
        // doesn't match TC's compiled shape of this function - scan fallibly so a miss just disables
        // camera auto-facing instead of throwing out of the ctor and taking down the whole plugin.
        var rmiCameraAddr = Service.SigScanner.TryScanText("48 8B C4 53 48 81 EC ?? ?? ?? ?? 44 0F 29 50 ??", out var addr) ? addr : IntPtr.Zero;
        if (rmiCameraAddr != IntPtr.Zero) {
            _rmiCameraHook = Service.Hook.HookFromAddress<RMICameraDelegate>(rmiCameraAddr, RMICameraDetour);
            Service.Log.Information($"RMICamera address: 0x{rmiCameraAddr:X}");
        } else {
            Service.Log.Warning("RMICamera signature not found - camera auto-facing disabled");
        }
    }

    public void Dispose() => _rmiCameraHook?.Dispose();

    private void RMICameraDetour(CameraEx* self, int inputMode, float speedH, float speedV) {
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

using Dalamud.Game.Config;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using visland.Helpers;

namespace visland.Gathering;

[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public unsafe struct PlayerMoveControllerFlyInput {
    [FieldOffset(0x0)] public float Forward;
    [FieldOffset(0x4)] public float Left;
    [FieldOffset(0x8)] public float Up;
    [FieldOffset(0xC)] public float Turn;
    [FieldOffset(0x10)] public float u10;
    [FieldOffset(0x14)] public byte DirMode;
    [FieldOffset(0x15)] public byte HaveBackwardOrStrafe;
}

public unsafe class OverrideMovement : IDisposable {
    public bool Enabled {
        get => _rmiWalkHook?.IsEnabled ?? false;
        set {
            if (value) {
                _rmiWalkHook?.Enable();
                _rmiFlyHook?.Enable();
            }
            else {
                _rmiWalkHook?.Disable();
                _rmiFlyHook?.Disable();
            }
        }
    }

    public bool IgnoreUserInput;
    public Vector3 DesiredPosition;
    public float Precision = 0.01f;

    private bool _legacyMode;

    private delegate bool RMIWalkIsInputEnabled(void* self);
    private readonly RMIWalkIsInputEnabled? _rmiWalkIsInputEnabled1;
    private readonly RMIWalkIsInputEnabled? _rmiWalkIsInputEnabled2;

    private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
    private readonly Hook<RMIWalkDelegate>? _rmiWalkHook;

    private delegate void RMIFlyDelegate(void* self, PlayerMoveControllerFlyInput* result);
    private readonly Hook<RMIFlyDelegate>? _rmiFlyHook;

    public OverrideMovement() {
        // Scan all four signatures fallibly (same pattern as OverrideCamera): a broken signature
        // after a game patch should only degrade movement assist, not throw out of the ctor and
        // (via Service.Init) take the whole plugin down.
        if (Service.SigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 75 10 38 43 3C", out var rmiWalkIsInputEnabled1Addr) &&
            Service.SigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 75 03 88 47 3F", out var rmiWalkIsInputEnabled2Addr)) {
            Service.Log.Information($"RMIWalkIsInputEnabled1 address: 0x{rmiWalkIsInputEnabled1Addr:X}");
            Service.Log.Information($"RMIWalkIsInputEnabled2 address: 0x{rmiWalkIsInputEnabled2Addr:X}");
            _rmiWalkIsInputEnabled1 = Marshal.GetDelegateForFunctionPointer<RMIWalkIsInputEnabled>(rmiWalkIsInputEnabled1Addr);
            _rmiWalkIsInputEnabled2 = Marshal.GetDelegateForFunctionPointer<RMIWalkIsInputEnabled>(rmiWalkIsInputEnabled2Addr);
        }
        else
            Service.Log.Warning("RMIWalkIsInputEnabled signature(s) not found - walk movement override disabled");

        // The walk detour calls both IsInputEnabled delegates, so only hook walk when they resolved.
        if (_rmiWalkIsInputEnabled1 != null && Service.SigScanner.TryScanText("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", out var rmiWalkAddr)) {
            _rmiWalkHook = Service.Hook.HookFromAddress<RMIWalkDelegate>(rmiWalkAddr, RMIWalkDetour);
            Service.Log.Information($"RMIWalk address: 0x{rmiWalkAddr:X}");
        }
        else if (_rmiWalkIsInputEnabled1 != null)
            Service.Log.Warning("RMIWalk signature not found - walk movement override disabled");

        if (Service.SigScanner.TryScanText("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8", out var rmiFlyAddr)) {
            _rmiFlyHook = Service.Hook.HookFromAddress<RMIFlyDelegate>(rmiFlyAddr, RMIFlyDetour);
            Service.Log.Information($"RMIFly address: 0x{rmiFlyAddr:X}");
        }
        else
            Service.Log.Warning("RMIFly signature not found - fly movement override disabled");

        Service.GameConfig.UiControlChanged += OnConfigChanged;
        UpdateLegacyMode();
    }

    public void Dispose() {
        _rmiWalkHook?.Dispose();
        _rmiFlyHook?.Dispose();
        Service.GameConfig.UiControlChanged -= OnConfigChanged;
    }

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. Everything we
    // add on top of Original() therefore runs inside a try, and the degraded behaviour is "don't
    // override" - Original has already run, so the player's own movement input passes through intact.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in
    // .NET Core). What it catches is managed exceptions - most importantly the
    // InvalidOperationException that ClientStructs' [StaticAddress]/[MemberFunction] members throw
    // when their signature stops resolving after a game patch.
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex) {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 2.
        var now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        Service.Log.Information($"OverrideMovement: movement override threw, leaving the game's own movement input alone (total {_detourErrors}): {ex}");
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk) {
        // only hooked when both IsInputEnabled delegates resolved, see ctor
        _rmiWalkHook!.OriginalDisposeSafe(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        try {
            var movementAllowed = bAdditiveUnk == 0 && _rmiWalkIsInputEnabled1!(self) && _rmiWalkIsInputEnabled2!(self);
            if (movementAllowed && (IgnoreUserInput || *sumLeft == 0 && *sumForward == 0) && DirectionToDestination(false) is var relDir && relDir != null) {
                var dir = relDir.Value.h.ToDirection();
                *sumLeft = dir.X;
                *sumForward = dir.Y;
            }
        }
        catch (Exception ex) {
            OnDetourError(ex);
        }
    }

    private void RMIFlyDetour(void* self, PlayerMoveControllerFlyInput* result) {
        _rmiFlyHook!.OriginalDisposeSafe(self, result);
        try {
            if ((IgnoreUserInput || result->Forward == 0 && result->Left == 0 && result->Up == 0) && DirectionToDestination(true) is var relDir && relDir != null) {
                var dir = relDir.Value.h.ToDirection();
                result->Forward = dir.Y;
                result->Left = dir.X;
                result->Up = relDir.Value.v.Rad;
            }
        }
        catch (Exception ex) {
            OnDetourError(ex);
        }
    }

    private (Angle h, Angle v)? DirectionToDestination(bool allowVertical) {
        var player = Service.ClientState.LocalPlayer;
        if (player == null)
            return null;

        var dist = DesiredPosition - player.Position;
        if (dist.LengthSquared() <= Precision * Precision)
            return null;

        var dirH = Angle.FromDirectionXZ(dist);
        var dirV = allowVertical ? Angle.FromDirection(new(dist.Y, new Vector2(dist.X, dist.Z).Length())) : default;

        var activeCamera = _legacyMode ? TryGetActiveCamera() : null;
        var refDir = activeCamera != null
            ? activeCamera->DirH.Radians() + 180.Degrees()
            : player.Rotation.Radians();
        return (dirH - refDir, dirV);
    }

    // CameraManager.GetActiveCamera() is a ClientStructs [MemberFunction], and CameraManager.Instance()
    // just forwards to Control.Instance(), a [StaticAddress]. When either signature stops resolving
    // they *throw* InvalidOperationException (InteropGenerator's ThrowHelper.ThrowNullAddress) instead
    // of returning null - so a null check on Instance() was never a guard against a broken signature.
    // This path is reached from the RMIWalk/RMIFly detours, i.e. it would be a managed exception
    // thrown inside a detour on every single frame. Check the resolved addresses up front and skip the
    // whole camera-reference path instead; legacy mode then falls back to the character's own facing
    // (steering is wrong-ish rather than fatal). The GetActiveCamera() result is null-checked too - it
    // was dereferenced unguarded before.
    private static bool CameraApiResolved
        => FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Addresses.Instance.Value != 0
        && CameraManager.Addresses.GetActiveCamera.Value != 0;

    private static FFXIVClientStructs.FFXIV.Client.Game.Camera* TryGetActiveCamera() {
        if (!CameraApiResolved)
            return null;
        var mgr = CameraManager.Instance();
        return mgr != null ? mgr->GetActiveCamera() : null;
    }

    private void OnConfigChanged(object? sender, ConfigChangeEvent evt) => UpdateLegacyMode();
    private void UpdateLegacyMode() {
        _legacyMode = Service.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;
        Service.Log.Debug($"Legacy mode is now {(_legacyMode ? "enabled" : "disabled")}");
    }
}

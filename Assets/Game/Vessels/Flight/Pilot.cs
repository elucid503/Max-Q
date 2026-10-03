using System;

using MaxQ.Sim.Numerics;
using MaxQ.Sim.Vessels;

using UnityEngine.InputSystem;

namespace MaxQ.Game.Vessels.Flight;

/// <summary>The keyboard at the controls, KSP's layout: W/S pitch, A/D yaw, Q/E roll; H/N translate forward and back
/// along the stack, I/K and J/L across it; shift and ctrl run the throttle up and down, Z full, X cut; space stages;
/// period and comma step the time warp; T holds the attitude and Y the prograde, each toggling the assist off again.</summary>
public sealed class Pilot {

    private const double ThrottleRate = 0.5;

    private double _throttle;
    private Hold _hold;

    public bool StageRequested { get; private set; }

    /// <summary>+1 or -1 when the warp is stepped this frame.</summary>
    public int WarpStep { get; private set; }

    /// <summary>The controls this frame; while not <paramref name="listening"/> the keys are left to the free camera and the
    /// throttle holds where it was.</summary>
    public Controls Read(double deltaSeconds, bool listening) {

        Keyboard keys = Keyboard.current;

        StageRequested = false;
        WarpStep = 0;

        if (keys == null || !listening) {

            return new Controls(_throttle, Vector3d.Zero, Vector3d.Zero, _hold);

        }

        _throttle = Math.Clamp(_throttle + ThrottleRate * deltaSeconds * Axis(keys.leftShiftKey.isPressed, keys.leftCtrlKey.isPressed), 0.0, 1.0);

        if (keys.zKey.wasPressedThisFrame) {

            _throttle = 1.0;

        }

        if (keys.xKey.wasPressedThisFrame) {

            _throttle = 0.0;

        }

        if (keys.tKey.wasPressedThisFrame) {

            _hold = _hold == Hold.Attitude ? Hold.Off : Hold.Attitude;

        }

        if (keys.yKey.wasPressedThisFrame) {

            _hold = _hold == Hold.Prograde ? Hold.Off : Hold.Prograde;

        }

        StageRequested = keys.spaceKey.wasPressedThisFrame;
        WarpStep = keys.periodKey.wasPressedThisFrame ? 1 : keys.commaKey.wasPressedThisFrame ? -1 : 0;

        // S lifts the nose: a positive turn about body X.
        Vector3d rotation = new Vector3d(Axis(keys.sKey.isPressed, keys.wKey.isPressed), Axis(keys.dKey.isPressed, keys.aKey.isPressed),
            Axis(keys.eKey.isPressed, keys.qKey.isPressed));
        Vector3d translation = new Vector3d(Axis(keys.lKey.isPressed, keys.jKey.isPressed), Axis(keys.iKey.isPressed, keys.kKey.isPressed),
            Axis(keys.hKey.isPressed, keys.nKey.isPressed));

        return new Controls(_throttle, rotation, translation, _hold);

    }

    /// <summary>Lets go of everything, as a fresh vessel starts.</summary>
    public void Release() {

        _throttle = 0.0;
        _hold = Hold.Off;

    }

    private static double Axis(bool positive, bool negative) => (positive ? 1.0 : 0.0) - (negative ? 1.0 : 0.0);

}

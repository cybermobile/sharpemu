// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class HostInputProfileTests
{
    private static readonly object EnvironmentGate = new();

    [Fact]
    public void DefaultProfilePreservesCanonicalButtonsAndTriggers()
    {
        var profile = HostInputProfile.CreateDefault();
        var state = State(
            HostGamepadButtons.Cross | HostGamepadButtons.L1 | HostGamepadButtons.R2,
            leftTrigger: 19,
            rightTrigger: 203);

        var mapped = profile.Apply(state);

        Assert.Equal(state.Buttons, mapped.Buttons);
        Assert.Equal((byte)19, mapped.LeftTrigger);
        Assert.Equal((byte)203, mapped.RightTrigger);
        Assert.True(profile.IsDefault);
    }

    [Fact]
    public void ControllerBindingsCanSwapFaceButtons()
    {
        var profile = HostInputProfile.CreateDefault();
        profile.SetControllerBinding(HostInputButton.Cross, HostInputButton.Circle);
        profile.SetControllerBinding(HostInputButton.Circle, HostInputButton.Cross);

        var mapped = profile.Apply(State(HostGamepadButtons.Cross));

        Assert.True(mapped.Buttons.HasFlag(HostGamepadButtons.Circle));
        Assert.False(mapped.Buttons.HasFlag(HostGamepadButtons.Cross));
        Assert.False(profile.IsDefault);
    }

    [Fact]
    public void TriggerSwapPreservesAnalogValues()
    {
        var profile = HostInputProfile.CreateDefault();
        profile.SetControllerBinding(HostInputButton.L2, HostInputButton.R2);
        profile.SetControllerBinding(HostInputButton.R2, HostInputButton.L2);

        var mapped = profile.Apply(State(
            HostGamepadButtons.L2 | HostGamepadButtons.R2,
            leftTrigger: 41,
            rightTrigger: 219));

        Assert.Equal((byte)219, mapped.LeftTrigger);
        Assert.Equal((byte)41, mapped.RightTrigger);
    }

    [Fact]
    public void StickOptionsApplySwapDeadzoneAndInversion()
    {
        var profile = HostInputProfile.CreateDefault();
        profile.SwapSticks = true;
        profile.InvertLeftX = true;
        profile.InvertRightY = true;
        profile.StickDeadzone = 10;
        var state = State(
            HostGamepadButtons.None,
            leftX: 140,
            leftY: 90,
            rightX: 220,
            rightY: 125);

        var mapped = profile.Apply(state);

        Assert.Equal((byte)35, mapped.LeftX);
        Assert.Equal((byte)128, mapped.LeftY);
        Assert.Equal((byte)140, mapped.RightX);
        Assert.Equal((byte)165, mapped.RightY);
    }

    [Fact]
    public void KeyboardBindingKeepsDefaultAliasesUntilReassigned()
    {
        var profile = HostInputProfile.CreateDefault();

        Assert.True(profile.IsKeyboardBindingDown(HostInputButton.Cross, key => key == 0x0D));

        profile.SetPrimaryKeyboardBinding(HostInputButton.Cross, 0x42);

        Assert.False(profile.IsKeyboardBindingDown(HostInputButton.Cross, key => key == 0x0D));
        Assert.True(profile.IsKeyboardBindingDown(HostInputButton.Cross, key => key == 0x42));
    }

    [Fact]
    public void SerializedProfileUsesStableNamedBindings()
    {
        var profile = HostInputProfile.CreateDefault();
        profile.SetControllerBinding(HostInputButton.Cross, HostInputButton.Circle);

        var json = profile.ToEnvironmentValue();

        Assert.Contains("\"Cross\":\"Circle\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentProfileRoundTripsIntoRuntimeConfiguration()
    {
        lock (EnvironmentGate)
        {
            var original = Environment.GetEnvironmentVariable(HostInputProfile.EnvironmentVariableName);
            try
            {
                var profile = HostInputProfile.CreateDefault();
                profile.SetControllerBinding(HostInputButton.Cross, HostInputButton.Circle);
                profile.StickDeadzone = 24;
                Environment.SetEnvironmentVariable(
                    HostInputProfile.EnvironmentVariableName,
                    profile.ToEnvironmentValue());

                var loaded = HostInputProfile.LoadFromEnvironment();

                Assert.Equal(HostInputButton.Circle, loaded.GetControllerBinding(HostInputButton.Cross));
                Assert.Equal(24, loaded.StickDeadzone);
            }
            finally
            {
                Environment.SetEnvironmentVariable(HostInputProfile.EnvironmentVariableName, original);
            }
        }
    }

    private static HostGamepadState State(
        HostGamepadButtons buttons,
        byte leftX = 128,
        byte leftY = 128,
        byte rightX = 128,
        byte rightY = 128,
        byte leftTrigger = 0,
        byte rightTrigger = 0) =>
        new(
            Connected: true,
            Buttons: buttons,
            LeftX: leftX,
            LeftY: leftY,
            RightX: rightX,
            RightY: rightY,
            LeftTrigger: leftTrigger,
            RightTrigger: rightTrigger);
}

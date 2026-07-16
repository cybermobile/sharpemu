// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEmu.HLE.Host;

/// <summary>Canonical controller inputs exposed by the host input seam.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostInputButton>))]
public enum HostInputButton
{
    Up,
    Down,
    Left,
    Right,
    Cross,
    Circle,
    Square,
    Triangle,
    L1,
    R1,
    L2,
    R2,
    L3,
    R3,
    Options,
    TouchPad,
}

/// <summary>
/// Global host-input mapping. The launcher serializes this profile into the
/// child process environment so the guest pad layer can apply it without a
/// dependency on GUI settings.
/// </summary>
public sealed class HostInputProfile
{
    public const string EnvironmentVariableName = "SHARPEMU_INPUT_PROFILE";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HostInputButton[] ButtonOrder = Enum.GetValues<HostInputButton>();

    public Dictionary<HostInputButton, HostInputButton> ControllerBindings { get; set; } =
        CreateDefaultControllerBindings();

    public Dictionary<HostInputButton, int[]> KeyboardBindings { get; set; } =
        CreateDefaultKeyboardBindings();

    public int LeftStickLeftKey { get; set; } = 0x41; // A
    public int LeftStickRightKey { get; set; } = 0x44; // D
    public int LeftStickUpKey { get; set; } = 0x57; // W
    public int LeftStickDownKey { get; set; } = 0x53; // S
    public int RightStickLeftKey { get; set; } = 0x4A; // J
    public int RightStickRightKey { get; set; } = 0x4C; // L
    public int RightStickUpKey { get; set; } = 0x49; // I
    public int RightStickDownKey { get; set; } = 0x4B; // K

    public int StickDeadzone { get; set; } = 10;
    public bool SwapSticks { get; set; }
    public bool InvertLeftX { get; set; }
    public bool InvertLeftY { get; set; }
    public bool InvertRightX { get; set; }
    public bool InvertRightY { get; set; }

    public static IReadOnlyList<HostInputButton> SupportedButtons => ButtonOrder;

    public bool IsDefault
    {
        get
        {
            Normalize();
            var defaults = CreateDefault();
            return ButtonOrder.All(button =>
                       GetControllerBinding(button) == defaults.GetControllerBinding(button) &&
                       GetKeyboardBinding(button).SequenceEqual(defaults.GetKeyboardBinding(button))) &&
                   LeftStickLeftKey == defaults.LeftStickLeftKey &&
                   LeftStickRightKey == defaults.LeftStickRightKey &&
                   LeftStickUpKey == defaults.LeftStickUpKey &&
                   LeftStickDownKey == defaults.LeftStickDownKey &&
                   RightStickLeftKey == defaults.RightStickLeftKey &&
                   RightStickRightKey == defaults.RightStickRightKey &&
                   RightStickUpKey == defaults.RightStickUpKey &&
                   RightStickDownKey == defaults.RightStickDownKey &&
                   StickDeadzone == defaults.StickDeadzone &&
                   SwapSticks == defaults.SwapSticks &&
                   InvertLeftX == defaults.InvertLeftX &&
                   InvertLeftY == defaults.InvertLeftY &&
                   InvertRightX == defaults.InvertRightX &&
                   InvertRightY == defaults.InvertRightY;
        }
    }

    public static HostInputProfile CreateDefault() => new();

    public static HostInputProfile LoadFromEnvironment()
    {
        var json = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefault();
        }

        try
        {
            var profile = JsonSerializer.Deserialize<HostInputProfile>(json, JsonOptions) ?? CreateDefault();
            profile.Normalize();
            return profile;
        }
        catch (JsonException)
        {
            return CreateDefault();
        }
    }

    public string ToEnvironmentValue()
    {
        Normalize();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public HostInputButton GetControllerBinding(HostInputButton target)
    {
        return ControllerBindings.TryGetValue(target, out var source) && Enum.IsDefined(source)
            ? source
            : target;
    }

    public void SetControllerBinding(HostInputButton target, HostInputButton source)
    {
        ControllerBindings[target] = source;
    }

    public IReadOnlyList<int> GetKeyboardBinding(HostInputButton target)
    {
        return KeyboardBindings.TryGetValue(target, out var keys) ? keys : [];
    }

    public void SetPrimaryKeyboardBinding(HostInputButton target, int virtualKey)
    {
        KeyboardBindings[target] = virtualKey == 0 ? [] : [virtualKey];
    }

    public bool IsKeyboardBindingDown(HostInputButton target, Func<int, bool> isKeyDown)
    {
        if (!KeyboardBindings.TryGetValue(target, out var keys))
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (key is > 0 and <= 0xFF && isKeyDown(key))
            {
                return true;
            }
        }

        return false;
    }

    public HostGamepadState Apply(HostGamepadState state)
    {
        var buttons = HostGamepadButtons.None;
        foreach (var target in ButtonOrder)
        {
            var source = GetControllerBinding(target);
            if ((state.Buttons & ToFlags(source)) != 0)
            {
                buttons |= ToFlags(target);
            }
        }

        var leftX = state.LeftX;
        var leftY = state.LeftY;
        var rightX = state.RightX;
        var rightY = state.RightY;
        if (SwapSticks)
        {
            (leftX, rightX) = (rightX, leftX);
            (leftY, rightY) = (rightY, leftY);
        }

        leftX = ApplyAxis(leftX, InvertLeftX);
        leftY = ApplyAxis(leftY, InvertLeftY);
        rightX = ApplyAxis(rightX, InvertRightX);
        rightY = ApplyAxis(rightY, InvertRightY);

        return state with
        {
            Buttons = buttons,
            LeftX = leftX,
            LeftY = leftY,
            RightX = rightX,
            RightY = rightY,
            LeftTrigger = ReadAnalogSource(GetControllerBinding(HostInputButton.L2), state),
            RightTrigger = ReadAnalogSource(GetControllerBinding(HostInputButton.R2), state),
        };
    }

    public void Normalize()
    {
        ControllerBindings ??= [];
        KeyboardBindings ??= [];
        var defaultController = CreateDefaultControllerBindings();
        var defaultKeyboard = CreateDefaultKeyboardBindings();
        foreach (var button in ButtonOrder)
        {
            if (!ControllerBindings.TryGetValue(button, out var source) || !Enum.IsDefined(source))
            {
                ControllerBindings[button] = defaultController[button];
            }

            if (!KeyboardBindings.TryGetValue(button, out var keys) || keys is null)
            {
                KeyboardBindings[button] = defaultKeyboard[button];
            }
            else
            {
                KeyboardBindings[button] = keys.Where(static key => key is > 0 and <= 0xFF).Distinct().ToArray();
            }
        }

        StickDeadzone = Math.Clamp(StickDeadzone, 0, 64);
        LeftStickLeftKey = NormalizeVirtualKey(LeftStickLeftKey, 0x41);
        LeftStickRightKey = NormalizeVirtualKey(LeftStickRightKey, 0x44);
        LeftStickUpKey = NormalizeVirtualKey(LeftStickUpKey, 0x57);
        LeftStickDownKey = NormalizeVirtualKey(LeftStickDownKey, 0x53);
        RightStickLeftKey = NormalizeVirtualKey(RightStickLeftKey, 0x4A);
        RightStickRightKey = NormalizeVirtualKey(RightStickRightKey, 0x4C);
        RightStickUpKey = NormalizeVirtualKey(RightStickUpKey, 0x49);
        RightStickDownKey = NormalizeVirtualKey(RightStickDownKey, 0x4B);
    }

    private static int NormalizeVirtualKey(int value, int fallback) => value is > 0 and <= 0xFF ? value : fallback;

    private byte ApplyAxis(byte value, bool invert)
    {
        var deadzone = Math.Clamp(StickDeadzone, 0, 64);
        if (Math.Abs(value - 128) <= deadzone)
        {
            return 128;
        }

        return invert ? (byte)(255 - value) : value;
    }

    private static byte ReadAnalogSource(HostInputButton source, HostGamepadState state)
    {
        return source switch
        {
            HostInputButton.L2 => state.LeftTrigger,
            HostInputButton.R2 => state.RightTrigger,
            _ => (state.Buttons & ToFlags(source)) != 0 ? byte.MaxValue : byte.MinValue,
        };
    }

    private static HostGamepadButtons ToFlags(HostInputButton button) => button switch
    {
        HostInputButton.Up => HostGamepadButtons.Up,
        HostInputButton.Down => HostGamepadButtons.Down,
        HostInputButton.Left => HostGamepadButtons.Left,
        HostInputButton.Right => HostGamepadButtons.Right,
        HostInputButton.Cross => HostGamepadButtons.Cross,
        HostInputButton.Circle => HostGamepadButtons.Circle,
        HostInputButton.Square => HostGamepadButtons.Square,
        HostInputButton.Triangle => HostGamepadButtons.Triangle,
        HostInputButton.L1 => HostGamepadButtons.L1,
        HostInputButton.R1 => HostGamepadButtons.R1,
        HostInputButton.L2 => HostGamepadButtons.L2,
        HostInputButton.R2 => HostGamepadButtons.R2,
        HostInputButton.L3 => HostGamepadButtons.L3,
        HostInputButton.R3 => HostGamepadButtons.R3,
        HostInputButton.Options => HostGamepadButtons.Options,
        HostInputButton.TouchPad => HostGamepadButtons.TouchPad,
        _ => HostGamepadButtons.None,
    };

    private static Dictionary<HostInputButton, HostInputButton> CreateDefaultControllerBindings()
    {
        return ButtonOrder.ToDictionary(static button => button, static button => button);
    }

    private static Dictionary<HostInputButton, int[]> CreateDefaultKeyboardBindings()
    {
        return new Dictionary<HostInputButton, int[]>
        {
            [HostInputButton.Up] = [0x26],
            [HostInputButton.Down] = [0x28],
            [HostInputButton.Left] = [0x25],
            [HostInputButton.Right] = [0x27],
            [HostInputButton.Cross] = [0x5A, 0x0D],
            [HostInputButton.Circle] = [0x58, 0x1B],
            [HostInputButton.Square] = [0x43],
            [HostInputButton.Triangle] = [0x56],
            [HostInputButton.L1] = [0x51],
            [HostInputButton.R1] = [0x45],
            [HostInputButton.L2] = [0x52],
            [HostInputButton.R2] = [0x46],
            [HostInputButton.L3] = [],
            [HostInputButton.R3] = [],
            [HostInputButton.Options] = [0x09, 0x08],
            [HostInputButton.TouchPad] = [],
        };
    }
}

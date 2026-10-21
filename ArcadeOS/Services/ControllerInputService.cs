using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ArcadeOS.Services;

[Flags] public enum CabinetInput { None = 0, Up = 1, Down = 2, Left = 4, Right = 8, Select = 16, Back = 32, Start = 64, Menu = 128 }
public sealed record ControllerBindings(int Select = 1, int Back = 2, int Start = 8, int Menu = 7)
{
    public CabinetInput Apply(uint buttons)
    {
        CabinetInput result = 0;
        foreach (var (index, action) in new[] { (Select, CabinetInput.Select), (Back, CabinetInput.Back), (Start, CabinetInput.Start), (Menu, CabinetInput.Menu) })
            if (index is >= 1 and <= 32 && (buttons & (1u << (index - 1))) != 0) result |= action;
        return result;
    }
    public ControllerBindings Set(CabinetInput action, int button) => action switch
    {
        CabinetInput.Select => this with { Select = button }, CabinetInput.Back => this with { Back = button },
        CabinetInput.Start => this with { Start = button }, CabinetInput.Menu => this with { Menu = button }, _ => this
    };
}
public sealed record ControllerReading(string Id, string Name, string Kind, CabinetInput Directions, uint Buttons, string? ProfileKey = null)
{
    public string MappingKey => ProfileKey ?? Kind + ":" + Name;
}
public interface IControllerReader { IReadOnlyList<ControllerReading> Read(); }
public sealed class CabinetInputRepeater
{
    private CabinetInput previous; private long nextRepeat; private bool waitingNeutral = true;
    public void Reset() { previous = 0; waitingNeutral = true; nextRepeat = 0; }
    public CabinetInput Advance(CabinetInput state, long milliseconds)
    {
        if (waitingNeutral) { if (state == 0) waitingNeutral = false; previous = state; return 0; }
        // Diagonal sticks move one grid axis at a time; opposing directions cancel.
        if ((state & (CabinetInput.Up | CabinetInput.Down)) == (CabinetInput.Up | CabinetInput.Down)) state &= ~(CabinetInput.Up | CabinetInput.Down);
        if ((state & (CabinetInput.Left | CabinetInput.Right)) == (CabinetInput.Left | CabinetInput.Right)) state &= ~(CabinetInput.Left | CabinetInput.Right);
        if ((state & (CabinetInput.Up | CabinetInput.Down)) != 0) state &= ~(CabinetInput.Left | CabinetInput.Right);
        var directions = state & (CabinetInput)15;
        var result = state & ~previous;
        if (directions != (previous & (CabinetInput)15)) nextRepeat = milliseconds + 400;
        else if (directions != 0 && milliseconds >= nextRepeat) { result |= directions; nextRepeat = milliseconds + 125; }
        previous = state; return result;
    }
}
public sealed class ControllerInputService
{
    private readonly IControllerReader reader;
    private readonly string settingsPath;
    private readonly CabinetInputRepeater repeat = new();
    private Dictionary<string, ControllerBindings> bindings = new();
    private Dictionary<string, uint> previousButtons = new();
    public IReadOnlyList<ControllerReading> Devices { get; private set; } = [];
    public bool HasRawInput => Devices.Any(device => device.Buttons != 0 || device.Directions != CabinetInput.None);
    public void RequireNeutral() => repeat.Reset();
    public ControllerInputService(IControllerReader? reader = null, string? settingsPath = null)
    {
        this.reader = reader ?? new WindowsControllerReader();
        this.settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcadeOS", "Metadata", "controllers.json");
        try { bindings = JsonSerializer.Deserialize<Dictionary<string, ControllerBindings>>(File.ReadAllText(this.settingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public CabinetInput Poll(bool enabled, long time)
    {
        Devices = reader.Read(); CabinetInput state = 0;
        foreach (var device in Devices) state |= device.Directions | GetBindings(device.MappingKey).Apply(device.Buttons);
        if (!enabled) { repeat.Reset(); return 0; }
        return repeat.Advance(state, time);
    }
    public void BeginCapture() { previousButtons = Devices.ToDictionary(d => d.Id, d => d.Buttons); repeat.Reset(); }
    public (string Kind, int Button)? CaptureButton()
    {
        (string, int)? captured = null;
        foreach (var device in Devices)
        {
            uint before = previousButtons.GetValueOrDefault(device.Id, device.Buttons);
            uint pressed = device.Buttons & ~before;
            if (pressed != 0 && captured is null) captured = (device.MappingKey, System.Numerics.BitOperations.TrailingZeroCount(pressed) + 1);
        }
        previousButtons = Devices.ToDictionary(d => d.Id, d => d.Buttons); return captured;
    }
    public ControllerBindings GetBindings(string kind) => bindings.GetValueOrDefault(kind) ?? new();
    public void SaveBindings(string kind, ControllerBindings value)
    {
        var updated = new Dictionary<string, ControllerBindings>(bindings) { [kind] = value };
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath + ".tmp", JsonSerializer.Serialize(updated)); File.Move(settingsPath + ".tmp", settingsPath, true);
        bindings = updated; repeat.Reset();
    }
    public static CabinetInput DecodeAxes(double x, double y, uint pov = 65535)
    {
        CabinetInput result = 0;
        if (x < -.5) result |= CabinetInput.Left; if (x > .5) result |= CabinetInput.Right;
        if (y < -.5) result |= CabinetInput.Up; if (y > .5) result |= CabinetInput.Down;
        if (pov != 65535 && pov < 36000)
        {
            if (pov >= 31500 || pov <= 4500) result |= CabinetInput.Up;
            if (pov >= 4500 && pov <= 13500) result |= CabinetInput.Right;
            if (pov >= 13500 && pov <= 22500) result |= CabinetInput.Down;
            if (pov >= 22500 && pov <= 31500) result |= CabinetInput.Left;
        }
        return result;
    }
}
public sealed class WindowsControllerReader : IControllerReader
{
    private readonly Dictionary<uint, JoyCaps> joystickCaps = new();
    private readonly HashSet<uint> xboxPorts = new();
    private long nextScan;
    public IReadOnlyList<ControllerReading> Read()
    {
        var result = new List<ControllerReading>(); bool scan = Environment.TickCount64 >= nextScan;
        if (scan) { nextScan = Environment.TickCount64 + 1000; joystickCaps.Clear(); xboxPorts.Clear(); }
        for (uint id = 0; id < 4; id++)
        {
            if (!scan && !xboxPorts.Contains(id)) continue;
            if (XInputGetState(id, out var state) != 0) { xboxPorts.Remove(id); continue; }
            xboxPorts.Add(id); var pad = state.Gamepad;
            var directions = ControllerInputService.DecodeAxes(pad.X / 32768d, -pad.Y / 32768d);
            if ((pad.Buttons & 1) != 0) directions |= CabinetInput.Up; if ((pad.Buttons & 2) != 0) directions |= CabinetInput.Down;
            if ((pad.Buttons & 4) != 0) directions |= CabinetInput.Left; if ((pad.Buttons & 8) != 0) directions |= CabinetInput.Right;
            uint buttons = 0; ushort[] masks = [0x1000, 0x2000, 0x4000, 0x8000, 0x100, 0x200, 0x20, 0x10, 0x40, 0x80];
            for (int i = 0; i < masks.Length; i++) if ((pad.Buttons & masks[i]) != 0) buttons |= 1u << i;
            if (pad.LeftTrigger > 127) buttons |= 1u << 10; if (pad.RightTrigger > 127) buttons |= 1u << 11;
            result.Add(new($"xinput:{id}", $"Xbox controller {id + 1}", "xinput", directions, buttons, "xinput"));
        }
        for (uint id = 0; id < 16; id++)
        {
            if (!scan && !joystickCaps.ContainsKey(id)) continue;
            var state = new JoyInfo { Size = (uint)Marshal.SizeOf<JoyInfo>(), Flags = 0xff };
            if (joyGetPosEx(id, ref state) != 0) { joystickCaps.Remove(id); continue; }
            if (!joystickCaps.TryGetValue(id, out var caps))
            {
                if (joyGetDevCapsW((UIntPtr)id, out caps, (uint)Marshal.SizeOf<JoyCaps>()) != 0) continue;
                joystickCaps[id] = caps;
            }
            // Xbox devices can also appear through WinMM; use their XInput reading once.
            if (xboxPorts.Count > 0 && (caps.Name.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || caps.Name.Contains("XInput", StringComparison.OrdinalIgnoreCase))) continue;
            double Normalize(uint value, uint min, uint max) => max > min ? 2d * ((double)value - min) / (max - min) - 1 : 0;
            result.Add(new($"joystick:{id}", caps.Name, "joystick", ControllerInputService.DecodeAxes(Normalize(state.X, caps.Xmin, caps.Xmax), Normalize(state.Y, caps.Ymin, caps.Ymax), (caps.Caps & 0x10) != 0 ? state.Pov : 65535), state.Buttons, $"joystick:{caps.Mid:X4}:{caps.Pid:X4}:{caps.RegKey}:{caps.Name}"));
        }
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] private struct XState { public uint Packet; public XPad Gamepad; }
    [StructLayout(LayoutKind.Sequential)] private struct XPad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short X, Y, RightX, RightY; }
    [StructLayout(LayoutKind.Sequential)] private struct JoyInfo { public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct JoyCaps
    {
        public ushort Mid, Pid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Xmin, Xmax, Ymin, Ymax, Zmin, Zmax, Buttons, PeriodMin, PeriodMax, Rmin, Rmax, Umin, Umax, Vmin, Vmax, Caps, MaxAxes, NumAxes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Oem;
    }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XState state);
    [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint id, ref JoyInfo info);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint joyGetDevCapsW(UIntPtr id, out JoyCaps caps, uint size);
}

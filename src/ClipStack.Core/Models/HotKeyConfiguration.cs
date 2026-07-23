namespace ClipStack.Core.Models;

public sealed class HotKeyConfiguration : IEquatable<HotKeyConfiguration>
{
    public bool Control { get; set; } = true;

    public bool Alt { get; set; }

    public bool Shift { get; set; } = true;

    public bool Win { get; set; }

    /// <summary>Virtual-key code (e.g. 0x53 for S).</summary>
    public int VirtualKey { get; set; } = 0x53;

    public static HotKeyConfiguration Default { get; } = new()
    {
        Control = true,
        Alt = false,
        Shift = true,
        Win = false,
        VirtualKey = 0x53,
    };

    public bool HasAtLeastOneModifier => Control || Alt || Shift || Win;

    public bool IsValid => HasAtLeastOneModifier && VirtualKey is > 0 and < 256;

    public string ToDisplayString()
    {
        var parts = new List<string>(5);
        if (Control) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(VirtualKeyToName(VirtualKey));
        return string.Join(" + ", parts);
    }

    public HotKeyConfiguration Clone() => new()
    {
        Control = Control,
        Alt = Alt,
        Shift = Shift,
        Win = Win,
        VirtualKey = VirtualKey,
    };

    public bool Equals(HotKeyConfiguration? other)
    {
        if (other is null) return false;
        return Control == other.Control
            && Alt == other.Alt
            && Shift == other.Shift
            && Win == other.Win
            && VirtualKey == other.VirtualKey;
    }

    public override bool Equals(object? obj) => Equals(obj as HotKeyConfiguration);

    public override int GetHashCode() => HashCode.Combine(Control, Alt, Shift, Win, VirtualKey);

    public static string VirtualKeyToName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
        0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
        0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xBA => ";",
        0xDE => "'",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBD => "-",
        0xBB => "=",
        0xC0 => "`",
        _ => $"VK_{vk:X2}",
    };
}

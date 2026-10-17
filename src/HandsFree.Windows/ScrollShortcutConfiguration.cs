namespace HandsFree.Windows;

[Flags]
public enum ShortcutModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

public readonly record struct GlobalShortcut(ShortcutModifiers Modifiers, uint VirtualKey);

public sealed record ScrollShortcutConfiguration(GlobalShortcut CaptureTarget, GlobalShortcut ScrollDown, GlobalShortcut ScrollUp)
{
    public static ScrollShortcutConfiguration Default { get; } = new(
        new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x78),
        new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x79),
        new GlobalShortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x7A));

    public static bool IsValid(GlobalShortcut shortcut)
    {
        const ShortcutModifiers allModifiers = ShortcutModifiers.Alt | ShortcutModifiers.Control | ShortcutModifiers.Shift | ShortcutModifiers.Windows;
        var key = shortcut.VirtualKey;
        var supportedKey = key is >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or 0x09 or 0x1B or 0x20 or 0x21 or 0x22 or 0x23 or 0x24
            or >= 0x25 and <= 0x28 or 0x2D or 0x2E
            || (key >= 0x70 && key <= 0x87 && key != 0x7B);
        return shortcut.Modifiers != 0 && (shortcut.Modifiers & ~allModifiers) == 0 && supportedKey;
    }

    public static bool TryParse(string captureTarget, string scrollDown, string scrollUp, out ScrollShortcutConfiguration? configuration, out string error)
    {
        configuration = null;
        if (!TryParseShortcut(captureTarget, out var capture) || !TryParseShortcut(scrollDown, out var down) || !TryParseShortcut(scrollUp, out var up))
        {
            error = "Use a shortcut such as Ctrl+Alt+F9, with one key and at least one modifier.";
            return false;
        }

        if (capture == down || capture == up || down == up)
        {
            error = "Capture, Scroll Down, and Scroll Up need different shortcuts.";
            return false;
        }

        configuration = new ScrollShortcutConfiguration(capture, down, up);
        error = string.Empty;
        return true;
    }

    private static bool TryParseShortcut(string text, out GlobalShortcut shortcut)
    {
        shortcut = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = (ShortcutModifiers)0;
        uint? key = null;
        foreach (var rawPart in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var part = rawPart.ToUpperInvariant();
            var modifier = part switch
            {
                "CTRL" or "CONTROL" => ShortcutModifiers.Control,
                "ALT" => ShortcutModifiers.Alt,
                "SHIFT" => ShortcutModifiers.Shift,
                "WIN" or "WINDOWS" => ShortcutModifiers.Windows,
                _ => (ShortcutModifiers)0
            };

            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) return false;
                modifiers |= modifier;
                continue;
            }

            if (key is not null || !TryGetVirtualKey(part, out var virtualKey)) return false;
            key = virtualKey;
        }

        if (modifiers == 0 || key is null) return false;
        shortcut = new GlobalShortcut(modifiers, key.Value);
        return IsValid(shortcut);
    }

    private static bool TryGetVirtualKey(string key, out uint virtualKey)
    {
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            virtualKey = key[0];
            return true;
        }

        if (key.Length is 2 or 3 && key[0] == 'F'
            && uint.TryParse(key.AsSpan(1), out var functionNumber) && functionNumber is >= 1 and <= 24 && functionNumber != 12)
        {
            virtualKey = 0x70 + functionNumber - 1;
            return true;
        }

        virtualKey = key switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "HOME" => 0x24,
            "END" => 0x23,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            _ => 0
        };
        return virtualKey != 0;
    }
}

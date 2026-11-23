namespace Atoll.Modules.Launcher;

/// <summary>A shortcut you pinned: an app, a file, a site, or a command of another program such as steam://run/...</summary>
public sealed class PinnedLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>What to open: a path, a web address, or a protocol link.</summary>
    public string Target { get; set; } = "";

    /// <summary>Pins with the same group name sit together under one chip. Empty means it stands alone.</summary>
    public string Group { get; set; } = "";

    /// <summary>An optional global shortcut that opens this pin from anywhere.</summary>
    public string Hotkey { get; set; } = "";
}

/// <summary>One chip in the pinned row: a single pin, or a named group of them.</summary>
public sealed record PinSlot(string Name, IReadOnlyList<PinnedLink> Pins)
{
    public bool IsGroup => Pins.Count > 1 || (Pins.Count == 1 && Pins[0].Group.Length > 0 && Pins[0].Group != Pins[0].Name);
    public PinnedLink Single => Pins[0];
}

/// <summary>Works out the row of chips from the flat list of pins. Pure, so grouping is testable.</summary>
public static class PinLayout
{
    /// <summary>
    /// Pins keep the order they were added in; a group takes the place of its first pin, and the rest of its pins
    /// follow it inside the group. Group names match ignoring case and spaces at the ends.
    /// </summary>
    public static IReadOnlyList<PinSlot> Build(IEnumerable<PinnedLink> pins)
    {
        var slots = new List<PinSlot>();
        var groups = new Dictionary<string, List<PinnedLink>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in pins)
        {
            var group = pin.Group.Trim();
            if (group.Length == 0)
            {
                slots.Add(new PinSlot(pin.Name, [pin]));
                continue;
            }
            if (groups.TryGetValue(group, out var members)) { members.Add(pin); continue; }
            members = [pin];
            groups[group] = members;
            slots.Add(new PinSlot(group, members)); // the list is shared, so later members appear in it
        }
        return slots;
    }

    /// <summary>True when <paramref name="target"/> is something the shell can open: a path, a web address or a protocol link.</summary>
    public static bool IsOpenable(string? target)
    {
        var text = (target ?? "").Trim().Trim('"');
        if (text.Length == 0) return false;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1) return true; // https://, steam://, mailto:
        return text.Length > 2 && (text[1] == ':' || text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("%", StringComparison.Ordinal));
    }

    /// <summary>A name for a pin made from its target when none is typed: "steam://run/570" becomes "Steam", a path its file name.</summary>
    public static string SuggestName(string target)
    {
        var text = target.Trim().Trim('"');
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && !uri.IsFile)
        {
            var host = uri.Scheme is "http" or "https" ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host : uri.Scheme;
            return host.Length == 0 ? text : char.ToUpperInvariant(host[0]) + host[1..];
        }
        var name = System.IO.Path.GetFileNameWithoutExtension(text);
        return name.Length == 0 ? text : name;
    }
}

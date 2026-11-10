using FinanceOS.UI.Localization;

namespace FinanceOS.App.Shell;

/// <summary>The ten top-level sections of the app (ARCHITECTURE §8), in sidebar order.</summary>
public enum Section
{
    Overview,
    MyDay,
    Tasks,
    Calendar,
    Reminders,
    Workflows,
    Projects,
    Analytics,
    Archive,
    Settings,
}

/// <summary>
/// Language-independent metadata for a section. Icon keys refer to Themes/Icons.xaml; texts live in
/// Strings.resx under <c>Section.{Section}.Title / Subtitle / EmptyTitle / EmptyDescription</c>.
/// </summary>
/// <param name="GroupKey">String key of the sidebar group label drawn above this item, if any.</param>
public sealed record SectionInfo(Section Section, string IconKey, string Shortcut, string? GroupKey = null)
{
    /// <summary>Overview and Settings have real content; every other section shows an empty state for now.</summary>
    public bool HasEmptyState => Section is not (Section.Overview or Section.Settings);

    public string Title(Localizer l) => l[$"Section.{Section}.Title"];

    public string Subtitle(Localizer l) => l[$"Section.{Section}.Subtitle"];

    public string EmptyTitle(Localizer l) => HasEmptyState ? l[$"Section.{Section}.EmptyTitle"] : "";

    public string EmptyDescription(Localizer l) => HasEmptyState ? l[$"Section.{Section}.EmptyDescription"] : "";

    public string? GroupHeader(Localizer l) => GroupKey is null ? null : l[GroupKey];
}

public static class SectionCatalog
{
    public static IReadOnlyList<SectionInfo> All { get; } =
    [
        new(Section.Overview, "Icon.LayoutDashboard", "Ctrl+1"),
        new(Section.MyDay, "Icon.Sun", "Ctrl+2"),
        new(Section.Tasks, "Icon.ListChecks", "Ctrl+3"),
        new(Section.Calendar, "Icon.Calendar", "Ctrl+4"),
        new(Section.Reminders, "Icon.Bell", "Ctrl+5"),
        new(Section.Workflows, "Icon.Workflow", "Ctrl+6", "Nav.Group.Organize"),
        new(Section.Projects, "Icon.Folder", "Ctrl+7"),
        new(Section.Analytics, "Icon.ChartColumn", "Ctrl+8", "Nav.Group.Insight"),
        new(Section.Archive, "Icon.Archive", "Ctrl+9"),
        new(Section.Settings, "Icon.Settings", "Ctrl+,"),
    ];

    public static SectionInfo Get(Section section) => All[(int)section];
}

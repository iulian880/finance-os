using FinanceOS.App.Shell;

namespace FinanceOS.App.Services;

/// <summary>Simple shows the essentials; Advanced shows everything. Same data either way (ARCHITECTURE §10).</summary>
public enum ExperienceMode
{
    Simple,
    Advanced,
}

/// <summary>
/// What the interface offers in a mode. Views read these flags; switching modes never touches data.
/// Advanced fields that already hold a value stay visible (read-only) in Simple, so nothing seems lost.
/// </summary>
public sealed record Capabilities(bool ShowWorkflows, bool ShowProjects, bool ShowAnalytics, bool ShowArchive, bool EditAdvancedFields)
{
    public static Capabilities Simple { get; } = new(false, false, false, false, false);

    public static Capabilities Advanced { get; } = new(true, true, true, true, true);

    public static Capabilities For(ExperienceMode mode) => mode == ExperienceMode.Advanced ? Advanced : Simple;

    public bool Shows(Section section) => section switch
    {
        Section.Workflows => ShowWorkflows,
        Section.Projects => ShowProjects,
        Section.Analytics => ShowAnalytics,
        Section.Archive => ShowArchive,
        _ => true,
    };
}

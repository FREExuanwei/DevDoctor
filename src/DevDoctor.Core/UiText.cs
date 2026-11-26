using System.Globalization;
using System.Resources;

namespace DevDoctor.Core;

/// <summary>Default Chinese display resources. Identifiers and serialized values never pass through this layer.</summary>
public static class UiText
{
    private static readonly ResourceManager Resources = new("DevDoctor.Core.UiText", typeof(UiText).Assembly);

    // A future UiText.en.resx can extend display languages without changing persisted IDs.
    // Unknown keys/IDs remain readable and cannot prevent the application from opening.
    public static string Get(string key, string? fallback = null)
    {
        if (string.IsNullOrEmpty(key)) return fallback ?? "";
        try { return Resources.GetString(key, CultureInfo.CurrentUICulture) ?? fallback ?? key; }
        catch (MissingManifestResourceException) { return fallback ?? key; }
        catch (MissingSatelliteAssemblyException) { return fallback ?? key; }
    }

    public static string ThemeName(string id) => Get("Theme." + id, id);
    public static string StyleName(string id) => Get("Style." + id, id);
    public static string ModeName(string id) => Get("Mode." + id, id);
    public static string ThemeDescription(string id) => Get("ThemeDescription." + id, ThemeName(id));
    public static string ProjectName(ProjectKind kind) => Get("Project." + kind, kind.ToString());
    public static string ToolName(string tool) => Get("Tool." + tool, tool);
    // Match only application-authored headings. Raw evidence, third-party output and rule keys stay intact.
    public static string EvidenceName(string label) => label switch {
        "Gradle JVM compatibility" => Get("Evidence.GradleJvm"),
        "Presets schema version" => Get("Evidence.PresetsSchema"),
        "Presets include" => Get("Evidence.PresetsInclude"),
        _ => label
    };
    public static string FindingTitle(string title) => EvidenceName(title);
    public static string ScanKindName(HistoryScanKind kind) => Get("ScanKind." + kind, kind.ToString());
    public static string CompletionName(ScanCompletion completion) => Get("Completion." + completion, completion.ToString());
    public static string CoverageName(CoverageState coverage) => Get("Coverage." + coverage, coverage.ToString());
    public static string PresenceName(ToolPresence presence) => Get("Presence." + presence, presence.ToString());
    public static string DifferenceName(DifferenceKind kind) => Get("Difference." + kind, kind.ToString());
    public static string EditionName(string edition) => Get("Edition." + edition, edition);
    public static string ExceptionName(Exception exception) => Get(exception switch {
        UnauthorizedAccessException => "Error.Access", IOException => "Error.File", ArgumentException => "Error.Argument",
        OperationCanceledException => "Error.Cancelled", InvalidOperationException => "Error.State", _ => "Error.Unexpected"
    });

    public static string ProfessionalStyle => StyleName("FluentProfessional");
    public static string SoftStyle => StyleName("SoftCards");
    public static string CompactStyle => StyleName("CompactPro");
    public static string ProfessionalDescription => Get("StyleDescription.FluentProfessional");
    public static string SoftDescription => Get("StyleDescription.SoftCards");
    public static string CompactDescription => Get("StyleDescription.CompactPro");
    public static string ProfessionalQuickName => Get("QuickStyle.FluentProfessional");
    public static string SoftQuickName => Get("QuickStyle.SoftCards");
    public static string CompactQuickName => Get("QuickStyle.CompactPro");
    public static string StyleOverview => Get("Appearance.StyleOverview");
    public static string AppearanceReady => Get("Appearance.Ready");
    public static string AppearanceSaving => Get("Appearance.Saving");
    public static string AppearanceSaved => Get("Appearance.Saved");
    public static string AppearanceSaveFailed => Get("Appearance.SaveFailed");
    public static string Light => ModeName("Light");
    public static string Dark => ModeName("Dark");
    public static string SystemMode => ModeName("System");
    public static string ThemePalette => Get("Appearance.Palette");
    public static string DisplayMode => Get("Appearance.Mode");
    public static string InterfaceStyle => Get("Appearance.Style");
    public static string Appearance => Get("Common.Appearance");
    public static string Cancel => Get("Common.Cancel");
    public static string PreviousPage => Get("Common.PreviousPage");
    public static string NextPage => Get("Common.NextPage");
    public static string ClipboardUnavailable => Get("Common.ClipboardUnavailable");
    public static string ConfirmContinue => Get("Common.ConfirmContinue");
    public static string Close => Get("Common.Close");
    public static string OverviewTitle => Get("Page.Overview");
    public static string ToolsTitle => Get("Page.Tools");
    public static string EnvironmentTitle => Get("Page.Environment");
    public static string IssuesTitle => Get("Page.Issues");
    public static string AdvancedTitle => Get("Page.Advanced");
    public static string ProjectTitle => Get("Page.Project");
    public static string HistoryTitle => Get("Page.History");
    public static string ReportsTitle => Get("Page.Reports");
    public static string SettingsTitle => Get("Page.Settings");
}

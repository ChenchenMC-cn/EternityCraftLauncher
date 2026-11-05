// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Windows;
using System.Windows.Controls;
using ECL.Services;

namespace ECL;

/// <summary>
/// First-run agreement. Shown once (per terms version) before the main window
/// exists; declining closes the launcher instead of leaving a half-usable UI.
///
/// The picker offers exactly the six hand-written languages — chrome included,
/// so the title bar, buttons and hint text change together with the body.
/// </summary>
public partial class TermsWindow : Window
{
    private sealed record LangChoice(string Code, string Display);

    private readonly List<LangChoice> _languages;

    public TermsWindow(string? initialLanguage = null)
    {
        InitializeComponent();

        string dataRoot = App.StartupSettings?.DataRoot ?? "";

        // Built-in six plus anything found under <dataRoot>/terms/*.json, so a
        // dropped-in translation is actually selectable.
        var codes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in TermsText.Translated) codes.Add(doc.Code);
        foreach (var code in TermsText.ExternalCodes(dataRoot)) codes.Add(code);

        _languages = codes
            .Select(code =>
            {
                var doc = TermsText.LoadExternal(dataRoot, code) ?? TermsText.For(code, out _);
                return new LangChoice(code, doc.Display);
            })
            .ToList();

        LanguageBox.ItemsSource = _languages;

        // Requested language → English (US). Deliberately NOT the Windows language:
        // the first-run dialog shows the agreement itself, so it should open in the
        // language the launcher ships in. The picker stays available below.
        // Try the exact code first so an external translation (terms/<code>.json) is
        // picked up; ResolveCode only knows the six built-ins.
        string wanted = string.IsNullOrWhiteSpace(initialLanguage)
            ? TermsText.FallbackCode
            : initialLanguage;

        LanguageBox.SelectedItem =
            FindLanguage(wanted)
            ?? FindLanguage(TermsText.ResolveCode(wanted))
            ?? FindLanguage(TermsText.FallbackCode)
            ?? _languages.FirstOrDefault();

        VersionText.Text = $"ECL {AppVersionText()} · terms v{TermsText.Version}";

        if (LanguageBox.SelectedItem is LangChoice initial) Apply(initial.Code);
    }

    private LangChoice? FindLanguage(string? code) => string.IsNullOrWhiteSpace(code)
        ? null
        : _languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    private static string AppVersionText()
    {
        // Must match the title bar, so read the same source: InformationalVersion
        // carries the "Fix N" / "BuildNN" suffix that the numeric form cannot.
        try { return AppInfo.Version; }
        catch
        {
            return "1.0";
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is LangChoice choice) Apply(choice.Code);
    }

    private void Apply(string code)
    {
        string dataRoot = App.StartupSettings?.DataRoot ?? "";

        // An external file wins over the built-in table, so translators can
        // iterate without a rebuild.
        TermsDocument? external = dataRoot.Length > 0 ? TermsText.LoadExternal(dataRoot, code) : null;
        var doc = external ?? TermsText.For(code, out _);

        // Window chrome follows the document too — otherwise a Japanese user gets
        // a Japanese body under a Chinese title bar and Chinese buttons.
        Title = $"{doc.Title} · Eternity Craft Launcher";
        TitleText.Text = doc.Title;
        SubtitleText.Text = doc.Subtitle;
        LanguageLabel.Text = doc.LanguageLabel;
        AcceptHint.Text = doc.AcceptHint;
        AcceptButton.Content = doc.AcceptButton;
        DeclineButton.Content = doc.DeclineButton;
        FallbackText.Text = doc.FallbackNotice;

        BlocksList.ItemsSource = doc.Blocks;
        SignatureText.Text = doc.Signature;

        bool translated = external != null || TermsText.HasTranslation(code);
        FallbackNotice.Visibility = translated ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
        Close();
    }

    private void OnDecline(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = false;
        Close();
    }

    /// <summary>True when the user accepted; false when they chose to quit.</summary>
    public bool Accepted { get; private set; }

    /// <summary>
    /// Same effect as the user pressing "agree". Only used by --auto-accept-terms,
    /// which exists so the dialog→main-window handoff can be tested headlessly.
    /// </summary>
    internal void SimulateAccept()
    {
        Accepted = true;
        DialogResult = true;
        Close();
    }
}

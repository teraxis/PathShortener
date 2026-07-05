using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using PathShortener.Core.Abbreviation;
using PathShortener.Core.Ai;
using PathShortener.Core.Archives;
using PathShortener.Core.Cache;
using PathShortener.Core.Folders;
using PathShortener.Core.Models;
using PathShortener.Core.Planning;
using PathShortener.Core.Report;

namespace PathShortener.Gui;

public enum TargetKind { ExtractTo, InPlace, CopyTo }

public partial class MainWindow : Window
{
    // ---- моделі рядків ----
    public sealed class SourceRow : INotifyPropertyChanged
    {
        public required bool IsArchive { get; init; }
        public required string SourcePath { get; init; }
        public TargetKind Kind { get; set; }
        public string? TargetPath { get; set; }

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
        }

        private string _targetText = "";
        public string TargetText
        {
            get => _targetText;
            private set { _targetText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetText))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void RefreshTargetText(string defaultDest) => TargetText = Kind switch
        {
            TargetKind.InPlace => Loc.T("target.inPlace"),
            TargetKind.CopyTo => Loc.F("target.copyTo", TargetPath),
            _ => Loc.F("target.extractTo", TargetPath
                 ?? (string.IsNullOrWhiteSpace(defaultDest) ? Loc.T("target.chooseTarget") : defaultDest)),
        };
    }

    public sealed class PlanRow : INotifyPropertyChanged
    {
        public string Orig { get; init; } = "";      // оригінальний відносний шлях (незмінний)
        public bool IsHeader { get; init; }
        public bool IsDir { get; init; }
        public int GroupIndex { get; init; } = -1;   // до якого джерела належить рядок
        public int DestRootLen { get; init; }
        public string PrevNew { get; set; } = "";     // значення до останнього редагування (для каскаду)

        private string _new = "", _len = "", _status = "", _source = "", _conflict = "";
        public string New { get => _new; set { _new = value; Notify(nameof(New)); } }
        public string Len { get => _len; set { _len = value; Notify(nameof(Len)); } }
        public string Status { get => _status; set { _status = value; Notify(nameof(Status)); } }
        public string Source { get => _source; set { _source = value; Notify(nameof(Source)); } }
        public string Conflict { get => _conflict; set { _conflict = value; Notify(nameof(Conflict)); } }

        public bool Over { get; set; }
        public bool Truncated { get; init; }
        public bool Critical { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>Метадані одного джерела в поточному плані (для повторної побудови й застосування).</summary>
    private sealed record PlanGroup(SourceRow Item, string DestRoot, string CsvDir,
        IReadOnlyList<ArchiveEntry> Entries, int Budget);

    public sealed class ModelOption
    {
        public required string Name { get; init; }
        public required bool Installed { get; init; }
        public string Note { get; init; } = "";
        public override string ToString()
            => Name + (Note.Length > 0 ? " — " + Note : "") + (Installed ? "" : " · " + Loc.T("model.notInstalled"));
    }

    private enum OllamaState { NotInstalled, NotRunning, ModelMissing, Ready }

    private static readonly (string Name, string NoteKey)[] RecommendedModels =
    {
        ("qwen2.5:3b",   "model.note.qwen3b"),
        ("qwen2.5:1.5b", "model.note.qwen15b"),
        ("qwen2.5:7b",   "model.note.qwen7b"),
        ("llama3.2:3b",  "model.note.llama3b"),
    };
    private const string DefaultModel = "qwen2.5:3b";

    private readonly ObservableCollection<SourceRow> _folders = new();
    private readonly ObservableCollection<SourceRow> _archives = new();
    private readonly ObservableCollection<PlanRow> _plan = new();

    private readonly JsonPatternCache _cache;
    private readonly OllamaManager _ollama = new();
    private readonly ProfileStore _profiles;
    private readonly ExecutionLog _log;
    private readonly List<PlanGroup> _groups = new();
    private bool _hasCriticalConflicts;

    // Порядок провайдерів у комбо (індекс = позиція).
    private static readonly AiProvider[] ProviderOrder =
    {
        AiProvider.OllamaLocal, AiProvider.OllamaRemote,
        AiProvider.Claude, AiProvider.OpenAI, AiProvider.Grok, AiProvider.Gemini
    };
    private AiProvider CurrentProvider => ProviderOrder[Math.Max(0, ProviderCombo.SelectedIndex)];
    private static bool IsPaid(AiProvider p) => p is AiProvider.Claude or AiProvider.OpenAI or AiProvider.Grok or AiProvider.Gemini;
    private OllamaState _ollamaState = OllamaState.NotInstalled;
    private bool _running;
    private bool _uiReady; // блокує обробники під час початкового заповнення комбобоксів
    private bool _lastIntelligent;
    private CancellationTokenSource? _cts;

    private string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public MainWindow()
    {
        var s = LoadSettings();
        Loc.Load(s.Language);
        InitializeComponent();

        FoldersGrid.ItemsSource = _folders;
        ArchivesGrid.ItemsSource = _archives;
        PreviewGrid.ItemsSource = _plan;
        PreviewGrid.LoadingRow += (_, e) =>
        {
            e.Row.Background = e.Row.DataContext is PlanRow r
                ? r.IsHeader ? Res("AppRowHeader") : (r.Critical || r.Over) ? Res("AppRowOver") : r.Truncated ? Res("AppRowTrunc") : Brushes.Transparent
                : Brushes.Transparent;
        };
        // після зміни теми перефарбувати рядки плану
        ActualThemeVariantChanged += (_, _) =>
        {
            var items = PreviewGrid.ItemsSource;
            PreviewGrid.ItemsSource = null;
            PreviewGrid.ItemsSource = items;
        };

        string baseDir = AppContext.BaseDirectory;
        _cache = new JsonPatternCache(Path.Combine(baseDir, "patterns.json"));
        string seedCsv = Path.Combine(baseDir, "seed_dictionary.csv");
        if (File.Exists(seedCsv)) _cache.SeedFromCsv(seedCsv);
        _profiles = new ProfileStore(Path.Combine(baseDir, "ai_profiles.json"));
        _log = new ExecutionLog(Path.Combine(baseDir, "execution.log"));

        Budget.Value = s.Budget;
        DefaultDest.Text = s.DefaultDest;
        FileLimit.Value = s.MaxFileName;
        FolderLimit.Value = s.MaxFolderName;
        RuleDict.IsChecked = s.RuleDict;
        RuleStop.IsChecked = s.RuleStop;
        RuleCollapse.IsChecked = s.RuleCollapse;
        RuleDedup.IsChecked = s.RuleDedup;
        RuleNormDates.IsChecked = s.RuleNormDates;
        RuleAcronyms.IsChecked = s.RuleAcronyms;
        RuleTruncWords.IsChecked = s.RuleTruncWords;
        WordLen.Value = s.WordMaxLen;
        ApplyTheme(s.Theme);
        FillInterfaceCombos(s.Theme, s.Language);
        ApplyTexts();
        FillProviderCombo();
        RefreshProfiles();
        RefreshDict();
        // словник поповнюється під час аналізу (llm) — оновлювати при відкритті таба
        MainTabs.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(MainTabs.SelectedItem, DictionaryTab)) RefreshDict();
        };
        _uiReady = true;

        Opened += async (_, _) => await RefreshOllamaAsync();
        Closed += (_, _) => { _cache.Dispose(); _ollama.Dispose(); };
    }

    private IBrush Res(string key)
        => this.TryGetResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Transparent;

    // ---- налаштування ----

    private sealed record AppSettings(
        string Model, string Theme, string Language, int Budget, string DefaultDest,
        int MaxFileName, int MaxFolderName,
        bool RuleDict, bool RuleStop, bool RuleCollapse, bool RuleDedup,
        bool RuleNormDates, bool RuleAcronyms, bool RuleTruncWords, int WordMaxLen);

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var r = doc.RootElement;
                string S(string k, string d) => r.TryGetProperty(k, out var p) ? p.GetString() ?? d : d;
                int I(string k, int d) => r.TryGetProperty(k, out var p) && p.TryGetInt32(out var i) ? i : d;
                bool B(string k, bool d) => r.TryGetProperty(k, out var p) ? p.GetBoolean() : d;
                return new AppSettings(
                    S("model", DefaultModel), S("theme", "light"), S("language", "uk"),
                    I("budget", 259), S("defaultDest", ""),
                    I("maxFileName", 0), I("maxFolderName", 0),
                    B("ruleDict", true), B("ruleStop", true), B("ruleCollapse", true), B("ruleDedup", true),
                    B("ruleNormDates", true), B("ruleAcronyms", false), B("ruleTruncWords", false), I("wordMaxLen", 5));
            }
        }
        catch { /* далі — дефолти */ }
        return new AppSettings(DefaultModel, "light", "uk", 259, "", 0, 0,
            true, true, true, true, true, false, false, 5);
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
            {
                model = SelectedModel,
                theme = ThemeCombo.SelectedIndex switch { 1 => "dark", 2 => "system", _ => "light" },
                language = LanguageCombo.SelectedIndex == 1 ? "en" : "uk",
                budget = (int)(Budget.Value ?? 259),
                defaultDest = DefaultDest.Text ?? "",
                maxFileName = (int)(FileLimit.Value ?? 0),
                maxFolderName = (int)(FolderLimit.Value ?? 0),
                ruleDict = RuleDict.IsChecked == true,
                ruleStop = RuleStop.IsChecked == true,
                ruleCollapse = RuleCollapse.IsChecked == true,
                ruleDedup = RuleDedup.IsChecked == true,
                ruleNormDates = RuleNormDates.IsChecked == true,
                ruleAcronyms = RuleAcronyms.IsChecked == true,
                ruleTruncWords = RuleTruncWords.IsChecked == true,
                wordMaxLen = (int)(WordLen.Value ?? 5)
            }));
        }
        catch { /* налаштування — не критичні */ }
    }

    private void OnRuleChanged(object? sender, RoutedEventArgs e)
    {
        if (_uiReady) SaveSettings();
    }

    private StandardRuleOptions CurrentRuleOptions() => new()
    {
        UseWordDictionary = RuleDict.IsChecked == true,
        DropStopWords = RuleStop.IsChecked == true,
        CollapseSeparators = RuleCollapse.IsChecked == true,
        RemoveDuplicateWords = RuleDedup.IsChecked == true,
        NormalizeDates = RuleNormDates.IsChecked == true,
        AllowAcronyms = RuleAcronyms.IsChecked == true,
        TruncateWords = RuleTruncWords.IsChecked == true,
        WordMaxLength = (int)(WordLen.Value ?? 5)
    };

    private void FillInterfaceCombos(string theme, string language)
    {
        ThemeCombo.ItemsSource = new[] { Loc.T("theme.light"), Loc.T("theme.dark"), Loc.T("theme.system") };
        ThemeCombo.SelectedIndex = theme switch { "dark" => 1, "system" => 2, _ => 0 };
        LanguageCombo.ItemsSource = new[] { Loc.T("language.ukrainian"), Loc.T("language.english") };
        LanguageCombo.SelectedIndex = language == "en" ? 1 : 0;
    }

    private static void ApplyTheme(string theme)
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                "dark" => ThemeVariant.Dark,
                "system" => ThemeVariant.Default,
                _ => ThemeVariant.Light,
            };
    }

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        ApplyTheme(ThemeCombo.SelectedIndex switch { 1 => "dark", 2 => "system", _ => "light" });
        SaveSettings();
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _uiReady = false;
        Loc.Load(LanguageCombo.SelectedIndex == 1 ? "en" : "uk");
        FillInterfaceCombos(ThemeCombo.SelectedIndex switch { 1 => "dark", 2 => "system", _ => "light" },
                            Loc.Lang);
        ApplyTexts();
        foreach (var r in _folders) r.RefreshTargetText(DefaultDest.Text ?? "");
        foreach (var r in _archives) r.RefreshTargetText(DefaultDest.Text ?? "");
        _uiReady = true;
        SaveSettings();
    }

    private void OnBudgetChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_uiReady) SaveSettings();
    }

    /// <summary>Усі видимі тексти — з ресурсів локалізації (жодного тексту в XAML).</summary>
    private void ApplyTexts()
    {
        Title = Loc.T("app.title");

        AnalyzeButton.Content = Loc.T("button.analyze");
        IntelligentButton.Content = Loc.T("button.intelligentAnalyze");
        ApplyButton.Content = Loc.T("button.apply");
        StopButton.Content = Loc.T("button.stop");
        PreviewTitle.Text = Loc.T("preview.title");
        if (!_running) StatusText.Text = Loc.T("status.ready");

        PreviewGrid.Columns[0].Header = Loc.T("preview.col.orig");
        PreviewGrid.Columns[1].Header = Loc.T("preview.col.new");
        PreviewGrid.Columns[2].Header = Loc.T("preview.col.len");
        PreviewGrid.Columns[3].Header = Loc.T("preview.col.status");
        PreviewGrid.Columns[4].Header = Loc.T("preview.col.source");
        PreviewGrid.Columns[5].Header = Loc.T("preview.col.conflict");

        AnalysisTab.Header = Loc.T("tab.analysis");
        FoldersTab.Header = Loc.T("tab.folders");
        ArchivesTab.Header = Loc.T("tab.archives");
        DictionaryTab.Header = Loc.T("tab.dictionary");
        SettingsTab.Header = Loc.T("tab.settings");
        StatsTab.Header = Loc.T("tab.stats");

        FoldersHint.Text = Loc.T("sources.folders.hint");
        ArchivesHint.Text = Loc.T("sources.archives.hint");
        AddFolderBtn.Content = Loc.T("button.addFolders");
        AddArchiveBtn.Content = Loc.T("button.addArchives");
        FolderTargetBtn.Content = Loc.T("button.changeTarget");
        ArchiveTargetBtn.Content = Loc.T("button.changeTarget");
        RemoveFolderBtn.Content = Loc.T("button.remove");
        RemoveArchiveBtn.Content = Loc.T("button.remove");
        ClearFoldersBtn.Content = Loc.T("button.clear");
        ClearArchivesBtn.Content = Loc.T("button.clear");
        foreach (var g in new[] { FoldersGrid, ArchivesGrid })
        {
            g.Columns[0].Header = Loc.T("sources.col.enabled");
            g.Columns[1].Header = Loc.T("sources.col.path");
            g.Columns[2].Header = Loc.T("sources.col.target");
        }

        InterfaceTitle.Text = Loc.T("settings.interface.title");
        ThemeLabel.Text = Loc.T("settings.interface.theme");
        LanguageLabel.Text = Loc.T("settings.interface.language");
        AboutButton.Content = Loc.T("button.about");
        LimitsTitle.Text = Loc.T("settings.limits.title");
        PathLimitLabel.Text = Loc.T("settings.limits.path");
        FileLimitLabel.Text = Loc.T("settings.limits.file");
        FolderLimitLabel.Text = Loc.T("settings.limits.folder");
        RulesTitle.Text = Loc.T("settings.rules.title");
        RuleDict.Content = Loc.T("settings.rules.dict");
        RuleStop.Content = Loc.T("settings.rules.stop");
        RuleCollapse.Content = Loc.T("settings.rules.collapse");
        RuleDedup.Content = Loc.T("settings.rules.dedup");
        RuleNormDates.Content = Loc.T("settings.rules.normDates");
        RuleAcronyms.Content = Loc.T("settings.rules.acronyms");
        RuleTruncWords.Content = Loc.T("settings.rules.truncWords");
        WordLenLabel.Text = Loc.T("settings.rules.wordLen");
        DefaultDestTitle.Text = Loc.T("settings.dest.title");
        DefaultDest.Watermark = Loc.T("settings.dest.watermark");
        ChooseDestBtn.Content = Loc.T("button.chooseFolder");
        OllamaTitle.Text = Loc.T("settings.ollama.title");
        ModelLabel.Text = Loc.T("settings.ollama.model");
        ToolTip.SetTip(OllamaRefreshBtn, Loc.T("ollama.refresh.tip"));

        ProviderTitle.Text = Loc.T("settings.provider.title");
        ProviderLabel.Text = Loc.T("settings.provider.select");
        ProfileListLabel.Text = Loc.T("settings.provider.profile");
        ProfileAddBtn.Content = Loc.T("profiles.add");
        ProfileEditBtn.Content = Loc.T("profiles.edit");
        ProfileDeleteBtn.Content = Loc.T("profiles.delete");
        ProfileActiveBtn.Content = Loc.T("profiles.setActive");
        ProfileTestBtn.Content = Loc.T("profiles.test");

        // словник (окремий таб)
        DictHint.Text = Loc.T("settings.dict.title");
        DictAddBtn.Content = Loc.T("dict.add");
        DictEditBtn.Content = Loc.T("dict.edit");
        DictDeleteBtn.Content = Loc.T("dict.delete");
        DictApproveBtn.Content = Loc.T("dict.approve");
        DictGrid.Columns[0].Header = Loc.T("dict.col.original");
        DictGrid.Columns[1].Header = Loc.T("dict.col.short");
        DictGrid.Columns[2].Header = Loc.T("dict.col.source");

        // статистика
        StatsTitle.Text = Loc.T("stats.title");
        StatsHint.Text = Loc.T("stats.hint");
        StatSourcesLabel.Text = Loc.T("stats.sources");
        StatPathsLabel.Text = Loc.T("stats.paths");
        StatModelLabel.Text = Loc.T("stats.model");
        StatCallsLabel.Text = Loc.T("stats.calls");
        StatTokensLabel.Text = Loc.T("stats.tokens");
        StatAcceptedLabel.Text = Loc.T("stats.accepted");
        UpdateStats();
    }

    // ---- провайдери та профілі ----

    private static string ProviderName(AiProvider p) => Loc.T(p switch
    {
        AiProvider.OllamaLocal => "provider.ollamaLocal",
        AiProvider.OllamaRemote => "provider.ollamaRemote",
        AiProvider.Claude => "provider.claude",
        AiProvider.OpenAI => "provider.openai",
        AiProvider.Grok => "provider.grok",
        _ => "provider.gemini",
    });

    private void FillProviderCombo()
    {
        bool was = _uiReady; _uiReady = false;
        ProviderCombo.ItemsSource = ProviderOrder.Select(ProviderName).ToList();
        ProviderCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ProviderOrder, _profiles.ActiveProvider));
        _uiReady = was;
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _profiles.ActiveProvider = CurrentProvider;
        _profiles.Save();
        RefreshProfiles();
    }

    private void RefreshProfiles()
    {
        var provider = CurrentProvider;
        // Локальна Ollama профілів не потребує — керується блоком нижче.
        bool needsProfiles = provider != AiProvider.OllamaLocal;
        ProfilesList.IsEnabled = ProfileButtons.IsEnabled = needsProfiles;

        var items = _profiles.For(provider).ToList();
        ProfilesList.ItemsSource = items.Select(p =>
            (p.IsActive ? "● " : "○ ") + p.ProfileName +
            (p.SelectedModel.Length > 0 ? $" · {p.SelectedModel}" : "") +
            (p.LastConnectionStatus.Length > 0 ? $" · {p.LastConnectionStatus}" : "")).ToList();
        ProfileStatus.Text = needsProfiles && items.Count == 0 ? Loc.T("profiles.none") : "";
    }

    private AiProfile? SelectedProfile()
    {
        int i = ProfilesList.SelectedIndex;
        var items = _profiles.For(CurrentProvider).ToList();
        return i >= 0 && i < items.Count ? items[i] : null;
    }

    private void OnProfileSelected(object? sender, SelectionChangedEventArgs e) { }

    private async void OnProfileAdd(object? sender, RoutedEventArgs e)
    {
        var profile = new AiProfile { Provider = CurrentProvider, ProfileName = ProviderName(CurrentProvider) };
        if (await new ProfileDialog(profile).ShowDialog<bool>(this))
        {
            if (!_profiles.For(CurrentProvider).Any()) profile.IsActive = true;
            _profiles.Profiles.Add(profile);
            _profiles.Save();
            RefreshProfiles();
        }
    }

    private async void OnProfileEdit(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile() is not { } p) { await Msg.Info(this, Loc.T("msg.selectRow")); return; }
        if (await new ProfileDialog(p).ShowDialog<bool>(this)) { _profiles.Save(); RefreshProfiles(); }
    }

    private async void OnProfileDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile() is not { } p) { await Msg.Info(this, Loc.T("msg.selectRow")); return; }
        if (await Msg.YesNo(this, Loc.F("msg.confirmDeleteProfile", p.ProfileName)))
        {
            _profiles.Remove(p);
            _profiles.Save();
            RefreshProfiles();
        }
    }

    private void OnProfileSetActive(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile() is not { } p) return;
        _profiles.SetActive(p);
        _profiles.Save();
        RefreshProfiles();
    }

    private async void OnProfileTest(object? sender, RoutedEventArgs e)
    {
        if (SelectedProfile() is not { } p) { await Msg.Info(this, Loc.T("msg.selectRow")); return; }
        ProfileStatus.Text = Loc.T("profiles.testing");
        try
        {
            using var model = new AiChatModel(p.Provider, p.SelectedModel, p.ApiKey, p.BaseUrl);
            var res = await model.TestAsync();
            p.LastConnectionStatus = res.Ok ? "ok" : "fail";
            p.LastCheckedAt = DateTime.Now;
            ProfileStatus.Text = res.Ok ? Loc.T("profiles.testOk") : Loc.F("profiles.testFail", res.Error ?? "");
            _profiles.Save();
            RefreshProfiles();
        }
        catch (Exception ex)
        {
            ProfileStatus.Text = Loc.F("profiles.testFail", ex.Message);
        }
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
        => await new AboutDialog().ShowDialog(this);

    // ---- Ollama ----

    private string SelectedModel => (ModelCombo.SelectedItem as ModelOption)?.Name ?? DefaultModel;

    private async void OnOllamaRefresh(object? sender, RoutedEventArgs e) => await RefreshOllamaAsync();

    private async Task RefreshOllamaAsync()
    {
        OllamaStatus.Text = Loc.T("ollama.checking");
        bool installed = OllamaManager.IsInstalled();
        bool running = await _ollama.IsRunningAsync();
        var installedModels = running ? await _ollama.ListModelsAsync() : Array.Empty<OllamaModelInfo>();

        string want = (ModelCombo.SelectedItem as ModelOption)?.Name ?? LoadSettings().Model;
        var options = new List<ModelOption>();
        foreach (var (name, noteKey) in RecommendedModels)
            options.Add(new ModelOption
            {
                Name = name,
                Note = Loc.T(noteKey),
                Installed = installedModels.Any(m => ModelEquals(m.Name, name))
            });
        foreach (var m in installedModels)
            if (!options.Any(o => ModelEquals(o.Name, m.Name)))
                options.Add(new ModelOption
                {
                    Name = m.Name,
                    Note = Loc.F("model.installed", (m.SizeBytes / 1024.0 / 1024 / 1024).ToString("0.0")),
                    Installed = true
                });

        bool wasReady = _uiReady;
        _uiReady = false;
        ModelCombo.ItemsSource = options;
        ModelCombo.SelectedItem = options.FirstOrDefault(o => ModelEquals(o.Name, want)) ?? options[0];
        _uiReady = wasReady;

        OllamaStatus.Text = !installed && !running ? Loc.T("ollama.notInstalled")
            : !running ? Loc.T("ollama.notRunning")
            : Loc.F("ollama.running", installedModels.Count);

        UpdateOllamaState(installed, running);
    }

    private void UpdateOllamaState(bool installed, bool running)
    {
        var selected = ModelCombo.SelectedItem as ModelOption;
        _ollamaState = !installed && !running ? OllamaState.NotInstalled
            : !running ? OllamaState.NotRunning
            : selected is { Installed: false } ? OllamaState.ModelMissing
            : OllamaState.Ready;

        (string? key, bool visible) = _ollamaState switch
        {
            OllamaState.NotInstalled => ("ollama.install", true),
            OllamaState.NotRunning => ("ollama.start", true),
            OllamaState.ModelMissing => ("ollama.pull", true),
            _ => ((string?)null, false),
        };
        OllamaActionButton.Content = key is null ? "" : Loc.T(key);
        OllamaActionButton.IsVisible = visible;
    }

    private void OnModelSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || ModelCombo.SelectedItem is not ModelOption) return;
        SaveSettings();
        bool running = _ollamaState is OllamaState.Ready or OllamaState.ModelMissing;
        UpdateOllamaState(installed: _ollamaState != OllamaState.NotInstalled, running);
    }

    private async void OnOllamaAction(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        SetUiEnabled(false);
        Progress.IsVisible = true;
        try
        {
            switch (_ollamaState)
            {
                case OllamaState.NotInstalled:
                    if (!await Msg.YesNo(this, Loc.T("ollama.confirmInstall"), Loc.T("ollama.confirmInstall.title")))
                        break;
                    string setup = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
                    var dlProg = new Progress<(long Done, long? Total)>(t =>
                    {
                        if (t.Total is { } total)
                        {
                            Progress.Maximum = total;
                            Progress.Value = t.Done;
                            StatusText.Text = Loc.F("ollama.downloading", t.Done / 1048576, total / 1048576);
                        }
                    });
                    await _ollama.DownloadInstallerAsync(setup, dlProg);
                    SetStatus(Loc.T("ollama.installing"), marquee: true);
                    await OllamaManager.RunInstallerAsync(setup);
                    SetStatus(Loc.T("ollama.waitingServer"), marquee: true);
                    await _ollama.WaitForServerAsync(TimeSpan.FromSeconds(60));
                    SetStatus(Loc.T("ollama.installed"), marquee: false);
                    break;

                case OllamaState.NotRunning:
                    SetStatus(Loc.T("ollama.starting"), marquee: true);
                    OllamaManager.StartServer();
                    bool up = await _ollama.WaitForServerAsync(TimeSpan.FromSeconds(20));
                    SetStatus(Loc.T(up ? "ollama.started" : "ollama.startFailed"), marquee: false);
                    break;

                case OllamaState.ModelMissing:
                    string model = SelectedModel;
                    if (!await Msg.YesNo(this, Loc.F("ollama.confirmPull", model), Loc.T("ollama.confirmPull.title")))
                        break;
                    var pullProg = new Progress<(string Status, double? Percent)>(t =>
                    {
                        if (t.Percent is { } p)
                        {
                            Progress.IsIndeterminate = false;
                            Progress.Maximum = 100;
                            Progress.Value = p;
                            StatusText.Text = Loc.F("ollama.pullingPct", model, t.Status, p.ToString("0"));
                        }
                        else
                        {
                            Progress.IsIndeterminate = true;
                            StatusText.Text = Loc.F("ollama.pulling", model, t.Status);
                        }
                    });
                    await _ollama.PullModelAsync(model, pullProg);
                    SetStatus(Loc.F("ollama.pulled", model), marquee: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await Msg.Info(this, Loc.F("msg.error", ex.Message));
            SetStatus(Loc.T("status.error"), marquee: false);
        }
        finally
        {
            _running = false;
            Progress.IsVisible = false;
            Progress.IsIndeterminate = false;
            SetUiEnabled(true);
            await RefreshOllamaAsync();
        }
    }

    private static bool ModelEquals(string a, string b)
    {
        static string Norm(string s) => s.EndsWith(":latest", StringComparison.Ordinal) ? s[..^7] : s;
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    // ---- джерела: папки та архіви ----

    private async void OnAddArchives(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("picker.archives.title"),
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("picker.archives.filter")) { Patterns = new[] { "*.zip", "*.7z", "*.rar" } },
                FilePickerFileTypes.All
            }
        });
        foreach (var f in files)
        {
            string? path = f.TryGetLocalPath();
            if (path is null) continue;
            if (_archives.Any(s => string.Equals(s.SourcePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var row = new SourceRow { IsArchive = true, SourcePath = path, Kind = TargetKind.ExtractTo };
            row.RefreshTargetText(DefaultDest.Text ?? "");
            _archives.Add(row);
        }
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = Loc.T("picker.folder.title"), AllowMultiple = true });
        foreach (var f in folders)
        {
            string? path = f.TryGetLocalPath()?.TrimEnd('\\', '/');
            if (path is null) continue;
            if (Path.GetDirectoryName(path) is null)
            {
                await Msg.Info(this, Loc.T("msg.driveRoot"));
                continue;
            }
            if (_folders.Any(s => string.Equals(s.SourcePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            var row = new SourceRow { IsArchive = false, SourcePath = path, Kind = TargetKind.InPlace };
            row.RefreshTargetText(DefaultDest.Text ?? "");
            _folders.Add(row);
        }
    }

    private void OnRemoveFolder(object? sender, RoutedEventArgs e)
    { if (FoldersGrid.SelectedItem is SourceRow r) _folders.Remove(r); }

    private void OnRemoveArchive(object? sender, RoutedEventArgs e)
    { if (ArchivesGrid.SelectedItem is SourceRow r) _archives.Remove(r); }

    private void OnClearFolders(object? sender, RoutedEventArgs e) => _folders.Clear();
    private void OnClearArchives(object? sender, RoutedEventArgs e) => _archives.Clear();

    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e) => OnFolderTarget(sender, e);
    private void OnArchiveDoubleTapped(object? sender, TappedEventArgs e) => OnArchiveTarget(sender, e);

    private async void OnFolderTarget(object? sender, RoutedEventArgs e)
    {
        if (FoldersGrid.SelectedItem is not SourceRow row)
        { await Msg.Info(this, Loc.T("msg.selectRow")); return; }
        var dlg = new TargetDialog(row.Kind, row.TargetPath);
        if (await dlg.ShowDialog<bool>(this))
        {
            row.Kind = dlg.ResultKind;
            row.TargetPath = dlg.ResultPath;
        }
        row.RefreshTargetText(DefaultDest.Text ?? "");
    }

    private async void OnArchiveTarget(object? sender, RoutedEventArgs e)
    {
        if (ArchivesGrid.SelectedItem is not SourceRow row)
        { await Msg.Info(this, Loc.T("msg.selectRow")); return; }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = Loc.T("target.archive.title") });
        if (folders.Count > 0) row.TargetPath = folders[0].TryGetLocalPath();
        row.RefreshTargetText(DefaultDest.Text ?? "");
    }

    private async void OnChooseDefault(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = Loc.T("picker.dest.title") });
        if (folders.Count == 0) return;
        DefaultDest.Text = folders[0].TryGetLocalPath() ?? "";
        foreach (var s in _folders) s.RefreshTargetText(DefaultDest.Text ?? "");
        foreach (var s in _archives) s.RefreshTargetText(DefaultDest.Text ?? "");
        SaveSettings();
    }

    // ---- аналіз і застосування ----

    private void OnAnalyze(object? sender, RoutedEventArgs e) => RunAnalyze(intelligent: false);
    private void OnIntelligentAnalyze(object? sender, RoutedEventArgs e) => RunAnalyze(intelligent: true);
    private void OnApply(object? sender, RoutedEventArgs e) => RunApply();
    private void OnStop(object? sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>Звичайний аналіз: лише затверджений словник (dict/manual) + правила; нічого не пише в кеш.</summary>
    private sealed class ApprovedOnlyCache : IPatternCache
    {
        private readonly JsonPatternCache _inner;
        public ApprovedOnlyCache(JsonPatternCache inner) => _inner = inner;
        public bool TryGet(string core, out string shortForm) => _inner.TryGetApproved(core, out shortForm);
        public void Put(string core, string shortForm, string source) { /* алгоритмічні результати не потрапляють у словник */ }
    }

    /// <summary>Аналіз: будує план для кожного джерела, показує в таблиці й запам'ятовує для застосування.</summary>
    private async void RunAnalyze(bool intelligent)
    {
        if (_running) return;
        var items = _folders.Concat(_archives).Where(i => i.Enabled).ToList();
        string defaultDest = DefaultDest.Text ?? "";
        if (items.Count == 0)
        { await Msg.Info(this, Loc.T("msg.addSource")); return; }
        if (items.Any(i => i.IsArchive && i.TargetPath is null) && string.IsNullOrWhiteSpace(defaultDest))
        { await Msg.Info(this, Loc.T("msg.chooseDest")); return; }

        ResetStats();

        // Вибір провайдера інтелектуального аналізу + відповідне попередження.
        AiChatModel? chatModel = null;
        if (intelligent)
        {
            var provider = _profiles.ActiveProvider;
            if (provider == AiProvider.OllamaLocal)
            {
                if (_ollamaState != OllamaState.Ready &&
                    !await Msg.YesNo(this, Loc.T("msg.ollamaUnavailable"), Loc.T("msg.ollamaUnavailable.title")))
                    return;
                chatModel = new AiChatModel(AiProvider.OllamaLocal, SelectedModel, "", null);
                _statModel = SelectedModel;
            }
            else
            {
                var prof = _profiles.ActiveProfile(provider);
                if (prof is null) { await Msg.Info(this, Loc.T("msg.noProfile")); return; }
                if (provider == AiProvider.OllamaRemote)
                    await Msg.Info(this, Loc.T("msg.remoteOllama"), Loc.T("msg.remoteOllama.title"));
                else if (!await Msg.YesNo(this, Loc.T("msg.paidApi"), Loc.T("msg.paidApi.title")))
                    return;
                chatModel = new AiChatModel(provider, prof.SelectedModel, prof.ApiKey, prof.BaseUrl);
                _statModel = $"{ProviderName(provider)} · {prof.SelectedModel}";
            }
        }

        _running = true;
        _lastIntelligent = intelligent;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetUiEnabled(false);
        StopButton.IsVisible = true;
        _plan.Clear();
        _groups.Clear();
        Progress.IsVisible = true;

        // Планувальний провайдер: правила → кеш → валідатор (ПІБ, акроніми, «не збільшує довжину») → дати.
        var opts = CurrentRuleOptions();
        Func<string, string, bool> isApproved = (core, result) =>
            _cache.TryGetApproved(core, out var a) && string.Equals(a, result, StringComparison.Ordinal);
        IPatternCache planCache = intelligent ? _cache : new ApprovedOnlyCache(_cache);
        IAbbreviationProvider abbrev = new DatePreservingAbbreviator(
            new CachedAbbreviator(
                new SafeAbbreviator(new RuleChainAbbreviator(opts), isApproved, opts.AllowAcronyms),
                planCache, source: "algorithm"),
            normalizeDates: opts.NormalizeDates);

        var limits = new SegmentLimits((int)(FileLimit.Value ?? 0), (int)(FolderLimit.Value ?? 0));
        var analyzer = chatModel is null ? null : new BatchAnalyzer(chatModel);

        try
        {
            var sevenZip = new SevenZipArchive(Resolve7Zip());
            var planner = new ShorteningPlanner(abbrev);
            int budget = (int)(Budget.Value ?? 259);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(item.SourcePath.TrimEnd('\\', '/'));

                bool exists = item.IsArchive ? File.Exists(item.SourcePath) : Directory.Exists(item.SourcePath);
                if (!exists)
                {
                    _plan.Add(new PlanRow { Orig = $"▸ {name} — {Loc.T("status.skippedMissing")}", IsHeader = true });
                    continue;
                }

                SetStatus(Loc.F(item.IsArchive ? "status.scanningArchives" : "status.scanningFolders", name), marquee: true);
                _log.Write(item.IsArchive ? Loc.F("log.scanArchives", name) : Loc.F("log.scanFolders", name));
                var entries = await Task.Run(() =>
                    item.IsArchive ? sevenZip.Read(item.SourcePath) : FolderScanner.Scan(item.SourcePath), ct);
                ct.ThrowIfCancellationRequested();
                _statSources++;
                _statPaths += entries.Count;

                string destRoot = item.Kind switch
                {
                    TargetKind.InPlace => Path.GetDirectoryName(Path.GetFullPath(item.SourcePath).TrimEnd('\\', '/'))!,
                    TargetKind.CopyTo => item.TargetPath!,
                    _ => Path.Combine(item.TargetPath ?? defaultDest, Path.GetFileNameWithoutExtension(item.SourcePath)),
                };
                string csvDir = item.Kind switch
                {
                    TargetKind.InPlace => destRoot,
                    TargetKind.CopyTo => item.TargetPath!,
                    _ => item.TargetPath ?? defaultDest,
                };

                if (analyzer is not null)
                {
                    _log.Write(Loc.F("log.intelligent", _profiles.ActiveProvider, name));
                    var segs = await Task.Run(() =>
                        BatchAnalyzer.CollectSegments(destRoot, entries, budget, limits, _cache), ct);
                    if (segs.Count > 0)
                    {
                        SetStatus(Loc.F("status.aiPrefetch", segs.Count), marquee: false);
                        var pfProg = new Progress<(int Done, int Total)>(t =>
                        {
                            Progress.Maximum = Math.Max(1, t.Total);
                            Progress.Value = Math.Min(t.Done, t.Total);
                        });
                        var outcome = await analyzer.WarmCacheAsync(segs, _cache, pfProg, ct);
                        _statCalls += outcome.ModelCalls;
                        _statTokens += outcome.TokensEstimate;
                        _statAccepted += outcome.Accepted;
                        _statRejected += outcome.Rejected;
                        _log.Write(Loc.F("log.batch", outcome.Sent, outcome.Accepted, outcome.Rejected));
                        SetStatus(Loc.F("status.aiPrefetchDone", outcome.Accepted, outcome.Rejected), marquee: false);
                    }
                }
                else _log.Write(Loc.F("log.analyze", name));

                SetStatus(Loc.F(intelligent ? "status.intelligentAnalyzing" : "status.analyzing", name), marquee: true);
                var plan = await Task.Run(() => planner.Plan(destRoot, entries, budget, limits), ct);

                int groupIndex = _groups.Count;
                _groups.Add(new PlanGroup(item, destRoot, csvDir, entries, budget));
                ShowPlan(name, plan, intelligent, groupIndex, destRoot);
            }
            RecheckConflicts();
            UpdateStats();
            SetStatus(Loc.T("status.planDone"), marquee: false);
        }
        catch (OperationCanceledException) { SetStatus(Loc.T("status.stopped"), marquee: false); _log.Write(Loc.T("log.stopped")); UpdateStats(); }
        catch (Exception ex)
        {
            await Msg.Info(this, Loc.F("msg.error", ex.Message));
            SetStatus(Loc.T("status.error"), marquee: false);
            _log.Write(Loc.F("log.error", ex.Message));
        }
        finally
        {
            chatModel?.Dispose();
            _cts?.Dispose(); _cts = null;
            _running = false;
            Progress.IsVisible = false; Progress.IsIndeterminate = false;
            StopButton.IsVisible = false;
            SetUiEnabled(true);
        }
    }

    /// <summary>Застосування: виконує ПОТОЧНИЙ (можливо, відредагований вручну) план після перевірки конфліктів.</summary>
    private async void RunApply()
    {
        if (_running) return;
        if (_groups.Count == 0) { await Msg.Info(this, Loc.T("msg.addSource")); return; }

        RecheckConflicts();
        if (_hasCriticalConflicts)
        {
            var kinds = string.Join(", ", CriticalKindLabels());
            await Msg.Info(this, Loc.F("apply.blocked", kinds), Loc.T("apply.blocked.title"));
            _log.Write(Loc.F("log.conflictsBlock", kinds));
            return;
        }

        _running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetUiEnabled(false);
        StopButton.IsVisible = true;
        Progress.IsVisible = true;

        try
        {
            var sevenZip = new SevenZipArchive(Resolve7Zip());
            for (int g = 0; g < _groups.Count; g++)
            {
                ct.ThrowIfCancellationRequested();
                var group = _groups[g];
                var item = group.Item;
                string name = Path.GetFileName(item.SourcePath.TrimEnd('\\', '/'));

                // Побудувати план із поточних (можливо, відредагованих) рядків цього джерела.
                var rows = _plan.Where(r => !r.IsHeader && r.GroupIndex == g).ToList();
                if (rows.Count == 0) continue;
                var plan = BuildPlan(group, rows);

                if (!plan.AllFit &&
                    !await Msg.YesNo(this, Loc.F("msg.overBudget", name, plan.OverBudgetCount), Loc.T("msg.overBudget.title")))
                    continue;

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string safeName = Path.GetFileNameWithoutExtension(name);
                MappingReport.WriteCsv(plan, Path.Combine(group.CsvDir, $"map_{safeName}_{stamp}.csv"));

                SetStatus(Loc.F(item.Kind == TargetKind.ExtractTo ? "status.extracting"
                              : item.Kind == TargetKind.InPlace ? "status.renaming" : "status.copying", name),
                          marquee: false);
                _log.Write(Loc.F("log.apply", item.Kind, name));
                var prog = new Progress<(int Done, int Total)>(t =>
                {
                    Progress.Maximum = Math.Max(1, t.Total);
                    Progress.Value = Math.Min(t.Done, t.Total);
                });
                await Task.Run(() =>
                {
                    switch (item.Kind)
                    {
                        case TargetKind.ExtractTo: sevenZip.ExtractWithPlan(item.SourcePath, plan, prog, ct); break;
                        case TargetKind.InPlace: FolderPlanApplier.ApplyInPlace(plan, prog, ct); break;
                        case TargetKind.CopyTo:
                            string sourceParent = Path.GetDirectoryName(Path.GetFullPath(item.SourcePath).TrimEnd('\\', '/'))!;
                            FolderPlanApplier.ApplyCopy(plan, sourceParent, group.Entries, prog, ct);
                            break;
                    }
                }, ct);
            }
            SetStatus(Loc.T("status.applyDone"), marquee: false);
            _log.Write(Loc.T("log.done"));
        }
        catch (OperationCanceledException) { SetStatus(Loc.T("status.stopped"), marquee: false); _log.Write(Loc.T("log.stopped")); }
        catch (Exception ex)
        {
            await Msg.Info(this, Loc.F("msg.error", ex.Message));
            SetStatus(Loc.T("status.error"), marquee: false);
            _log.Write(Loc.F("log.error", ex.Message));
        }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _running = false;
            Progress.IsVisible = false; Progress.IsIndeterminate = false;
            StopButton.IsVisible = false;
            SetUiEnabled(true);
        }
    }

    /// <summary>Будує ShorteningPlan із поточних рядків таблиці (для застосування відредагованого плану).</summary>
    private static ShorteningPlan BuildPlan(PlanGroup group, IReadOnlyList<PlanRow> rows)
    {
        var entries = rows.Select(r =>
        {
            int full = group.DestRoot.TrimEnd('\\', '/').Length + 1 + r.New.Length;
            return new PlanEntry(
                OriginalRelativePath: r.Orig,
                NewRelativePath: r.New,
                FinalFullLength: full,
                WithinBudget: full <= group.Budget,
                WasShortened: !string.Equals(r.Orig, r.New, StringComparison.Ordinal),
                WasTruncated: r.Truncated);
        }).ToList();
        return new ShorteningPlan { DestinationRoot = group.DestRoot, Budget = group.Budget, Entries = entries };
    }

    private void SetStatus(string text, bool marquee)
    {
        StatusText.Text = text;
        Progress.IsIndeterminate = marquee;
        if (!marquee) Progress.Value = 0;
    }

    private void SetUiEnabled(bool enabled)
    {
        ActionButtons.IsEnabled = enabled;
        FolderButtons.IsEnabled = enabled;
        ArchiveButtons.IsEnabled = enabled;
        FoldersGrid.IsEnabled = enabled;
        ArchivesGrid.IsEnabled = enabled;
        DefaultDestRow.IsEnabled = enabled;
        OllamaRow.IsEnabled = enabled;
        Budget.IsEnabled = enabled;
        ThemeCombo.IsEnabled = enabled;
        LanguageCombo.IsEnabled = enabled;
    }

    private void ShowPlan(string sourceName, ShorteningPlan plan, bool intelligent, int groupIndex, string destRoot)
    {
        _plan.Add(new PlanRow
        {
            Orig = Loc.F("preview.group", sourceName, plan.TotalEntries, plan.ShortenedCount,
                         plan.TruncatedCount, plan.OverBudgetCount),
            IsHeader = true
        });
        int destLen = destRoot.TrimEnd('\\', '/').Length;
        string changedSource = Loc.T(intelligent ? "source.ai" : "source.standard");
        var dirSet = plan.Entries.Where(e =>
            plan.Entries.Any(o => o != e && o.OriginalRelativePath.StartsWith(e.OriginalRelativePath + "\\", StringComparison.Ordinal)))
            .Select(e => e.OriginalRelativePath).ToHashSet(StringComparer.Ordinal);

        foreach (var e in plan.Entries)
        {
            _plan.Add(new PlanRow
            {
                Orig = e.OriginalRelativePath,
                New = e.NewRelativePath,
                PrevNew = e.NewRelativePath,
                Len = e.FinalFullLength.ToString(),
                Status = !e.WithinBudget ? Loc.T("preview.status.over")
                       : e.WasTruncated ? Loc.T("preview.status.truncated")
                       : e.WasShortened ? Loc.T("preview.status.shortened") : Loc.T("preview.status.none"),
                Source = e.WasShortened ? changedSource : Loc.T("source.none"),
                Over = !e.WithinBudget,
                Truncated = e.WasTruncated,
                GroupIndex = groupIndex,
                DestRootLen = destLen,
                IsDir = dirSet.Contains(e.OriginalRelativePath),
            });
        }
    }

    // ---- ручне редагування, каскад, конфлікти ----

    private async void OnPreviewCellEdited(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.Row.DataContext is not PlanRow row || row.IsHeader) return;
        string newVal = (row.New ?? "").Trim();
        row.New = newVal;
        if (string.Equals(newVal, row.PrevNew, StringComparison.Ordinal)) return;

        // Каскад: якщо змінено сегмент ПАПКИ, який зустрічається в інших шляхах цього джерела.
        string oldVal = row.PrevNew;
        var oldParts = oldVal.Split('\\');
        var newParts = newVal.Split('\\');
        if (oldParts.Length == newParts.Length)
        {
            int changed = -1, diffs = 0;
            for (int i = 0; i < oldParts.Length; i++)
                if (!string.Equals(oldParts[i], newParts[i], StringComparison.Ordinal)) { changed = i; diffs++; }

            // рівно один змінений сегмент, і він НЕ останній (тобто це папка)
            if (diffs == 1 && changed < oldParts.Length - 1)
            {
                string oldPrefix = string.Join('\\', oldParts.Take(changed + 1)) + "\\";
                string newPrefix = string.Join('\\', newParts.Take(changed + 1)) + "\\";
                var affected = _plan.Where(r => !r.IsHeader && r.GroupIndex == row.GroupIndex && r != row &&
                    r.New.StartsWith(oldPrefix, StringComparison.Ordinal)).ToList();
                if (affected.Count > 0)
                {
                    int choice = await Msg.ThreeWay(this, Loc.T("cascade.text"), Loc.T("cascade.title"),
                        Loc.T("cascade.all"), Loc.T("cascade.one"), Loc.T("button.cancel"));
                    if (choice == 2) { row.New = oldVal; return; } // Скасувати
                    if (choice == 0)                                 // Так, до всіх
                        foreach (var r in affected)
                        {
                            r.New = newPrefix + r.New.Substring(oldPrefix.Length);
                            MarkManual(r);
                        }
                }
            }
        }

        MarkManual(row);
        RecheckConflicts();
    }

    private void MarkManual(PlanRow row)
    {
        row.PrevNew = row.New;
        row.Source = Loc.T("source.manual");
        int full = row.DestRootLen + 1 + row.New.Length;
        row.Len = full.ToString();
        row.Over = full > (int)(Budget.Value ?? 259);
    }

    /// <summary>Перераховує конфлікти по всіх джерелах і блокує застосування за критичних.</summary>
    private void RecheckConflicts()
    {
        _hasCriticalConflicts = false;
        var byGroup = _plan.Where(r => !r.IsHeader).GroupBy(r => r.GroupIndex);
        foreach (var grp in byGroup)
        {
            var rows = grp.ToList();
            string? destRoot = grp.Key >= 0 && grp.Key < _groups.Count ? _groups[grp.Key].DestRoot : null;
            int budget = grp.Key >= 0 && grp.Key < _groups.Count ? _groups[grp.Key].Budget : (int)(Budget.Value ?? 259);
            var conflicts = ConflictDetector.Analyze(
                rows.Select(r => (r.Orig, r.New)).ToList(), budget, destRoot);
            for (int i = 0; i < rows.Count; i++)
            {
                var kind = conflicts[i].Kind;
                rows[i].Conflict = ConflictLabel(kind);
                rows[i].Critical = (kind & ConflictDetector.Critical) != 0;
                if (rows[i].Critical) _hasCriticalConflicts = true;
            }
        }
        // перемалювати підсвітку рядків
        var src = PreviewGrid.ItemsSource;
        PreviewGrid.ItemsSource = null;
        PreviewGrid.ItemsSource = src;
    }

    private static string ConflictLabel(ConflictKind kind)
    {
        if (kind == ConflictKind.None) return "";
        var parts = new List<string>();
        if (kind.HasFlag(ConflictKind.DuplicateName)) parts.Add(Loc.T("conflict.duplicate"));
        if (kind.HasFlag(ConflictKind.InvalidChars)) parts.Add(Loc.T("conflict.invalid"));
        if (kind.HasFlag(ConflictKind.ReservedName)) parts.Add(Loc.T("conflict.reserved"));
        if (kind.HasFlag(ConflictKind.EmptyName)) parts.Add(Loc.T("conflict.empty"));
        if (kind.HasFlag(ConflictKind.ExistsOnDisk)) parts.Add(Loc.T("conflict.exists"));
        if (kind.HasFlag(ConflictKind.OverBudget)) parts.Add(Loc.T("conflict.over"));
        return string.Join(", ", parts);
    }

    private IEnumerable<string> CriticalKindLabels()
        => _plan.Where(r => !r.IsHeader && r.Critical).Select(r => r.Conflict).Where(c => c.Length > 0).Distinct();

    // ---- таб «Словник» ----

    public sealed class DictRow
    {
        public required string Key { get; init; }
        public string Original { get; init; } = "";
        public string Short { get; init; } = "";
        public string Source { get; init; } = "";
    }

    private void RefreshDict()
    {
        DictGrid.ItemsSource = _cache.Snapshot()
            .Select(x => new DictRow { Key = x.Key, Original = x.Entry.Original, Short = x.Entry.ShortForm, Source = x.Entry.Source })
            .OrderBy(r => r.Original, StringComparer.CurrentCulture)
            .ToList();
    }

    private async void OnDictAdd(object? sender, RoutedEventArgs e)
    {
        var (ok, orig, shortForm) = await DictEntryDialog.Show(this, "", "");
        if (ok && orig.Length > 0) { _cache.Upsert(orig, shortForm, "manual"); RefreshDict(); }
    }

    private async void OnDictEdit(object? sender, RoutedEventArgs e)
    {
        if (DictGrid.SelectedItem is not DictRow r) { await Msg.Info(this, Loc.T("msg.selectRow")); return; }
        var (ok, orig, shortForm) = await DictEntryDialog.Show(this, r.Original, r.Short);
        if (ok && orig.Length > 0) { _cache.Upsert(orig, shortForm, "manual"); RefreshDict(); }
    }

    private async void OnDictDelete(object? sender, RoutedEventArgs e)
    {
        if (DictGrid.SelectedItem is not DictRow r) { await Msg.Info(this, Loc.T("msg.selectRow")); return; }
        _cache.Remove(r.Key); RefreshDict();
    }

    private async void OnDictApprove(object? sender, RoutedEventArgs e)
    {
        if (DictGrid.SelectedItem is not DictRow r) { await Msg.Info(this, Loc.T("msg.selectRow")); return; }
        _cache.Approve(r.Key); RefreshDict();
    }

    // ---- таб «Статистика» ----

    private int _statSources, _statPaths, _statCalls, _statTokens, _statAccepted, _statRejected;
    private string _statModel = "";

    private void ResetStats()
    {
        _statSources = _statPaths = _statCalls = _statTokens = _statAccepted = _statRejected = 0;
        _statModel = "";
    }

    private void UpdateStats()
    {
        StatSourcesValue.Text = _statSources.ToString();
        StatPathsValue.Text = _statPaths.ToString();
        StatModelValue.Text = _statModel.Length > 0 ? _statModel : Loc.T("stats.na");
        StatCallsValue.Text = _statCalls.ToString();
        StatTokensValue.Text = _statTokens > 0 ? _statTokens.ToString("N0") : "0";
        StatAcceptedValue.Text = $"{_statAccepted} / {_statRejected}";
    }

    /// <summary>
    /// Шлях до 7z.exe: змінна середовища PATHSHORTENER_7Z → файл 7z.txt поряд з .exe →
    /// стандартні місця встановлення → "7z" із PATH.
    /// </summary>
    internal static string Resolve7Zip()
    {
        string? env = Environment.GetEnvironmentVariable("PATHSHORTENER_7Z");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        string cfg = Path.Combine(AppContext.BaseDirectory, "7z.txt");
        if (File.Exists(cfg))
        {
            string p = File.ReadAllText(cfg).Trim();
            if (p.Length > 0 && File.Exists(p)) return p;
        }

        foreach (var candidate in new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        })
            if (File.Exists(candidate)) return candidate;

        return "7z"; // сподіваємось на PATH
    }
}

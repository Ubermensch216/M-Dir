using System.Text.RegularExpressions;
using MDir.Localization;
using MDir.Settings;
using MDir.Shell;
using static MDir.Tests.UiTest;

namespace MDir.Tests;

/// <summary>
/// The language is process-wide state, so every test here runs through the UI gate (one at a time, together with the
/// window tests that look at Korean texts) and puts Korean back before the gate opens again.
/// </summary>
public class LocalizationTests
{
    private static void InKorean(Action body) =>
        RunSta(() =>
        {
            try
            {
                Loc.Set(Loc.Korean);
                body();
            }
            finally
            {
                Loc.Set(Loc.Korean);
            }
        });

    [Fact]
    public void Korean_ReturnsTheTextItself_English_ReturnsTheTranslation_AndUnknownTextsStay()
    {
        InKorean(() =>
        {
            Assert.Equal("파일(&F)", Loc.T("파일(&F)"));

            Assert.Equal(Loc.English, Loc.Set(Loc.English));
            Assert.Equal("&File", Loc.T("파일(&F)"));
            Assert.Equal("a text nobody translated 가나다", Loc.T("a text nobody translated 가나다"));
            Assert.Equal("1,234 items", Loc.F("항목 {0:N0}개", 1234).Replace(" ", " "));
        });
    }

    [Fact]
    public void Set_RaisesChangedOnlyWhenTheLanguageActuallyChanges()
    {
        InKorean(() =>
        {
            int changes = 0;
            EventHandler handler = (_, _) => changes++;
            Loc.Changed += handler;
            try
            {
                Loc.Set(Loc.Korean);
                Assert.Equal(0, changes);
                Loc.Set(Loc.English);
                Loc.Set(Loc.English);
                Assert.Equal(1, changes);
                Loc.Set(Loc.Korean);
                Assert.Equal(2, changes);
            }
            finally
            {
                Loc.Changed -= handler;
            }
        });
    }

    [Fact]
    public void EveryEnglishText_KeepsItsPlaceholdersAndAccessKeys()
    {
        var placeholder = new Regex(@"\{(\d+)(?::[^}]*)?\}");
        foreach ((string korean, string english) in EnglishStrings.Table)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), $"empty translation of '{korean}'");
            Assert.Equal(
                placeholder.Matches(korean).Select(match => match.Groups[1].Value).Order(),
                placeholder.Matches(english).Select(match => match.Groups[1].Value).Order());

            // A menu text with an access key in the Korean "(&X)" form needs exactly one in the English text ("&&" is a literal ampersand).
            if (Regex.IsMatch(korean, @"\(&.\)"))
                Assert.True(Regex.Matches(english, @"(?<!&)&(?!&)").Count == 1, $"access key of '{english}'");
        }
    }

    [Fact]
    public void LanguageFile_IsReadWrittenAndMergedOverEnglish()
    {
        using var temp = new TempDirectory();
        string lang = temp.File("Lang");
        Directory.CreateDirectory(lang);
        File.WriteAllText(Path.Combine(lang, "xx.lng"),
            "# test pack\r\n파일(&F)\tFichier\r\n여러 줄\t첫째 줄\\n둘째 줄\r\n\r\nno tab here\r\n새 탭\tNouvel onglet\r\n");

        InKorean(() =>
        {
            Assert.Contains("xx", Loc.Available(lang));

            Assert.Equal("xx", Loc.Set("xx", lang));
            Assert.Equal("Fichier", Loc.T("파일(&F)"));
            Assert.Equal("첫째 줄\n둘째 줄", Loc.T("여러 줄"));
            Assert.Equal("&Edit", Loc.T("편집(&E)"));

            Dictionary<string, string> read = Loc.ReadPack(Path.Combine(lang, "xx.lng"))!;
            string written = Loc.WritePack(read);
            File.WriteAllText(Path.Combine(lang, "yy.lng"), written);
            Assert.Equal(read, Loc.ReadPack(Path.Combine(lang, "yy.lng")));
        });
    }

    [Fact]
    public void UnknownOrBrokenLanguage_KeepsKorean()
    {
        using var temp = new TempDirectory();
        InKorean(() =>
        {
            Assert.Equal(Loc.Korean, Loc.Set("zz", temp.Path));
            Assert.Equal("파일(&F)", Loc.T("파일(&F)"));
            Assert.Equal(Loc.Korean, Loc.Set("../escape", temp.Path));
            Assert.Null(Loc.ReadPack(temp.File("missing.lng")));
        });
    }

    [Fact]
    public void Apply_TranslatesAControlTreeAndGoesBackAndForth()
    {
        InKorean(() =>
        {
            var form = new Form { Text = "단축키 편집" };
            var ok = new Button { Text = "확인" };
            var box = new TextBox { Text = "닫기" };
            var list = new ListView { View = View.Details };
            list.Columns.Add("이름");
            var strip = new ToolStrip();
            var item = new ToolStripButton("추가") { ToolTipText = "닫기" };
            strip.Items.Add(item);
            form.Controls.AddRange([ok, box, list, strip]);

            Loc.Set(Loc.English);
            Loc.Apply(form);
            Assert.Equal("Edit Keyboard Shortcuts", form.Text);
            Assert.Equal("OK", ok.Text);
            Assert.Equal("닫기", box.Text);
            Assert.Equal("Name", list.Columns[0].Text);
            Assert.Equal("Add", item.Text);
            Assert.Equal("Close", item.ToolTipText);

            Loc.Set(Loc.Korean);
            Loc.Apply(form);
            Assert.Equal("단축키 편집", form.Text);
            Assert.Equal("확인", ok.Text);
            Assert.Equal("이름", list.Columns[0].Text);
            Assert.Equal("추가", item.Text);

            ok.Text = "취소";
            Loc.Set(Loc.English);
            Loc.Apply(form);
            Assert.Equal("Cancel", ok.Text);
            form.Dispose();
        });
    }
}

/// <summary>The language switch of the real window: menus, tool bar, status line, command texts and the setting.</summary>
public class LocalizationWindowTests
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Fact]
    public void SwitchingToEnglish_TranslatesMenusToolbarCommandsAndStatus_AndBackToKorean()
    {
        using var temp = new TempDirectory();
        var profile = new AppProfile(AppPaths.Resolve(appDirectory: temp.Path, roamingAppData: temp.Path), new AppSettings());

        Run(() => new MainForm(ShellLocation.Parse(WindowsDir), null, null, profile) { Size = new Size(1300, 800) }, async form =>
        {
            try
            {
                Loc.Set(Loc.Korean);
                await WaitUntil(() => form.ActivePane.Browser.ItemCount > 0, "folder listed");
                string[] korean = [.. form.MainMenuStrip!.Items.Cast<ToolStripItem>().Select(item => item.Text ?? string.Empty)];
                Assert.Equal("파일(&F)", korean[0]);

                form.SetLanguage("en");

                string[] english = [.. form.MainMenuStrip!.Items.Cast<ToolStripItem>().Select(item => item.Text ?? string.Empty)];
                Assert.Equal(["&File", "&Edit", "&View", "&Go", "F&avorites", "&Tabs", "&Layout", "T&ools", "&Window", "&Help"], english);
                Assert.Equal("&New Tab", form.Commands.Get("tab.new").Text);
                Assert.Equal("Tabs", form.Commands.Get("tab.new").CategoryText);
                Assert.Equal("New Tab", form.Commands.Get("tab.new").DisplayText);
                Assert.Contains(form.Toolbar!.Items.OfType<ToolStripButton>(), button => button.ToolTipText?.StartsWith("New Tab") == true);
                Assert.StartsWith("Layout", form.Toolbar.Items.OfType<ToolStripDropDownButton>().Single().Text);

                var fileMenu = (ToolStripMenuItem)form.MainMenuStrip.Items[0];
                Assert.Contains(fileMenu.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text == "&New Workspace");
                Assert.Matches(@"^[\d,]+ items", form.ActivePane.StatusText);
                Assert.Equal("en", AppSettings.Load(profile.Paths.SettingsFile).Language);
                Assert.Contains(((ToolStripMenuItem)form.MainMenuStrip.Items[8]).DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text == "&Language");

                form.SetLanguage("ko");

                Assert.Equal("파일(&F)", form.MainMenuStrip!.Items[0].Text);
                Assert.Equal("새 탭(&N)", form.Commands.Get("tab.new").Text);
                Assert.Matches(@"^항목 [\d,]+개", form.ActivePane.StatusText);
                Assert.Equal("ko", AppSettings.Load(profile.Paths.SettingsFile).Language);
            }
            finally
            {
                Loc.Set(Loc.Korean);
            }
        });
    }

    [Fact]
    public void LanguageCommands_AreCheckedForTheCurrentLanguage()
    {
        Run(() => new MainForm(ShellLocation.Parse(WindowsDir), null, null, null) { Size = new Size(1200, 800) }, form =>
        {
            try
            {
                Loc.Set(Loc.Korean);
                Assert.True(form.Commands.Get("window.language.ko").IsChecked!());
                Assert.False(form.Commands.Get("window.language.en").IsChecked!());

                Assert.True(form.RunCommand("window.language.en"));

                Assert.False(form.Commands.Get("window.language.ko").IsChecked!());
                Assert.True(form.Commands.Get("window.language.en").IsChecked!());
            }
            finally
            {
                Loc.Set(Loc.Korean);
            }

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Dialogs_AreTranslatedWhenShown()
    {
        Run(() => new MainForm(ShellLocation.Parse(WindowsDir), null, null, null) { Size = new Size(1200, 800), SilentFileOperations = true }, async form =>
        {
            try
            {
                await WaitUntil(() => form.ActivePane.Browser.ItemCount > 0, "folder listed");
                form.SetLanguage("en");
                string? title = null;
                string? closeText = null;
                form.RunDialog = dialog =>
                {
                    Loc.Apply(dialog);
                    title = dialog.Text;
                    closeText = FindButton(dialog, "Close")?.Text;
                    return DialogResult.OK;
                };

                Assert.True(form.RunCommand("tools.search"));

                Assert.Equal("Search", title);
                Assert.Equal("Close", closeText);
            }
            finally
            {
                Loc.Set(Loc.Korean);
            }
        });
    }

    private static Button? FindButton(Control root, string text) =>
        root.Controls.OfType<Button>().FirstOrDefault(button => button.Text == text)
        ?? root.Controls.Cast<Control>().Select(child => FindButton(child, text)).FirstOrDefault(found => found is not null);
}

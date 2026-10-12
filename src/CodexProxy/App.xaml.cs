using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Security.Principal;
using CodexProxy.Core;

namespace CodexProxy;

public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    private EventWaitHandle? show;
    public static string T(string chinese, string english) => UiText.Language == "en-US" ? english : chinese;

    public static void ChangeLanguage(string language)
    {
        var dictionary = new ResourceDictionary { Source = new Uri($"Languages/{language}.xaml", UriKind.Relative) };
        Current.Resources.MergedDictionaries[0] = dictionary;
        UiText.Language = language;
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var index = Array.IndexOf(e.Args, "--state-dir");
        var directory = index >= 0 && index + 1 < e.Args.Length ? Path.GetFullPath(e.Args[index + 1]) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codex-proxy");
        // One instance for this Windows user and session, regardless of install or state directory.
        var key = WindowsIdentity.GetCurrent().User!.Value;
        mutex = new Mutex(false, "Local\\CodexProxy-Desktop-" + key, out var created);
        show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexProxy-Desktop-Show-" + key);
        if (!created) { show.Set(); Shutdown(); return; }
        // Wake a running 0.4-or-earlier instance during the first upgrade.
        var legacyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..16];
        if (EventWaitHandle.TryOpenExisting("Local\\CodexProxy-Show-" + legacyKey, out var legacyShow))
        { using (legacyShow) legacyShow.Set(); Shutdown(); return; }
        string language;
        try { language = LanguageSettings.Load(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { language = "zh-CN"; }
        ChangeLanguage(language);
        var window = new MainWindow(directory);
        MainWindow = window;
        window.Show();
        _ = Task.Run(() => { while (show.WaitOne()) Dispatcher.Invoke(window.Restore); });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        mutex?.Dispose();
        base.OnExit(e);
    }
}

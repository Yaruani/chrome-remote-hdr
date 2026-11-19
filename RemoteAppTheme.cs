using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

sealed class RemoteAppTheme
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string RegistryValue = "AppsUseLightTheme";
    private readonly string enabledPath;
    private readonly string restorePath;
    private readonly Func<int?> readMode;
    private readonly Action<int?> writeMode;

    public RemoteAppTheme() : this(Diagnostics.Folder, ReadWindowsMode, SetWindowsMode) { }

    internal RemoteAppTheme(string folder, Func<int?> readMode, Action<int?> writeMode)
    {
        enabledPath = Path.Combine(folder, "remote-light-theme.enabled");
        restorePath = Path.Combine(folder, "app-theme-restore.json");
        this.readMode = readMode;
        this.writeMode = writeMode;
    }

    public bool Enabled => File.Exists(enabledPath);

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(enabledPath)!);
            File.WriteAllText(enabledPath, string.Empty);
        }
        else
        {
            File.Delete(enabledPath);
            Restore();
        }
    }

    public int? CurrentMode => readMode();

    public void SetManualMode(int mode)
    {
        if (readMode() == mode)
            return;

        writeMode(mode);
        Diagnostics.Log($"Windowsのアプリモードを手動で{(mode == 1 ? "ライト" : "ダーク")}に変更");
    }

    public void Apply()
    {
        if (!Enabled || File.Exists(restorePath))
            return;

        var previous = readMode();
        // Save the original mode before changing the shared Windows setting.
        var stagingPath = restorePath + ".tmp";
        using (var file = File.Create(stagingPath))
        {
            JsonSerializer.Serialize(file, previous);
            file.Flush(flushToDisk: true);
        }

        File.Move(stagingPath, restorePath);
        if (previous != 1)
        {
            writeMode(1);
            Diagnostics.Log("Windowsのアプリモードをライトに変更");
        }
    }

    public void Restore()
    {
        if (!File.Exists(restorePath))
            return;

        var previous = JsonSerializer.Deserialize<int?>(File.ReadAllText(restorePath));
        if (previous is not (null or 0 or 1))
            throw new InvalidDataException("元のアプリモードの記録が無効です。");

        if (readMode() != previous)
        {
            writeMode(previous);
            Diagnostics.Log("Windowsのアプリモードを復元");
        }

        File.Delete(restorePath);
    }

    private static int? ReadWindowsMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(RegistryValue) switch
        {
            null => null,
            int mode when mode is 0 or 1 && key.GetValueKind(RegistryValue) == RegistryValueKind.DWord => mode,
            _ => throw new InvalidDataException("Windowsのアプリモードを読み取れません。")
        };
    }

    private static void SetWindowsMode(int? mode)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (mode.HasValue)
            key.SetValue(RegistryValue, mode.Value, RegistryValueKind.DWord);
        else
            key.DeleteValue(RegistryValue, throwOnMissingValue: false);
        NotifyWindows();
    }

    private static void NotifyWindows() =>
        SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, "ImmersiveColorSet",
            0x0002, 100, out _);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam,
        string lParam, uint flags, uint timeoutMs, out UIntPtr result);
}

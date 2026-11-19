using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

internal static class Program {
 [STAThread] static void Main(string[] args) {
  if (args.Length>0 && args[0]=="--watch") { Environment.ExitCode=Diagnostics.Watch(args); return; }
  if (args.Contains("--check")) { try { foreach(var x in Display.Get()) Console.WriteLine($"{x.Low}:{x.High}:{x.Id} HDR={x.Enabled}"); } catch(Exception e) { Console.Error.WriteLine(e.Message); Environment.ExitCode=1; } return; }
  if (args.Contains("--check-event")) { try { var q=new EventLogQuery("Application",PathType.LogName,"*[System[Provider[@Name='chromoting'] and (EventID=1 or EventID=2)]]"); using var w=new EventLogWatcher(q); w.Enabled=true; Console.WriteLine("イベントログ監視を開始できました。"); } catch(Exception e) { Console.Error.WriteLine(e.Message); Environment.ExitCode=1; } return; }
  using var mutex = new Mutex(true, "Local\\ChromeRemoteHDR_User", out bool created); if (!created) return;
  ApplicationConfiguration.Initialize(); var warning=Diagnostics.Begin(args.Contains("--recovered")); Application.Run(new Tray(warning));
 }
}
record MonitorState(uint Low, int High, uint Id, bool Enabled);
static class Display {
 [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
 [StructLayout(LayoutKind.Sequential)] struct Source { public Luid Adapter; public uint Id, Mode, Status; }
 [StructLayout(LayoutKind.Sequential)] struct Target { public Luid Adapter; public uint Id, Mode, Technology, Rotation, Scaling, Numerator, Denominator, Scan, Available, Status; }
 [StructLayout(LayoutKind.Sequential)] struct PathInfo { public Source Source; public Target Target; public uint Flags; }
 [StructLayout(LayoutKind.Sequential, Size=64)] struct ModeInfo { public uint Type, Id; public Luid Adapter; }
 [StructLayout(LayoutKind.Sequential)] struct Header { public int Type; public uint Size; public Luid Adapter; public uint Id; }
 [StructLayout(LayoutKind.Sequential)] struct Color { public Header Header; public uint Value, Encoding, Bits, Active; }
 [StructLayout(LayoutKind.Sequential)] struct SetState { public Header Header; public uint Value; }
 [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
 [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] pathInfo, ref uint modes, [Out] ModeInfo[] modeInfo, IntPtr topology);
 [DllImport("user32.dll", EntryPoint="DisplayConfigGetDeviceInfo")] static extern int GetInfo(ref Color info);
 [DllImport("user32.dll", EntryPoint="DisplayConfigSetDeviceInfo")] static extern int SetInfo(ref SetState state);
 public static List<MonitorState> Get() {
  int rc=GetDisplayConfigBufferSizes(2,out var p,out var m); if(rc!=0) throw new Exception($"ディスプレイの取得に失敗しました (エラー {rc})。");
  var paths=new PathInfo[p]; var modes=new ModeInfo[m]; rc=QueryDisplayConfig(2,ref p,paths,ref m,modes,IntPtr.Zero); if(rc!=0) throw new Exception($"ディスプレイの取得に失敗しました (エラー {rc})。");
  var list=new List<MonitorState>(); foreach(var path in paths.Take((int)p)) { var t=path.Target; var c=new Color { Header=new Header {Type=15,Size=(uint)Marshal.SizeOf<Color>(),Adapter=t.Adapter,Id=t.Id} }; rc=GetInfo(ref c); if(rc!=0) throw new Exception($"HDR状態の取得に失敗しました (エラー {rc})。Windows 11 24H2以降が必要です。"); if((c.Value & 16)!=0) list.Add(new(t.Adapter.Low,t.Adapter.High,t.Id,(c.Value & 32)!=0)); } return list.DistinctBy(x=>(x.Low,x.High,x.Id)).ToList();
 }
 public static void Set(MonitorState s, bool enabled) { var state=new SetState {Header=new Header {Type=16,Size=(uint)Marshal.SizeOf<SetState>(),Adapter=new Luid {Low=s.Low,High=s.High},Id=s.Id},Value=enabled?1u:0u}; var rc=SetInfo(ref state); if(rc!=0) throw new Exception($"HDRの切り替えに失敗しました (ディスプレイ {s.Id}, エラー {rc})。"); }
}
sealed class Tray : ApplicationContext {
 readonly NotifyIcon icon; readonly ToolStripMenuItem status, startup, logging, themeToggle;
 readonly RemoteAppTheme remoteTheme = new();
 readonly HdrTrayIcons hdrIcons = new();
 readonly System.Windows.Forms.Timer displayTimer = new() { Interval = 3000 };
 readonly ToolStripMenuItem hdrMenu = new();
 readonly ToolStripMenuItem hdrOn = new("オン");
 readonly ToolStripMenuItem hdrOff = new("オフ");
 readonly ToolStripMenuItem themeMenu = new();
 readonly ToolStripMenuItem themeLight = new("ライト");
 readonly ToolStripMenuItem themeDark = new("ダーク");
 bool themeConnected;
 bool quitting;
 EventLogWatcher? watcher; readonly object gate=new(); bool connected; readonly string statePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ChromeRemoteHDR","restore.json");
 const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 public Tray(string? warning) {
  status=new("状態: 監視開始中") {Enabled=false}; startup=new("Windowsログイン時に起動") {CheckOnClick=false}; startup.Click+=(_,_)=>ToggleStartup();
  logging=new("動作ログを保存") {Checked=Diagnostics.Enabled}; logging.Click+=(_,_)=>ToggleLogging();
  themeToggle = new("接続中はWindowsのアプリをライトにする") { Checked = remoteTheme.Enabled };
  themeToggle.Click += (_, _) => ToggleRemoteTheme();
  hdrOn.Click += (_, _) => SetManualHdr(true);
  hdrOff.Click += (_, _) => SetManualHdr(false);
  hdrMenu.DropDownItems.AddRange(new ToolStripItem[] { hdrOn, hdrOff });
  themeLight.Click += (_, _) => SetManualTheme(1);
  themeDark.Click += (_, _) => SetManualTheme(0);
  themeMenu.DropDownItems.AddRange(new ToolStripItem[] { themeLight, themeDark });

  var menu = new ContextMenuStrip();
  menu.Items.AddRange(new ToolStripItem[] { status, hdrMenu, themeMenu, themeToggle, new ToolStripSeparator(), startup, logging });
  menu.Items.Add("ログ保存先を開く", null, (_, _) =>
  {
   try { Diagnostics.OpenFolder(); } catch (Exception e) { Notify(e.Message); }
  });
  menu.Items.Add("保存されたHDR設定を復元", null, (_, _) =>
  {
   lock (gate) Restore();
   RefreshIndicators();
  });
  menu.Items.Add("終了", null, (_, _) => Quit());
  menu.Opening += (_, _) => RefreshIndicators();

  icon = new NotifyIcon { ContextMenuStrip = menu, Icon = hdrIcons[HdrState.Unavailable], Text = "Chrome Remote HDR" };
  RefreshIndicators();
  icon.Visible = true;
  displayTimer.Tick += (_, _) => RefreshIndicators();
  displayTimer.Start();
  UpdateStartup();
  TryThemeChange(remoteTheme.Restore);
  try { var query=new EventLogQuery("Application",PathType.LogName,"*[System[Provider[@Name='chromoting'] and (EventID=1 or EventID=2)]]"); watcher=new EventLogWatcher(query); watcher.EventRecordWritten+=OnEvent; watcher.Enabled=true; SetStatus(File.Exists(statePath)?"監視中・復元待ちの設定あり":"監視中"); }
  catch(Exception e) { Diagnostics.Log("イベント監視エラー: "+e); SetStatus("監視エラー"); Notify("イベントログの監視を開始できません: "+e.Message); }
  if(warning!=null) Notify(warning); else if(!Diagnostics.StorageAvailable) Notify("ログを保存できません。保存先を確認してください。");
 }
 void OnEvent(object? sender, EventRecordWrittenEventArgs e)
 {
  try
  {
   if (e.EventException != null)
    throw e.EventException;
   using var record = e.EventRecord;
   if (record == null)
    return;

   lock (gate)
   {
    if (quitting)
     return;

    Diagnostics.Log("接続イベント ID=" + record.Id);
    if (record.Id == 1)
    {
     themeConnected = true;
     try { Connect(); }
     finally { TryThemeChange(remoteTheme.Apply); }
    }
    else if (record.Id == 2)
    {
     themeConnected = false;
     try { Restore(); }
     finally { TryThemeChange(remoteTheme.Restore); }
    }
   }
  }
  catch (Exception ex)
  {
   Diagnostics.Log("接続処理エラー: " + ex);
   SetStatus("エラー");
   Notify(ex.Message);
  }
 }
 void Connect()
 {
  Diagnostics.Log("HDRオフ処理開始");
  if (connected || File.Exists(statePath))
  {
   connected = true;
   SetStatus("接続中・復元待ち");
   return;
  }

  var monitors = Display.Get();
  SaveHdrState(monitors);
  connected = true;
  var changed = monitors.Where(monitor => monitor.Enabled).ToList();
  foreach (var monitor in changed)
   Display.Set(monitor, false);
  SetStatus($"接続中・HDRオフ {changed.Count}台");
  Diagnostics.Log($"HDRオフ完了: {changed.Count}台");
 }

 void Restore()
 {
  Diagnostics.Log("HDR復元処理開始");
  connected = false;
  if (!File.Exists(statePath))
  {
   SetStatus("監視中");
   return;
  }

  try
  {
   var saved = JsonSerializer.Deserialize<List<MonitorState>>(File.ReadAllText(statePath))
       ?? throw new InvalidDataException("保存した設定を読み取れません。");
   var active = Display.Get().ToDictionary(monitor => (monitor.Low, monitor.High, monitor.Id));
   var remaining = new List<MonitorState>();
   foreach (var original in saved)
   {
    if (!active.TryGetValue((original.Low, original.High, original.Id), out var current))
    {
     remaining.Add(original);
     continue;
    }

    try
    {
     if (current.Enabled != original.Enabled)
      Display.Set(current, original.Enabled);
    }
    catch (Exception e)
    {
     remaining.Add(original);
     Diagnostics.Log("HDR復元エラー: " + e.Message);
    }
   }

   if (remaining.Count == 0)
   {
    File.Delete(statePath);
    SetStatus("監視中");
    Diagnostics.Log("HDR復元完了");
   }
   else
   {
    SaveHdrState(remaining);
    SetStatus("復元未完了");
    Diagnostics.Log($"HDR復元未完了: {remaining.Count}台");
    Notify($"{remaining.Count}台のHDRを復元できません。ディスプレイ接続を確認し、メニューから再試行してください。");
   }
  }
  catch (Exception e)
  {
   Diagnostics.Log("HDR復元エラー: " + e);
   SetStatus("復元エラー");
   Notify(e.Message);
  }
 }

 void SaveHdrState(List<MonitorState> monitors)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
  var temp = statePath + ".tmp";
  using (var file = File.Create(temp))
  {
   JsonSerializer.Serialize(file, monitors);
   file.Flush(flushToDisk: true);
  }
  File.Move(temp, statePath, overwrite: true);
 }

 void RefreshIndicators()
 {
  HdrState state;
  string hdrLabel;
  try
  {
   var monitors = Display.Get();
   var enabled = monitors.Count(monitor => monitor.Enabled);
   (state, hdrLabel) = (monitors.Count, enabled) switch
   {
    (0, _) => (HdrState.Unavailable, "対応画面なし"),
    (_, 0) => (HdrState.Off, "オフ"),
    (var total, var on) when total == on => (HdrState.On, "オン"),
    _ => (HdrState.Mixed, $"混在（{enabled}/{monitors.Count}台オン）")
   };
  }
  catch
  {
   state = HdrState.Unavailable;
   hdrLabel = "取得不可";
  }

  var stateIcon = hdrIcons[state];
  if (!ReferenceEquals(icon.Icon, stateIcon))
   icon.Icon = stateIcon;

  var hdrText = $"HDR: {hdrLabel}";
  var tooltip = $"Chrome Remote HDR · {hdrText}";
  if (icon.Text != tooltip)
   icon.Text = tooltip;
  hdrMenu.Text = hdrText;
  hdrMenu.Enabled = state != HdrState.Unavailable;
  hdrOn.Checked = state == HdrState.On;
  hdrOff.Checked = state == HdrState.Off;

  int? mode;
  try { mode = remoteTheme.CurrentMode; }
  catch { mode = null; }
  themeMenu.Text = "Windowsアプリのテーマ: " + (mode == 1 ? "ライト" : mode == 0 ? "ダーク" : "不明");
  themeLight.Checked = mode == 1;
  themeDark.Checked = mode == 0;
 }

 void SetManualHdr(bool enabled)
 {
  var target = enabled ? "オン" : "オフ";
  try
  {
   lock (gate)
   {
    var monitors = Display.Get();
    if (monitors.Count == 0)
     throw new InvalidOperationException("HDRに対応するディスプレイがありません。");

    var failed = 0;
    foreach (var monitor in monitors.Where(monitor => monitor.Enabled != enabled))
    {
     try { Display.Set(monitor, enabled); }
     catch (Exception e)
     {
      failed++;
      Diagnostics.Log("HDRの手動切り替えエラー: " + e.Message);
     }
    }

    if (failed > 0)
     throw new InvalidOperationException($"{failed}台のHDRを{target}に切り替えられませんでした。");
    Diagnostics.Log($"HDRを手動で{target}に設定");
   }
  }
  catch (Exception e)
  {
   Diagnostics.Log("HDRの手動切り替え: " + e.Message);
   Notify(e.Message);
  }
  finally { RefreshIndicators(); }
 }

 void SetManualTheme(int mode)
 {
  lock (gate) TryThemeChange(() => remoteTheme.SetManualMode(mode));
  RefreshIndicators();
 }

 void ToggleRemoteTheme()
 {
  lock (gate)
  {
   TryThemeChange(() =>
   {
    remoteTheme.SetEnabled(!remoteTheme.Enabled);
    if (themeConnected)
     remoteTheme.Apply();
   });
   themeToggle.Checked = remoteTheme.Enabled;
  }
 }
 void TryThemeChange(Action change)
 {
  try { change(); }
  catch (Exception e)
  {
   Diagnostics.Log("アプリモード切り替えエラー: " + e);
   Notify("Windowsのアプリモードを切り替えられません: " + e.Message);
  }
 }
 void ToggleLogging() { try { if(Diagnostics.Enabled) Diagnostics.Log("ログをオフにしました。"); Diagnostics.SetEnabled(!Diagnostics.Enabled); logging.Checked=Diagnostics.Enabled; } catch(Exception e) { Notify("ログ設定を保存できません: "+e.Message); } }
 void ToggleStartup() { try { using var key=Registry.CurrentUser.CreateSubKey(RunKey); if(key.GetValue("ChromeRemoteHDR")!=null) key.DeleteValue("ChromeRemoteHDR"); else key.SetValue("ChromeRemoteHDR", "\""+Application.ExecutablePath+"\""); UpdateStartup(); } catch(Exception e) { Notify(e.Message); } }
 void UpdateStartup() { using var key=Registry.CurrentUser.OpenSubKey(RunKey); startup.Checked=key?.GetValue("ChromeRemoteHDR")!=null; }
 void SetStatus(string s) { if(status.Owner?.InvokeRequired==true) { status.Owner.BeginInvoke(()=>SetStatus(s)); return; } status.Text="状態: "+s; }
 void Notify(string text) { icon.ShowBalloonTip(10000,"Chrome Remote HDR",text,ToolTipIcon.Warning); }
 void Quit() { if(File.Exists(statePath)) { MessageBox.Show("復元待ちのHDR設定があります。終了後は自動復元されません。必要に応じて再起動して復元してください。","Chrome Remote HDR",MessageBoxButtons.OK,MessageBoxIcon.Warning); } lock (gate) { quitting = true; TryThemeChange(remoteTheme.Restore); } Diagnostics.CleanExit(); watcher?.Dispose(); displayTimer.Dispose(); icon.Visible=false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); hdrIcons.Dispose(); ExitThread(); }
}

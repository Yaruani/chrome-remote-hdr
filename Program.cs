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
 bool themeConnected;
 bool quitting;
 EventLogWatcher? watcher; readonly object gate=new(); bool connected; readonly string statePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ChromeRemoteHDR","restore.json");
 const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 public Tray(string? warning) {
  status=new("状態: 監視開始中") {Enabled=false}; startup=new("Windowsログイン時に起動") {CheckOnClick=false}; startup.Click+=(_,_)=>ToggleStartup();
  logging=new("動作ログを保存") {Checked=Diagnostics.Enabled}; logging.Click+=(_,_)=>ToggleLogging();
  themeToggle = new("接続中はWindowsのアプリをライトにする") { Checked = remoteTheme.Enabled };
  themeToggle.Click += (_, _) => ToggleRemoteTheme();
  var menu=new ContextMenuStrip(); menu.Items.Add(status); menu.Items.Add(startup); menu.Items.Add(logging); menu.Items.Add(themeToggle); menu.Items.Add("ログ保存先を開く",null,(_,_)=>{ try {Diagnostics.OpenFolder();} catch(Exception e){Notify(e.Message);} }); menu.Items.Add("保存されたHDR設定を復元",null,(_,_)=>{ lock(gate) Restore(); }); menu.Items.Add("終了",null,(_,_)=>Quit());
  icon=new NotifyIcon {Text="Chrome Remote HDR",Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,ContextMenuStrip=menu,Visible=true}; UpdateStartup();
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
 void Connect() { Diagnostics.Log("HDRオフ処理開始"); if(connected || File.Exists(statePath)) { connected=true; SetStatus("接続中・復元待ち"); return; } var items=Display.Get().Where(x=>x.Enabled).ToList(); Directory.CreateDirectory(Path.GetDirectoryName(statePath)!); File.WriteAllText(statePath,JsonSerializer.Serialize(items)); connected=true; foreach(var item in items) Display.Set(item,false); SetStatus($"接続中・HDRオフ {items.Count}台"); Diagnostics.Log($"HDRオフ完了: {items.Count}台"); }
 void Restore() { Diagnostics.Log("HDR復元処理開始"); if(!File.Exists(statePath)) { connected=false; SetStatus("監視中"); return; } try { var saved=JsonSerializer.Deserialize<List<MonitorState>>(File.ReadAllText(statePath)) ?? throw new Exception("保存した設定を読み取れません。"); var active=Display.Get(); var missing=new List<MonitorState>(); foreach(var item in saved) { if(!active.Any(x=>x.Low==item.Low && x.High==item.High && x.Id==item.Id)) {missing.Add(item); continue;} try { Display.Set(item,true); } catch { missing.Add(item); } } connected=false; if(missing.Count>0) { File.WriteAllText(statePath,JsonSerializer.Serialize(missing)); SetStatus("復元未完了"); Diagnostics.Log($"HDR復元未完了: {missing.Count}台"); Notify($"{missing.Count}台のHDRを復元できません。ディスプレイ接続を確認し、メニューから再試行してください。"); } else {File.Delete(statePath); SetStatus("監視中"); Diagnostics.Log("HDR復元完了");} } catch(Exception e) { Diagnostics.Log("HDR復元エラー: "+e); SetStatus("復元エラー"); Notify(e.Message); } }
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
 void Quit() { if(File.Exists(statePath)) { MessageBox.Show("復元待ちのHDR設定があります。終了後は自動復元されません。必要に応じて再起動して復元してください。","Chrome Remote HDR",MessageBoxButtons.OK,MessageBoxIcon.Warning); } lock (gate) { quitting = true; TryThemeChange(remoteTheme.Restore); } Diagnostics.CleanExit(); watcher?.Dispose(); icon.Visible=false; icon.Dispose(); ExitThread(); }
}

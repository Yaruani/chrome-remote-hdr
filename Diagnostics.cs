using System.Diagnostics;
using System.Text;
using System.Text.Json;

static class Diagnostics {
 static readonly object gate=new();
 public static readonly string Folder=Environment.GetEnvironmentVariable("CHROME_REMOTE_HDR_TEST_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ChromeRemoteHDR");
 static string Settings=>Path.Combine(Folder,"logging.json");
 static string Marker=>Path.Combine(Folder,"running.json");
 static string Incidents=>Path.Combine(Folder,"unexpected-exits.log");
 static string LogFile=>Path.Combine(Folder,"activity.log");
 static string? token;
 public static bool Enabled {get;private set;}=true;
 public static bool StorageAvailable {get;private set;}=true;
 static void Prepare()=>Directory.CreateDirectory(Folder);
 public static string? Begin(bool recovered) {
  try {
   Prepare(); if(File.Exists(Settings)) Enabled=JsonSerializer.Deserialize<bool>(File.ReadAllText(Settings));
   var previous=File.Exists(Marker); token=Guid.NewGuid().ToString("N");
   File.WriteAllText(Marker,JsonSerializer.Serialize(new MarkerData(token,Environment.ProcessId,DateTimeOffset.Now)));
   AppDomain.CurrentDomain.UnhandledException+=(_,e)=>Log("未処理例外: "+e.ExceptionObject);
   Application.ThreadException+=(_,e)=>Log("UI例外: "+e.Exception);
   Microsoft.Win32.SystemEvents.SessionEnding+=(_,_)=>CleanExit();
   Log("アプリ起動"+(recovered?"（自動再起動）":""));
   var p=Process.Start(new ProcessStartInfo(Application.ExecutablePath,$"--watch {Environment.ProcessId} {token} {(recovered?0:1)}") {UseShellExecute=false,CreateNoWindow=true});
   if(p==null) throw new IOException("監視プロセスを開始できませんでした。");
   return previous?"前回の終了状態を確認できません。HDR復元待ちと異常終了記録を確認してください。":null;
  } catch(Exception e) {StorageAvailable=false; return "診断機能を開始できません: "+e.Message;}
 }
 public static void SetEnabled(bool value) { Prepare(); File.WriteAllText(Settings,JsonSerializer.Serialize(value)); Enabled=value; if(value) Log("ログをオンにしました。"); }
 public static void Log(string message) { if(!Enabled)return; try { lock(gate) {Prepare();if(File.Exists(LogFile) && new FileInfo(LogFile).Length>2_000_000) File.Move(LogFile,LogFile+".old",true);File.AppendAllText(LogFile,$"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message.Replace('\r',' ').Replace('\n',' ')}{Environment.NewLine}",Encoding.UTF8); } } catch {StorageAvailable=false;} }
 public static void CleanExit() { Log("正常終了"); try {if(token!=null && File.Exists(Marker) && JsonSerializer.Deserialize<MarkerData>(File.ReadAllText(Marker))?.Token==token) File.Delete(Marker);} catch {StorageAvailable=false;} }
 public static int Watch(string[] args) {
  if(args.Length!=4 || !int.TryParse(args[1],out int pid) || args[2].Length!=32 || (args[3]!="0" && args[3]!="1")) return 2;
  try {
   using var parent=Process.GetProcessById(pid); var handle=parent.SafeHandle; parent.WaitForExit(); string exit; try { exit=parent.ExitCode.ToString(); } catch(InvalidOperationException) { exit="取得不可"; }
   if(!File.Exists(Marker)) return 0; var state=JsonSerializer.Deserialize<MarkerData>(File.ReadAllText(Marker)); if(state?.Token!=args[2]) return 0;
   File.AppendAllText(Incidents,$"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} 終了状態不明 PID={pid} ExitCode={exit}。Windows終了・強制終了・アプリ異常の区別は未確定。{Environment.NewLine}",Encoding.UTF8);
   if(args[3]=="1") Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--recovered") {UseShellExecute=false,CreateNoWindow=true});
   return 0;
  } catch(Exception e) { try { Prepare(); File.AppendAllText(Incidents,$"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} 監視プロセスエラー: {e.GetType().Name}: {e.Message}{Environment.NewLine}",Encoding.UTF8); } catch { } return 1;}
 }
 public static void OpenFolder(){Prepare();Process.Start(new ProcessStartInfo("explorer.exe",Folder){UseShellExecute=true});}
 public record MarkerData(string Token,int Pid,DateTimeOffset Started);
}





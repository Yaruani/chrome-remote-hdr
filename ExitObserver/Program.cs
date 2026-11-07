using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

internal static class Program {
 static string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ChromeRemoteHDR-observer");
 static string targetMarker=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ChromeRemoteHDR","running.json");
 static string targetExe=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","app","ChromeRemoteHDR.exe"));
 static string LogPath=>Path.Combine(folder,"observer.log");
 static string Marker=>Path.Combine(folder,"observer-running.json");
 static volatile bool quitting;
 static string? token;
 [STAThread] static int Main(string[] args) {
  if(args.Length==6 && args[0]=="--test") {folder=Path.GetFullPath(args[1]);targetMarker=Path.GetFullPath(args[2]);targetExe=Path.GetFullPath(args[3]);if(!int.TryParse(args[4],out int seconds)||seconds<1||seconds>60 || args[5]!="isolated")return 2;return Run(seconds,true);}
  if(args.Length!=0) return 2;
  using var mutex=new Mutex(true,"Local\\ChromeRemoteHDR_ExitObserver",out var created);if(!created)return 0;
  return Run(5,false);
 }
 static int Run(int interval, bool test) {
  Process? target=null; int? targetId=null; string? lastStatus=null;
  try {
   Directory.CreateDirectory(folder);
   if(File.Exists(Marker)) Log("前回の監視アプリの終了状態は不明です。記録時刻とWindowsイベントログを確認してください。");
   token=Guid.NewGuid().ToString("N"); File.WriteAllText(Marker,JsonSerializer.Serialize(new {Token=token,Pid=Environment.ProcessId,Started=DateTimeOffset.Now}));
   AppDomain.CurrentDomain.UnhandledException+=(_,e)=>Log("監視アプリの未処理例外: "+e.ExceptionObject);
   if(!test) SystemEvents.SessionEnding+=(_,_)=>Stop();
   Log($"監視開始 PID={Environment.ProcessId} interval={interval}秒");
   var nextHeartbeat=DateTimeOffset.Now.AddMinutes(1);
   while(!quitting) {
    try {
     if(target!=null && target.WaitForExit(0)) {
      string code;try{code=target.ExitCode.ToString();}catch(Exception ex){code="取得不可("+ex.GetType().Name+")";}
      Log($"本体終了検出 PID={targetId} ExitCode={code} 検出時刻であり終了原因は未確定");
      target.Dispose();target=null;targetId=null;lastStatus=null;
     }
     int? markerId=ReadMarker(out string markerStatus);
     if(markerId!=null && targetId!=markerId){
      var candidate=Process.GetProcessById(markerId.Value);
      try {
       var actual=candidate.MainModule?.FileName;
       if(!string.Equals(actual,targetExe,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("PIDの実行ファイルが一致しません。");
       _=candidate.SafeHandle;
       if(target!=null){Log($"監視対象PID変更 旧={targetId} 新={markerId}");target.Dispose();}
       target=candidate;targetId=markerId;lastStatus=null;
       Log($"本体監視開始 PID={targetId} 起動時刻={candidate.StartTime:yyyy-MM-dd HH:mm:ss zzz}");
      } catch {candidate.Dispose();throw;}
     }
     string status=target!=null ? (markerId==targetId?"本体実行中":"本体プロセスを保持中・状態記録を照合できない") : markerStatus;
     if(lastStatus!=status){Log("状態: "+status);lastStatus=status;}
     if(DateTimeOffset.Now>=nextHeartbeat){Log($"定期記録: {status} PID={(targetId?.ToString()??"なし")}");nextHeartbeat=DateTimeOffset.Now.AddMinutes(1);}
    } catch(Exception e) {var message="監視処理エラー: "+e.GetType().Name+": "+e.Message;if(lastStatus!=message){Log(message);lastStatus=message;}}
    for(int i=0;i<interval*10&&!quitting;i++) Thread.Sleep(100);
   }
   target?.Dispose();CleanExit();return 0;
  } catch(Exception e) {try{Log("監視アプリ停止: "+e);}catch{} return 1;}
 }
 static int? ReadMarker(out string status) {
  if(!File.Exists(targetMarker)){status="本体の実行状態を確認できない（本体状態ファイルなし）";return null;}
  try {using var doc=JsonDocument.Parse(File.ReadAllText(targetMarker));if(doc.RootElement.TryGetProperty("Pid",out var p)&&p.TryGetInt32(out int pid)&&pid>0){status="状態ファイルあり";return pid;} status="本体状態ファイルにPIDなし";return null;}
  catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException){status="本体状態ファイルを読めない: "+e.GetType().Name;return null;}
 }
 static void Stop()=>quitting=true;
 static void CleanExit(){Log("監視アプリ正常終了");try {if(token!=null && File.Exists(Marker)){using var doc=JsonDocument.Parse(File.ReadAllText(Marker));if(doc.RootElement.GetProperty("Token").GetString()==token)File.Delete(Marker);}}catch(Exception e){Log("監視アプリの終了記録を削除できません: "+e.Message);}}
 static void Log(string msg){Directory.CreateDirectory(folder);if(File.Exists(LogPath)&&new FileInfo(LogPath).Length>2_000_000)File.Move(LogPath,LogPath+".old",true);File.AppendAllText(LogPath,$"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {msg.Replace('\r',' ').Replace('\n',' ')}{Environment.NewLine}",Encoding.UTF8);}
}

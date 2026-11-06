using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace WogiOS;

// 진단용: 게임이 오류 창 없이 꺼질 때 wog.log 에 마지막 흔적을 남긴다.
//  - 첫 번째 예외(잡혀서 넘어가는 것 포함) 40개의 종류 · 메시지 · 호출 위치
//  - 처리되지 않은 예외 / 작업 스레드의 예외 / 프로세스 종료(ProcessExit)
//  - 시작 후 30초 동안 0.05초마다 메모리 (마지막 줄이 죽기 직전 상태)
// 원인을 찾은 뒤에는 GameRunner.cs 의 DiagHook.Install 한 줄만 지우면 된다.
internal static class DiagHook
{
    static int _installed, _count;
    [ThreadStatic] static bool _busy;

    public static void Install(Action<string> log)
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (_busy || Interlocked.Increment(ref _count) > 40) return;
                _busy = true;
                try
                {
                    string st = e.Exception.StackTrace ?? "";
                    string[] lines = st.Split('\n');
                    string top = string.Join(" | ", lines, 0, Math.Min(3, lines.Length)).Replace("\r", "").Trim();
                    log("[diag] first-chance #" + _count + " (thread " + Environment.CurrentManagedThreadId + ") "
                        + e.Exception.GetType().FullName + ": " + e.Exception.Message + "  @ " + top);
                }
                catch (Exception) { }
                finally { _busy = false; }
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => log("[diag] UNHANDLED: " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => log("[diag] unobserved task exception: " + e.Exception);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => log("[diag] process exit (ProcessExit 이벤트 — 정상 종료 경로)");

            var t = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 30)
                {
                    string line = "[diag] t=" + sw.ElapsedMilliseconds + "ms";
                    try { line += " managed=" + (GC.GetTotalMemory(false) / (1024 * 1024)) + "MB"; } catch (Exception) { }
                    try { line += " workingSet=" + (Environment.WorkingSet / (1024 * 1024)) + "MB"; } catch (Exception) { }
                    log(line);
                    Thread.Sleep(50);
                }
            }) { IsBackground = true, Name = "diag-heartbeat" };
            t.Start();
            log("[diag] installed");
        }
        catch (Exception ex) { log("[diag] install failed: " + ex.Message); }
    }
}

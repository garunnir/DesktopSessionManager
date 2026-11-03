using System.Diagnostics;
using System.Reflection;
using DesktopSessionManager;

// Explicitly invoked integration probe. Uses isolated app data and only the
// profile's Remote Control target; normal test runs never touch the desktop.
internal static class LiveRestoreDiagnostics
{
    public static void Run(string profilePath)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "live-restore-data");
        Directory.CreateDirectory(Path.Combine(root, "Profiles"));
        File.Copy(profilePath, Path.Combine(root, "Profiles", "probe.json"), true);
        AppContext.SetData("DesktopSessionManager.TestDataRoot", root);
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
        Trace.AutoFlush = true;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Console.WriteLine("DIAGNOSTIC: VDA initialize");
                Vda.Initialize(root);
                Console.WriteLine(Vda.Status);
                var snapshot = ProfileStore.Read(profilePath);
                var targets = snapshot.Windows.Where(w => w.ClaudeRemoteControl).ToList();
                if (targets.Count != 1) throw new InvalidOperationException("Expected one Remote Control target.");
                Console.WriteLine("DIAGNOSTIC: construct form");
                using var form = new MainForm();
                var scan = typeof(MainForm).GetMethod("ScanIdentified", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Dump(string label)
                {
                    var current = (List<LiveWindow>)scan.Invoke(form, null)!;
                    Console.WriteLine($"DIAGNOSTIC: {label} found {current.Count} windows");
                    foreach (var w in current.Where(w => w.Data.Path.EndsWith("Code.exe", StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"DIAGNOSTIC: {label} VSCode {w.Handle} [{w.Data.Title}] project=[{w.Data.ProjectPath}] desktop={w.Data.DesktopIndex}");
                }
                Dump("UI thread");
                form.Shown += async (_, _) =>
                {
                    await Task.Run(() => Dump("Worker thread"));
                    Console.WriteLine("DIAGNOSTIC: run actual restore progress dialog");
                    try
                    {
                        var task = (Task)typeof(MainForm).GetMethod("RunRestore", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(form, [targets, false, false])!;
                        await task;
                        Console.WriteLine("DIAGNOSTIC: restore returned");
                        form.Close();
                    }
                    catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            Console.WriteLine("DIAGNOSTIC: 60 second timeout; ending only this diagnostic process.");
            Environment.Exit(2);
        }
        Thread.Sleep(3000);
        var sessions = (Dictionary<string, Process>)typeof(MainForm).GetField("ClaudeSessions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var session in sessions)
            Console.WriteLine($"DIAGNOSTIC: Claude terminal PID {session.Value.Id}, alive={!session.Value.HasExited}");
    }
}

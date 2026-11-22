using System.IO;
using System.Windows;

namespace FilmPick;

public partial class App : Application
{
    private FileStream? instance;

    protected override void OnStartup(StartupEventArgs args)
    {
        if (args.Args is ["--thumbnail-worker"])
        {
            Shutdown(VideoPlayer.RunThumbnailWorker());
            return;
        }
        base.OnStartup(args);
        var benchmark = args.Args is ["--benchmark", _, _];
        var pickerCheck = args.Args is ["--check-picker", _, _];
        var browseCheck = args.Args is ["--check-browse", _, _];
        var state = benchmark || pickerCheck || browseCheck
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args.Args[2]))!, "bench-state") : Store.DefaultData;
        try
        {
            Directory.CreateDirectory(state);
            instance = new FileStream(Path.Combine(state, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开本地状态。请确认 FilmPick 未在另一个窗口运行。\n" + ex.Message, "FilmPick");
            Shutdown(1);
            return;
        }
        var window = new MainWindow(state);
        MainWindow = window;
        window.Show();
        if (benchmark) window.StartBenchmark(args.Args[1], args.Args[2]);
        else if (browseCheck) window.StartBrowseCheck(args.Args[1], args.Args[2]);
        else if (pickerCheck)
        {
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var path = FolderPicker.Pick(new System.Windows.Interop.WindowInteropHelper(window).Handle, args.Args[1]);
                    File.WriteAllText(args.Args[2], path ?? "cancelled");
                    Shutdown(path == null ? 2 : 0);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args.Args[2], ex.ToString());
                    Shutdown(1);
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        else if (args.Args.Length == 1) window.OpenFromArgument(args.Args[0]);
    }

    protected override void OnExit(ExitEventArgs args)
    {
        instance?.Dispose();
        base.OnExit(args);
    }
}

namespace TinyMouseMacro;

static class Program
{
    [STAThread]
    static void Main()
    {
        NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }    
}

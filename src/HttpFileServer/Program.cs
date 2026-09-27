namespace HttpFileServer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Global\HttpFileServer.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("HttpFileServer 已在运行中。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject.ToString(), "严重错误", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new UI.MainForm(AppSettings.Load()));
    }
}

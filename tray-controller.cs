using System.ComponentModel;

namespace WechatDictationProbe;

internal sealed class TrayController : IDisposable
{
    private readonly Form _ownerForm;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private bool _disposed;

    public TrayController(Form ownerForm)
    {
        _ownerForm = ownerForm;

        _contextMenu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示主界面");
        showItem.Click += (_, _) => ShowOwnerForm();

        var updateItem = new ToolStripMenuItem("检查更新...");
        updateItem.Click += async (_, _) =>
        {
            await AppUpdater.CheckForUpdatesAsync(_ownerForm, silentIfLatest: false);
        };

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            Application.Exit();
        };

        _contextMenu.Items.Add(showItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(updateItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        Icon appIcon;
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            appIcon = Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        catch
        {
            appIcon = SystemIcons.Application;
        }

        _notifyIcon = new NotifyIcon
        {
            Text = "微信语音桥",
            Icon = appIcon,
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => ShowOwnerForm();
    }

    private void ShowOwnerForm()
    {
        if (_ownerForm.IsDisposed) return;

        if (!_ownerForm.Visible)
        {
            _ownerForm.Show();
        }

        if (_ownerForm.WindowState == FormWindowState.Minimized)
        {
            _ownerForm.WindowState = FormWindowState.Normal;
        }

        _ownerForm.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}

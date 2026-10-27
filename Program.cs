using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace WechatDictationProbe;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            AppUpdater.CleanupOldBackups();
            ApplicationConfiguration.Initialize();
            Application.Run(new ProbeForm());
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), ex.ToString());
            MessageBox.Show(ex.ToString(), "启动异常");
        }
    }
}

internal sealed class ProbeForm : Form
{
    private const int VkControl = 0x11;
    private const int VkLeftWin = 0x5B;
    private const int InputKeyboard = 1;
    private const uint KeyEventUp = 0x0002;
    private const int WmHotkey = 0x0312;
    private const int BridgeHotkeyId = 0x5744;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF8 = 0x77;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private const int SwRestore = 9;

    private readonly TextBox _captureBox;
    private readonly Label _instructions;
    private readonly Label _status;
    private readonly Label _stats;
    private readonly FloatingTalkForm _floatingTalkForm;
    private bool _shortcutHeld;
    private bool _bridgeRecording;
    private bool _hotkeyRegistered;
    private DateTime _lastTextChange;
    private IntPtr _bridgeTargetWindow;
    private string _bridgeHotkeyDisplay = "未注册";
    private string _bridgeTargetDescription = "尚未记录目标";

    public ProbeForm()
    {
        Text = $"微信语音桥 v{Application.ProductVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 620;
        Height = 440;
        MinimumSize = new Size(500, 320);
        MinimizeBox = false;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 248, 250);
        FormClosing += OnFormClosing;

        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(16, 12, 16, 6),
            BackColor = Color.FromArgb(246, 248, 250)
        };

        _instructions = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "使用说明：在远控或输入框点出光标，按住悬浮按钮说话（可拖拽换位），松开自动回填。",
            Font = new Font("Microsoft YaHei UI", 9.5F),
            ForeColor = Color.FromArgb(75, 85, 99)
        };
        topPanel.Controls.Add(_instructions);

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 72,
            Padding = new Padding(16, 8, 16, 10),
            BackColor = Color.FromArgb(240, 242, 245)
        };

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 28,
            Text = "等待测试。请先确认电脑版微信正在运行。",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 41, 55),
            AutoEllipsis = true
        };

        _stats = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 24,
            Text = "尚未收到文字。",
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = Color.FromArgb(107, 114, 128),
            AutoEllipsis = true
        };

        bottomPanel.Controls.Add(_status);
        bottomPanel.Controls.Add(_stats);

        _captureBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 13F),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(17, 24, 39),
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "微信听写结果接收框"
        };
        _captureBox.TextChanged += (_, _) =>
        {
            _lastTextChange = DateTime.Now;
            _stats.Text = $"已收到 {_captureBox.TextLength} 个字符 ｜ 最后变化：{_lastTextChange:HH:mm:ss.fff} ｜ 目标：{_bridgeTargetDescription}";
            _status.Text = _captureBox.TextLength > 0
                ? "识别成功：文字已同步进入接收框"
                : "接收框已清空。";
            _status.ForeColor = _captureBox.TextLength > 0 ? Color.FromArgb(5, 150, 105) : Color.FromArgb(75, 85, 99);
            _captureBox.SelectionStart = _captureBox.TextLength;
            _captureBox.ScrollToCaret();
        };

        _floatingTalkForm = new FloatingTalkForm(StartCaptureFromFloatingButton, StopBridgeRecording, ShowDashboard);

        var menuStrip = new MenuStrip
        {
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(246, 248, 250),
            Font = new Font("Microsoft YaHei UI", 9F)
        };

        var updateItem = new ToolStripMenuItem("检查更新(&U)...");
        updateItem.Click += async (_, _) =>
        {
            await AppUpdater.CheckForUpdatesAsync(this, silentIfLatest: false);
        };

        var helpMenu = new ToolStripMenuItem("帮助(&H)");
        var githubItem = new ToolStripMenuItem("访问 GitHub 仓库");
        githubItem.Click += (_, _) =>
        {
            AppUpdater.OpenBrowser("https://github.com/lsby/wechat-dictation-bridge");
        };
        var aboutItem = new ToolStripMenuItem("关于");
        aboutItem.Click += (_, _) =>
        {
            MessageBox.Show(
                this,
                $"微信语音桥 v{Application.ProductVersion}\r\n\r\n基于微信听写跳板机制，支持将微信语音输入结果自动转发回填到远控（如悠悠远程、ToDesk）与本地目标窗口。",
                "关于 微信语音桥",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        };

        helpMenu.DropDownItems.Add(githubItem);
        helpMenu.DropDownItems.Add(aboutItem);

        menuStrip.Items.Add(updateItem);
        menuStrip.Items.Add(helpMenu);

        var textHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 4, 16, 6),
            BackColor = Color.FromArgb(246, 248, 250)
        };
        textHost.Controls.Add(_captureBox);

        Controls.Add(textHost);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);
        Controls.Add(menuStrip);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _captureBox.Focus();
        _floatingTalkForm.Show();
        _hotkeyRegistered = TryRegisterBridgeHotkey();
        if (_hotkeyRegistered)
        {
            _status.Text = $"就绪：全局热键 {_bridgeHotkeyDisplay} 或按住右上角悬浮按钮听写。";
            _status.ForeColor = Color.DarkGreen;
        }
        else
        {
            _status.Text = "热键注册失败，请直接使用右上角绿色悬浮按钮听写。";
            _status.ForeColor = Color.DarkOrange;
        }
    }

    private bool TryRegisterBridgeHotkey()
    {
        var candidates = new (uint Modifiers, uint Key, string Display)[]
        {
            (ModControl | ModAlt | ModNoRepeat, VkF9, "Ctrl+Alt+F9"),
            (ModControl | ModShift | ModNoRepeat, VkF9, "Ctrl+Shift+F9"),
            (ModControl | ModAlt | ModShift | ModNoRepeat, VkF9, "Ctrl+Alt+Shift+F9"),
            (ModControl | ModAlt | ModNoRepeat, VkF10, "Ctrl+Alt+F10"),
            (ModControl | ModShift | ModNoRepeat, VkF10, "Ctrl+Shift+F10"),
            (ModNoRepeat, VkF8, "F8")
        };

        foreach (var candidate in candidates)
        {
            if (!RegisterHotKey(Handle, BridgeHotkeyId, candidate.Modifiers, candidate.Key))
                continue;

            _bridgeHotkeyDisplay = candidate.Display;
            return true;
        }

        return false;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam.ToInt32() == BridgeHotkeyId)
        {
            ToggleRemoteBridge();
            return;
        }

        base.WndProc(ref message);
    }

    private void ToggleRemoteBridge()
    {
        if (_bridgeRecording)
        {
            StopBridgeRecording();
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == Handle)
        {
            _status.Text = $"请先切换到远控窗口，并在远端输入框点出光标，再按 {_bridgeHotkeyDisplay}。";
            _status.ForeColor = Color.DarkRed;
            return;
        }

        _bridgeTargetWindow = foreground;
        _bridgeTargetDescription = DescribeWindow(foreground);
        Activate();
        _captureBox.Focus();
        _captureBox.Clear();
        Application.DoEvents();
        BeginWechatShortcut();
        _bridgeRecording = _shortcutHeld;

        if (_bridgeRecording)
        {
            _status.Text = $"桥接录音中：请说话，结束时再按一次 {_bridgeHotkeyDisplay}。";
            _status.ForeColor = Color.DarkBlue;
        }
    }

    private void StartCaptureFromFloatingButton()
    {
        if (_bridgeRecording) return;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == Handle || foreground == _floatingTalkForm.Handle)
        {
            _status.Text = "悬浮按钮下方没有可用的目标窗口。请先激活远控窗口并点出远端输入光标。";
            _status.ForeColor = Color.DarkRed;
            return;
        }

        _bridgeTargetWindow = foreground;
        _bridgeTargetDescription = DescribeWindow(foreground);
        Show();
        Activate();
        _captureBox.Focus();
        _captureBox.Clear();
        Application.DoEvents();
        BeginWechatShortcut();
        _bridgeRecording = _shortcutHeld;

        if (_bridgeRecording)
        {
            _floatingTalkForm.SetRecording(true);
            _status.Text = $"焦点跳板录音中；已记录原目标“{_bridgeTargetDescription}”。松开结束。";
            _status.ForeColor = Color.DarkBlue;
        }
        else
        {
            _status.Text = "无法启动微信听写快捷键。";
            _status.ForeColor = Color.DarkRed;
        }
    }

    private void ShowDashboard()
    {
        Show();
        Activate();
    }

    private void StopBridgeRecording()
    {
        if (!_bridgeRecording) return;

        _bridgeRecording = false;
        _floatingTalkForm.SetRecording(false);
        EndWechatShortcut();
        _status.Text = "听写已结束，等待识别文字稳定后回填远控窗口……";
        _status.ForeColor = Color.DarkOrange;
        _ = CompleteBridgeAfterRecognitionAsync();
    }

    private async Task CompleteBridgeAfterRecognitionAsync()
    {
        try
        {
            var started = DateTime.Now;
            while (DateTime.Now - started < TimeSpan.FromSeconds(5))
            {
                if (_captureBox.TextLength > 0 &&
                    DateTime.Now - _lastTextChange >= TimeSpan.FromMilliseconds(500))
                {
                    await PasteBackToTargetAsync();
                    return;
                }

                await Task.Delay(100);
            }

            _status.Text = "等待 5 秒仍未收到稳定的识别结果，未执行目标窗口粘贴。";
            _status.ForeColor = Color.DarkRed;
        }
        catch (Exception ex)
        {
            _status.Text = $"桥接流程发生异常：{ex.GetType().Name}: {ex.Message}";
            _status.ForeColor = Color.DarkRed;
        }
    }

    private async Task PasteBackToTargetAsync()
    {
        var result = _captureBox.Text;
        if (string.IsNullOrWhiteSpace(result) || _bridgeTargetWindow == IntPtr.Zero)
            return;

        try
        {
            Clipboard.SetDataObject(result, true);
        }
        catch (ExternalException ex)
        {
            _status.Text = $"写入剪贴板失败：{ex.Message}";
            _status.ForeColor = Color.DarkRed;
            return;
        }

        // Give remote-control clients time to observe the local clipboard update.
        await Task.Delay(800);

        // Keep the result window visible as a manual fallback, but transfer keyboard
        // focus back to the recorded target before sending Ctrl+V.
        if (GetForegroundWindow() != _bridgeTargetWindow && !ForceForegroundWindow(_bridgeTargetWindow))
        {
            _status.Text = "识别成功，但无法把焦点交还原远控窗口；文字已保留在接收框和剪贴板。";
            _status.ForeColor = Color.DarkOrange;
            return;
        }

        await Task.Delay(350);
        if (GetForegroundWindow() != _bridgeTargetWindow)
        {
            _status.Text = "准备粘贴时前台窗口再次改变，已停止自动按键；文字仍在剪贴板。";
            _status.ForeColor = Color.DarkOrange;
            return;
        }

        var sent = SendKeyboardInputs(
            KeyboardInput(VkControl, false),
            KeyboardInput(0x56, false),
            KeyboardInput(0x56, true),
            KeyboardInput(VkControl, true));

        _status.Text = sent == 4
            ? $"已向目标“{_bridgeTargetDescription}”发送 {result.Length} 个字符的粘贴指令。"
            : $"文字已复制，但粘贴按键只发送了 {sent}/4 个事件。";
        _status.ForeColor = sent == 4 ? Color.DarkGreen : Color.DarkOrange;
    }

    private static bool ForceForegroundWindow(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow)) return false;

        if (IsIconic(targetWindow))
            ShowWindowAsync(targetWindow, SwRestore);

        var currentThread = GetCurrentThreadId();
        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
        var targetThread = GetWindowThreadProcessId(targetWindow, IntPtr.Zero);
        var attachedForeground = false;
        var attachedTarget = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            BringWindowToTop(targetWindow);
            SetForegroundWindow(targetWindow);
            SetFocus(targetWindow);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
        }

        return GetForegroundWindow() == targetWindow;
    }

    private static string DescribeWindow(IntPtr windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0) return $"无标题窗口 0x{windowHandle.ToInt64():X}";

        var title = new StringBuilder(length + 1);
        GetWindowText(windowHandle, title, title.Capacity);
        return $"{title} [0x{windowHandle.ToInt64():X}]";
    }

    private void BeginWechatShortcut()
    {
        if (_shortcutHeld) return;

        Activate();
        _captureBox.Focus();
        _captureBox.Clear();
        Application.DoEvents();

        var sent = SendKeyboardInputs(
            KeyboardInput(VkControl, false),
            KeyboardInput(VkLeftWin, false));

        _shortcutHeld = sent == 2;
        _status.Text = _shortcutHeld
            ? "已模拟按下 Ctrl+Win；请继续按住鼠标并说话。"
            : $"模拟按键失败，只发送了 {sent}/2 个事件，Win32 错误：{Marshal.GetLastWin32Error()}。";
        _status.ForeColor = _shortcutHeld ? Color.DarkBlue : Color.DarkRed;
    }

    private void EndWechatShortcut()
    {
        if (!_shortcutHeld) return;

        var sent = SendKeyboardInputs(
            KeyboardInput(VkLeftWin, true),
            KeyboardInput(VkControl, true));

        _shortcutHeld = false;
        _status.Text = sent == 2
            ? "已松开 Ctrl+Win，等待微信写入识别结果……"
            : $"释放按键不完整，只发送了 {sent}/2 个事件。";
        _status.ForeColor = sent == 2 ? Color.DarkOrange : Color.DarkRed;
        _captureBox.Focus();
    }

    private void OnFormClosing(object? sender, CancelEventArgs e)
    {
        _floatingTalkForm.Close();
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(Handle, BridgeHotkeyId);
            _hotkeyRegistered = false;
        }

        if (!_shortcutHeld) return;
        SendKeyboardInputs(
            KeyboardInput(VkLeftWin, true),
            KeyboardInput(VkControl, true));
        _shortcutHeld = false;
    }

    private static INPUT KeyboardInput(int virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = (ushort)virtualKey,
                ScanCode = 0,
                Flags = keyUp ? KeyEventUp : 0,
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            }
        }
    };

    private static uint SendKeyboardInputs(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, IntPtr processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }
}

internal sealed class FloatingTalkForm : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private readonly Action _onStart;
    private readonly Action _onStop;
    private readonly Action _onShowDashboard;
    private readonly System.Windows.Forms.Timer _releasePollTimer;
    private readonly System.Windows.Forms.Timer _holdCheckTimer;
    private readonly ToolTip _toolTip;

    private bool _pressed;
    private bool _recording;
    private bool _isMouseDown;
    private Point _mousePressScreenPos;
    private Point _formStartPos;
    private bool _hasDraggedAway;

    public FloatingTalkForm(Action onStart, Action onStop, Action onShowDashboard)
    {
        _onStart = onStart;
        _onStop = onStop;
        _onShowDashboard = onShowDashboard;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(76, 76);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;

        UpdateCircleRegion();

        _toolTip = new ToolTip
        {
            InitialDelay = 500,
            ReshowDelay = 200,
            AutoPopDelay = 6000
        };
        _toolTip.SetToolTip(this, "按住说话，松手输入\n拖拽可任意换位置\n右键点击打开面板");

        _releasePollTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _releasePollTimer.Tick += (_, _) =>
        {
            if (_pressed && Control.MouseButtons == MouseButtons.None)
                FinishPress();
        };

        // 180ms 按住防抖计时器：只有按住超过 180ms 且未大幅拖动，才真正进入录音听写，避免拖拽时误弹主界面
        _holdCheckTimer = new System.Windows.Forms.Timer { Interval = 180 };
        _holdCheckTimer.Tick += (_, _) =>
        {
            _holdCheckTimer.Stop();
            if (_isMouseDown && !_hasDraggedAway && !_pressed)
            {
                _pressed = true;
                _releasePollTimer.Start();
                _onStart();
            }
        };

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 28, area.Top + 68);
    }

    private void UpdateCircleRegion()
    {
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, Width, Height);
        Region = new Region(path);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    public void SetRecording(bool recording)
    {
        _recording = recording;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // 绘制球体背景渐变
        var circleBounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var topColor = _recording ? Color.FromArgb(244, 63, 94) : Color.FromArgb(16, 185, 129);
        var bottomColor = _recording ? Color.FromArgb(225, 29, 72) : Color.FromArgb(5, 150, 105);

        using (var brush = new LinearGradientBrush(circleBounds, topColor, bottomColor, LinearGradientMode.Vertical))
        {
            g.FillEllipse(brush, circleBounds);
        }

        // 内高光环，增强立体质感
        using (var ringPen = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f))
        {
            g.DrawEllipse(ringPen, 2, 2, Width - 5, Height - 5);
        }

        if (_recording)
        {
            // 录音状态：绘制科技感声波律动线条
            using var wavePen = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int cx = Width / 2;
            int cy = 25;
            g.DrawLine(wavePen, cx - 14, cy - 3, cx - 14, cy + 3);
            g.DrawLine(wavePen, cx - 7, cy - 8, cx - 7, cy + 8);
            g.DrawLine(wavePen, cx, cy - 12, cx, cy + 12);
            g.DrawLine(wavePen, cx + 7, cy - 8, cx + 7, cy + 8);
            g.DrawLine(wavePen, cx + 14, cy - 3, cx + 14, cy + 3);

            // 录音文字
            using var font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            var textRect = new Rectangle(0, 44, Width, 22);
            TextRenderer.DrawText(
                g,
                "松手发送",
                font,
                textRect,
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }
        else
        {
            // 常态：纯矢量手工绘制高清晰极简麦克风图标 (绝不失真/不偏色)
            int cx = Width / 2;
            int cy = 25;

            using var whiteBrush = new SolidBrush(Color.White);
            using var whitePen = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            // 1. 麦克风胶囊头 (宽 10, 高 16)
            var micHeadRect = new Rectangle(cx - 5, cy - 10, 10, 15);
            using var micPath = new GraphicsPath();
            micPath.AddArc(micHeadRect.X, micHeadRect.Y, micHeadRect.Width, micHeadRect.Width, 180, 180);
            micPath.AddArc(micHeadRect.X, micHeadRect.Bottom - micHeadRect.Width, micHeadRect.Width, micHeadRect.Width, 0, 180);
            micPath.CloseFigure();
            g.FillPath(whiteBrush, micPath);

            // 2. 麦克风弧形托架
            g.DrawArc(whitePen, cx - 9, cy - 5, 18, 14, 0, 180);

            // 3. 支柱与底座
            g.DrawLine(whitePen, cx, cy + 9, cx, cy + 14);
            g.DrawLine(whitePen, cx - 6, cy + 14, cx + 6, cy + 14);

            // 常态文字：安全内收，绝不切边
            using var font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            var textRect = new Rectangle(0, 44, Width, 22);
            TextRenderer.DrawText(
                g,
                "按住说",
                font,
                textRect,
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        _mousePressScreenPos = Cursor.Position;
        _formStartPos = Location;
        _hasDraggedAway = false;
        _isMouseDown = true;
        Capture = true;

        if (e.Button == MouseButtons.Left && !_pressed)
        {
            // 启动防抖计时器，等待判断是拖动还是真按住说话，绝不提前弹出主界面！
            _holdCheckTimer.Start();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_isMouseDown) return;

        var cur = Cursor.Position;
        var dx = cur.X - _mousePressScreenPos.X;
        var dy = cur.Y - _mousePressScreenPos.Y;

        // 只要位移超过 5 像素，立即锁定为拖动模式！
        if (!_hasDraggedAway && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5))
        {
            _hasDraggedAway = true;
            _holdCheckTimer.Stop(); // 彻底掐死说话触发，绝不惊动主界面！

            if (_pressed)
            {
                _pressed = false;
                _releasePollTimer.Stop();
                _onStop();
            }
        }

        if (_hasDraggedAway)
        {
            Location = new Point(_formStartPos.X + dx, _formStartPos.Y + dy);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        _holdCheckTimer.Stop();
        Capture = false;
        _isMouseDown = false;

        // 如果用户只是拖动换位置，松手即停，不触发任何输入或面板弹出
        if (_hasDraggedAway)
        {
            _hasDraggedAway = false;
            return;
        }

        // 原地松手
        if (e.Button == MouseButtons.Right)
        {
            _onShowDashboard();
            return;
        }

        if (e.Button == MouseButtons.Left && _pressed)
        {
            FinishPress();
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!_pressed || Control.MouseButtons != MouseButtons.None) return;
        FinishPress();
    }

    private void FinishPress()
    {
        if (!_pressed) return;
        _pressed = false;
        _releasePollTimer.Stop();
        _onStop();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmMouseActivate)
        {
            message.Result = (IntPtr)MaNoActivate;
            return;
        }

        base.WndProc(ref message);
    }
}

using System.ComponentModel;
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
    private readonly Button _holdButton;
    private readonly FloatingTalkForm _floatingTalkForm;
    private readonly TrayController _trayController;
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
        Width = 720;
        Height = 520;
        MinimumSize = new Size(620, 430);
        Font = new Font("Microsoft YaHei UI", 10F);
        FormClosing += OnFormClosing;

        _instructions = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 132,
            Padding = new Padding(14, 12, 14, 4),
            Text = "用途：验证微信语音输入能否把识别结果写入我们控制的文本框。\r\n" +
                   "测试 A：在下方按钮上按住鼠标说话，松开结束；程序会模拟 Ctrl+Win。\r\n" +
                   "测试 B：若 A 没唤起微信，点“仅聚焦接收框”，再亲自按住 Ctrl+Win 说话。\r\n" +
                   "远控桥接：启动后会自动选择一个可用热键；先在远端输入框点出光标，按一次开始，再按一次结束。"
        };

        _status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 35,
            Padding = new Padding(14, 7, 14, 4),
            Text = "等待测试。请先确认电脑版微信正在运行。",
            ForeColor = Color.DarkSlateGray
        };

        _stats = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 32,
            Padding = new Padding(14, 4, 14, 7),
            Text = "尚未收到文字。",
            ForeColor = Color.DimGray
        };

        _captureBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(14),
            Font = new Font("Microsoft YaHei UI", 14F),
            AccessibleName = "微信听写结果接收框"
        };
        _captureBox.TextChanged += (_, _) =>
        {
            _lastTextChange = DateTime.Now;
            _stats.Text = $"已收到 {_captureBox.TextLength} 个字符；最后变化：{_lastTextChange:HH:mm:ss.fff}；目标：{_bridgeTargetDescription}";
            _status.Text = _captureBox.TextLength > 0
                ? "成功：文本已经进入自有控件，可以直接读取 TextBox.Text。"
                : "接收框已清空。";
            _status.ForeColor = _captureBox.TextLength > 0 ? Color.DarkGreen : Color.DarkSlateGray;
        };

        _floatingTalkForm = new FloatingTalkForm(StartCaptureFromFloatingButton, StopBridgeRecording, ShowDashboard);
        _trayController = new TrayController(this);

        _holdButton = new Button
        {
            Text = "按住这里说话，松开结束",
            Width = 250,
            Height = 48,
            BackColor = Color.FromArgb(20, 130, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _holdButton.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _holdButton.Capture = true;
            BeginWechatShortcut();
        };
        _holdButton.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            EndWechatShortcut();
            _holdButton.Capture = false;
        };
        _holdButton.MouseCaptureChanged += (_, _) =>
        {
            if (_shortcutHeld && Control.MouseButtons == MouseButtons.None)
                EndWechatShortcut();
        };

        var focusButton = new Button
        {
            Text = "仅聚焦接收框",
            Width = 170,
            Height = 48
        };
        focusButton.Click += (_, _) =>
        {
            Activate();
            _captureBox.Focus();
            _status.Text = "接收框已获得焦点。现在请亲自按住 Ctrl+Win 说话。";
            _status.ForeColor = Color.DarkBlue;
        };

        var clearButton = new Button
        {
            Text = "清空结果",
            Width = 120,
            Height = 48
        };
        clearButton.Click += (_, _) =>
        {
            _captureBox.Clear();
            _captureBox.Focus();
        };

        var copyButton = new Button
        {
            Text = "复制结果",
            Width = 100,
            Height = 48
        };
        copyButton.Click += (_, _) =>
        {
            if (_captureBox.TextLength == 0)
            {
                _status.Text = "接收框里还没有可复制的文字。";
                _status.ForeColor = Color.DarkOrange;
                return;
            }

            try
            {
                Clipboard.SetDataObject(_captureBox.Text, true);
                _status.Text = $"已重新复制 {_captureBox.TextLength} 个字符，可以手动切换到目标窗口粘贴。";
                _status.ForeColor = Color.DarkGreen;
            }
            catch (ExternalException ex)
            {
                _status.Text = $"复制结果失败：{ex.Message}";
                _status.ForeColor = Color.DarkRed;
            }
        };

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 66,
            Padding = new Padding(14, 8, 14, 6),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        buttonRow.Controls.AddRange([_holdButton, focusButton, clearButton, copyButton]);

        var textHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 4, 14, 8)
        };
        textHost.Controls.Add(_captureBox);

        Controls.Add(textHost);
        Controls.Add(_stats);
        Controls.Add(_status);
        Controls.Add(buttonRow);
        Controls.Add(_instructions);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _captureBox.Focus();
        _floatingTalkForm.Show();
        _hotkeyRegistered = TryRegisterBridgeHotkey();
        if (_hotkeyRegistered)
        {
            _status.Text = $"全局桥接键 {_bridgeHotkeyDisplay} 已注册。可以进行远控窗口自动回填测试。";
            _status.ForeColor = Color.DarkGreen;
            _instructions.Text += $"\r\n当前桥接热键：{_bridgeHotkeyDisplay}";
        }
        else
        {
            _status.Text = $"候选热键全部注册失败。最后一个 Win32 错误：{Marshal.GetLastWin32Error()}。";
            _status.ForeColor = Color.DarkRed;
        }

        _instructions.Text += "\r\n焦点跳板：按住右上角悬浮按钮听写；识别后主窗口保持显示，并回到原窗口粘贴。";
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
        _trayController.Dispose();
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
    private bool _pressed;
    private bool _recording;

    public FloatingTalkForm(Action onStart, Action onStop, Action onShowDashboard)
    {
        _onStart = onStart;
        _onStop = onStop;
        _onShowDashboard = onShowDashboard;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(184, 52);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;

        _releasePollTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _releasePollTimer.Tick += (_, _) =>
        {
            if (_pressed && Control.MouseButtons == MouseButtons.None)
                FinishPress();
        };

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 24, area.Top + 72);
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
        var color = _recording ? Color.FromArgb(200, 55, 55) : Color.FromArgb(20, 145, 85);
        using var background = new SolidBrush(color);
        using var border = new Pen(Color.White, 2);
        using var font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
        var text = _recording ? "微信听写中，松开结束" : "按住说话｜右键看状态";
        TextRenderer.DrawText(
            e.Graphics,
            text,
            font,
            ClientRectangle,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _pressed) return;
        _pressed = true;
        Capture = true;
        _releasePollTimer.Start();
        _onStart();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            _onShowDashboard();
            return;
        }
        if (e.Button != MouseButtons.Left || !_pressed) return;
        FinishPress();
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
        Capture = false;
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

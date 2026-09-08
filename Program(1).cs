using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MacroForge;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MacroEvent
{
    public string Type { get; set; } = "";
    public long TimeMs { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Data { get; set; }
    public int Key { get; set; }
    public bool Down { get; set; }
}

public sealed class MacroDocument
{
    public string Name { get; set; } = "My Macro";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<MacroEvent> Events { get; set; } = [];
}

public sealed class MainForm : Form
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14, WM_HOTKEY = 0x0312;
    private const int HC_ACTION = 0;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;
    private const int VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79, VK_F12 = 0x7B;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207,
        WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A;
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacroForge");
    private readonly string macroDir;
    private readonly Stopwatch recordClock = new();
    private readonly List<MacroEvent> events = [];
    private readonly List<Control> automationControls = [];
    private readonly object stateLock = new();

    private IntPtr keyboardHook, mouseHook;
    private HookProc? keyboardProc, mouseProc;
    private CancellationTokenSource? actionCts;
    private Keys? heldKey;
    private bool recording;
    private long lastMoveMs;
    private Point lastMove;

    private readonly Label globalStatus = MakeLabel("READY", 10, true, Color.FromArgb(116, 224, 148));
    private readonly Label recordStatus = MakeLabel("0 events", 10, true, Color.FromArgb(116, 224, 148));
    private readonly Label clickStatus = MakeLabel("Ready", 10, true, Color.FromArgb(116, 224, 148));
    private readonly Label keyStatus = MakeLabel("Ready", 10, true, Color.FromArgb(116, 224, 148));
    private readonly TextBox macroName = MakeTextBox("My Macro");
    private readonly NumericUpDown macroLoops = MakeNumber(1, 0, 999999);
    private readonly ComboBox speedBox = MakeCombo(["0.25x", "0.5x", "1.0x", "1.5x", "2.0x", "3.0x", "5.0x"], 2);
    private readonly ListBox macroList = new();
    private readonly ComboBox clickButton = MakeCombo(["Left", "Right", "Middle"], 0);
    private readonly NumericUpDown clickInterval = MakeNumber(100, 1, 3600000);
    private readonly NumericUpDown clickCount = MakeNumber(0, 0, 999999999);
    private readonly TextBox holdKeyBox = MakeTextBox("W");

    public MainForm()
    {
        macroDir = Path.Combine(dataDir, "Macros");
        Directory.CreateDirectory(macroDir);
        Text = "MacroForge";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 570);
        Size = new Size(970, 680);
        BackColor = Color.FromArgb(11, 11, 14);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        BuildUi();
        RefreshMacros();
        Shown += (_, _) => RegisterHotkeys();
        FormClosing += (_, _) => Shutdown();
    }

    private void BuildUi()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(24, 18, 24, 8), BackColor = BackColor };
        var title = MakeLabel("MacroForge", 23, true, Color.White); title.Dock = DockStyle.Left; title.AutoSize = true;
        globalStatus.Dock = DockStyle.Right; globalStatus.AutoSize = true; globalStatus.Padding = new Padding(0, 10, 0, 0);
        header.Controls.Add(globalStatus); header.Controls.Add(title); Controls.Add(header);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 8), ItemSize = new Size(160, 36), SizeMode = TabSizeMode.Fixed };
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.DrawItem += (_, e) => {
            var selected = e.Index == tabs.SelectedIndex;
            using var brush = new SolidBrush(selected ? Color.FromArgb(46, 46, 54) : Color.FromArgb(24, 24, 29));
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, e.Bounds, selected ? Color.White : Color.Silver,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        var recorder = MakeTab("Macro Recorder");
        var clicker = MakeTab("Auto Clicker");
        var holder = MakeTab("Key Holder");
        var info = MakeTab("Hotkeys");
        BuildRecorder(recorder); BuildClicker(clicker); BuildHolder(holder); BuildInfo(info);
        tabs.TabPages.AddRange([recorder, clicker, holder, info]);
        var container = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 0, 24, 22), BackColor = BackColor };
        container.Controls.Add(tabs); Controls.Add(container); container.BringToFront();
    }

    private void BuildRecorder(TabPage tab)
    {
        var card = MakeCard(); tab.Controls.Add(card);
        AddHeading(card, "Record mouse and keyboard", "Works across your complete multi-monitor desktop.");
        var fields = new TableLayoutPanel { Left = 18, Top = 88, Width = 850, Height = 78, ColumnCount = 4, RowCount = 2, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        fields.Controls.Add(MakeLabel("Macro name"), 0, 0); fields.Controls.Add(MakeLabel("Loops (0 = infinite)"), 1, 0); fields.Controls.Add(MakeLabel("Playback speed"), 2, 0);
        macroName.Dock = DockStyle.Fill; macroLoops.Dock = DockStyle.Fill; speedBox.Dock = DockStyle.Fill; recordStatus.Dock = DockStyle.Fill; recordStatus.TextAlign = ContentAlignment.MiddleRight;
        fields.Controls.Add(macroName, 0, 1); fields.Controls.Add(macroLoops, 1, 1); fields.Controls.Add(speedBox, 2, 1); fields.Controls.Add(recordStatus, 3, 1); card.Controls.Add(fields);
        var buttons = new FlowLayoutPanel { Left = 18, Top = 180, Width = 850, Height = 48, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        buttons.Controls.Add(MakeButton("Start Recording", StartRecording, true)); buttons.Controls.Add(MakeButton("Stop & Save", StopRecording));
        buttons.Controls.Add(MakeButton("Play Selected", PlaySelected)); buttons.Controls.Add(MakeButton("STOP ALL", StopAll, false, true)); card.Controls.Add(buttons);
        var savedLabel = MakeLabel("Saved macros", 14, true, Color.White); savedLabel.Location = new Point(18, 252); savedLabel.AutoSize = true; card.Controls.Add(savedLabel);
        macroList.Location = new Point(18, 286); macroList.Size = new Size(850, 168); macroList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        macroList.BackColor = Color.FromArgb(31, 31, 37); macroList.ForeColor = Color.White; macroList.BorderStyle = BorderStyle.FixedSingle; macroList.IntegralHeight = false; card.Controls.Add(macroList);
        var listButtons = new FlowLayoutPanel { Left = 18, Top = 466, Width = 850, Height = 44, Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        listButtons.Controls.Add(MakeButton("Refresh", RefreshMacros)); listButtons.Controls.Add(MakeButton("Delete", DeleteSelected)); card.Controls.Add(listButtons);
        automationControls.AddRange([macroName, macroLoops, speedBox, .. buttons.Controls.Cast<Control>(), .. listButtons.Controls.Cast<Control>()]);
    }

    private void BuildClicker(TabPage tab)
    {
        var card = MakeCard(); tab.Controls.Add(card); AddHeading(card, "Auto Clicker", "Clicks at the current cursor position. Move the mouse while it runs.");
        var fields = new TableLayoutPanel { Left = 18, Top = 95, Width = 850, Height = 85, ColumnCount = 3, RowCount = 2, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        for (var i = 0; i < 3; i++) fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        fields.Controls.Add(MakeLabel("Mouse button"), 0, 0); fields.Controls.Add(MakeLabel("Interval (milliseconds)"), 1, 0); fields.Controls.Add(MakeLabel("Clicks (0 = infinite)"), 2, 0);
        clickButton.Dock = DockStyle.Fill; clickInterval.Dock = DockStyle.Fill; clickCount.Dock = DockStyle.Fill;
        fields.Controls.Add(clickButton, 0, 1); fields.Controls.Add(clickInterval, 1, 1); fields.Controls.Add(clickCount, 2, 1); card.Controls.Add(fields);
        var row = new FlowLayoutPanel { Left = 18, Top = 200, Width = 850, Height = 52, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        row.Controls.Add(MakeButton("Start Auto Clicker", StartAutoClicker, true)); row.Controls.Add(MakeButton("Stop", StopAll, false, true)); clickStatus.Margin = new Padding(25, 11, 0, 0); row.Controls.Add(clickStatus); card.Controls.Add(row);
        automationControls.AddRange([clickButton, clickInterval, clickCount, .. row.Controls.OfType<Button>()]);
    }

    private void BuildHolder(TabPage tab)
    {
        var card = MakeCard(); tab.Controls.Add(card); AddHeading(card, "Key Holder", "Choose one key. It remains pressed until Stop or F12 is used.");
        var label = MakeLabel("Key to hold"); label.Location = new Point(18, 100); label.AutoSize = true; card.Controls.Add(label);
        holdKeyBox.Location = new Point(18, 127); holdKeyBox.Width = 300; card.Controls.Add(holdKeyBox);
        var examples = MakeLabel("Examples: W, Space, Shift, Ctrl, Alt, Enter, F5"); examples.Location = new Point(18, 170); examples.AutoSize = true; card.Controls.Add(examples);
        var row = new FlowLayoutPanel { Left = 18, Top = 210, Width = 850, Height = 52, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        row.Controls.Add(MakeButton("Hold Key", StartKeyHold, true)); row.Controls.Add(MakeButton("Release", StopAll, false, true)); keyStatus.Margin = new Padding(25, 11, 0, 0); row.Controls.Add(keyStatus); card.Controls.Add(row);
        automationControls.AddRange([holdKeyBox, .. row.Controls.OfType<Button>()]);
    }

    private void BuildInfo(TabPage tab)
    {
        var card = MakeCard(); tab.Controls.Add(card); AddHeading(card, "Global hotkeys", "Hotkeys work when MacroForge is not focused.");
        var text = MakeLabel("F6   Start auto clicker\r\nF7   Hold selected key\r\nF8   Start recording\r\nF9   Stop recording and save\r\nF10  Play selected macro\r\nF12  Emergency stop and release keys", 11, false, Color.Gainsboro);
        text.Location = new Point(22, 95); text.Size = new Size(820, 360); card.Controls.Add(text);
    }

    private void StartRecording()
    {
        if (!CanStart()) return;
        lock (stateLock) { recording = true; events.Clear(); lastMoveMs = 0; lastMove = Point.Empty; recordClock.Restart(); }
        InstallHooks(); SetStatus("RECORDING", Color.FromArgb(255, 105, 115)); recordStatus.Text = "Recording…";
    }

    private void StopRecording()
    {
        lock (stateLock) { if (!recording) return; recording = false; recordClock.Stop(); }
        RemoveHooks();
        var name = SafeName(macroName.Text);
        var doc = new MacroDocument { Name = name, Events = [.. events] };
        File.WriteAllText(Path.Combine(macroDir, name + ".json"), JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        recordStatus.Text = $"Saved {events.Count} events"; RefreshMacros(name); SetStatus("READY", Color.FromArgb(116, 224, 148));
    }

    private async void PlaySelected()
    {
        if (!CanStart() || macroList.SelectedItem is null) { if (macroList.SelectedItem is null) MessageBox.Show("Select a saved macro first.", "MacroForge"); return; }
        var path = Path.Combine(macroDir, macroList.SelectedItem + ".json");
        var doc = JsonSerializer.Deserialize<MacroDocument>(File.ReadAllText(path)); if (doc is null) return;
        actionCts = new CancellationTokenSource(); var token = actionCts.Token; var loops = (int)macroLoops.Value;
        var speed = double.Parse(speedBox.Text.TrimEnd('x'), System.Globalization.CultureInfo.InvariantCulture);
        SetStatus("PLAYING", Color.FromArgb(105, 183, 255)); ToggleAutomation(false, true);
        try
        {
            await Task.Run(async () => {
                var iteration = 0;
                while (!token.IsCancellationRequested && (loops == 0 || iteration++ < loops))
                {
                    long previous = 0;
                    foreach (var item in doc.Events)
                    {
                        var delay = Math.Max(0, (int)((item.TimeMs - previous) / speed)); previous = item.TimeMs;
                        await Task.Delay(delay, token); Execute(item);
                    }
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        finally { actionCts?.Dispose(); actionCts = null; ToggleAutomation(true); ShowIdleStatus(); }
    }

    private async void StartAutoClicker()
    {
        if (!CanStart()) return; actionCts = new CancellationTokenSource(); var token = actionCts.Token;
        var interval = (int)clickInterval.Value; var count = (int)clickCount.Value; var button = clickButton.Text;
        clickStatus.Text = "Running…"; SetStatus("AUTO CLICKING", Color.FromArgb(105, 183, 255)); ToggleAutomation(false, true);
        var done = 0;
        try { await Task.Run(async () => { while (!token.IsCancellationRequested && (count == 0 || done < count)) { ClickMouse(button); done++; await Task.Delay(interval, token); } }, token); }
        catch (OperationCanceledException) { }
        finally { clickStatus.Text = $"Stopped ({done} clicks)"; actionCts?.Dispose(); actionCts = null; ToggleAutomation(true); ShowIdleStatus(); }
    }

    private void StartKeyHold()
    {
        if (!CanStart()) return;
        if (!TryParseKey(holdKeyBox.Text, out var key)) { MessageBox.Show("Enter one key, for example W, Space, Shift, Ctrl, Enter, or F5.", "MacroForge"); return; }
        heldKey = key; SendKey((ushort)key, true); keyStatus.Text = $"Holding {key}"; SetStatus("HOLDING KEY", Color.FromArgb(105, 183, 255));
    }

    private bool CanStart()
    {
        if (recording || actionCts is not null || heldKey is not null) { MessageBox.Show("Stop the current action first.", "MacroForge"); return false; }
        return true;
    }

    private void StopAll()
    {
        actionCts?.Cancel();
        if (recording) StopRecording();
        if (heldKey is Keys key) { SendKey((ushort)key, false); heldKey = null; keyStatus.Text = "Released"; }
        ShowIdleStatus();
    }

    private void InstallHooks()
    {
        keyboardProc = KeyboardCallback; mouseProc = MouseCallback; var module = GetModuleHandle(null);
        keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0); mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
        if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero) { RemoveHooks(); recording = false; MessageBox.Show("Windows could not start input recording.", "MacroForge"); }
    }

    private void RemoveHooks()
    {
        if (keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        keyboardProc = mouseProc = null;
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && recording)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLKHF_INJECTED) == 0 && data.vkCode is not (>= VK_F6 and <= VK_F10) and not VK_F12)
                events.Add(new MacroEvent { Type = "key", TimeMs = recordClock.ElapsedMilliseconds, Key = (int)data.vkCode, Down = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN });
        }
        return CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && recording)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam); if ((data.flags & LLMHF_INJECTED) != 0) return CallNextHookEx(mouseHook, code, wParam, lParam);
            var msg = wParam.ToInt32(); var now = recordClock.ElapsedMilliseconds;
            if (msg == WM_MOUSEMOVE) { if (now - lastMoveMs < 16 || Math.Abs(data.pt.X - lastMove.X) + Math.Abs(data.pt.Y - lastMove.Y) < 3) return CallNextHookEx(mouseHook, code, wParam, lParam); lastMoveMs = now; lastMove = data.pt; }
            var type = msg == WM_MOUSEMOVE ? "move" : msg == WM_MOUSEWHEEL ? "wheel" : "mouse";
            var value = msg == WM_MOUSEWHEEL ? (short)(data.mouseData >> 16) : msg;
            events.Add(new MacroEvent { Type = type, TimeMs = now, X = data.pt.X, Y = data.pt.Y, Data = value });
            if (events.Count % 10 == 0) BeginInvoke(new Action(() => recordStatus.Text = $"{events.Count} events"));
        }
        return CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    private static void Execute(MacroEvent e)
    {
        if (e.Type == "key") SendKey((ushort)e.Key, e.Down);
        else if (e.Type == "move") MoveMouse(e.X, e.Y);
        else if (e.Type == "wheel") { MoveMouse(e.X, e.Y); SendMouse(MOUSEEVENTF_WHEEL, e.Data); }
        else { MoveMouse(e.X, e.Y); var flag = e.Data switch { WM_LBUTTONDOWN => MOUSEEVENTF_LEFTDOWN, WM_LBUTTONUP => MOUSEEVENTF_LEFTUP, WM_RBUTTONDOWN => MOUSEEVENTF_RIGHTDOWN, WM_RBUTTONUP => MOUSEEVENTF_RIGHTUP, WM_MBUTTONDOWN => MOUSEEVENTF_MIDDLEDOWN, WM_MBUTTONUP => MOUSEEVENTF_MIDDLEUP, _ => 0u }; if (flag != 0) SendMouse(flag); }
    }

    private static void MoveMouse(int x, int y)
    {
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77); var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        var nx = (int)Math.Round((x - left) * 65535.0 / Math.Max(1, width - 1)); var ny = (int)Math.Round((y - top) * 65535.0 / Math.Max(1, height - 1));
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0, nx, ny);
    }

    private static void ClickMouse(string button)
    {
        var (down, up) = button switch { "Right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP), "Middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP), _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP) };
        SendMouse(down); SendMouse(up);
    }

    private static void SendMouse(uint flags, int data = 0, int dx = 0, int dy = 0)
    {
        var input = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort key, bool down)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = key, dwFlags = down ? 0 : KEYEVENTF_KEYUP } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private void RegisterHotkeys() { RegisterHotKey(Handle, 1, 0, VK_F6); RegisterHotKey(Handle, 2, 0, VK_F7); RegisterHotKey(Handle, 3, 0, VK_F8); RegisterHotKey(Handle, 4, 0, VK_F9); RegisterHotKey(Handle, 5, 0, VK_F10); RegisterHotKey(Handle, 6, 0, VK_F12); }
    private void UnregisterHotkeys() { for (var i = 1; i <= 6; i++) UnregisterHotKey(Handle, i); }
    protected override void WndProc(ref Message m) { if (m.Msg == WM_HOTKEY) { switch (m.WParam.ToInt32()) { case 1: StartAutoClicker(); break; case 2: StartKeyHold(); break; case 3: StartRecording(); break; case 4: StopRecording(); break; case 5: PlaySelected(); break; case 6: StopAll(); break; } } base.WndProc(ref m); }

    private void RefreshMacros() => RefreshMacros(null);
    private void RefreshMacros(string? select)
    {
        var names = Directory.GetFiles(macroDir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(x => x is not null).OrderBy(x => x).ToArray(); macroList.Items.Clear(); macroList.Items.AddRange(names!);
        if (macroList.Items.Count > 0) macroList.SelectedItem = select is not null && macroList.Items.Contains(select) ? select : macroList.Items[0];
    }
    private void DeleteSelected() { if (macroList.SelectedItem is null) return; var name = macroList.SelectedItem.ToString()!; if (MessageBox.Show($"Delete '{name}'?", "MacroForge", MessageBoxButtons.YesNo) == DialogResult.Yes) { File.Delete(Path.Combine(macroDir, name + ".json")); RefreshMacros(); } }
    private static string SafeName(string value) { var bad = Path.GetInvalidFileNameChars(); var result = new string(value.Trim().Where(c => !bad.Contains(c)).ToArray()); return string.IsNullOrWhiteSpace(result) ? "Untitled Macro" : result[..Math.Min(80, result.Length)]; }
    private static bool TryParseKey(string text, out Keys key) { var aliases = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase) { ["CTRL"] = Keys.ControlKey, ["CONTROL"] = Keys.ControlKey, ["SHIFT"] = Keys.ShiftKey, ["ALT"] = Keys.Menu, ["SPACE"] = Keys.Space, ["ENTER"] = Keys.Enter, ["TAB"] = Keys.Tab, ["ESC"] = Keys.Escape }; return aliases.TryGetValue(text.Trim(), out key) || Enum.TryParse(text.Trim(), true, out key); }
    private void ToggleAutomation(bool enabled, bool keepStopButtons = false) { foreach (var c in automationControls) { if (keepStopButtons && Equals(c.Tag, "stop")) continue; c.Enabled = enabled; } }
    private void ShowIdleStatus() { SetStatus("READY", Color.FromArgb(116, 224, 148)); }
    private void SetStatus(string text, Color color) { globalStatus.Text = text; globalStatus.ForeColor = color; }
    private void Shutdown() { StopAll(); RemoveHooks(); UnregisterHotkeys(); }

    private static TabPage MakeTab(string text) => new(text) { BackColor = Color.FromArgb(11, 11, 14), Padding = new Padding(10) };
    private static Panel MakeCard() => new() { Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0), Padding = new Padding(18), BackColor = Color.FromArgb(22, 22, 27) };
    private static void AddHeading(Control parent, string title, string subtitle) { var h = MakeLabel(title, 15, true, Color.White); h.Location = new Point(18, 18); h.AutoSize = true; var s = MakeLabel(subtitle); s.Location = new Point(18, 54); s.AutoSize = true; parent.Controls.Add(h); parent.Controls.Add(s); }
    private static Label MakeLabel(string text, float size = 10, bool bold = false, Color? color = null) => new() { Text = text, AutoSize = false, ForeColor = color ?? Color.Gainsboro, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
    private static TextBox MakeTextBox(string text) => new() { Text = text, BackColor = Color.FromArgb(33, 33, 39), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10), Height = 34 };
    private static NumericUpDown MakeNumber(decimal value, decimal min, decimal max) => new() { Value = value, Minimum = min, Maximum = max, BackColor = Color.FromArgb(33, 33, 39), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private static ComboBox MakeCombo(string[] items, int selected) { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(33, 33, 39), ForeColor = Color.White, FlatStyle = FlatStyle.Flat }; c.Items.AddRange(items); c.SelectedIndex = selected; return c; }
    private static Button MakeButton(string text, Action action, bool accent = false, bool danger = false) { var b = new Button { Text = text, AutoSize = true, Height = 38, Padding = new Padding(10, 4, 10, 4), FlatStyle = FlatStyle.Flat, BackColor = danger ? Color.FromArgb(125, 38, 49) : accent ? Color.WhiteSmoke : Color.FromArgb(45, 45, 53), ForeColor = accent ? Color.Black : Color.White, Cursor = Cursors.Hand, Tag = danger ? "stop" : null }; b.FlatAppearance.BorderSize = 0; b.Click += (_, _) => action(); return b; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; public static implicit operator Point(POINT p) => new(p.X, p.Y); }
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, int key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}

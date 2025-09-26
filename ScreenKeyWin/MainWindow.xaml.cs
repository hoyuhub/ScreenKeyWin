using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenKeyWin;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;

    private LowLevelKeyboardProc _proc;
    private IntPtr _hookID = IntPtr.Zero;

    private HashSet<string> _pressedModifiers = new HashSet<string>();
    private System.Windows.Threading.DispatcherTimer _hideTimer;
    private System.Windows.Threading.DispatcherTimer _topmostResetTimer;

    private string _lastHookChar = null;
    private DateTime _lastHookTime = DateTime.MinValue;
    private readonly TimeSpan _dedupThreshold = TimeSpan.FromMilliseconds(150);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += MainWindow_Loaded;
        this.Closing += MainWindow_Closing;
        this.Topmost = true;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 保留原大小和位置
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        this.Width = screenWidth;
        this.Height = 60;
        this.Left = 0;
        this.Top = screenHeight - this.Height - 100;

        // 初始隐藏，动画用
        this.Opacity = 0;
        this.Hide();

        if (!(this.RenderTransform is TranslateTransform))
            this.RenderTransform = new TranslateTransform(0, 0);

        // 安装钩子
        _proc = HookCallback;
        _hookID = SetHook(_proc);
        Debug.WriteLine($"[Debug] Hook handle = {_hookID}");

        _hideTimer = new System.Windows.Threading.DispatcherTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(10);
        _hideTimer.Tick += (s, ev) => HideWithAnimation();

        _topmostResetTimer = new System.Windows.Threading.DispatcherTimer();
        _topmostResetTimer.Interval = TimeSpan.FromMilliseconds(350);
        _topmostResetTimer.Tick += (s, ev) =>
        {
            this.Topmost = false;
            _topmostResetTimer.Stop();
        };

        this.PreviewTextInput += this.MainWindow_TextInput;
    }

    private IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using (var curProcess = Process.GetCurrentProcess())
        using (var curModule = curProcess.MainModule)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private bool IsModifier(string keyName) =>
        keyName == "Ctrl" || keyName == "Shift" || keyName == "Alt" || keyName == "Win";

    private string MapKeyName(string key) =>
        key switch
        {
            "LeftCtrl" or "RightCtrl" => "Ctrl",
            "LeftShift" or "RightShift" => "Shift",
            "LeftAlt" or "RightAlt" => "Alt",
            "LWin" or "RWin" => "Win",
            "Back" => "←",
            "Delete" or "Del" => "Delete",
            "Enter" => "↵",
            "Return" => "↵",
            "Space" => "␣",
            "OemQuestion"=> "?",
            _ => key.Length == 1 ? key.ToUpper() : key
        };

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vkCode = hookStruct.vkCode;
            string rawKey = KeyInterop.KeyFromVirtualKey(vkCode).ToString();
            string keyName = MapKeyName(rawKey);

            if (wParam == (IntPtr)WM_KEYDOWN)
            {
                if (IsModifier(keyName))
                {
                    _pressedModifiers.Add(keyName);
                }
                else
                {
                    var primaryMods = _pressedModifiers.Where(m => m != "Shift").ToList();
                    string combo;
                    if (primaryMods.Count > 0)
                    {
                        if (_pressedModifiers.Contains("Shift")) primaryMods.Add("Shift");
                        combo = string.Join("+", primaryMods) + "+" + keyName;
                    }
                    else combo = keyName;

                    ShowKey(combo);

                    if (keyName.Length == 1)
                    {
                        _lastHookChar = keyName;
                        _lastHookTime = DateTime.UtcNow;
                    }
                    else _lastHookChar = null;
                }
            }
            else if (wParam == (IntPtr)WM_KEYUP)
            {
                if (IsModifier(keyName)) _pressedModifiers.Remove(keyName);
            }
        }
        return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    private void MainWindow_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 1 && char.IsLetterOrDigit(e.Text[0]))
        {
            if (!string.IsNullOrEmpty(_lastHookChar)
                && string.Equals(e.Text, _lastHookChar, StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - _lastHookTime) < _dedupThreshold)
            {
                return;
            }
        }
        ShowKey(e.Text);
    }

    private void ShowKey(string text)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!(this.RenderTransform is TranslateTransform))
                this.RenderTransform = new TranslateTransform(0, 0);

            if (!this.IsVisible || this.Opacity <= 0.01)
            {
                ((TranslateTransform)this.RenderTransform).Y = 30;
                this.Opacity = 0;
                this.Show();
                this.Topmost = true;
                this.Activate();

                var opacityAnim = new DoubleAnimation(0, 0.95, TimeSpan.FromMilliseconds(200));
                var translateAnim = new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new BackEase { Amplitude = 0.15, EasingMode = EasingMode.EaseOut }
                };

                this.BeginAnimation(Window.OpacityProperty, opacityAnim);
                ((TranslateTransform)this.RenderTransform).BeginAnimation(TranslateTransform.YProperty, translateAnim);

                _topmostResetTimer.Stop();
                _topmostResetTimer.Start();
            }
            else if (this.Opacity < 0.95)
            {
                var quick = new DoubleAnimation(this.Opacity, 0.95, TimeSpan.FromMilliseconds(120));
                this.BeginAnimation(Window.OpacityProperty, quick);
            }

            KeyText.Text += text;

            _hideTimer.Stop();
            _hideTimer.Start();
        });
    }

    private void HideWithAnimation()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _hideTimer.Stop();

            if (this.IsLoaded && this.Opacity > 0)
            {
                var fadeOut = new DoubleAnimation(this.Opacity, 0, TimeSpan.FromMilliseconds(300));
                fadeOut.Completed += (s, e) =>
                {
                    this.Opacity = 0;
                    this.Hide();
                    this.KeyText.Text = string.Empty;
                    this.Topmost = false;
                };
                this.BeginAnimation(Window.OpacityProperty, fadeOut);
            }
        });
    }

    private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_hookID != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookID);
            _hookID = IntPtr.Zero;
        }
    }
}
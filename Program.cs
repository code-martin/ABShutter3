#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq; // Added for LINQ
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

class Program
{
    // --- Configuration & State ---
    static readonly string IniPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ABShutter3", "keymap.ini");
    static Dictionary<string, Dictionary<Keys, Keys>> maps = new();
    static HashSet<Keys> pressedOriginalKeys = new HashSet<Keys>();
    static volatile bool isSending = false;
    static volatile bool isConnected = false;
    static string targetDeviceName = "AB Shutter3"; // Default, updated from INI or Train arg
    static string? targetDeviceId = null;
    static BluetoothLEDevice? bleDevice = null;
    static DeviceWatcher? watcher = null;
    static IntPtr hookId = IntPtr.Zero;
    static NativeMethods.LowLevelKeyboardProc? hookDelegate; // Keep delegate alive

    // --- P/Invoke & Constants Grouped ---
    static class NativeMethods
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const uint KEYEVENTF_KEYDOWN = 0x0000;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        public static extern int GetPrivateProfileString(string? s, string? k, string d, StringBuilder r, int sz, string f);
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        public static extern long WritePrivateProfileString(string s, string k, string v, string f);
    }

    static void Log(string msg) => Console.WriteLine($"{DateTime.Now:T} {msg}"); // Concise Log

    // --- INI Handling ---
    static void LoadMapsFromIni()
    {
        maps.Clear();
        Log($"INI: {Path.GetFullPath(IniPath)}");
        StringBuilder sectionsBuffer = new StringBuilder(2048);
        NativeMethods.GetPrivateProfileString(null, null, "", sectionsBuffer, sectionsBuffer.Capacity, IniPath);
        string[] sections = sectionsBuffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries);

        bool isFirstSection = true;
        foreach (var section in sections.Where(s => !string.IsNullOrWhiteSpace(s) && !"DEFAULT".Equals(s, StringComparison.OrdinalIgnoreCase)))
        {
            if (isFirstSection) {
                targetDeviceName = section; // Assume first section is the target
                Log($"Target set from INI: [{targetDeviceName}]");
                isFirstSection = false;
            }
            Log($"Loading map: [{section}]");
            var map = new Dictionary<Keys, Keys>();
            var keysBuffer = new StringBuilder(4096); // Reduced capacity slightly
            NativeMethods.GetPrivateProfileString(section, null, "", keysBuffer, keysBuffer.Capacity, IniPath);
            foreach (var keyStr in keysBuffer.ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var valBuffer = new StringBuilder(256);
                NativeMethods.GetPrivateProfileString(section, keyStr, "", valBuffer, valBuffer.Capacity, IniPath);
                if (Enum.TryParse<Keys>(keyStr, true, out var fromKey) && Enum.TryParse<Keys>(valBuffer.ToString(), true, out var toKey))
                {
                     if (!map.TryAdd(fromKey, toKey)) Log($"Warn: Duplicate key '{fromKey}' in [{section}]");
                } else Log($"Warn: Cannot parse [{section}] '{keyStr}'='{valBuffer}'");
            }
            maps[section] = map;
        }
        if (maps.Count == 0) Log("Warn: No maps loaded from INI.");
        else if (!maps.ContainsKey(targetDeviceName)) Log($"Warn: No map found for target '{targetDeviceName}'.");
    }

    // --- Bluetooth Monitoring ---
    static async Task StartBluetoothMonitoringAsync()
    {
        Log("Starting BTLE watcher...");
        watcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelector());

        watcher.Added += async (s, di) => {
            if (di.Name.Equals(targetDeviceName, StringComparison.OrdinalIgnoreCase)) {
                 Log($"Found: {di.Name} ({di.Id})");
                 targetDeviceId = di.Id;
                 await ConnectDeviceAsync(di.Id);
            }
        };
        watcher.Removed += async (s, diu) => {
             if (targetDeviceId != null && diu.Id == targetDeviceId) {
                 Log($"Removed: {targetDeviceName}");
                 await CleanupBluetoothDeviceAsync();
                 targetDeviceId = null;
             }
        };
        watcher.EnumerationCompleted += (s, o) => Log("Watcher enum complete.");
        watcher.Stopped += (s, o) => Log("Watcher stopped.");

        watcher.Start();
        Log("Watcher started.");

        // Try initial connection
        var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromDeviceName(targetDeviceName));
        if (devices.Any()) { // Use LINQ Any()
             targetDeviceId = devices[0].Id;
             Log($"Initial find: {devices[0].Name} ({targetDeviceId}). Connecting...");
             await ConnectDeviceAsync(targetDeviceId);
        } else Log($"Initial find: '{targetDeviceName}' not found.");
    }

    static async Task ConnectDeviceAsync(string deviceId)
    {
        if (bleDevice != null && isConnected) return; // Already connected
        Log($"Connecting to {deviceId}");
        await CleanupBluetoothDeviceAsync(false); // Clean up silently first

        try {
            bleDevice = await BluetoothLEDevice.FromIdAsync(deviceId);
            if (bleDevice != null) {
                 bleDevice.ConnectionStatusChanged += Device_ConnectionStatusChanged;
                 Log($"Device object obtained for {bleDevice.Name}. Status: {bleDevice.ConnectionStatus}");
                 UpdateConnectionStatus(bleDevice.ConnectionStatus); // Initial status update
            } else {
                 Log($"Failed to get BLE device for {deviceId}.");
                 UpdateConnectionStatus(BluetoothConnectionStatus.Disconnected);
            }
        } catch (Exception ex) {
            Log($"Connect error: {ex.Message}");
            await CleanupBluetoothDeviceAsync(false); // Clean up on error
        }
    }

    static void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
         Log($"BT Status Event: {sender?.ConnectionStatus ?? BluetoothConnectionStatus.Disconnected}");
         UpdateConnectionStatus(sender?.ConnectionStatus ?? BluetoothConnectionStatus.Disconnected);
    }

    static void UpdateConnectionStatus(BluetoothConnectionStatus status)
    {
        bool nowConnected = (status == BluetoothConnectionStatus.Connected);
        if (isConnected != nowConnected) {
            isConnected = nowConnected;
            Log($"Connection state CHANGED -> {status}");
            // No activeDevices list needed now, hook uses 'isConnected' directly
        }
    }

    static async Task CleanupBluetoothDeviceAsync(bool log = true)
    {
        if (bleDevice != null) {
            if(log) Log($"Cleaning up BT device: {bleDevice.DeviceId}");
            bleDevice.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
            bleDevice.Dispose();
            bleDevice = null;
        }
        UpdateConnectionStatus(BluetoothConnectionStatus.Disconnected); // Ensure state is disconnected
        await Task.CompletedTask; // Added to match original async signature if needed elsewhere, trivial cost
    }

    // --- Training Mode ---
    static void Train(string deviceName)
    {
        targetDeviceName = deviceName; // Use arg for training context
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!); // Ensure dir exists

        Log($"--- Training Mode: [{deviceName}] ---");
        Log($"Ensure device is ON and PAIRED.");
        Log($"INI: {Path.GetFullPath(IniPath)}");
        Console.WriteLine($"Press REMOTE key, then KEYBOARD target key. ESC on keyboard to save/exit.");

        var learnedMap = maps.TryGetValue(deviceName, out var existingMap) ? existingMap : new Dictionary<Keys, Keys>();
        Keys? firstKey = null;

        NativeMethods.LowLevelKeyboardProc trainCallback = (nCode, wParam, lParam) => {
            if (nCode < 0) return NativeMethods.CallNextHookEx(hookId, nCode, wParam, lParam);

            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            Keys key = (Keys)kb.vkCode;
            bool isDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;

            if (!isDown) return NativeMethods.CallNextHookEx(hookId, nCode, wParam, lParam); // Only process key down

            if (key == Keys.Escape) {
                Log("ESC pressed, ending training.");
                Application.Exit();
                return (IntPtr)1;
            }

            // Modifier key check simplified
            if (key >= Keys.LShiftKey && key <= Keys.RMenu || key == Keys.LWin || key == Keys.RWin) {
                 if (firstKey != null) { // If it's the second key (target key)
                      Console.WriteLine($"\nERROR: Modifier key ({key}) cannot be a target. Start over.");
                      firstKey = null; // Reset
                      Console.WriteLine("\nPress next button on REMOTE or ESC to exit...");
                      return (IntPtr)1; // Suppress modifier as target
                 }
                 // Allow modifiers as the *first* key (from remote) if necessary, though unlikely
            }


            Log($"Train KeyDown: {key}");

            if (firstKey == null) {
                firstKey = key;
                Console.WriteLine($"\nINPUT> {firstKey}. Press TARGET keyboard key...");
            } else {
                Console.WriteLine($"MAP>   {firstKey} -> {key}");
                NativeMethods.WritePrivateProfileString(deviceName, firstKey.ToString(), key.ToString(), IniPath);
                learnedMap[firstKey.Value] = key; // Update local map view
                firstKey = null; // Reset for next pair
                Console.WriteLine("\nPress next button on REMOTE or ESC to exit...");
            }
            return (IntPtr)1; // Suppress key during training
        };

        hookDelegate = trainCallback;
        hookId = SetupHook(hookDelegate);
        if (hookId == IntPtr.Zero) { Log("FATAL: Failed to set training hook."); return; }

        Application.ApplicationExit += (s, e) => {
            Log("Training finished.");
            if (hookId != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hookId);
            Console.WriteLine("\n--- Mappings in INI ---");
            try { Console.WriteLine(File.ReadAllText(IniPath)); } catch { Console.WriteLine("(INI not found/readable)");}
            Console.WriteLine("----------------------");
        };
        Application.Run();
    }

    // --- Hook Setup & Callback ---
    static IntPtr SetupHook(NativeMethods.LowLevelKeyboardProc proc)
    {
        Log("Setting hook...");
        using var p = Process.GetCurrentProcess();
        using var m = p.MainModule;
        IntPtr hMod = (m != null) ? NativeMethods.GetModuleHandle(m.ModuleName) : IntPtr.Zero;
        if (hMod == IntPtr.Zero) { Log($"ERR: GetModuleHandle failed: {Marshal.GetLastWin32Error()}"); return IntPtr.Zero; }
        IntPtr hHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, proc, hMod, 0);
        if (hHook == IntPtr.Zero) Log($"ERR: SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        else Log("Hook set OK.");
        return hHook;
    }

    static IntPtr MainHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || isSending) return NativeMethods.CallNextHookEx(hookId, nCode, wParam, lParam);

        bool remap = false;
        Keys targetKey = Keys.None;
        var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        Keys originalKey = (Keys)kb.vkCode;

        // *** Core Logic: Check connection AND if a map exists/matches ***
        if (isConnected && maps.TryGetValue(targetDeviceName, out var deviceMap) && deviceMap.TryGetValue(originalKey, out targetKey))
        {
            remap = true;
        }

        bool isKeyDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp = wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP;

        if (remap)
        {
            if (isKeyDown)
            {
                if (!pressedOriginalKeys.Add(originalKey)) return (IntPtr)1; // Suppress repeat if already added
                Log($"Remap DN {originalKey} -> {targetKey}");
                try {
                    isSending = true;
                    NativeMethods.keybd_event((byte)targetKey, 0, NativeMethods.KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                    NativeMethods.keybd_event((byte)targetKey, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero); // Auto up+down
                } finally { isSending = false; }
                return (IntPtr)1; // Suppress original down
            }
            else if (isKeyUp)
            {
                 if (pressedOriginalKeys.Remove(originalKey)) return (IntPtr)1; // Suppress original up if we handled the down
            }
        }
        // Pass through if not connected, no map, or no match
        return NativeMethods.CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    // --- Main Entry ---
    [STAThread]
    static async Task Main(string[] args)
    {
        if (args.Length == 1 && !string.IsNullOrWhiteSpace(args[0])) {
            Train(args[0]); // Blocking train mode
            return;
        }
        if (args.Length > 0) { // Basic usage hint
             Console.WriteLine($"Usage: {AppDomain.CurrentDomain.FriendlyName} [\"Device Name for Training\"]");
             return;
        }

        Log("Starting ABShutter Remapper (Minimal)");
        LoadMapsFromIni(); // Load maps, potentially sets targetDeviceName

        // Start monitoring (fire and forget, runs in background)
        _ = StartBluetoothMonitoringAsync(); // Discard task result, it runs independently

        // Set main hook
        hookDelegate = MainHookCallback;
        hookId = SetupHook(hookDelegate);

        if (hookId == IntPtr.Zero) {
            Log("FATAL: Failed to set main hook. Exiting.");
            return;
        }

        Log("Remapper active. Close window or Ctrl+C to exit.");
        Console.CancelKeyPress += async (s, e) => { // Graceful Ctrl+C
             e.Cancel = true; // Prevent immediate termination
             Log("Ctrl+C detected, shutting down...");
             Application.Exit(); // Trigger Application.Run() to exit
             await CleanupAndExit();
        };

        // Keep app alive for hooks and WinRT events
        Application.Run();

        // Cleanup happens after Application.Run() exits
        await CleanupAndExit();
    }

    static async Task CleanupAndExit()
    {
         Log("Cleaning up...");
         watcher?.Stop(); // Stop watcher if running
         await CleanupBluetoothDeviceAsync(); // Clean up BT object
         if (hookId != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hookId);
         Log("Cleanup complete. Exiting.");
    }
}
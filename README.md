# AB Shutter 3 - Bluetooth Key Remapper for Windows

A lightweight Windows console application to remap the buttons of common "AB Shutter 3" Bluetooth remotes (and similar clones) to standard keyboard key presses.

<img src="https://github.com/user-attachments/assets/f72a8332-f546-4f41-91da-3cdbc102f9ab" alt="AB Shutter 3" width="300">


## Problem Solved

Cheap Bluetooth camera shutter remotes like the "AB Shutter 3" are widely available. However, on Windows, they typically only send basic commands like `Volume Up`, making them less useful as general-purpose input devices. This application allows you to intercept these inputs and translate them into *any* desired keyboard key press (e.g., Enter, Page Down, F5, letters, numbers, etc.), effectively turning the remote into a simple, customizable macro pad or presentation clicker.

## ✨ Features

*   **Remap Buttons:** Translate the physical button presses on the AB Shutter 3 remote to different keyboard keys.
*   **Automatic Detection:** Uses the modern Windows Runtime (WinRT) Bluetooth LE APIs to automatically detect when the paired remote connects or disconnects. Remapping is only active when the device is truly connected.
*   **Easy Configuration:** Uses a simple `.ini` file (`keymap.ini`) to store your desired mappings.
*   **Interactive Training Mode:** A built-in mode guides you through pressing buttons on the remote and the desired target keys on your keyboard to automatically generate the `keymap.ini` file. No manual editing required!
*   **Low-Level Hook:** Uses a Windows low-level keyboard hook (`WH_KEYBOARD_LL`) to intercept the original key presses globally and reliably.
*   **Simulated Keystrokes:** Sends standard keyboard events (`keybd_event`) that work in most applications.
*   **Minimalist:** Runs as a simple console application with logging output.

## Prerequisites

*   **Operating System:** Windows 10 or Windows 11 (Required for WinRT Bluetooth LE APIs).
*   **.NET Runtime:** .NET 6.0 Runtime (or newer, depending on the build target). The release executable might be self-contained or require the runtime installation.
*   **Bluetooth:** A working Bluetooth adapter supported by Windows.
*   **AB Shutter 3 Remote:** The physical remote device, successfully **paired** with your Windows PC.

## Installation

1.  **Download:** Go to the [Releases](https://github.com/YourUsername/YourRepoName/releases) page and download the latest `ABShutter3Remapper.exe` (or similar) file.
2.  **Place:** Put the downloaded executable file anywhere you like (e.g., `C:\Tools\ABShutterRemapper\`).

*(Alternatively, if you want to build from source):*
1.  Clone this repository.
2.  Open the solution in Visual Studio or use the .NET CLI.
3.  Build the project (e.g., `dotnet build -c Release`). The executable will be in a subfolder like `bin\Release\net6.0\`.

## Configuration (Training Mode) - **Important!**

Before you can use the remapper, you need to tell it which remote button should trigger which keyboard key. You do this using the **Training Mode**.

1.  **Find Device Name:** Go to Windows Settings -> Bluetooth & devices -> Devices. Find your paired "AB Shutter 3" remote and note its **exact name** (e.g., "AB Shutter3", "Camera Remote", etc.). Case sensitivity might matter.
2.  **Turn On Remote:** Make sure your remote button ("AB Shutter 3") is turned on and connected (usually indicated by a blinking or solid LED stopping).
3.  **Run Training:**
    *   Open Command Prompt (`cmd`) or PowerShell **in the directory where you placed the executable**.
    *   Run the application with the device name as a command-line argument, enclosed in quotes if it contains spaces:
        ```bash
        .\ABShutter3.exe "AB Shutter3"
        ```
        (Replace `"AB Shutter3"` with the exact name you found in step 1).
4.  **Follow Prompts:** The console will guide you:
    *   It will ask you to press a button on the **REMOTE**.
    *   Then, it will ask you to press the desired **TARGET** key on your **KEYBOARD**. (Do **NOT** use modifier keys like Ctrl, Shift, Alt, Win as *target* keys).
    *   Repeat for any other buttons on the remote you want to map.
5.  **Exit Training:** Press the `ESC` key on your **KEYBOARD** to save the mappings and exit the training mode.

This process creates (or updates) a configuration file named `keymap.ini` located in `%APPDATA%\ABShutter3\`.

**Example `keymap.ini`:**

```ini
[AB Shutter3]
VolumeUp=Right  ; Maps the remote's usual 'Volume Up' button to Next Page (PowerPoint)
```

## Notes

This application was created via an iterative process of design, bug fixing, and tuning, assisted extensively by Google's Gemini 2.5 Pro.

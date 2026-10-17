# HandsFree

A Windows-first, local-first foundation for a hands-free desktop application, built with Avalonia UI and .NET 10. The shell provides a manual test panel for launching an executable and issuing one-shot cursor, click, and scroll actions. Its opt-in keyboard scroll mode captures a target window and posts one wheel message per shortcut press. It has no hand tracking, speech recognition, AI, or screen understanding.

## Prerequisites

- .NET 10 SDK
- Windows for the primary desktop experience. Avalonia keeps the UI portable to Linux and macOS.
- Internet access for the first restore of Avalonia NuGet packages. The app has no runtime service or AI model requirement.

## Build and run

From this directory:

```powershell
dotnet restore HandsFree.sln
dotnet build HandsFree.sln --no-restore
dotnet run --project tests/HandsFree.Core.Tests
dotnet run --project src/HandsFree.App
```

## Structure

- `src/HandsFree.App` — Avalonia desktop shell and app startup.
- `src/HandsFree.Core` — platform-independent interaction contracts, validation, and central control engine.
- `src/HandsFree.Windows` — Windows adapter for process launching, cursor movement, click, targeted wheel messages, target capture, shortcut parsing, and global-hotkey lifecycle.
- `tests/HandsFree.Core.Tests` — dependency-free checks for Core validation/action routing, target state, and shortcut mapping/lifecycle. Tests use fakes and never generate real input.

## Development principles

- Run locally without an internet connection or AI model after package restore.
- Keep optional AI on-demand, with no model download or loading at startup.
- Keep resource use low; do not add background services without a concrete feature need.
- Put platform-independent validation and routing in Core and platform APIs in the Windows adapter.
- Keep computer-control actions explicit and single-shot; no automatic input loop or startup behavior.
- Add dependencies only when a feature requires them.

## Targeted keyboard scroll test mode

The mode starts disabled. Enable it, choose **Capture Target Window**, move the pointer over the target application's scrollable content, then press the capture shortcut. Defaults are `Ctrl+Alt+F9` to capture, `Ctrl+Alt+F10` to scroll down, and `Ctrl+Alt+F11` to scroll up. The captured target's process name, title, and PID are shown. Use **Recapture Target Window** to select another child window or **Clear Target** to remove the saved target. Windows reserves F12, and all three shortcuts must be distinct.

The Windows adapter records the child HWND under the pointer and its top-level owner. A scroll action posts one `WM_MOUSEWHEEL` message to that captured child; the cursor and focus are not changed. The UI reports that the message was posted, not that scrolling was visible. Applications may handle posted wheel messages differently from physical input; this behavior needs manual verification in each target app. If the target closes or its handle becomes invalid, the adapter clears it and requires recapture.

The Windows adapter uses `RegisterHotKey`/`UnregisterHotKey` on a dedicated message thread and `MOD_NOREPEAT`. It does not install a keyboard hook or poll in the background. Closing the app unregisters the capture and scroll shortcuts.

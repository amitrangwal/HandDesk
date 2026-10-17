# HandsFree

A Windows-first, local-first foundation for a hands-free desktop application, built with Avalonia UI and .NET 10. The shell provides a manual test panel for launching an executable and issuing one-shot cursor, click, and scroll actions. It has no hand tracking, speech recognition, AI, or screen understanding.

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
- `src/HandsFree.Windows` — Windows adapter for process launching, cursor movement, click, and scroll.
- `tests/HandsFree.Core.Tests` — dependency-free automated checks for Core validation and action routing. Tests use a fake platform adapter and never generate real input.

## Development principles

- Run locally without an internet connection or AI model after package restore.
- Keep optional AI on-demand, with no model download or loading at startup.
- Keep resource use low; do not add background services without a concrete feature need.
- Put platform-independent validation and routing in Core and platform APIs in the Windows adapter.
- Keep computer-control actions explicit and single-shot; no automatic input loop or startup behavior.
- Add dependencies only when a feature requires them.

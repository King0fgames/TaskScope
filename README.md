# TaskScope

A faster, more informative replacement for Windows Task Manager, built to stop memory-hungry jobs from taking the whole PC down.

Windows can run out of **commit** (memory that programs have reserved, backed by RAM + the page file) long before RAM looks full. When it does, programs and drivers crash. Task Manager barely shows this, so TaskScope puts it front and center and gives you ways to act before it's too late.

![TaskScope main window: usage tiles with history, process list grouped by app, and the details pane for the selected app](docs/screenshot.png)

## Features

**Memory safety**
- **Commit tile**: committed memory vs. the commit limit, with history. It turns red, shows a banner listing the biggest processes, and sends a Windows notification when free commit drops below a threshold you choose (default 16 GB).
- **Pause (suspend) / Resume**: freeze a runaway process so it stops growing without losing its work, then resume it when there's room.
- **Memory watches**: rules like *"if `java.exe` goes over 20 GB, notify me / pause it / end it."* Create one from the right-click menu (**Watch memory…**) or in Settings.

**Detail panels and graphs** (click any tile)
- **CPU**: *Overall*, *Cores* (physical cores, marked **P**/**E** on hybrid Intel CPUs), or *Threads* (one graph per logical processor). If the selected process is limited to certain CPUs (affinity), those CPUs are outlined.
- **GPU**: *Overall*, or *Per GPU* with each adapter's name, usage and dedicated memory (used / total).
- **Disks**: one card per physical disk with active time, read and write speed.
- **Memory / Commit**: a large graph plus the top apps right now.
- **Stacked graphs** toggle: every graph becomes a stacked, color-coded chart. Tile and large graphs are split by the top apps (with "Other" in gray), CPU core/thread graphs by user vs. kernel time, disk graphs by reads vs. writes, and per-GPU graphs by app, with the same colors as the GPU legend.

**Better process list**
- Every process is read in a single `NtQuerySystemInformation` call, so updates are cheap even with hundreds of processes.
- Group by app, with expandable groups, or a flat list.
- **Cores** column: CPU use expressed in cores (2.5 = as busy as two and a half logical processors). The details pane adds how many of the process's threads are actually busy and which CPUs it may run on.
- Columns: CPU, committed memory, RAM (working set), disk I/O, GPU %, GPU memory, threads, handles, and a **Suspended** status (detected from thread wait states).
- Search by name, PID, path, **or command line**, so you can tell your `python.exe` processes apart.
- A details pane with path, full command line, parent process, start time, priority, and 2-minute CPU and memory graphs.
- Heat shading on busy cells, and live tiles for CPU, RAM, commit, GPU and disk.

**Actions**
- End task, End process tree, Pause/Resume, Set priority, Open file location, and copy name/PID/path/command line.
- Warns before you end or pause critical Windows processes.
- Protects against PID reuse: every action checks the process creation time first.
- **Run as admin** button for controlling elevated and system processes.
- Tray icon with a live summary tooltip; alerts appear as Windows notifications.

## Keyboard

| Key | Action |
| --- | --- |
| `Ctrl+F` | Search |
| `Esc` | Clear search |
| `Del` | End selected process(es) |
| `Space` | Freeze/unfreeze the list (graphs keep running) |
| `→` / `←` | Expand / collapse a group |

## Requirements

- Windows 10 or 11, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (to run) or the .NET 10 SDK (to build)

## Build and run

```bash
dotnet build -c Release
```

```bash
dotnet run -c Release
```

To produce a single `TaskScope.exe`:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Settings

Settings (warning threshold, update speed, memory watches, window position) are stored in `%AppData%\TaskScope\settings.json`.

## How it works

| File | What it does |
| --- | --- |
| `Sampler.cs` | Process snapshot via `NtQuerySystemInformation(SystemProcessInformation)` (including per-thread CPU time for busy-thread counts), per-processor CPU (`SystemProcessorPerformanceInformation`), core topology and P/E cores (`GetLogicalProcessorInformationEx`), GPU usage per process and per adapter (PDH `GPU Engine` / `GPU Process Memory` / `GPU Adapter Memory`, names from DXGI), per-disk I/O (PDH `PhysicalDisk`) |
| `Charts.cs` | Graph control (line or stacked), per-app stacked history, chart palette |
| `MainWindow.Panel.cs` | Detail panel: CPU cores/threads, per-GPU, per-disk, legends, affinity |
| `Models.cs` | Row view models (process and app group), formatting, heat shading |
| `Actions.cs` | End, suspend/resume (`NtSuspendProcess`), and priority, all guarded against PID reuse |
| `MainWindow.xaml(.cs)` | UI, list reconciliation (keeps selection and scroll position across updates), alerts, tray |
| `SettingsWindow.xaml(.cs)` | Threshold, update speed, memory watch rules |
| `Ui.cs` | Sparkline graph control and icon helpers |

The UI uses WPF's Fluent theme and follows the Windows light/dark setting.

## License

[MIT](LICENSE)

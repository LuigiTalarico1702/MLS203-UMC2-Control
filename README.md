# MLS203 / UMC2 Control

Windows WPF application for commissioning and basic operation of a Thorlabs
MLS203 XY stage through a UMC2 controller over USB or Ethernet, using the
Thorlabs XA SDK.

## Requirements

- Windows 10/11 x64
- Visual Studio 2022 with **.NET desktop development** and .NET Framework 4.8
- Thorlabs XA Software/SDK installed
- UMC2 with MLS203 connected to brushless channels 1 and 2
- USB connection or a PC Ethernet adapter configured in the same IPv4 subnet
  as the UMC2

## Build

1. Close the XA GUI; only one application should own the controller connection.
2. From Windows PowerShell, run `powershell -ExecutionPolicy Bypass -File .\setup-xa.ps1`
   in this folder. If XA is in a non-standard folder, append
   `-XaPath 'C:\path\to\XA'`.
3. Open `MLS203-UMC2-Control.sln` in Visual Studio.
4. Select the **x64** configuration and build.

The setup script copies `tlmc_xa_dotnet.dll` and `tlmc_xa_native.dll` from the
local XA installation. Thorlabs binaries are intentionally not distributed in
this project, and both DLLs must come from the same XA installation/version.

## First Ethernet commissioning

1. Using the USB service connection and XA, configure or verify the UMC2 IPv4
   address, subnet mask and TCP port; then save/apply the controller settings.
2. Connect UMC2 Ethernet directly or through the intended switch. Configure the
   PC NIC in the same subnet and verify the controller responds to `ping`.
3. Close XA. For an unambiguous test, disconnect the USB service cable.
4. Start this application. Enter the controller IP as both **First IP** and
   **Last IP**. The default HCCP port is `40303`; use the value configured in XA.
5. Select the UMC2 base unit (or a child channel), click **Connect**, clear the
   complete XY travel area, and execute **Home X + Y**.

## USB connection

1. Close the Thorlabs XA GUI so it does not own the controller connection.
2. Connect and power the UMC2, then connect its USB cable to the PC.
3. Start this application and click **USB**.
4. Select the UMC2 base unit (or a child channel) and click **Connect**.

## Implemented functions

- XA Ethernet discovery through `TLMC_DiscoverEthernetDeviceInfo`
- XA USB discovery through `SystemManager.GetDeviceList`
- Opening the `Umcx` base unit and two `UmcxBrushlessLogicalChannel` channels
- Enable, sequential X/Y homing, live position/status polling, absolute XY move,
  relative jog
- Immediate stop request on both axes
- Software travel checks: X 0–110 mm and Y 0–75 mm

## XA API mapping

| Operation | XA API used |
| --- | --- |
| SDK lifecycle | `SystemManager.Create`, `Startup`, `Shutdown` |
| Ethernet discovery | Native `TLMC_DiscoverEthernetDeviceInfo` |
| USB discovery | `SystemManager.GetDeviceList` |
| Open UMC2 | `TryOpenDevice(..., Umcx)` |
| Open X/Y axes | `TryOpenDevice(..., UmcxBrushlessLogicalChannel)` for channels 1 and 2 |
| Enable / reference | `SetEnableState`, `Home` |
| Unit conversion | `FromPhysicalToDeviceUnit`, `FromDeviceUnitToPhysical` |
| Motion | `Move` with absolute or relative mode |
| Feedback / status | `GetUmcStatus`, `FromDeviceUnitToPhysical` |
| Stop | Immediate `Stop` on both logical channels |

The implementation follows the official UMCx .NET example tested by Thorlabs
with XA 1.6.8. The relative-move enum is resolved by name because XA releases
have exposed it as either `RelativeMove` or `Relative`.

## Safety and integration notes

- The software limits are an additional check, not a safety function. Hardware
  limits, emergency stopping and guarding remain external responsibilities.
- Confirm channel 1 = X and channel 2 = Y. If the cabling differs, update the
  mapping in `Umc2StageController.Connect` before motion tests.
- Absolute and jog controls stay disabled until this application completes homing.
- The current XY move starts independent channel commands concurrently; it is not
  a coordinated/interpolated path. Add XA synchronized/path movement if the process
  requires straight-line trajectories or raster scanning.

## API sources

- [Thorlabs XA software and SDK download](https://www.thorlabs.com/software_pages/ViewSoftwarePage.cfm?Code=Motion_Control)
- Thorlabs XA User Guide, DOC-101911 (installed with XA)
- [Official Thorlabs `Motion_Control_Examples`, C#/XA/UMCx](https://github.com/Thorlabs/Motion_Control_Examples/tree/main/C%23/XA/UMCx)
- XA native API shipped with the installed XA SDK

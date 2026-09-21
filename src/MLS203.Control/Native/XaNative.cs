using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Mls203.Control.Core;

namespace Mls203.Control.Native
{
    internal sealed class XaResultInfo
    {
        public int Code { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
    }

    internal sealed class XaDiscoveryResult
    {
        public XaResultInfo Result { get; set; }
        public IReadOnlyList<DiscoveredDevice> Devices { get; set; }
    }

    internal static class XaNative
    {
        private const string NativeDll = "tlmc_xa_native.dll";

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        private struct DeviceInfo
        {
            public byte DeviceFamily;
            public uint DeviceType;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
            public string PartNumber;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string Device;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Transport;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string ParentDevice;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string DeviceTypeDescription;
        }

        [DllImport(NativeDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int TLMC_Startup(IntPtr settings);

        [DllImport(NativeDll, CallingConvention = CallingConvention.StdCall)]
        private static extern int TLMC_Shutdown();

        [DllImport(NativeDll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int TLMC_DiscoverEthernetDeviceInfo(
            [MarshalAs(UnmanagedType.LPStr)] string firstHost,
            [MarshalAs(UnmanagedType.LPStr)] string lastHost,
            int portNumber,
            uint openTimeoutMicroseconds,
            IntPtr deviceInfoArray,
            ushort deviceInfoArrayLength,
            out ushort numberOfItemsDiscovered);

        public static XaDiscoveryResult Discover(
            string firstHost,
            string lastHost,
            int port,
            TimeSpan perHostTimeout)
        {
            const ushort capacity = 64;
            const int success = 0;
            const int alreadyStarted = 7;
            int startupResult = TLMC_Startup(IntPtr.Zero);
            if (startupResult != success && startupResult != alreadyStarted)
            {
                XaResultInfo startupInfo = DescribeResultCode(startupResult);
                throw new InvalidOperationException("XA native startup failed: " + FormatResult(startupInfo));
            }

            bool ownsStartup = startupResult == success;
            int itemSize = Marshal.SizeOf(typeof(DeviceInfo));
            IntPtr buffer = Marshal.AllocHGlobal(itemSize * capacity);

            try
            {
                for (int i = 0; i < itemSize * capacity; i++)
                    Marshal.WriteByte(buffer, i, 0);

                double requestedMicroseconds = perHostTimeout.TotalMilliseconds * 1000.0;
                uint timeoutMicroseconds = (uint)Math.Max(1.0, Math.Min(uint.MaxValue, requestedMicroseconds));

                ushort found;
                int result = TLMC_DiscoverEthernetDeviceInfo(
                    firstHost,
                    lastHost,
                    port,
                    timeoutMicroseconds,
                    buffer,
                    capacity,
                    out found);

                XaResultInfo resultInfo = DescribeResultCode(result);
                if (result != success)
                {
                    return new XaDiscoveryResult
                    {
                        Result = resultInfo,
                        Devices = new List<DiscoveredDevice>()
                    };
                }

                var devices = new List<DiscoveredDevice>(found);
                int count = Math.Min(found, capacity);
                for (int i = 0; i < count; i++)
                {
                    IntPtr address = IntPtr.Add(buffer, i * itemSize);
                    var native = (DeviceInfo)Marshal.PtrToStructure(address, typeof(DeviceInfo));
                    devices.Add(new DiscoveredDevice
                    {
                        DeviceId = native.Device ?? string.Empty,
                        PartNumber = native.PartNumber ?? string.Empty,
                        Description = native.DeviceTypeDescription ?? string.Empty,
                        ParentDevice = native.ParentDevice ?? string.Empty,
                        Transport = native.Transport ?? string.Empty
                    });
                }

                return new XaDiscoveryResult
                {
                    Result = resultInfo,
                    Devices = devices
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                if (ownsStartup)
                    TLMC_Shutdown();
            }
        }

        public static string FormatResult(XaResultInfo result)
        {
            return result.Code + " (" + result.Name + "): " + result.Description;
        }

        private static XaResultInfo DescribeResultCode(int code)
        {
            switch (code)
            {
                case 0: return Result(code, "TLMC_Success", "The requested operation completed successfully.");
                case 1: return Result(code, "TLMC_FunctionNotSupported", "The requested function is not available in the current context.");
                case 2: return Result(code, "TLMC_DeviceNotFound", "The requested device could not be found. Check the device is plugged in securely and is switched on.");
                case 3: return Result(code, "TLMC_DeviceNotSupported", "The requested device was found, but is not supported by this version of XA.");
                case 4: return Result(code, "TLMC_Timeout", "The requested operation exceeeded the maximum wait period allowed for completion.");
                case 5: return Result(code, "TLMC_Fail", "The requested operation was unable to complete. As far as possible, the system will have returned to the state it was in prior to the call that failed.");
                case 6: return Result(code, "TLMC_InsufficientFirmware", "The requested operation is not available on the due to insufficient firmware being installed on the device. Ensure the device is using the latest firmware available and try again.");
                case 7: return Result(code, "TLMC_AlreadyStarted", "The XA system has already been started. Ensure each call to TLMC_Startup has a corresponding call to TLMC_Shutdown.");
                case 8: return Result(code, "TLMC_StartRequired", "The XA system hasn't been started. A call to TLMC_Startup is required.");
                case 9: return Result(code, "TLMC_AllocationError", "A resource allocation error was encountered that resulted in the operation not completing.");
                case 10: return Result(code, "TLMC_InternalError", "An unexpected error was encountered within the XA system that resulted in the operation not completing.");
                case 11: return Result(code, "TLMC_InvalidHandle", "The device handle provided is no longer valid or was not obtained via a call to TLMC_Open.");
                case 12: return Result(code, "TLMC_InvalidArgument", "An argument was provided that has a invalid or out of range value.");
                case 13: return Result(code, "TLMC_ItemIsReadOnly", "The requested operation was unable to complete as it requires modifying a read only item. This will be returned when attempting to modify a read only setting.");
                case 14: return Result(code, "TLMC_LoadParamsError", "One of the parameter groups failed to load. Loading the parameter groups is performed as part of the TLMC_Open call. To prevent this behaviour, include TLMC_OperatingMode_DoNotLoadParamsOnConnect in the operating modes mask.");
                case 15: return Result(code, "TLMC_TransportError", "The requested operation was unable to complete due to a low-level transport/communication error. As far as possible, the system will return to the state it was in prior to the call that failed.");
                case 16: return Result(code, "TLMC_TransportClosed", "The requested operation was unable to start as the low-level transport/communication medium is unavailable at the moment.");
                case 17: return Result(code, "TLMC_TransportNotAvailable", "The requested transport is not available.");
                case 18: return Result(code, "TLMC_SharingModeNotAvailable", "The device is being used by an internal XA component and is unavaiable for API use.");
                case 19: return Result(code, "TLMC_NotInitialized", "The requested operation could not complete due to an uninitialized internal component.");
                case 20: return Result(code, "TLMC_NoFreeHandles", "The requested operation could not complete due to the system running out of device handles. This should never happen, and is indicative of a potential problem in user code. Ensure every successful call to TLMC_Open has a corresponding call to TLMC_Close.");
                case 21: return Result(code, "TLMC_VerificationFailure", "The requested operation included a verification phase which revealed the device was in an unexpected state.");
                case 22: return Result(code, "TLMC_DataNotLoaded", "The requested operation could not complete as required data has not been loaded from the device. This is most likely due to a device being opened with the TLMC_OperatingMode_DoNotLoadParamsOnConnect operating mode, and a get (with a timeout) or set has yet to be issued for a parameter group.");
                case 23: return Result(code, "TLMC_ConnectedProductNotSupported", "The device does not support the specified connected product.");
                case 24: return Result(code, "TLMC_SimulationCreationError", "An attempt to create a simulated device failed.");
                default: return Result(code, "TLMC_UnknownResultCode", "The XA API returned a result code outside the supported 0-24 range.");
            }
        }

        private static XaResultInfo Result(int code, string name, string description)
        {
            return new XaResultInfo { Code = code, Name = name, Description = description };
        }
    }
}

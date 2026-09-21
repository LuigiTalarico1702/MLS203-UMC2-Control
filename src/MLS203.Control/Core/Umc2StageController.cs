using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Thorlabs.MotionControl.XA;
using Thorlabs.MotionControl.XA.Products;

namespace Mls203.Control.Core
{
    public sealed class StageStatusSnapshot
    {
        public double XPositionMillimetres { get; set; }
        public double YPositionMillimetres { get; set; }
        public short XVelocity { get; set; }
        public short YVelocity { get; set; }
        public UniversalStatusBits XStatusBits { get; set; }
        public UniversalStatusBits YStatusBits { get; set; }
    }

    public sealed class Umc2StageController : IDisposable
    {
        private readonly object _sync = new object();
        private SystemManager _manager;
        private Umcx _baseUnit;
        private UmcxBrushlessLogicalChannel _x;
        private UmcxBrushlessLogicalChannel _y;
        private Unit _xUnit;
        private Unit _yUnit;
        private bool _started;

        public bool IsConnected { get; private set; }
        public string XProductName { get; private set; }
        public string YProductName { get; private set; }

        public void Startup()
        {
            if (_started)
                return;

            _manager = SystemManager.Create();
            _manager.Startup();
            _started = true;
        }

        public IReadOnlyList<DiscoveredDevice> DiscoverUsbDevices()
        {
            lock (_sync)
            {
                if (IsConnected)
                    throw new InvalidOperationException("USB discovery is unavailable while a controller is connected.");

                // XA builds its available-device list when the system manager starts.
                // Recreate the session so a controller attached after an earlier scan
                // is enumerated instead of returning the previous cached list.
                RestartSystemManager();
                return _manager.GetDeviceList()
                    .Select(device => new DiscoveredDevice
                    {
                        DeviceId = device.Device ?? string.Empty,
                        PartNumber = device.PartNumber ?? string.Empty,
                        Description = device.DeviceTypeDescription ?? string.Empty,
                        ParentDevice = device.ParentDevice ?? string.Empty,
                        Transport = device.Transport ?? string.Empty
                    })
                    .ToList();
            }
        }

        private void RestartSystemManager()
        {
            if (_manager != null)
            {
                try
                {
                    if (_started)
                        _manager.Shutdown();
                }
                finally
                {
                    _started = false;
                    _manager.Dispose();
                    _manager = null;
                }
            }

            Startup();
        }

        public void Connect(
            string baseDeviceId,
            string baseTransport,
            string xChannelId,
            string xTransport,
            string yChannelId,
            string yTransport)
        {
            if (IsConnected)
                throw new InvalidOperationException("A controller is already connected.");

            Startup();
            bool openedBase = _manager.TryOpenDevice(baseDeviceId, baseTransport, OperatingModes.Default, out _baseUnit);
            if (!openedBase || _baseUnit == null)
                throw new InvalidOperationException("Unable to open UMC2 base unit " + baseDeviceId + ".");

            try
            {
                OpenChannel(xChannelId, xTransport, out _x);
                OpenChannel(yChannelId, yTransport, out _y);

                ConnectedProductInfo xInfo = _x.GetConnectedProductInfo();
                ConnectedProductInfo yInfo = _y.GetConnectedProductInfo();
                _xUnit = xInfo.UnitType;
                _yUnit = yInfo.UnitType;
                XProductName = xInfo.ProductName;
                YProductName = yInfo.ProductName;
                IsConnected = true;
            }
            catch
            {
                CloseDevices();
                throw;
            }
        }

        private void OpenChannel(string channelId, string transport, out UmcxBrushlessLogicalChannel channel)
        {
            bool opened = _manager.TryOpenDevice(
                channelId,
                transport,
                OperatingModes.Default,
                out channel);
            if (!opened || channel == null)
                throw new InvalidOperationException("Unable to open brushless channel " + channelId + ".");
        }

        public Task HomeAsync()
        {
            EnsureConnected();
            return Task.Run(() =>
            {
                _x.SetEnableState(EnableState.Enabled, TimeSpan.FromSeconds(2));
                _y.SetEnableState(EnableState.Enabled, TimeSpan.FromSeconds(2));
                // Sequential homing is intentional for conservative commissioning.
                _x.Home(TimeSpan.FromSeconds(90));
                _y.Home(TimeSpan.FromSeconds(90));
            });
        }

        public Task EnableAxisAsync(bool xAxis)
        {
            EnsureConnected();
            return Task.Run(() =>
            {
                lock (_sync)
                {
                    EnsureConnected();
                    UmcxBrushlessLogicalChannel channel = xAxis ? _x : _y;
                    channel.SetEnableState(EnableState.Enabled, TimeSpan.FromSeconds(2));
                }
            });
        }

        public Task MoveAbsoluteAsync(double xMillimetres, double yMillimetres)
        {
            EnsureConnected();
            return Task.WhenAll(
                Task.Run(() => Move(_x, _xUnit, ResolveMoveMode("Absolute"), xMillimetres)),
                Task.Run(() => Move(_y, _yUnit, ResolveMoveMode("Absolute"), yMillimetres)));
        }

        public Task JogAsync(bool xAxis, double deltaMillimetres)
        {
            EnsureConnected();
            return Task.Run(() => Move(
                xAxis ? _x : _y,
                xAxis ? _xUnit : _yUnit,
                ResolveMoveMode("RelativeMove", "Relative"),
                deltaMillimetres));
        }

        // XA releases have used both RelativeMove and Relative for the same enum
        // member. Resolve it by name so this source works with either SDK family.
        private static MoveMode ResolveMoveMode(params string[] candidateNames)
        {
            foreach (string name in candidateNames)
            {
                if (Enum.TryParse(name, true, out MoveMode value))
                    return value;
            }

            throw new NotSupportedException(
                "The installed XA SDK does not expose a supported move mode (" +
                string.Join(", ", candidateNames) + ").");
        }

        private static void Move(UmcxBrushlessLogicalChannel channel, Unit unit, MoveMode mode, double value)
        {
            long deviceUnits = channel.FromPhysicalToDeviceUnit(ScaleType.Distance, unit, value);
            int commandValue = checked((int)deviceUnits);
            channel.Move(mode, commandValue, TimeSpan.FromSeconds(90));
        }

        public async Task<Tuple<double, double>> ReadPositionAsync()
        {
            StageStatusSnapshot status = await ReadStatusAsync().ConfigureAwait(false);
            return Tuple.Create(status.XPositionMillimetres, status.YPositionMillimetres);
        }

        public Task<StageStatusSnapshot> ReadStatusAsync()
        {
            EnsureConnected();
            return Task.Run(() =>
            {
                lock (_sync)
                {
                    // Disconnect may have been requested after this task was
                    // scheduled but before it acquired the device lock.
                    EnsureConnected();

                    // A GetUmcStatus call with a non-zero timeout loads fresh data
                    // from the controller. Calling RequestStatus first duplicates
                    // the USB transaction and can make a 300 ms GUI cycle overrun.
                    // Position is the calibrated stage coordinate; EncoderCount is
                    // the raw encoder counter and has a different origin.
                    UmcStatus xStatus = _x.GetUmcStatus(TimeSpan.FromSeconds(1));
                    UmcStatus yStatus = _y.GetUmcStatus(TimeSpan.FromSeconds(1));
                    UnitConversionResult x = _x.FromDeviceUnitToPhysical(ScaleType.Distance, xStatus.Position);
                    UnitConversionResult y = _y.FromDeviceUnitToPhysical(ScaleType.Distance, yStatus.Position);
                    return new StageStatusSnapshot
                    {
                        XPositionMillimetres = x.Value,
                        YPositionMillimetres = y.Value,
                        XVelocity = xStatus.Velocity,
                        YVelocity = yStatus.Velocity,
                        XStatusBits = xStatus.StatusBits,
                        YStatusBits = yStatus.StatusBits
                    };
                }
            });
        }

        public Task StopAsync()
        {
            EnsureConnected();
            return Task.WhenAll(
                Task.Run(() => InvokeImmediateStop(_x)),
                Task.Run(() => InvokeImmediateStop(_y)));
        }

        // Reflection keeps this compatible with XA versions that expose the wait argument
        // as either TimeSpan or an integer number of milliseconds.
        private static void InvokeImmediateStop(object channel)
        {
            MethodInfo stop = channel.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Stop" && m.GetParameters().Length == 2);
            if (stop == null)
                throw new MissingMethodException(channel.GetType().FullName, "Stop");

            ParameterInfo[] parameters = stop.GetParameters();
            object immediate = Enum.Parse(parameters[0].ParameterType, "Immediate", true);
            object wait = parameters[1].ParameterType == typeof(TimeSpan)
                ? (object)TimeSpan.FromSeconds(3)
                : Convert.ChangeType(3000, parameters[1].ParameterType);
            stop.Invoke(channel, new[] { immediate, wait });
        }

        private void EnsureConnected()
        {
            if (!IsConnected || _x == null || _y == null)
                throw new InvalidOperationException("UMC2 is not connected.");
        }

        public void Disconnect()
        {
            CloseDevices();
        }

        private void CloseDevices()
        {
            lock (_sync)
            {
                IsConnected = false;
                try { _x?.Close(); } catch { }
                try { _y?.Close(); } catch { }
                try { _baseUnit?.Disconnect(); } catch { }
                try { _baseUnit?.Close(); } catch { }
                _x = null;
                _y = null;
                _baseUnit = null;
            }
        }

        public void Dispose()
        {
            CloseDevices();
            if (_started)
            {
                try { _manager.Shutdown(); } catch { }
                _started = false;
            }
        }
    }
}

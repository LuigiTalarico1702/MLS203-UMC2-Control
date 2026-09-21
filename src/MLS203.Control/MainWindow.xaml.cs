using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mls203.Control.Core;
using Mls203.Control.Native;
using Thorlabs.MotionControl.XA;

namespace Mls203.Control
{
    public partial class MainWindow : Window
    {
        private sealed class EthernetNetwork
        {
            public string InterfaceName { get; set; }
            public IPAddress Address { get; set; }
            public uint Network { get; set; }
            public uint Broadcast { get; set; }
        }

        private const double XMinimum = 0.0;
        private const double XMaximum = 110.0;
        private const double YMinimum = 0.0;
        private const double YMaximum = 75.0;
        private const int DefaultPositionPollingMs = 300;
        private const int MinimumPositionPollingMs = 50;
        private const int MaximumPositionPollingMs = 5000;

        private readonly ObservableCollection<DiscoveredDevice> _devices = new ObservableCollection<DiscoveredDevice>();
        private readonly Umc2StageController _controller = new Umc2StageController();
        private readonly DispatcherTimer _positionTimer;
        private bool _homed;
        private bool _busy;
        private bool _polling;
        private bool _handlingConnectionFailure;
        private int _positionPollingFailures;
        private long _positionFeedbackCount;
        private bool _ledBlinkPhase;

        private static readonly UniversalStatusBits AxisErrorMask =
            UniversalStatusBits.Error |
            UniversalStatusBits.PositionError |
            UniversalStatusBits.InstrumentError |
            UniversalStatusBits.Interlock |
            UniversalStatusBits.Overtemperature |
            UniversalStatusBits.BusVoltageFault |
            UniversalStatusBits.CommutationError |
            UniversalStatusBits.Overload |
            UniversalStatusBits.EncoderFault |
            UniversalStatusBits.Overcurrent |
            UniversalStatusBits.BusCurrentFault;

        private static readonly UniversalStatusBits AxisMovingMask =
            UniversalStatusBits.MovingClockwise |
            UniversalStatusBits.MovingCounterclockwise |
            UniversalStatusBits.JoggingClockwise |
            UniversalStatusBits.JoggingCounterclockwise |
            UniversalStatusBits.Homing |
            UniversalStatusBits.Tracking;

        public MainWindow()
        {
            InitializeComponent();
            DeviceGrid.ItemsSource = _devices;
            SoftwareLimitsTextBlock.Text =
                "Movement requires homing to be completed\n" +
                "Software limits X: " + FormatLimit(XMinimum) + " - " + FormatLimit(XMaximum) + " [mm]\n" +
                "Software limits Y: " + FormatLimit(YMinimum) + " - " + FormatLimit(YMaximum) + " [mm]";
            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DefaultPositionPollingMs) };
            _positionTimer.Tick += PositionTimer_Tick;
            PositionPollingMsBox.Text = DefaultPositionPollingMs.ToString(CultureInfo.CurrentCulture);
            LogMessage("INFO", "Application initialized. Ready for Ethernet discovery.");
            ConfigureWiredEthernetDiscovery();
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadNetworkInputs(out string firstIp, out string lastIp, out int port, out int timeoutMs))
                return;

            LogMessage("INFO", "Ethernet discovery parameters: " + firstIp + " to " + lastIp
                               + ", port " + port + ", timeout " + timeoutMs + " ms per host.");
            await RunBusyAsync("Scanning Ethernet devices...", async () =>
            {
                XaDiscoveryResult discovery = await Task.Run(() => XaNative.Discover(firstIp, lastIp, port, TimeSpan.FromMilliseconds(timeoutMs)));
                string xaResult = "TLMC_DiscoverEthernetDeviceInfo returned " + XaNative.FormatResult(discovery.Result);
                LogMessage(discovery.Result.Code == 0 ? "INFO" : "ERROR", xaResult);
                if (discovery.Result.Code != 0)
                    throw new InvalidOperationException(xaResult);

                IReadOnlyList<DiscoveredDevice> found = discovery.Devices;
                _devices.Clear();
                foreach (DiscoveredDevice device in found.OrderBy(d => d.DeviceId))
                    _devices.Add(device);

                if (found.Count == 0)
                    SetStatus("No XA device found. Check NIC subnet, firewall, IP range and port.", "WARNING");
                else
                    SetStatus(found.Count + " XA device entries found. Select the UMC2 base unit or one of its channels.");
            });
        }

        private async void ScanUsbButton_Click(object sender, RoutedEventArgs e)
        {
            await RunBusyAsync("Scanning XA USB devices...", async () =>
            {
                LogMessage("INFO", "Refreshing the XA session to detect USB hot-plug changes.");
                IReadOnlyList<DiscoveredDevice> found = await Task.Run(() => _controller.DiscoverUsbDevices());
                _devices.Clear();
                foreach (DiscoveredDevice device in found.OrderBy(device => device.DeviceId))
                    _devices.Add(device);

                if (found.Count == 0)
                    SetStatus("No XA USB device found. Check the USB cable, controller power and XA driver.", "WARNING");
                else
                    SetStatus(found.Count + " XA USB device entries found. Select the UMC2 base unit or one of its channels.");
            });
        }

        private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = DeviceGrid.SelectedItem as DiscoveredDevice;
            if (selected == null)
                return;

            DiscoveredDevice baseDevice = _devices.FirstOrDefault(device =>
                device.DeviceId == selected.BaseDeviceId && string.IsNullOrWhiteSpace(device.ParentDevice));
            if (baseDevice == null)
                baseDevice = selected;

            DeviceIdBox.Text = baseDevice.DeviceId;
            TransportBox.Text = baseDevice.TransportDisplay;
            LogMessage("INFO", "Selected UMC2 base " + baseDevice.DeviceId + " using transport "
                               + baseDevice.TransportDisplay + ".");
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_controller.IsConnected)
            {
                LogMessage("INFO", "An UMC2 connection is already active in this application; disconnecting it now.");
                _positionTimer.Stop();
                _controller.Disconnect();
                _homed = false;
                ProductText.Text = string.Empty;
                XPositionText.Text = "--";
                YPositionText.Text = "--";
                PositionFeedbackStatusText.Text = "Feedback: disconnected";
                _positionFeedbackCount = 0;
                ResetStatusIndicators();
                SetStatus("Disconnected.");
                UpdateControls();
                return;
            }

            string deviceId = DeviceIdBox.Text.Trim();
            if (deviceId.Length == 0)
            {
                ShowError("Select a discovered UMC2 entry first.");
                return;
            }

            DiscoveredDevice baseDevice = _devices.FirstOrDefault(device =>
                device.DeviceId == deviceId && string.IsNullOrWhiteSpace(device.ParentDevice));
            DiscoveredDevice[] channels = _devices
                .Where(device => string.Equals(device.ParentDevice, deviceId, StringComparison.Ordinal))
                .OrderBy(device => device.DeviceId)
                .ToArray();
            if (baseDevice == null || channels.Length < 2)
            {
                ShowError("The selected UMC2 requires one base entry and two logical channel entries. Run USB or Ethernet discovery again.");
                return;
            }

            await RunBusyAsync("Connecting to UMC2...", async () =>
            {
                try
                {
                    await Task.Run(() => _controller.Connect(
                        baseDevice.DeviceId,
                        baseDevice.Transport,
                        channels[0].DeviceId,
                        channels[0].Transport,
                        channels[1].DeviceId,
                        channels[1].Transport));
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.StartsWith("Unable to open", StringComparison.Ordinal))
                {
                    LogMessage("INFO", "The UMC2 connection is already in use by another XA application or process. Close the other connection and try again.");
                    throw;
                }
                ProductText.Text = "Channel 1: " + _controller.XProductName
                                   + Environment.NewLine
                                   + "Channel 2: " + _controller.YProductName;
                SetStatus("Connected over " + baseDevice.TransportDisplay + ". Axis and power states are shown by the status LEDs.");
                await RefreshPositionAsync();
                _positionTimer.Start();
            });
        }

        private async void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirmation = MessageBox.Show(
                "The stage will move both axes to their reference positions. Is the entire travel area clear?",
                "Confirm homing",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                LogMessage("INFO", "Homing cancelled by the user.");
                return;
            }

            await RunBusyAsync("Homing X, then Y...", async () =>
            {
                await _controller.HomeAsync();
                _homed = true;
                await RefreshPositionAsync();
                SetStatus("Homing completed. Absolute and jog movements are enabled.");
            });
        }

        private async void EnableAxisButton_Click(object sender, RoutedEventArgs e)
        {
            bool xAxis = string.Equals(Convert.ToString(((Button)sender).Tag), "X", StringComparison.Ordinal);
            string axisName = xAxis ? "X" : "Y";
            await RunBusyAsync("Enabling axis " + axisName + "...", async () =>
            {
                await _controller.EnableAxisAsync(xAxis);
                await RefreshPositionAsync();
                SetStatus("Axis " + axisName + " enabled.");
            });
        }

        private async void DisableAxisButton_Click(object sender, RoutedEventArgs e)
        {
            bool xAxis = string.Equals(Convert.ToString(((Button)sender).Tag), "X", StringComparison.Ordinal);
            string axisName = xAxis ? "X" : "Y";
            await RunBusyAsync("Disabling axis " + axisName + "...", async () =>
            {
                await _controller.DisableAxisAsync(xAxis);
                await RefreshPositionAsync();
                SetStatus("Axis " + axisName + " disabled.");
            });
        }

        private void ResetStatusIndicators()
        {
            PowerStatusLed.Fill = Brushes.Gray;
            XEnableLed.Fill = Brushes.Gray;
            YEnableLed.Fill = Brushes.Gray;
            PowerStatusLed.ToolTip = "Power status unavailable";
            XEnableLed.ToolTip = "X axis status unavailable";
            YEnableLed.ToolTip = "Y axis status unavailable";
        }

        private async void MoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseNumber(TargetXBox.Text, out double x) || !TryParseNumber(TargetYBox.Text, out double y))
            {
                ShowError("Enter valid numeric X and Y positions.");
                return;
            }

            if (x < XMinimum || x > XMaximum || y < YMinimum || y > YMaximum)
            {
                ShowError(
                    "Target outside software limits: X " + FormatLimit(XMinimum) + " - " + FormatLimit(XMaximum) +
                    " [mm]; Y " + FormatLimit(YMinimum) + " - " + FormatLimit(YMaximum) + " [mm].");
                return;
            }

            await RunBusyAsync("Moving to X=" + x.ToString("F3") + " mm, Y=" + y.ToString("F3") + " mm...", async () =>
            {
                await _controller.MoveAbsoluteAsync(x, y);
                SetStatus("Absolute movement completed.");
            });
        }

        private async void JogButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseNumber(JogStepBox.Text, out double step) || step <= 0 || step > 10)
            {
                ShowError("Jog step must be greater than 0 and no more than 10 mm.");
                return;
            }

            string command = Convert.ToString(((Button)sender).Tag, CultureInfo.InvariantCulture);
            bool xAxis = command.StartsWith("X", StringComparison.Ordinal);
            double delta = command.EndsWith("+", StringComparison.Ordinal) ? step : -step;

            // Read the controller immediately before validating the relative move.
            // A stale/invalid UI value must never bypass the software travel check.
            Tuple<double, double> currentPosition;
            try
            {
                currentPosition = await _controller.ReadPositionAsync();
            }
            catch (Exception ex)
            {
                await VerifyConnectionAfterExceptionAsync("position verification", ex);
                ShowError("Unable to verify the current position: " + BaseExceptionMessage(ex), ex);
                return;
            }

            double target = (xAxis ? currentPosition.Item1 : currentPosition.Item2) + delta;
            double minimum = xAxis ? XMinimum : YMinimum;
            double maximum = xAxis ? XMaximum : YMaximum;
            if (target < minimum || target > maximum)
            {
                ShowError("Jog rejected by the software travel limit.");
                return;
            }

            await RunBusyAsync("Jogging " + command + " by " + step.ToString("F3") + " mm...", async () =>
            {
                await _controller.JogAsync(xAxis, delta);
                SetStatus("Jog completed.");
            });
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetStatus("STOP requested...");
                await _controller.StopAsync();
                SetStatus("Both axes stopped.");
            }
            catch (Exception ex)
            {
                await VerifyConnectionAfterExceptionAsync("STOP command", ex);
                ShowError("Stop command failed: " + BaseExceptionMessage(ex), ex);
            }
        }

        private async void PositionTimer_Tick(object sender, EventArgs e)
        {
            if (_polling || !_controller.IsConnected)
                return;

            _polling = true;
            try
            {
                await RefreshPositionAsync();
                if (_positionPollingFailures > 0)
                    LogMessage("INFO", "Position feedback restored.");
                _positionPollingFailures = 0;
            }
            catch (Exception ex)
            {
                _positionPollingFailures++;
                await VerifyConnectionAfterExceptionAsync("position feedback", ex);
            }
            finally
            {
                _polling = false;
            }
        }

        private async Task RefreshPositionAsync()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            StageStatusSnapshot status = await _controller.ReadStatusAsync();
            stopwatch.Stop();
            _positionFeedbackCount++;
            XPositionText.Text = status.XPositionMillimetres.ToString("F4", CultureInfo.CurrentCulture);
            YPositionText.Text = status.YPositionMillimetres.ToString("F4", CultureInfo.CurrentCulture);
            UpdateStatusIndicators(status);
            PositionFeedbackStatusText.Text = "Feedback #" + _positionFeedbackCount
                                                + " at " + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture)
                                                + " (read " + stopwatch.ElapsedMilliseconds + " ms)";
        }

        private void UpdateStatusIndicators(StageStatusSnapshot status)
        {
            _ledBlinkPhase = !_ledBlinkPhase;
            SetAxisStatusIndicator(XEnableLed, "X", status.XStatusBits, status.XVelocity);
            SetAxisStatusIndicator(YEnableLed, "Y", status.YStatusBits, status.YVelocity);

            bool hasError = HasAny(status.XStatusBits, AxisErrorMask)
                            || HasAny(status.YStatusBits, AxisErrorMask);
            bool powerOk = HasAny(status.XStatusBits, UniversalStatusBits.PowerOk)
                           && HasAny(status.YStatusBits, UniversalStatusBits.PowerOk);
            bool initializing = HasAny(status.XStatusBits, UniversalStatusBits.Initializing)
                                || HasAny(status.YStatusBits, UniversalStatusBits.Initializing);
            bool connected = HasAny(status.XStatusBits, UniversalStatusBits.Connected)
                             && HasAny(status.YStatusBits, UniversalStatusBits.Connected);
            if (!connected)
            {
                PowerStatusLed.Fill = Brushes.Gray;
                PowerStatusLed.ToolTip = "Power status unavailable";
            }
            else if (hasError)
            {
                PowerStatusLed.Fill = Brushes.Red;
                PowerStatusLed.ToolTip = "Power status: error";
            }
            else if (powerOk)
            {
                PowerStatusLed.Fill = Brushes.Green;
                PowerStatusLed.ToolTip = "Power status: normal";
            }
            else
            {
                PowerStatusLed.Fill = Brushes.Blue;
                PowerStatusLed.ToolTip = initializing ? "Power status: starting" : "Power status: not ready";
            }
        }

        private void SetAxisStatusIndicator(
            System.Windows.Shapes.Ellipse indicator,
            string axis,
            UniversalStatusBits bits,
            short velocity)
        {
            string state;
            if (!HasAny(bits, UniversalStatusBits.Connected))
            {
                indicator.Fill = Brushes.Gray;
                state = "status unavailable";
            }
            else if (HasAny(bits, AxisErrorMask))
            {
                indicator.Fill = Brushes.Red;
                state = "error";
            }
            else if (HasAny(bits, UniversalStatusBits.Initializing))
            {
                indicator.Fill = _ledBlinkPhase ? Brushes.Blue : Brushes.LightGray;
                state = "initializing (blinking blue)";
            }
            else if (!HasAny(bits, UniversalStatusBits.PowerOk))
            {
                indicator.Fill = Brushes.Purple;
                state = "powering down";
            }
            else if (velocity != 0 || HasAny(bits, AxisMovingMask))
            {
                indicator.Fill = Brushes.Orange;
                state = "in motion";
            }
            else if (HasAny(bits, UniversalStatusBits.Enabled))
            {
                indicator.Fill = Brushes.Green;
                state = "enabled and stationary";
            }
            else
            {
                indicator.Fill = Brushes.Blue;
                state = "motor disabled";
            }

            indicator.ToolTip = "Axis " + axis + ": " + state;
        }

        private static bool HasAny(UniversalStatusBits value, UniversalStatusBits mask)
        {
            return (value & mask) != 0;
        }

        private void ApplyPositionPollingButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PositionPollingMsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int intervalMs)
                || intervalMs < MinimumPositionPollingMs
                || intervalMs > MaximumPositionPollingMs)
            {
                ShowError("Position polling interval must be between 50 and 5000 ms.");
                return;
            }

            _positionTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
            PositionPollingMsBox.Text = intervalMs.ToString(CultureInfo.CurrentCulture);
            SetStatus("Position polling interval set to " + intervalMs + " ms.");
        }

        private async Task RunBusyAsync(string message, Func<Task> operation)
        {
            if (_busy)
            {
                LogMessage("WARNING", "Command ignored because another operation is running.");
                return;
            }

            _busy = true;
            SetStatus(message);
            UpdateControls();
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                await VerifyConnectionAfterExceptionAsync("XA command", ex);
                ShowError(BaseExceptionMessage(ex), ex);
            }
            finally
            {
                _busy = false;
                UpdateControls();
            }
        }

        private async Task VerifyConnectionAfterExceptionAsync(string context, Exception originalException)
        {
            if (!_controller.IsConnected || _handlingConnectionFailure)
                return;

            _handlingConnectionFailure = true;
            _positionTimer.Stop();
            LogMessage("WARNING", "XA exception during " + context + ": " + BaseExceptionMessage(originalException));
            LogMessage("INFO", "Verifying the USB connection with GetUmcStatus() on both axes.");
            try
            {
                await _controller.VerifyConnectionAsync();
                LogMessage("INFO", "GetUmcStatus() succeeded; the controller connection is still present.");
                if (_controller.IsConnected)
                    _positionTimer.Start();
            }
            catch (Exception verificationException)
            {
                LogMessage("ERROR", "USB connection verification failed: " + BaseExceptionMessage(verificationException));
                LogMessage("INFO", "Releasing XA resources in order: Disconnect, Close, Shutdown, Dispose.");
                _busy = true;
                UpdateControls();
                IReadOnlyList<string> cleanupErrors = await Task.Run(() => _controller.DisconnectAndDispose());
                foreach (string cleanupError in cleanupErrors)
                    LogMessage("WARNING", cleanupError);

                _homed = false;
                ProductText.Text = string.Empty;
                XPositionText.Text = "--";
                YPositionText.Text = "--";
                PositionFeedbackStatusText.Text = "Feedback: USB connection lost";
                ResetStatusIndicators();
                SetStatus("USB connection lost. XA resources released; run USB discovery before reconnecting.", "ERROR");
            }
            finally
            {
                _handlingConnectionFailure = false;
                _busy = false;
                UpdateControls();
            }
        }

        private void UpdateControls()
        {
            bool connected = _controller.IsConnected;
            ScanButton.IsEnabled = !_busy && !connected;
            ScanUsbButton.IsEnabled = !_busy && !connected;
            ConnectButton.IsEnabled = !_busy;
            ConnectButton.Content = connected ? "Disconnect" : "Connect";
            HomeButton.IsEnabled = connected && !_busy;
            EnableXButton.IsEnabled = connected && !_busy;
            EnableYButton.IsEnabled = connected && !_busy;
            DisableXButton.IsEnabled = connected && !_busy;
            DisableYButton.IsEnabled = connected && !_busy;
            StopButton.IsEnabled = connected;
            bool motionEnabled = connected && _homed && !_busy;
            MoveButton.IsEnabled = motionEnabled;
            XMinusButton.IsEnabled = motionEnabled;
            XPlusButton.IsEnabled = motionEnabled;
            YMinusButton.IsEnabled = motionEnabled;
            YPlusButton.IsEnabled = motionEnabled;
        }

        private bool TryReadNetworkInputs(out string firstIp, out string lastIp, out int port, out int timeoutMs)
        {
            firstIp = FirstIpBox.Text.Trim();
            lastIp = LastIpBox.Text.Trim();
            port = 0;
            timeoutMs = 0;
            IPAddress firstAddress = null;
            IPAddress lastAddress = null;
            bool valid = IPAddress.TryParse(firstIp, out firstAddress)
                         && IPAddress.TryParse(lastIp, out lastAddress)
                         && firstAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         && lastAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         && ToIpv4Number(firstAddress) <= ToIpv4Number(lastAddress)
                         && int.TryParse(PortBox.Text, out port) && port > 0 && port <= 65535
                         && int.TryParse(TimeoutBox.Text, out timeoutMs) && timeoutMs >= 50 && timeoutMs <= 30000;
            if (valid && !IsRangeOnWiredEthernet(firstAddress, lastAddress))
            {
                ShowError("The scan range must belong to an active wired Ethernet interface. Wi-Fi networks are excluded.");
                return false;
            }
            if (!valid)
            {
                ShowError("Check the IPv4 range (first ≤ last), port (1…65535), and timeout (50…30000 ms).");
            }
            return valid;
        }

        private void ConfigureWiredEthernetDiscovery()
        {
            EthernetNetwork network = GetActiveWiredEthernetNetworks().FirstOrDefault();
            if (network == null)
            {
                LogMessage("WARNING", "No active wired Ethernet IPv4 interface found. Wi-Fi interfaces are intentionally excluded.");
                return;
            }

            uint firstHost = network.Broadcast > network.Network + 1 ? network.Network + 1 : network.Network;
            uint lastHost = network.Broadcast > network.Network + 1 ? network.Broadcast - 1 : network.Broadcast;
            FirstIpBox.Text = FromIpv4Number(firstHost).ToString();
            LastIpBox.Text = FromIpv4Number(lastHost).ToString();
            LogMessage("INFO", "Wired Ethernet only: " + network.InterfaceName + " uses " + network.Address
                               + "; discovery range set to " + FirstIpBox.Text + " - " + LastIpBox.Text + ".");
        }

        private static bool IsRangeOnWiredEthernet(IPAddress firstAddress, IPAddress lastAddress)
        {
            uint first = ToIpv4Number(firstAddress);
            uint last = ToIpv4Number(lastAddress);
            return GetActiveWiredEthernetNetworks().Any(network => first >= network.Network && last <= network.Broadcast);
        }

        private static EthernetNetwork[] GetActiveWiredEthernetNetworks()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up
                                           && IsWiredEthernet(networkInterface.NetworkInterfaceType))
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask != null)
                    .Select(address =>
                    {
                        uint ip = ToIpv4Number(address.Address);
                        uint mask = ToIpv4Number(address.IPv4Mask);
                        uint subnet = ip & mask;
                        return new EthernetNetwork
                        {
                            InterfaceName = networkInterface.Name,
                            Address = address.Address,
                            Network = subnet,
                            Broadcast = subnet | ~mask
                        };
                    }))
                .ToArray();
        }

        private static bool IsWiredEthernet(NetworkInterfaceType type)
        {
            return type == NetworkInterfaceType.Ethernet
                   || type == NetworkInterfaceType.GigabitEthernet
                   || type == NetworkInterfaceType.FastEthernetFx
                   || type == NetworkInterfaceType.FastEthernetT;
        }

        private static uint ToIpv4Number(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }

        private static IPAddress FromIpv4Number(uint address)
        {
            return new IPAddress(new[]
            {
                (byte)(address >> 24),
                (byte)(address >> 16),
                (byte)(address >> 8),
                (byte)address
            });
        }

        private static bool TryParseNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                   || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string FormatLimit(double value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string BaseExceptionMessage(Exception exception)
        {
            return exception.GetBaseException().Message;
        }

        private void SetStatus(string message, string level = "INFO")
        {
            StatusText.Text = message;
            LogMessage(level, message);
        }

        private void LogException(Exception exception)
        {
            LogMessage("ERROR", exception.ToString());
        }

        private void LogMessage(string level, string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => LogMessage(level, message)));
                return;
            }

            string spacingAfterLevel = new string(' ', Math.Max(1, 8 - level.Length));
            MessageLogBox.AppendText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                                     + " [" + level + "]" + spacingAfterLevel + message + Environment.NewLine);
            MessageLogBox.ScrollToEnd();
        }

        private void ShowError(string message, Exception exception = null)
        {
            StatusText.Text = message;
            if (exception == null)
                LogMessage("ERROR", message);
            else
                LogException(exception);
            MessageBox.Show(message, "MLS203 / UMC2", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _positionTimer.Stop();
            bool wasConnected = _controller.IsConnected;
            string activeTransport = string.IsNullOrWhiteSpace(TransportBox.Text)
                ? "XA"
                : TransportBox.Text;

            LogMessage("INFO", "Close requested; stopping position polling.");
            if (wasConnected)
            {
                LogMessage("INFO", "Disconnecting the active motor controller transport: " + activeTransport + ".");
                _controller.Disconnect();
                LogMessage("INFO", "Motor controller disconnected.");
            }

            // Shutdown releases the XA system and its USB/Ethernet resources.
            _controller.Dispose();
            LogMessage("INFO", "XA USB/Ethernet resources released. Application closing.");
        }
    }
}

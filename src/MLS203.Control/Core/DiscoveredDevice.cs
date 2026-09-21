namespace Mls203.Control.Core
{
    public sealed class DiscoveredDevice
    {
        public string DeviceId { get; set; }
        public string PartNumber { get; set; }
        public string Description { get; set; }
        public string ParentDevice { get; set; }
        public string Transport { get; set; }

        public string BaseDeviceId => string.IsNullOrWhiteSpace(ParentDevice) ? DeviceId : ParentDevice;
        public string TransportDisplay => string.IsNullOrWhiteSpace(Transport) ? "USB (automatic)" : Transport;
    }
}

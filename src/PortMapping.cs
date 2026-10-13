using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mono.Nat;

namespace QingDownloadManager
{
    public sealed class PortMapping
    {
        readonly int tcpPort, udpPort;
        readonly bool dht;
        readonly string language;
        readonly string description = "QingFDM " + Guid.NewGuid().ToString("N");
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly Dictionary<string, Task> devices = new Dictionary<string, Task>();
        bool started, stopped;
        public string Status { get; private set; }

        public PortMapping(int tcpPort, int udpPort, bool dhtEnabled, string language)
        {
            this.tcpPort = tcpPort; this.udpPort = udpPort; dht = dhtEnabled; this.language = language;
            Status = T("UPnP：等待启动", "UPnP: waiting");
        }

        string T(string zh, string en) { return Localization.Text(language, en, zh); }

        public void Start()
        {
            lock (devices)
            {
                if (started || stopped) return;
                started = true; NatUtility.DeviceFound += DeviceFound;
                Status = T("UPnP：正在查找网关", "UPnP: searching for a gateway");
                NatUtility.StartDiscovery(NatProtocol.Upnp);
            }
        }

        void DeviceFound(object sender, DeviceEventArgs args)
        {
            if (args.Device.NatProtocol != NatProtocol.Upnp) return;
            lock (devices)
            {
                string key = args.Device.DeviceEndpoint.ToString();
                if (stopped || devices.ContainsKey(key)) return;
                devices.Add(key, MapDeviceAsync(args.Device));
            }
        }

        async Task MapDeviceAsync(INatDevice device)
        {
            List<Mapping> owned = new List<Mapping>();
            List<Mapping> requested = new List<Mapping> { new Mapping(Protocol.Tcp, tcpPort, tcpPort, 600, description) };
            if (dht) requested.Add(new Mapping(Protocol.Udp, udpPort, udpPort, 600, description));
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    Mapping[] existing = await device.GetAllMappingsAsync().ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    foreach (Mapping request in requested)
                    {
                        Mapping conflict = existing.FirstOrDefault(x => x.Protocol == request.Protocol && x.PublicPort == request.PublicPort && x.Description != description);
                        if (conflict != null) throw new InvalidOperationException(T("端口已被其他映射使用。", "The port belongs to another mapping."));
                        Mapping mapped = await device.CreatePortMapAsync(new Mapping(request.Protocol, request.PrivatePort, request.PublicPort, 600, description)).ConfigureAwait(false);
                        owned.RemoveAll(x => x.Protocol == mapped.Protocol && x.PublicPort == mapped.PublicPort); owned.Add(mapped);
                        cancellation.Token.ThrowIfCancellationRequested();
                    }
                    Status = "UPnP: TCP " + tcpPort + (dht ? " / UDP " + udpPort : "") + T(" 已映射", " mapped");
                    await Task.Delay(TimeSpan.FromMinutes(5), cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Status = T("UPnP：", "UPnP: ") + error.Message; }
            if (owned.Count > 0)
            {
                try
                {
                    Mapping[] existing = await device.GetAllMappingsAsync().ConfigureAwait(false);
                    foreach (Mapping mapping in owned)
                        if (existing.Any(x => x.Protocol == mapping.Protocol && x.PublicPort == mapping.PublicPort && x.Description == description))
                            await device.DeletePortMapAsync(mapping).ConfigureAwait(false);
                }
                catch (Exception error) { Status = T("UPnP：清理映射失败，等待租约到期。", "UPnP: cleanup failed; waiting for lease expiry. ") + error.Message; }
            }
        }

        public async Task StopAsync()
        {
            Task[] pending;
            lock (devices)
            {
                if (!stopped)
                {
                    stopped = true; cancellation.Cancel(); NatUtility.DeviceFound -= DeviceFound;
                    if (started) NatUtility.StopDiscovery();
                }
                pending = devices.Values.ToArray();
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }
}

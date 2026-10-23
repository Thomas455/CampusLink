using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CampusLink.Services
{
    public class NetworkAdapterInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Ipv4 { get; set; } = "";

        public string DisplayName
        {
            get
            {
                var builder = new StringBuilder(Name);
                if (!string.IsNullOrWhiteSpace(Mac)) builder.Append("  ").Append(Mac);
                if (!string.IsNullOrWhiteSpace(Ipv4)) builder.Append("  ").Append(Ipv4);
                return builder.ToString();
            }
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>枚举本机可用网卡（拿 MAC 和 IPv4），账号绑定网卡后即用其 MAC 认证、用其 IP 作为源地址。</summary>
    public static class NetworkAdapterService
    {
        public static List<NetworkAdapterInfo> GetAdapters()
        {
            var list = new List<NetworkAdapterInfo>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;

                    var address = nic.GetPhysicalAddress();
                    if (address == null) continue;
                    var mac = FormatMac(address.ToString());
                    if (mac.Length != 12) continue;

                    var ipv4 = "";
                    try
                    {
                        foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                                && !IPAddress.IsLoopback(unicast.Address))
                            {
                                ipv4 = unicast.Address.ToString();
                                break;
                            }
                        }
                    }
                    catch
                    {
                    }

                    list.Add(new NetworkAdapterInfo
                    {
                        Id = nic.Id,
                        Name = string.IsNullOrWhiteSpace(nic.Name) ? nic.Description : nic.Name,
                        Description = nic.Description,
                        Mac = mac,
                        Ipv4 = ipv4
                    });
                }
            }
            catch
            {
            }

            // 有 IPv4 的排前面，方便用户选到真正在用的那块
            list.Sort((a, b) =>
            {
                var av = string.IsNullOrEmpty(a.Ipv4) ? 1 : 0;
                var bv = string.IsNullOrEmpty(b.Ipv4) ? 1 : 0;
                if (av != bv) return av - bv;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        public static NetworkAdapterInfo Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (var adapter in GetAdapters())
            {
                if (string.Equals(adapter.Id, id, StringComparison.OrdinalIgnoreCase)) return adapter;
            }
            return null;
        }

        private static string FormatMac(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var builder = new StringBuilder(12);
            foreach (var ch in raw)
            {
                if (Uri.IsHexDigit(ch)) builder.Append(char.ToUpperInvariant(ch));
            }
            return builder.ToString();
        }
    }
}

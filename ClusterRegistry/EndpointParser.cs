using System.Net;

namespace ClusterRegistry;

internal static class EndpointParser
{
    public static IPEndPoint Parse(string value, int defaultPort)
    {
        if (!TryParse(value, defaultPort, out IPEndPoint? endpoint))
            throw new ArgumentException($"Invalid endpoint '{value}', expected host[:port]");

        return endpoint!;
    }

    public static bool TryParse(string value, int defaultPort, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        int colon = value.LastIndexOf(':');
        string host = colon >= 0 ? value[..colon] : value;
        int port = defaultPort;

        if (colon >= 0 && !int.TryParse(value[(colon + 1)..], out port))
            return false;
        if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
            return false;

        if (!IPAddress.TryParse(host, out IPAddress? address))
        {
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                if (addresses.Length == 0)
                    return false;
                address = addresses[0];
            }
            catch
            {
                return false;
            }
        }

        endpoint = new IPEndPoint(address, port);
        return true;
    }
}

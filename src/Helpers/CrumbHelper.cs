// CrumbHelper.cs
//  Andrew Baylis
//  Created: 29/10/2024

#region using

using System.Runtime.CompilerServices;
using System.Net;

#endregion

[assembly: InternalsVisibleTo("OoplesFinance.YahooFinanceAPI.Tests.Unit")]

namespace OoplesFinance.YahooFinanceAPI.Helpers;

internal sealed class CrumbHelper
{
    #region Fields

    private static CrumbHelper? _instance;

    internal static HttpMessageHandler? handler;
    private static HttpMessageHandler? _cachedHandler;

    #endregion

    static CrumbHelper()
    {
#if NET48
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
    }

    private CrumbHelper()
    {
        Crumb = string.Empty;
    }

    #region Properties

    /// <summary>
    ///     Crumb value for the Yahoo Finance API
    /// </summary>
    internal string Crumb { get; private set; }

    /// <summary>
    ///     Single instance of the CrumbHelper
    /// </summary>
    private static CrumbHelper Instance
    {
        get { return _instance ??= new CrumbHelper(); }
    }

    #endregion

    #region Static Methods

    public static HttpClient GetHttpClient()
    {
        var h = handler ?? GetClientHandler();
        HttpClient client = new(h, disposeHandler: false);

        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        client.DefaultRequestHeaders.Add("Connection", "keep-alive");
        client.DefaultRequestHeaders.Add("Pragma", "no-cache");
        client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");

        return client;
    }

    public static async Task<CrumbHelper> GetInstance(bool setCrumb = true)
    {
        if (string.IsNullOrEmpty(Instance.Crumb) && setCrumb)
        {
            await Instance.SetCrumbAsync();
        }

        return Instance;
    }

    private static HttpMessageHandler GetClientHandler()
    {
        if (_cachedHandler != null) return _cachedHandler;

        HttpMessageHandler h;

        if (YahooClient.IsThrottled)
        {
            h = new DownloadThrottleQueueHandler(40, TimeSpan.FromMinutes(1), 4); //40 calls in a minute, no more than 4 simultaneously
        }
        else
        {
#if NET48
            h = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
#else
            h = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All
            };
#endif
        }

        _cachedHandler = h;
        return h;
    }

    #endregion

    #region Public Methods

    public async Task SetCrumbAsync()
    {
        using var client = GetHttpClient();

        using var crumbResponse = await client.GetAsync("https://query1.finance.yahoo.com/v1/test/getcrumb");

        if (crumbResponse.IsSuccessStatusCode)
        {
            Crumb = await crumbResponse.Content.ReadAsStringAsync();
        }

        if (string.IsNullOrEmpty(Crumb))
        {
            throw new Exception("Failed to get crumb");
        }
    }

    #endregion

    #region Internal Methods

    internal static void Destroy()
    {
        _instance = null;
    }

    internal static void Reset()
    {
        _instance = null;
        handler = null;
        (_cachedHandler as IDisposable)?.Dispose();
        _cachedHandler = null;
    }

    #endregion
}

#if NET48
internal class DownloadThrottleQueueHandler : HttpClientHandler
#else
internal class DownloadThrottleQueueHandler : DelegatingHandler
#endif
{
    #region Fields

    private readonly TimeSpan _maxPeriod;
    private readonly SemaphoreSlim _throttleLoad, _throttleRate;

    #endregion

    public DownloadThrottleQueueHandler(int maxPerPeriod, TimeSpan maxPeriod, int maxParallel = -1)
    {
        if (maxParallel < 0 || maxParallel > maxPerPeriod)
        {
            maxParallel = maxPerPeriod;
        }

        _throttleLoad = new SemaphoreSlim(maxParallel, maxParallel);
        _throttleRate = new SemaphoreSlim(maxPerPeriod, maxPerPeriod);
        _maxPeriod = maxPeriod;

#if NET48
        AllowAutoRedirect = true;
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
#else
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
#endif
    }

    #region Override Methods

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _throttleLoad.WaitAsync(cancellationToken); // Allow bursts up to maxParallel requests at once
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _throttleRate.WaitAsync(cancellationToken);

            // Release after period [Note: Intentionally not awaited]
            // - Do not allow more than maxPerPeriod requests per period
            _ = Task.Delay(_maxPeriod).ContinueWith(tt => { _throttleRate.Release(1); }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            _throttleLoad.Release();
        }
    }

    #endregion
}

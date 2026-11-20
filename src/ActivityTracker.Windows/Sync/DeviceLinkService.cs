using System.Diagnostics;
using ActivityTracker.Core;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace ActivityTracker.Windows.Sync;

/// <summary>
/// Il collegamento senza codici, lato app: guida <see cref="DeviceLinkFlow"/> con un timer dell'interfaccia. Crea la
/// richiesta, apre il browser predefinito su <c>{server}/collega/{codice}</c> e ritira ogni <c>intervallo</c> secondi
/// fino a esito o scadenza; al collegamento riuscito il token va in Gestione credenziali con lo stesso percorso di
/// sempre (<see cref="SyncService.SaveCredentials"/>) e il sync parte subito.
///
/// Il browser si apre da solo soltanto al primo avvio senza credenziali; poi resta la voce «Collega questo computer».
/// Tutti i metodi vanno chiamati dal thread dell'interfaccia. Il codice del dispositivo e il token non si scrivono mai.
/// </summary>
public sealed class DeviceLinkService
{
    private readonly SyncService _sync;
    private readonly AppSettings _settings;
    private readonly DeviceLinkMachine _machine = new();
    private readonly DeviceLinkFlow? _flow;
    private readonly SyncFault? _urlError;
    private Timer? _timer;
    private bool _starting;

    /// <summary>Lo stato è cambiato (per aggiornare icona, menu e Impostazioni).</summary>
    public event Action? Changed;

    public DeviceLinkService(SyncService sync, AppSettings settings, IHttpTransport? transport = null)
    {
        _sync = sync;
        _settings = settings;
        var (url, error) = SyncClient.Validate(sync.ConnectUrl, sync.AllowInsecureLocalhost);
        _urlError = error;
        if (url is not null)
        {
            _flow = new DeviceLinkFlow(new DeviceLinkClient(url, AppIdentity.AgentVersion, transport), CurrentDevice,
                                       token => _sync.SaveCredentials(token) ? null : _sync.LastError ?? "Credenziali non salvate",
                                       _machine, log: Log.Write);
        }
    }

    public LinkPhase Phase => _machine.Phase;
    /// <summary>Richiesta in creazione o in attesa della persona.</summary>
    public bool IsActive => _machine.IsActive;
    public string StatusText => _machine.StatusText;
    /// <summary>Perché non è riuscito, o l'ultimo errore di rete durante l'attesa.</summary>
    public string? Detail => _machine.Detail;

    /// <summary>Da chiamare una volta all'avvio, dopo <see cref="SyncService.Start"/>: senza credenziali, e solo la
    /// prima volta, il collegamento parte da solo.</summary>
    public void Start()
    {
        if (_sync.HasToken) _machine.MarkLinked();
        if (DeviceLinkPolicy.StartsAtLaunch(_sync.HasToken, _settings.LinkAttempted)) Connect();
    }

    /// <summary>«Collega questo computer» (o «Collega di nuovo»): crea la richiesta e apre il browser. Con una
    /// richiesta già in attesa riapre il browser sulla stessa, senza crearne un'altra.</summary>
    public async void Connect()
    {
        if (_starting) return;
        if (_flow is null)
        {
            if (_machine.Begin()) _machine.RequestFailed(_urlError ?? new SyncFault(SyncErrorKind.InvalidUrl));
            Notify();
            return;
        }
        if (_flow.LinkUrl is { } pending)
        {
            OpenBrowser(pending);
            return;
        }
        _starting = true;
        Notify();
        bool waiting;
        try
        {
            var begin = _flow.BeginAsync();
            Notify();
            waiting = await begin;
        }
        catch (Exception e)
        {
            waiting = false;
            Log.Write("[Collegamento] errore inatteso: " + e.Message);
        }
        _starting = false;
        if (waiting && _flow.LinkUrl is { } url)
        {
            _settings.LinkAttempted = true;
            OpenBrowser(url);
            StartTimer();
        }
        Notify();
    }

    /// <summary>«Scollega»: la richiesta in attesa, se c'è, si dimentica.</summary>
    public void Reset()
    {
        StopTimer();
        _machine.Reset();
        Notify();
    }

    public void Shutdown() => StopTimer();

    // Interni

    private void StartTimer()
    {
        StopTimer();
        _timer = new Timer { Interval = Math.Max(1000, (int)_machine.Interval.TotalMilliseconds) };
        _timer.Tick += async (_, _) =>
        {
            if (_flow is null) return;
            var before = (_machine.Phase, _machine.Detail);
            try { await _flow.PollAsync(); }
            catch (Exception e) { Log.Write("[Collegamento] errore inatteso: " + e.Message); }
            if (!_machine.IsActive) StopTimer();
            if (before != (_machine.Phase, _machine.Detail)) Notify();
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static void OpenBrowser(string url)
    {
        if (AppSettings.NoBrowser)
        {
            Log.Write("[Collegamento] AT_NO_BROWSER: da aprire nel browser " + url);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Write("[Collegamento] impossibile aprire il browser: " + e.Message);
        }
    }

    private void Notify() => Changed?.Invoke();

    /// <summary>Impronta e nome di questo computer: <c>AT_DEVICE_ID</c> nelle prove, altrimenti il <c>MachineGuid</c>
    /// di Windows, altrimenti un GUID conservato nelle impostazioni.</summary>
    private DeviceIdentity CurrentDevice() =>
        new(DeviceIdentity.ResolveId(AppSettings.DeviceIdOverride, MachineGuid(), _settings.DeviceGuid), Environment.MachineName);

    /// <summary><c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> (vista a 64 bit), leggibile da un utente normale.</summary>
    private static string? MachineGuid()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

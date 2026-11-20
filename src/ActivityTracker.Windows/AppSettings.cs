using System.Text.Json;
using System.Text.Json.Serialization;
using ActivityTracker.Core;

namespace ActivityTracker.Windows;

/// <summary>
/// Preferenze persistenti (<c>%LOCALAPPDATA%\activity-tracker\settings.json</c>; il Mac usa UserDefaults) e variabili
/// d'ambiente di controllo. Qui non c'è nessun segreto: URL del server e token stanno in Gestione credenziali.
/// </summary>
public sealed class AppSettings
{
    public const int MinThresholdMinutes = 1;
    public const int MaxThresholdMinutes = 30;
    public const int DefaultThresholdMinutes = 3;

    /// <summary>
    /// Programmi la cui riproduzione o richiesta «schermo acceso» NON è video (regola 5): lettori solo audio e
    /// programmi che tengono acceso lo schermo senza video. Confronto senza maiuscole e senza «.exe».
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultVideoExclusions =
    [
        // solo audio
        "Spotify", "AppleMusic", "iTunes", "Music.UI", "foobar2000", "AIMP", "MusicBee", "Deezer", "TIDAL",
        "Amazon Music", "winamp", "MediaMonkey",
        // tengono acceso lo schermo senza video
        "PowerToys.Awake", "caffeine", "caffeine32", "caffeine64", "DontSleep", "DontSleep_x64", "Insomnia", "NoSleep",
        AppIdentity.InternalName,
    ];

    private sealed class Data
    {
        [JsonPropertyName("thresholdMinutes")] public int? ThresholdMinutes { get; set; }
        [JsonPropertyName("videoExclusions")] public List<string>? VideoExclusions { get; set; }
        [JsonPropertyName("loginItemDisabledByUser")] public bool LoginItemDisabledByUser { get; set; }
        [JsonPropertyName("linkAttempted")] public bool LinkAttempted { get; set; }
        [JsonPropertyName("deviceGuid")] public string? DeviceGuid { get; set; }
    }

    private readonly string _path;
    private Data _data;

    public AppSettings(string? path = null)
    {
        _path = path ?? Path.Combine(AppIdentity.DataDirectory(), "settings.json");
        try
        {
            _data = File.Exists(_path) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new Data() : new Data();
        }
        catch (Exception)
        {
            _data = new Data();
        }
    }

    public int ThresholdMinutes
    {
        get => Math.Clamp(_data.ThresholdMinutes ?? DefaultThresholdMinutes, MinThresholdMinutes, MaxThresholdMinutes);
        set
        {
            _data.ThresholdMinutes = Math.Clamp(value, MinThresholdMinutes, MaxThresholdMinutes);
            Save();
        }
    }

    public double ThresholdSeconds => ThresholdMinutes * 60;

    public IReadOnlyList<string> VideoExclusions
    {
        get => _data.VideoExclusions ?? DefaultVideoExclusions.ToList();
        set
        {
            _data.VideoExclusions = value.ToList();
            Save();
        }
    }

    /// <summary>La persona ha spento «Avvia all'accesso a Windows» dalle Impostazioni (true quando la spegne, false
    /// quando la riaccende): finché è true la voce <c>Run</c> non si riscrive agli avvii.</summary>
    public bool LoginItemDisabledByUser
    {
        get => _data.LoginItemDisabledByUser;
        set
        {
            if (_data.LoginItemDisabledByUser == value) return;
            _data.LoginItemDisabledByUser = value;
            Save();
        }
    }

    /// <summary>Il browser del collegamento si è già aperto una volta: negli avvii successivi senza credenziali non
    /// si riapre da solo, resta la voce di menu «Collega questo computer».</summary>
    public bool LinkAttempted
    {
        get => _data.LinkAttempted;
        set
        {
            if (_data.LinkAttempted == value) return;
            _data.LinkAttempted = value;
            Save();
        }
    }

    /// <summary>Ripiego per l'impronta del computer quando il <c>MachineGuid</c> di Windows non si legge: un GUID
    /// generato una volta e conservato qui. Non è un segreto.</summary>
    public string DeviceGuid()
    {
        if (string.IsNullOrWhiteSpace(_data.DeviceGuid))
        {
            _data.DeviceGuid = Guid.NewGuid().ToString("D");
            Save();
        }
        return _data.DeviceGuid!;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.Write("[Settings] salvataggio fallito: " + e.Message);
        }
    }

    // Variabili d'ambiente (prove)

    /// <summary><c>AT_NO_LOGIN_ITEM=1</c>: all'avvio non tocca la voce di avvio all'accesso (né la scrive né la toglie).</summary>
    public static bool SkipLoginItem => Flag("AT_NO_LOGIN_ITEM");
    /// <summary><c>AT_DEVICE_ID=&lt;64 esadecimali&gt;</c>: impronta del computer fissata a mano (prove).</summary>
    public static string? DeviceIdOverride => Environment.GetEnvironmentVariable(DeviceIdentity.Variable);
    /// <summary><c>AT_NO_BROWSER_URL=1</c>: non legge la barra degli indirizzi (l'equivalente di <c>AT_NO_APPLE_EVENTS</c>).</summary>
    public static bool SkipBrowserUrl => Flag("AT_NO_BROWSER_URL") || Flag("AT_NO_APPLE_EVENTS");
    /// <summary><c>AT_STOP_ON_QUIT=1</c>: anche «Esci» chiude la sessione su equipe-track, come lo spegnimento.</summary>
    public static bool StopOnQuit => Flag("AT_STOP_ON_QUIT");
    /// <summary><c>AT_NO_BROWSER=1</c>: il collegamento non apre il browser e scrive l'indirizzo nel log (prove).</summary>
    public static bool NoBrowser => Flag("AT_NO_BROWSER");
    /// <summary><c>AT_DB_PATH=C:\percorso\file.sqlite</c>: database alternativo.</summary>
    public static string? DatabasePathOverride => Environment.GetEnvironmentVariable("AT_DB_PATH") is { Length: > 0 } p ? p : null;

    private static bool Flag(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v is not null && !(v.Length == 0 || v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Registro minimo su file (<c>%LOCALAPPDATA%\activity-tracker\agent.log</c>), mai segreti né indirizzi
/// (sola eccezione, per le prove: con <c>AT_NO_BROWSER=1</c> l'indirizzo del collegamento, che non contiene segreti).</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly string FilePath = Path.Combine(AppIdentity.DataDirectory(), "agent.log");

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception)
            {
            }
        }
    }
}

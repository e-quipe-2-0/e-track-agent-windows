using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ActivityTracker.Core;

// IL COLLEGAMENTO SENZA CODICI (giro U). Un flusso a codice di dispositivo: l'agente senza credenziali crea una
// richiesta, apre il browser su `{server}/collega/{codice_utente}` e aspetta; la persona, già entrata in equipe-track,
// preme «Collega»; l'agente ritira il token e lo salva in Gestione credenziali. Nessun codice si vede né si copia.
//
//   POST /api/collega/richiesta   { dispositivo: { id, nome, sistema: "windows" }, versione }
//        201 { ok, codice_dispositivo, codice_utente, scade_tra, intervallo } · 400 · 429
//   POST /api/collega/ritira      { codice_dispositivo, dispositivo_id }
//        200 { stato: "in_attesa" } · 200 { stato: "collegato", token } (una volta sola)
//        403 { stato: "altro_computer" } · 404 { stato: "sconosciuta" } · 410 { stato: "scaduta" | "usata" | "rifiutata" }
//
// Nessuna autenticazione e nessun cookie: chi chiama non ha ancora niente. Il codice del dispositivo e il token non
// vanno MAI in un log né in un messaggio.

// Tipi del contratto (chiavi come nel contratto)

public sealed record DispositivoCollegamento(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("sistema")] string Sistema);

/// <summary>Corpo di <c>POST /api/collega/richiesta</c>.</summary>
public sealed record RichiestaCollegamento(
    [property: JsonPropertyName("dispositivo")] DispositivoCollegamento Dispositivo,
    [property: JsonPropertyName("versione")] string Versione);

/// <summary>Risposta 201 di <c>POST /api/collega/richiesta</c>.</summary>
public sealed class RispostaRichiestaCollegamento
{
    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    /// <summary>Resta all'agente: è il solo modo di ritirare il token. Mai nei log.</summary>
    [JsonPropertyName("codice_dispositivo")] public required string CodiceDispositivo { get; init; }
    /// <summary>Va nell'indirizzo che si apre nel browser.</summary>
    [JsonPropertyName("codice_utente")] public required string CodiceUtente { get; init; }
    [JsonPropertyName("scade_tra")] public required int ScadeTra { get; init; }
    [JsonPropertyName("intervallo")] public required int Intervallo { get; init; }

    public override string ToString() => "RispostaRichiestaCollegamento(codici ***)";
}

/// <summary>Corpo di <c>POST /api/collega/ritira</c>.</summary>
public sealed record RitiroCollegamento(
    [property: JsonPropertyName("codice_dispositivo")] string CodiceDispositivo,
    [property: JsonPropertyName("dispositivo_id")] string DispositivoId)
{
    public override string ToString() => "RitiroCollegamento(codice ***)";
}

/// <summary>Risposta di <c>POST /api/collega/ritira</c> (anche 403 / 404 / 410: lo stato sta nel corpo).</summary>
public sealed class RispostaRitiroCollegamento
{
    [JsonPropertyName("ok")] public bool? Ok { get; init; }
    [JsonPropertyName("stato")] public required string Stato { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }

    public override string ToString() => $"RispostaRitiroCollegamento({Stato})";
}

// Il computer

/// <summary>
/// Chi è questo computer per equipe-track: l'impronta (64 cifre esadecimali minuscole, SHA-256 di una stringa stabile
/// per macchina con un prefisso fisso dell'app, così non è confrontabile con l'impronta presa da altri programmi) e il
/// nome mostrato alla persona nella pagina «Collega questo computer».
/// </summary>
public sealed record DeviceIdentity(string Id, string Name)
{
    /// <summary><c>AT_DEVICE_ID=&lt;64 esadecimali&gt;</c>: impronta fissata a mano (solo prove).</summary>
    public const string Variable = "AT_DEVICE_ID";
    public const string SystemName = "windows";
    public const string FingerprintPrefix = AppIdentity.Identifier + "/dispositivo/";

    /// <summary>SHA-256 esadecimale minuscolo di prefisso + stringa stabile (senza spazi ai bordi, in minuscolo).</summary>
    public static string Fingerprint(string stable) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FingerprintPrefix + stable.Trim().ToLowerInvariant())));

    public static bool IsWellFormedId(string? id) =>
        id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>L'impronta, nell'ordine: <c>AT_DEVICE_ID</c> se ha la forma giusta (maiuscole ammesse); l'impronta del
    /// <c>MachineGuid</c> di Windows; altrimenti quella di un GUID generato una volta e salvato nelle impostazioni
    /// (<paramref name="savedGuid"/> lo legge o lo crea).</summary>
    public static string ResolveId(string? environmentValue, string? machineGuid, Func<string> savedGuid)
    {
        var forced = environmentValue?.Trim().ToLowerInvariant();
        if (IsWellFormedId(forced)) return forced!;
        return Fingerprint(string.IsNullOrWhiteSpace(machineGuid) ? savedGuid() : machineGuid);
    }

    public override string ToString() => $"DeviceIdentity({Name})";
}

// Il client

public enum PickupKind
{
    /// <summary>Nessuno ha ancora premuto «Collega»: si riprova dopo <c>intervallo</c> secondi.</summary>
    Waiting,
    /// <summary>Collegato: il token esce una volta sola.</summary>
    Linked,
    /// <summary>403: l'impronta non è quella della creazione.</summary>
    OtherComputer,
    /// <summary>404.</summary>
    Unknown,
    /// <summary>410.</summary>
    Expired,
    Used,
    Refused,
}

/// <summary>Esito di un ritiro. Il token c'è solo su <see cref="PickupKind.Linked"/> e non si stampa mai.</summary>
public sealed record PickupOutcome(PickupKind Kind, string? Token = null)
{
    public override string ToString() => $"PickupOutcome({Kind}{(Token is null ? "" : ", token ***")})";
}

/// <summary>Le due chiamate del collegamento. Stesso trasporto iniettabile di <see cref="SyncClient"/>; nessuna
/// intestazione <c>Authorization</c>, nessun cookie.</summary>
public sealed class DeviceLinkClient
{
    public const string RequestPath = "/api/collega/richiesta";
    public const string PickupPath = "/api/collega/ritira";
    public const string PagePath = "/collega/";

    public Uri BaseUrl { get; }
    public string AgentVersion { get; }
    public IHttpTransport Transport { get; }

    public DeviceLinkClient(Uri baseUrl, string agentVersion = AppIdentity.AgentVersion, IHttpTransport? transport = null)
    {
        BaseUrl = baseUrl;
        AgentVersion = agentVersion;
        Transport = transport ?? new HttpClientTransport();
    }

    /// <summary>Forma dei due codici: base64url. Si controlla prima di usarli (uno finisce in un indirizzo da aprire).</summary>
    public static bool IsWellFormedDeviceCode(string? code) => IsBase64Url(code, 40, 64);
    public static bool IsWellFormedUserCode(string? code) => IsBase64Url(code, 16, 64);

    private static bool IsBase64Url(string? s, int min, int max) =>
        s is not null && s.Length >= min && s.Length <= max
        && s.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    /// <summary>L'indirizzo da aprire nel browser: lo costruisce l'agente dal SUO indirizzo del server (il server non
    /// glielo dice). null se il codice non ha la forma attesa.</summary>
    public string? LinkUrl(string userCode) =>
        IsWellFormedUserCode(userCode) ? SyncClient.Text(BaseUrl) + PagePath + userCode : null;

    /// <summary><c>POST /api/collega/richiesta</c>. Errori come <see cref="SyncException"/>: 400 = rifiutata, 429 =
    /// troppe richieste (riprovare più tardi), rete e 5xx = transitori.</summary>
    public async Task<RispostaRichiestaCollegamento> RichiestaAsync(DeviceIdentity device, CancellationToken ct = default)
    {
        var body = new RichiestaCollegamento(new DispositivoCollegamento(device.Id, device.Name, DeviceIdentity.SystemName), AgentVersion);
        var (status, data) = await PostAsync(RequestPath, SyncJson.Encode(body), ct).ConfigureAwait(false);
        switch (status)
        {
            case >= 200 and < 300:
                RispostaRichiestaCollegamento r;
                try { r = SyncJson.Decode<RispostaRichiestaCollegamento>(data); }
                catch (Exception) { throw new SyncException(new SyncFault(SyncErrorKind.Decoding)); }
                if (!IsWellFormedDeviceCode(r.CodiceDispositivo) || !IsWellFormedUserCode(r.CodiceUtente))
                    throw new SyncException(new SyncFault(SyncErrorKind.Decoding));
                return r;
            default:
                throw new SyncException(Fault(status, data));
        }
    }

    /// <summary><c>POST /api/collega/ritira</c>. Gli stati del contratto (anche 403 / 404 / 410) tornano come esito;
    /// rete, 429 e 5xx come <see cref="SyncException"/> transitoria: si riprova al giro successivo.</summary>
    public async Task<PickupOutcome> RitiraAsync(string deviceCode, string deviceId, CancellationToken ct = default)
    {
        var (status, data) = await PostAsync(PickupPath, SyncJson.Encode(new RitiroCollegamento(deviceCode, deviceId)), ct).ConfigureAwait(false);
        RispostaRitiroCollegamento? r = null;
        try { r = SyncJson.Decode<RispostaRitiroCollegamento>(data); } catch (Exception) { }
        switch (status, r?.Stato)
        {
            case (>= 200 and < 300, "in_attesa"):
                return new PickupOutcome(PickupKind.Waiting);
            case (>= 200 and < 300, "collegato"):
                if (r!.Token is not { } token || !SyncClient.IsWellFormedToken(token)) throw new SyncException(new SyncFault(SyncErrorKind.Decoding));
                return new PickupOutcome(PickupKind.Linked, token.Trim());
            case (>= 200 and < 300, _):
                throw new SyncException(new SyncFault(SyncErrorKind.Decoding));
            case (403, "altro_computer"):
                return new PickupOutcome(PickupKind.OtherComputer);
            case (404, "sconosciuta"):
                return new PickupOutcome(PickupKind.Unknown);
            case (410, "scaduta"):
                return new PickupOutcome(PickupKind.Expired);
            case (410, "usata"):
                return new PickupOutcome(PickupKind.Used);
            case (410, _):
                return new PickupOutcome(PickupKind.Refused);
            default:
                throw new SyncException(Fault(status, data));
        }
    }

    // Interni

    private async Task<(int Status, byte[] Data)> PostAsync(string path, byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SyncClient.Text(BaseUrl) + path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await Transport.SendAsync(request, ct).ConfigureAwait(false);
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return ((int)response.StatusCode, data);
        }
        catch (Exception e)
        {
            throw new SyncException(new SyncFault(SyncErrorKind.Network, SyncClient.Describe(e)));
        }
    }

    private static SyncFault Fault(int status, byte[] data) => status switch
    {
        429 => new SyncFault(SyncErrorKind.RateLimited),
        >= 500 and < 600 => new SyncFault(SyncErrorKind.Server, Status: status),
        401 => new SyncFault(SyncErrorKind.Gate),
        403 => new SyncFault(SyncErrorKind.Forbidden),
        400 or 404 or 413 => new SyncFault(SyncErrorKind.BadRequest, SyncClient.ServerMessage(data, $"richiesta rifiutata ({status})")),
        _ => new SyncFault(SyncErrorKind.BadRequest, SyncClient.ServerMessage(data, $"risposta inattesa ({status})")),
    };
}

// La macchina a stati

public enum LinkPhase
{
    /// <summary>Non collegato, nessuna richiesta in corso.</summary>
    NotLinked,
    /// <summary>Richiesta in corso (<c>POST /api/collega/richiesta</c> partita).</summary>
    Requesting,
    /// <summary>In attesa che la persona prema «Collega» nel browser, fino a <see cref="DeviceLinkMachine.ExpiresAt"/>.</summary>
    Waiting,
    Linked,
    /// <summary>Dieci minuti passati senza che nessuno abbia premuto «Collega».</summary>
    Expired,
    /// <summary>Il server ha chiuso la richiesta: già usata, di un altro computer, sconosciuta o rifiutata.</summary>
    Rejected,
    /// <summary>Errore non transitorio (richiesta non creata, risposta non valida, credenziali non salvate).</summary>
    Failed,
}

/// <summary>
/// Stato del collegamento, pura: nessuna rete, nessun orologio proprio (gli istanti arrivano da chi la guida), così si
/// verifica senza server. Un errore di rete durante l'attesa non perde la richiesta: si riprova al giro successivo
/// finché non scade. Il codice del dispositivo vive solo qui dentro, in memoria, e si cancella alla fine.
/// </summary>
public sealed class DeviceLinkMachine
{
    public const int DefaultLifetimeSeconds = 600;
    public const int DefaultIntervalSeconds = 3;

    public LinkPhase Phase { get; private set; }
    /// <summary>Quando scade la richiesta in attesa (orologio di questo computer, dall'istante della risposta).</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }
    /// <summary>Ogni quanto ritirare.</summary>
    public TimeSpan Interval { get; private set; } = TimeSpan.FromSeconds(DefaultIntervalSeconds);
    /// <summary>Il codice pubblico, per l'indirizzo da aprire. Solo in attesa.</summary>
    public string? UserCode { get; private set; }
    /// <summary>Perché è finita male, o l'ultimo errore transitorio durante l'attesa. Mai codici né token.</summary>
    public string? Detail { get; private set; }

    private string? _deviceCode;

    public DeviceLinkMachine(bool linked = false) => Phase = linked ? LinkPhase.Linked : LinkPhase.NotLinked;

    /// <summary>C'è una richiesta viva (in creazione o in attesa).</summary>
    public bool IsActive => Phase is LinkPhase.Requesting or LinkPhase.Waiting;

    /// <summary>Comincia una richiesta nuova. Falso (e niente cambia) se ce n'è già una viva.</summary>
    public bool Begin()
    {
        if (IsActive) return false;
        Clear();
        Phase = LinkPhase.Requesting;
        return true;
    }

    public void RequestSucceeded(RispostaRichiestaCollegamento r, DateTimeOffset now)
    {
        if (Phase != LinkPhase.Requesting) return;
        _deviceCode = r.CodiceDispositivo;
        UserCode = r.CodiceUtente;
        ExpiresAt = now.AddSeconds(r.ScadeTra is > 0 and <= 3600 ? r.ScadeTra : DefaultLifetimeSeconds);
        Interval = TimeSpan.FromSeconds(r.Intervallo is > 0 and <= 60 ? r.Intervallo : DefaultIntervalSeconds);
        Detail = null;
        Phase = LinkPhase.Waiting;
    }

    /// <summary>La richiesta non è stata creata: non c'è niente da conservare, si riprova dal menu.</summary>
    public void RequestFailed(SyncFault fault)
    {
        if (Phase != LinkPhase.Requesting) return;
        End(LinkPhase.Failed, SyncMessages.For(fault));
    }

    /// <summary>Il codice da presentare al prossimo ritiro; null se non si è in attesa. Se la richiesta è scaduta lo
    /// stato passa a <see cref="LinkPhase.Expired"/> e non si ritira più.</summary>
    public string? PickupCode(DateTimeOffset now)
    {
        if (Phase != LinkPhase.Waiting) return null;
        if (ExpiresAt is { } t && now >= t)
        {
            End(LinkPhase.Expired, null);
            return null;
        }
        return _deviceCode;
    }

    /// <summary>La risposta riguarda la richiesta viva? (Falso dopo uno «Scollega» o una richiesta nuova.)</summary>
    public bool IsCurrent(string deviceCode) => Phase == LinkPhase.Waiting && _deviceCode == deviceCode;

    /// <summary>Esito di un ritiro. Restituisce il token solo su «collegato» (chi chiama lo salva subito; qui non resta).</summary>
    public string? PickupSucceeded(PickupOutcome outcome, DateTimeOffset now)
    {
        if (Phase != LinkPhase.Waiting) return null;
        switch (outcome.Kind)
        {
            case PickupKind.Waiting:
                Detail = null;
                PickupCode(now);
                return null;
            case PickupKind.Linked:
                End(LinkPhase.Linked, null);
                return outcome.Token;
            case PickupKind.Expired:
                End(LinkPhase.Expired, null);
                return null;
            default:
                End(LinkPhase.Rejected, outcome.Kind switch
                {
                    PickupKind.Used => "richiesta già usata",
                    PickupKind.OtherComputer => "la richiesta appartiene a un altro computer",
                    PickupKind.Unknown => "richiesta sconosciuta al server",
                    _ => "richiesta rifiutata",
                });
                return null;
        }
    }

    /// <summary>Ritiro fallito. Transitorio (rete, 5xx, 429): la richiesta resta, si riprova al giro successivo finché
    /// non scade. Altrimenti è finita.</summary>
    public void PickupFailed(SyncFault fault, DateTimeOffset now)
    {
        if (Phase != LinkPhase.Waiting) return;
        if (!fault.IsRetryable)
        {
            End(LinkPhase.Failed, SyncMessages.For(fault));
            return;
        }
        Detail = SyncMessages.For(fault);
        PickupCode(now);
    }

    /// <summary>Il token è arrivato ma non si è potuto salvare (Gestione credenziali).</summary>
    public void SaveFailed(string message) => End(LinkPhase.Failed, message);

    /// <summary>Credenziali presenti (avvio già collegato).</summary>
    public void MarkLinked()
    {
        if (!IsActive) End(LinkPhase.Linked, null);
    }

    /// <summary>«Scollega»: si torna a non collegato e la richiesta viva, se c'è, si dimentica.</summary>
    public void Reset() => End(LinkPhase.NotLinked, null);

    private void End(LinkPhase phase, string? detail)
    {
        Clear();
        Phase = phase;
        Detail = detail;
    }

    private void Clear()
    {
        _deviceCode = null;
        UserCode = null;
        ExpiresAt = null;
        Detail = null;
    }

    /// <summary>La riga di stato per tray e Impostazioni.</summary>
    public string StatusText => Phase switch
    {
        LinkPhase.NotLinked => "Non collegato",
        LinkPhase.Requesting => "Collegamento in corso…",
        LinkPhase.Waiting => "In attesa del collegamento nel browser…",
        LinkPhase.Linked => "Collegato",
        LinkPhase.Expired => "Collegamento scaduto: riprova dal menu",
        _ => "Collegamento non riuscito: riprova dal menu",
    };

    public override string ToString() => $"DeviceLinkMachine({Phase})";
}

/// <summary>Le decisioni del collegamento che non dipendono da Windows.</summary>
public static class DeviceLinkPolicy
{
    /// <summary>All'avvio il collegamento parte da solo (richiesta + browser) solo la PRIMA volta che l'agente si trova
    /// senza credenziali; negli avvii successivi senza credenziali resta la voce di menu «Collega questo computer».</summary>
    public static bool StartsAtLaunch(bool hasCredentials, bool linkAttempted) => !hasCredentials && !linkAttempted;

    /// <summary>La voce «Collega questo computer» c'è quando non si è collegati o quando il server ha rifiutato il token.</summary>
    public static bool OffersLink(bool hasCredentials, bool tokenRejected) => !hasCredentials || tokenRejected;
}

/// <summary>
/// Il giro del collegamento: la macchina a stati più le due chiamate. Non ha timer né thread: chi lo guida (nell'app
/// un timer dell'interfaccia, nelle verifiche il codice di prova) chiama <see cref="BeginAsync"/> una volta e
/// <see cref="PollAsync"/> ogni <see cref="DeviceLinkMachine.Interval"/> finché <see cref="DeviceLinkMachine.IsActive"/>.
/// </summary>
public sealed class DeviceLinkFlow
{
    public DeviceLinkMachine Machine { get; }

    private readonly DeviceLinkClient _client;
    private readonly Func<DeviceIdentity> _device;
    private readonly Func<string, string?> _saveToken;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private DeviceIdentity? _identity;
    private bool _polling;

    /// <param name="saveToken">Salva il token appena ritirato (stesso percorso del salvataggio delle credenziali);
    /// restituisce null se è andata bene, altrimenti il messaggio d'errore.</param>
    public DeviceLinkFlow(DeviceLinkClient client, Func<DeviceIdentity> device, Func<string, string?> saveToken,
                          DeviceLinkMachine? machine = null, Func<DateTimeOffset>? clock = null, Action<string>? log = null)
    {
        _client = client;
        _device = device;
        _saveToken = saveToken;
        Machine = machine ?? new DeviceLinkMachine();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    /// <summary>L'indirizzo da aprire nel browser per la richiesta in attesa; null se non ce n'è una.</summary>
    public string? LinkUrl => Machine.Phase == LinkPhase.Waiting && Machine.UserCode is { } code ? _client.LinkUrl(code) : null;

    /// <summary>Crea la richiesta. True se ora si è in attesa della persona (c'è un <see cref="LinkUrl"/> da aprire).</summary>
    public async Task<bool> BeginAsync()
    {
        if (!Machine.Begin()) return false;
        try
        {
            _identity ??= _device();
            var r = await _client.RichiestaAsync(_identity);
            Machine.RequestSucceeded(r, _clock());
            _log?.Invoke($"[Collegamento] richiesta creata, scade fra {r.ScadeTra} s");
        }
        catch (Exception e)
        {
            Machine.RequestFailed(e is SyncException { Fault: var fault } ? fault : new SyncFault(SyncErrorKind.Network, e.Message));
            _log?.Invoke("[Collegamento] richiesta non creata: " + Machine.Detail);
        }
        return Machine.Phase == LinkPhase.Waiting;
    }

    /// <summary>Un ritiro. Non fa niente se non si è in attesa o se un ritiro è già in volo.</summary>
    public async Task PollAsync()
    {
        if (_polling || _identity is null || Machine.Phase != LinkPhase.Waiting) return;
        if (Machine.PickupCode(_clock()) is not { } code)
        {
            LogEnd();
            return;
        }
        _polling = true;
        try
        {
            var outcome = await _client.RitiraAsync(code, _identity.Id);
            if (!Machine.IsCurrent(code)) return;
            if (Machine.PickupSucceeded(outcome, _clock()) is { } token)
            {
                if (_saveToken(token) is { } error) Machine.SaveFailed(error);
            }
        }
        catch (Exception e)
        {
            if (!Machine.IsCurrent(code)) return;
            Machine.PickupFailed(e is SyncException { Fault: var fault } ? fault : new SyncFault(SyncErrorKind.Network, e.Message), _clock());
        }
        finally
        {
            _polling = false;
        }
        LogEnd();
    }

    private void LogEnd()
    {
        if (Machine.IsActive) return;
        _log?.Invoke("[Collegamento] " + Machine.Phase switch
        {
            LinkPhase.Linked => "computer collegato",
            LinkPhase.Expired => "richiesta scaduta",
            _ => "non riuscito: " + Machine.Detail,
        });
    }
}

using System.Text;
using System.Text.Json;
using ActivityTracker.Core;
using static ActivityTracker.Checks.H;

namespace ActivityTracker.Checks;

/// <summary>Trasporto vero che, a comando, fa fallire le prossime richieste come se mancasse la rete.</summary>
public sealed class FlakyTransport : IHttpTransport
{
    private readonly HttpClientTransport _real = new();
    public int FailNext;
    public int Failed;

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        if (FailNext > 0)
        {
            FailNext--;
            Failed++;
            throw new HttpRequestException("rete assente (finta)", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable));
        }
        return _real.SendAsync(request, cancellationToken);
    }
}

/// <summary>Il collegamento senza codici (giro U): impronta, macchina a stati, client e il giro vero contro il server finto.</summary>
public static class LinkChecks
{
    private static readonly string DeviceId = DeviceIdentity.Fingerprint("11111111-2222-3333-4444-555555555555");
    private static readonly DeviceIdentity Device = new(DeviceId, "PC-DI-PROVA");
    private static readonly string DeviceCode = new('D', 43);
    private static readonly string UserCode = new('u', 22);

    private static RispostaRichiestaCollegamento Created(int scadeTra = 600, int intervallo = 3) =>
        new() { Ok = true, CodiceDispositivo = DeviceCode, CodiceUtente = UserCode, ScadeTra = scadeTra, Intervallo = intervallo };

    private static DeviceLinkMachine WaitingMachine()
    {
        var m = new DeviceLinkMachine();
        m.Begin();
        m.RequestSucceeded(Created(), T0);
        return m;
    }

    public static void Run()
    {
        Fingerprint();
        Machine();
        Client();
        Permissions();
        FullRound();
    }

    private static void Fingerprint()
    {
        Section("Collegamento: impronta del computer");
        var guid = "8f6a2c1e-0b7d-4e55-9a3f-1c2d3e4f5a6b";
        var id = DeviceIdentity.Fingerprint(guid);
        Check("impronta: 64 cifre esadecimali minuscole", DeviceIdentity.IsWellFormedId(id), id);
        Check("impronta stabile, senza differenze per maiuscole o spazi ai bordi", id == DeviceIdentity.Fingerprint("  " + guid.ToUpperInvariant() + " "));
        Check("impronta con il prefisso dell'app: non è lo SHA-256 del solo MachineGuid",
              id != Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(guid))));
        Check("macchine diverse → impronte diverse", id != DeviceIdentity.Fingerprint("00000000-0000-0000-0000-000000000000"));
        var forced = new string('a', 60) + "0f9e";
        var saved = 0;
        string Saved()
        {
            saved++;
            return "guid-salvato-nelle-impostazioni";
        }
        Check("AT_DEVICE_ID valido vince (maiuscole ammesse, esce minuscolo)", DeviceIdentity.ResolveId(forced.ToUpperInvariant(), guid, Saved) == forced);
        Check("AT_DEVICE_ID di forma sbagliata ignorato → MachineGuid", DeviceIdentity.ResolveId("abc", guid, Saved) == id && DeviceIdentity.ResolveId(new string('g', 64), guid, Saved) == id);
        Check("MachineGuid leggibile: il GUID delle impostazioni non si tocca", saved == 0);
        Check("MachineGuid non leggibile → impronta del GUID salvato nelle impostazioni",
              DeviceIdentity.ResolveId(null, null, Saved) == DeviceIdentity.Fingerprint("guid-salvato-nelle-impostazioni")
              && DeviceIdentity.ResolveId("", "  ", Saved) == DeviceIdentity.Fingerprint("guid-salvato-nelle-impostazioni") && saved == 2);
        Check("forma dell'impronta: solo 64 esadecimali minuscoli", !DeviceIdentity.IsWellFormedId(id.ToUpperInvariant()) && !DeviceIdentity.IsWellFormedId(id[..63]) && !DeviceIdentity.IsWellFormedId(null));
    }

    private static void Machine()
    {
        Section("Collegamento: macchina a stati (pura, senza rete)");
        var m = new DeviceLinkMachine();
        Check("all'inizio: non collegato, niente di vivo", m.Phase == LinkPhase.NotLinked && !m.IsActive && m.StatusText == "Non collegato");
        Check("nessuna richiesta → nessun codice da ritirare", m.PickupCode(T0) is null && m.Phase == LinkPhase.NotLinked);
        Check("comincia → richiesta in corso", m.Begin() && m.Phase == LinkPhase.Requesting && m.IsActive);
        Check("una richiesta alla volta: il secondo «comincia» non fa niente", !m.Begin() && m.Phase == LinkPhase.Requesting);
        m.RequestSucceeded(Created(), T0);
        Check("richiesta creata → in attesa della persona, scadenza fra 600 s, ritiro ogni 3 s",
              m.Phase == LinkPhase.Waiting && m.ExpiresAt == T0.AddSeconds(600) && m.Interval == TimeSpan.FromSeconds(3) && m.UserCode == UserCode);
        Check("testo: «In attesa del collegamento nel browser…»", m.StatusText == "In attesa del collegamento nel browser…");
        Check("in attesa: il codice da ritirare c'è fino alla scadenza", m.PickupCode(T0.AddSeconds(599)) == DeviceCode && m.IsCurrent(DeviceCode));
        Check("in attesa non si comincia un'altra richiesta", !m.Begin() && m.Phase == LinkPhase.Waiting);
        Check("«in attesa» dal server → si resta in attesa", m.PickupSucceeded(new PickupOutcome(PickupKind.Waiting), T0.AddSeconds(3)) is null && m.Phase == LinkPhase.Waiting);

        m.PickupFailed(new SyncFault(SyncErrorKind.Network, "nessuna connessione"), T0.AddSeconds(6));
        Check("errore di rete: la richiesta non si perde, si riprova al giro successivo",
              m.Phase == LinkPhase.Waiting && m.PickupCode(T0.AddSeconds(9)) == DeviceCode && m.Detail == "Rete: nessuna connessione", m.Detail ?? "null");
        m.PickupFailed(new SyncFault(SyncErrorKind.Server, Status: 503), T0.AddSeconds(9));
        m.PickupFailed(new SyncFault(SyncErrorKind.RateLimited), T0.AddSeconds(12));
        Check("5xx e 429 al ritiro: transitori anche loro", m.Phase == LinkPhase.Waiting && m.ExpiresAt == T0.AddSeconds(600));
        m.PickupSucceeded(new PickupOutcome(PickupKind.Waiting), T0.AddSeconds(15));
        Check("alla prima risposta buona l'errore transitorio sparisce", m.Detail is null);
        var token = m.PickupSucceeded(new PickupOutcome(PickupKind.Linked, "tok_" + new string('x', 30)), T0.AddSeconds(18));
        Check("«collegato» → il token esce una volta, lo stato è collegato", token == "tok_" + new string('x', 30) && m.Phase == LinkPhase.Linked && m.StatusText == "Collegato");
        Check("collegato: codici dimenticati, niente da ritirare", m.UserCode is null && m.ExpiresAt is null && m.PickupCode(T0.AddSeconds(21)) is null && !m.IsCurrent(DeviceCode));
        Check("una risposta arrivata dopo non cambia niente", m.PickupSucceeded(new PickupOutcome(PickupKind.Used), T0.AddSeconds(24)) is null && m.Phase == LinkPhase.Linked);

        var e = WaitingMachine();
        Check("scadenza: allo scadere non si ritira più → scaduta", e.PickupCode(T0.AddSeconds(600)) is null && e.Phase == LinkPhase.Expired && !e.IsActive);
        Check("testo: «Collegamento scaduto: riprova dal menu»", e.StatusText == "Collegamento scaduto: riprova dal menu");
        Check("dopo la scadenza si può ricominciare dal menu", e.Begin() && e.Phase == LinkPhase.Requesting);
        var e2 = WaitingMachine();
        e2.PickupFailed(new SyncFault(SyncErrorKind.Network, "tempo scaduto"), T0.AddSeconds(601));
        Check("errore di rete oltre la scadenza → scaduta, non si ritenta all'infinito", e2.Phase == LinkPhase.Expired);
        var e3 = WaitingMachine();
        e3.PickupSucceeded(new PickupOutcome(PickupKind.Expired), T0.AddSeconds(10));
        Check("410 «scaduta» dal server → scaduta", e3.Phase == LinkPhase.Expired);

        foreach (var (kind, text) in new[] { (PickupKind.Used, "richiesta già usata"), (PickupKind.OtherComputer, "la richiesta appartiene a un altro computer"),
                                             (PickupKind.Unknown, "richiesta sconosciuta al server"), (PickupKind.Refused, "richiesta rifiutata") })
        {
            var r = WaitingMachine();
            var t = r.PickupSucceeded(new PickupOutcome(kind), T0.AddSeconds(10));
            Check($"{kind} → fine senza token, non si ritenta da soli ({text})",
                  t is null && r.Phase == LinkPhase.Rejected && !r.IsActive && r.Detail == text && r.PickupCode(T0.AddSeconds(13)) is null, r.Detail ?? "null");
        }
        Check("testo dopo un rifiuto: «Collegamento non riuscito: riprova dal menu»",
              new Func<string>(() => { var r = WaitingMachine(); r.PickupSucceeded(new PickupOutcome(PickupKind.Used), T0); return r.StatusText; })() == "Collegamento non riuscito: riprova dal menu");

        var f = WaitingMachine();
        f.PickupFailed(new SyncFault(SyncErrorKind.BadRequest, "codice_dispositivo non valido"), T0.AddSeconds(3));
        Check("errore non transitorio al ritiro → errore, fine", f.Phase == LinkPhase.Failed && f.Detail!.Contains("codice_dispositivo non valido"));
        var g = new DeviceLinkMachine();
        g.Begin();
        g.RequestFailed(new SyncFault(SyncErrorKind.RateLimited));
        Check("429 alla creazione → errore «riprova fra qualche minuto», si riprova dal menu", g.Phase == LinkPhase.Failed && g.Detail!.Contains("429") && g.Begin());
        var s = WaitingMachine();
        s.PickupSucceeded(new PickupOutcome(PickupKind.Linked, "tok_" + new string('y', 30)), T0);
        s.SaveFailed("Gestione credenziali: accesso negato");
        Check("token arrivato ma non salvato → errore con il motivo", s.Phase == LinkPhase.Failed && s.Detail == "Gestione credenziali: accesso negato");

        var d = WaitingMachine();
        d.Reset();
        Check("«Scollega» durante l'attesa: non collegato, richiesta dimenticata", d.Phase == LinkPhase.NotLinked && !d.IsCurrent(DeviceCode) && d.UserCode is null);
        var l = new DeviceLinkMachine(linked: true);
        Check("avvio con le credenziali: collegato; «Collega di nuovo» ricomincia", l.Phase == LinkPhase.Linked && l.Begin());
        var odd = new DeviceLinkMachine();
        odd.Begin();
        odd.RequestSucceeded(Created(scadeTra: 0, intervallo: -5), T0);
        Check("durata o intervallo assurdi dal server → 600 s e 3 s", odd.ExpiresAt == T0.AddSeconds(600) && odd.Interval == TimeSpan.FromSeconds(3));
        Check("i codici e il token non si stampano mai",
              !WaitingMachine().ToString().Contains(DeviceCode) && !Created().ToString().Contains(DeviceCode) && !Created().ToString().Contains(UserCode)
              && !new RitiroCollegamento(DeviceCode, DeviceId).ToString().Contains(DeviceCode)
              && !new PickupOutcome(PickupKind.Linked, "tok_segretissimo_123456789").ToString().Contains("segretissimo"));

        Check("primo avvio senza credenziali → il collegamento parte da solo", DeviceLinkPolicy.StartsAtLaunch(hasCredentials: false, linkAttempted: false));
        Check("avvii successivi senza credenziali → non parte da solo (resta il menu)", !DeviceLinkPolicy.StartsAtLaunch(hasCredentials: false, linkAttempted: true));
        Check("con le credenziali non parte mai da solo", !DeviceLinkPolicy.StartsAtLaunch(true, false) && !DeviceLinkPolicy.StartsAtLaunch(true, true));
        Check("voce «Collega questo computer»: senza credenziali o con il token rifiutato",
              DeviceLinkPolicy.OffersLink(false, false) && DeviceLinkPolicy.OffersLink(true, true) && !DeviceLinkPolicy.OffersLink(true, false));
        Check("401 della rotta: il messaggio dice di ricollegare il computer dal menu, non parla più di codici",
              SyncMessages.For(new SyncFault(SyncErrorKind.Unauthorized)).Contains("ricollega questo computer dal menu")
              && !Enum.GetValues<SyncErrorKind>().Any(k => SyncMessages.For(new SyncFault(k, "x", Status: 500)).Contains("odice di collegamento")));
    }

    private static void Client()
    {
        Section("Collegamento: client con trasporto finto");
        var transport = new MockTransport();
        var client = new DeviceLinkClient(new Uri("https://track.esempio.it"), "9.9.9-windows", transport);

        transport.Responses.Enqueue(new(201, $$"""{"ok":true,"codice_dispositivo":"{{DeviceCode}}","codice_utente":"{{UserCode}}","scade_tra":600,"intervallo":3}"""));
        var created = client.RichiestaAsync(Device).GetAwaiter().GetResult();
        var req = transport.Last!;
        var body = transport.LastBodyJson()!.Value;
        Check("richiesta: POST /api/collega/richiesta", req.Method == "POST" && req.Url == "https://track.esempio.it/api/collega/richiesta", req.Url);
        Check("richiesta: dispositivo { id, nome, sistema: windows } e versione",
              body.GetProperty("dispositivo").GetProperty("id").GetString() == DeviceId && body.GetProperty("dispositivo").GetProperty("nome").GetString() == "PC-DI-PROVA"
              && body.GetProperty("dispositivo").GetProperty("sistema").GetString() == "windows" && body.GetProperty("versione").GetString() == "9.9.9-windows");
        Check("richiesta: solo le chiavi del contratto", SyncChecks.JsonKeys(body).SetEquals(["dispositivo", "id", "nome", "sistema", "versione"]), Join(SyncChecks.JsonKeys(body)));
        Check("richiesta: nessuna autenticazione, nessun cookie", !req.Headers.ContainsKey("Authorization") && !req.Headers.ContainsKey("Cookie"));
        Check("richiesta: risposta 201 decodificata", created.CodiceDispositivo == DeviceCode && created.CodiceUtente == UserCode && created.ScadeTra == 600 && created.Intervallo == 3);
        Check("indirizzo da aprire: {server}/collega/{codice_utente}", client.LinkUrl(UserCode) == "https://track.esempio.it/collega/" + UserCode, client.LinkUrl(UserCode) ?? "null");
        Check("indirizzo: il percorso del server resta, la barra finale no",
              new DeviceLinkClient(SyncClient.Validate("https://esempio.it/track/", false).Url!, transport: transport).LinkUrl(UserCode) == "https://esempio.it/track/collega/" + UserCode);
        Check("codice pubblico di forma sbagliata → nessun indirizzo da aprire", client.LinkUrl("../../altro?x=1") is null && client.LinkUrl("") is null);
        Check("versione predefinita: quella dell'agente", new DeviceLinkClient(new Uri("https://track.esempio.it"), transport: transport).AgentVersion == AppIdentity.AgentVersion);

        SyncFault? RequestFailure(MockTransport.Canned canned)
        {
            transport.Responses.Enqueue(canned);
            try { client.RichiestaAsync(Device).GetAwaiter().GetResult(); return null; }
            catch (SyncException e) { return e.Fault; }
        }
        Check("richiesta: 429 → troppe richieste, da riprovare più tardi", RequestFailure(new(429, """{"ok":false,"errore":"troppe richieste: riprova fra qualche minuto"}""")) is { Kind: SyncErrorKind.RateLimited, IsRetryable: true });
        Check("richiesta: 400 → rifiutata col messaggio del server", RequestFailure(new(400, """{"ok":false,"errore":"dispositivo.id: servono 64 cifre esadecimali"}"""))
              == new SyncFault(SyncErrorKind.BadRequest, "dispositivo.id: servono 64 cifre esadecimali"));
        Check("richiesta: 500 → errore del server", RequestFailure(new(500, "")) == new SyncFault(SyncErrorKind.Server, Status: 500));
        Check("richiesta: rete assente → errore di rete", RequestFailure(new(0, "", new TaskCanceledException("timeout"))) == new SyncFault(SyncErrorKind.Network, "tempo scaduto"));
        Check("richiesta: 201 senza i codici → risposta non valida", RequestFailure(new(201, """{"ok":true}"""))?.Kind == SyncErrorKind.Decoding);
        Check("richiesta: codici di forma sbagliata → risposta non valida",
              RequestFailure(new(201, $$"""{"ok":true,"codice_dispositivo":"corto","codice_utente":"{{UserCode}}","scade_tra":600,"intervallo":3}"""))?.Kind == SyncErrorKind.Decoding
              && RequestFailure(new(201, $$"""{"ok":true,"codice_dispositivo":"{{DeviceCode}}","codice_utente":"a/b?c=d#e@f.example/xx","scade_tra":600,"intervallo":3}"""))?.Kind == SyncErrorKind.Decoding);

        PickupOutcome? Pickup(int status, string json)
        {
            transport.Responses.Enqueue(new(status, json));
            try { return client.RitiraAsync(DeviceCode, DeviceId).GetAwaiter().GetResult(); }
            catch (SyncException) { return null; }
        }
        SyncFault? PickupFailure(MockTransport.Canned canned)
        {
            transport.Responses.Enqueue(canned);
            try { client.RitiraAsync(DeviceCode, DeviceId).GetAwaiter().GetResult(); return null; }
            catch (SyncException e) { return e.Fault; }
        }
        Check("ritiro: 200 in_attesa → attesa", Pickup(200, """{"ok":true,"stato":"in_attesa"}""") == new PickupOutcome(PickupKind.Waiting));
        var pickupBody = transport.LastBodyJson()!.Value;
        Check("ritiro: POST /api/collega/ritira con codice_dispositivo e dispositivo_id",
              transport.Last!.Method == "POST" && transport.Last.Url == "https://track.esempio.it/api/collega/ritira"
              && pickupBody.GetProperty("codice_dispositivo").GetString() == DeviceCode && pickupBody.GetProperty("dispositivo_id").GetString() == DeviceId
              && SyncChecks.JsonKeys(pickupBody).SetEquals(["codice_dispositivo", "dispositivo_id"]));
        Check("ritiro: nessuna autenticazione, nessun cookie", !transport.Last.Headers.ContainsKey("Authorization") && !transport.Last.Headers.ContainsKey("Cookie"));
        var tok = "tok_" + new string('B', 40);
        Check("ritiro: 200 collegato → il token", Pickup(200, $$"""{"ok":true,"stato":"collegato","token":"{{tok}}"}""") == new PickupOutcome(PickupKind.Linked, tok));
        Check("ritiro: 403 altro_computer", Pickup(403, """{"ok":false,"stato":"altro_computer"}""")?.Kind == PickupKind.OtherComputer);
        Check("ritiro: 404 sconosciuta", Pickup(404, """{"ok":false,"stato":"sconosciuta"}""")?.Kind == PickupKind.Unknown);
        Check("ritiro: 410 scaduta / usata / rifiutata",
              Pickup(410, """{"ok":false,"stato":"scaduta"}""")?.Kind == PickupKind.Expired && Pickup(410, """{"ok":false,"stato":"usata"}""")?.Kind == PickupKind.Used
              && Pickup(410, """{"ok":false,"stato":"rifiutata"}""")?.Kind == PickupKind.Refused);
        Check("ritiro: 200 collegato senza token (o di forma sbagliata) → risposta non valida",
              PickupFailure(new(200, """{"ok":true,"stato":"collegato"}"""))?.Kind == SyncErrorKind.Decoding
              && PickupFailure(new(200, """{"ok":true,"stato":"collegato","token":"corto"}"""))?.Kind == SyncErrorKind.Decoding);
        Check("ritiro: 404 del proxy (rotta assente, nessuno stato) → errore, non «sconosciuta»", PickupFailure(new(404, "Not Found"))?.Kind == SyncErrorKind.BadRequest);
        Check("ritiro: 403 senza stato → errore, non «altro computer»", PickupFailure(new(403, "Accesso negato"))?.Kind == SyncErrorKind.Forbidden);
        Check("ritiro: 400 → rifiutata col messaggio", PickupFailure(new(400, """{"ok":false,"errore":"codice_dispositivo non valido"}""")) == new SyncFault(SyncErrorKind.BadRequest, "codice_dispositivo non valido"));
        Check("ritiro: 429, 5xx e rete → transitori",
              PickupFailure(new(429, "")) is { IsRetryable: true } && PickupFailure(new(502, "")) is { IsRetryable: true }
              && PickupFailure(new(0, "", new HttpRequestException("giù"))) is { Kind: SyncErrorKind.Network, IsRetryable: true });
    }

    private static void Permissions()
    {
        Section("equipe-track: X-Agente-Permessi sulle tre rotte dell'agente");
        var transport = new MockTransport();
        var client = new SyncClient(new Uri("https://track.esempio.it"), "tok_" + new string('A', 32), "9.9.9", transport);
        transport.Responses.Enqueue(new(200, """{"ok":true,"traccia":false,"sessione":null,"adesso":"2024-03-10T10:00:00Z"}"""));
        client.SessioneAsync().GetAwaiter().GetResult();
        Check("GET /api/agente/sessione porta X-Agente-Permessi: ok", transport.Last!.Url.EndsWith("/api/agente/sessione", StringComparison.Ordinal) && transport.Last.Headers.GetValueOrDefault("X-Agente-Permessi") == "ok");
        transport.Responses.Enqueue(new(200, """{"ok":true,"giorno":"2024-03-10","scritti":0,"rimossi":0}"""));
        client.InviaSegmentiAsync(new InvioSegmenti("2024-03-10", [])).GetAwaiter().GetResult();
        Check("POST /api/agente/segmenti porta X-Agente-Permessi: ok", transport.Last!.Url.EndsWith("/api/agente/segmenti", StringComparison.Ordinal) && transport.Last.Headers.GetValueOrDefault("X-Agente-Permessi") == "ok");
        transport.Responses.Enqueue(new(200, """{"ok":true,"chiusa":false}"""));
        client.StopAsync(null).GetAwaiter().GetResult();
        Check("POST /api/agente/stop porta X-Agente-Permessi: ok", transport.Last!.Url.EndsWith("/api/agente/stop", StringComparison.Ordinal) && transport.Last.Headers.GetValueOrDefault("X-Agente-Permessi") == "ok");
        Check("le altre intestazioni restano: Bearer e versione", transport.Requests.All(x => x.Headers.ContainsKey("Authorization") && x.Headers.GetValueOrDefault("X-Agente-Versione") == "9.9.9"));
    }

    private static void FullRound()
    {
        Section("Collegamento: il giro intero contro un server finto (HTTP vero su 127.0.0.1)");
        var token = "tok_" + Guid.NewGuid().ToString("N");
        using var server = new FakeEquipeTrack(token);
        if (!server.Start())
        {
            Check("server finto in ascolto su 127.0.0.1", false, "impossibile aprire la porta");
            return;
        }
        var serverUrl = $"http://127.0.0.1:{server.Port}";
        if (SyncClient.Validate(serverUrl, true).Url is not { } baseUrl)
        {
            Check("URL del server finto valido", false);
            return;
        }
        var log = new List<string>();
        // Il salvataggio: lo stesso percorso dell'app (EquipeCredentials.ForLink + Save), nello store in memoria.
        Func<string, string?> SaveInto(InMemoryTokenStore store) => t =>
        {
            var (credentials, error) = EquipeCredentials.ForLink(serverUrl, t, allowInsecureLocalhost: true);
            if (credentials is null) return SyncMessages.For(error!);
            credentials.Save(store);
            return null;
        };
        DeviceLinkFlow Flow(InMemoryTokenStore store, IHttpTransport? transport = null, Func<DateTimeOffset>? clock = null, DeviceIdentity? device = null) =>
            new(new DeviceLinkClient(baseUrl, "0.0.0-checks", transport), () => device ?? Device, SaveInto(store), clock: clock, log: log.Add);
        int Count(string path) => server.RichiesteRicevute().Count(x => x.Percorso == path);

        try
        {
            // 1. Collegamento riuscito.
            var store = new InMemoryTokenStore();
            var flow = Flow(store);
            var waiting = flow.BeginAsync().GetAwaiter().GetResult();
            var richiesta = server.Collegamenti().LastOrDefault();
            Check("richiesta creata → in attesa della persona", waiting && flow.Machine.Phase == LinkPhase.Waiting && richiesta is not null, flow.Machine.Detail ?? "");
            if (richiesta is null) return;
            Check("il server ha ricevuto impronta, nome, sistema e versione",
                  richiesta.DispositivoId == DeviceId && richiesta.Nome == "PC-DI-PROVA" && richiesta.Sistema == "windows" && richiesta.Versione == "0.0.0-checks");
            Check("indirizzo da aprire: http://127.0.0.1:<porta>/collega/<codice_utente>", flow.LinkUrl == $"{serverUrl}/collega/{richiesta.CodiceUtente}", flow.LinkUrl ?? "null");
            Check("l'indirizzo non contiene il codice del dispositivo", flow.LinkUrl?.Contains(richiesta.CodiceDispositivo) == false);
            flow.PollAsync().GetAwaiter().GetResult();
            flow.PollAsync().GetAwaiter().GetResult();
            Check("nessuno ha premuto «Collega» → in attesa, nessuna credenziale", flow.Machine.Phase == LinkPhase.Waiting && store.Count == 0);
            Check("la persona preme «Collega»", server.ApprovaCollegamento(richiesta.CodiceUtente));
            flow.PollAsync().GetAwaiter().GetResult();
            Check("ritiro riuscito → collegato", flow.Machine.Phase == LinkPhase.Linked && flow.LinkUrl is null, $"{flow.Machine.Phase} {flow.Machine.Detail}");
            Check("credenziali salvate nello store: URL del server e token", EquipeCredentials.Load(store) == new EquipeCredentials(serverUrl, token) && store.Count == 2);
            var ritiri = Count(DeviceLinkClient.PickupPath);
            flow.PollAsync().GetAwaiter().GetResult();
            Check("dopo il collegamento non si ritira più", Count(DeviceLinkClient.PickupPath) == ritiri);
            var saved = EquipeCredentials.Load(store)!;
            var sync = new SyncClient(SyncClient.Validate(saved.ServerUrl, true).Url!, saved.Token, "0.0.0-checks");
            CheckT("con le credenziali salvate il sync parte: GET /sessione risponde", () => sync.SessioneAsync().GetAwaiter().GetResult().Risposta.Ok);
            var direct = new DeviceLinkClient(baseUrl, "0.0.0-checks");
            Check("il token esce una volta sola: un secondo ritiro trova «usata»",
                  direct.RitiraAsync(richiesta.CodiceDispositivo, DeviceId).GetAwaiter().GetResult().Kind == PickupKind.Used);

            // 2. Scadenza: dal server (410) e dall'orologio dell'agente (nessuna richiesta in più).
            var store2 = new InMemoryTokenStore();
            var expiring = Flow(store2);
            expiring.BeginAsync().GetAwaiter().GetResult();
            server.ScadiCollegamento(server.Collegamenti().Last().CodiceUtente);
            expiring.PollAsync().GetAwaiter().GetResult();
            Check("scadenza (410 dal server) → «Collegamento scaduto: riprova dal menu», nessuna credenziale",
                  expiring.Machine.Phase == LinkPhase.Expired && expiring.Machine.StatusText == "Collegamento scaduto: riprova dal menu" && store2.Count == 0);
            Check("approvare una richiesta scaduta non serve", !server.ApprovaCollegamento(server.Collegamenti().Last().CodiceUtente));
            var now = DateTimeOffset.UtcNow;
            var late = Flow(store2, clock: () => now);
            late.BeginAsync().GetAwaiter().GetResult();
            now = now.AddSeconds(601);
            ritiri = Count(DeviceLinkClient.PickupPath);
            late.PollAsync().GetAwaiter().GetResult();
            Check("scadenza (orologio dell'agente) → scaduta senza altre richieste al server",
                  late.Machine.Phase == LinkPhase.Expired && Count(DeviceLinkClient.PickupPath) == ritiri && store2.Count == 0);
            Check("dopo la scadenza si ricomincia dal menu con una richiesta nuova", late.BeginAsync().GetAwaiter().GetResult() && late.Machine.Phase == LinkPhase.Waiting);

            // 3. Già usata: il token è già uscito verso qualcun altro.
            var store3 = new InMemoryTokenStore();
            var used = Flow(store3);
            used.BeginAsync().GetAwaiter().GetResult();
            var usata = server.Collegamenti().Last();
            server.ApprovaCollegamento(usata.CodiceUtente);
            direct.RitiraAsync(usata.CodiceDispositivo, DeviceId).GetAwaiter().GetResult();
            used.PollAsync().GetAwaiter().GetResult();
            Check("già usata (410) → fine, nessuna credenziale, non si ritenta da soli",
                  used.Machine.Phase == LinkPhase.Rejected && used.Machine.Detail == "richiesta già usata" && store3.Count == 0, $"{used.Machine.Phase} {used.Machine.Detail}");
            ritiri = Count(DeviceLinkClient.PickupPath);
            used.PollAsync().GetAwaiter().GetResult();
            Check("già usata: nessun altro ritiro", Count(DeviceLinkClient.PickupPath) == ritiri);

            // 4. Altro computer: la richiesta è legata all'impronta di chi l'ha creata.
            var store4 = new InMemoryTokenStore();
            var mine = Flow(store4);
            mine.BeginAsync().GetAwaiter().GetResult();
            var legata = server.Collegamenti().Last();
            server.ApprovaCollegamento(legata.CodiceUtente);
            var otherId = DeviceIdentity.Fingerprint("un-altro-computer");
            var foreign = direct.RitiraAsync(legata.CodiceDispositivo, otherId).GetAwaiter().GetResult();
            Check("altro computer (403): con un'altra impronta il token non esce", foreign.Kind == PickupKind.OtherComputer && foreign.Token is null);
            var thief = new DeviceLinkMachine();
            thief.Begin();
            thief.RequestSucceeded(new RispostaRichiestaCollegamento { Ok = true, CodiceDispositivo = legata.CodiceDispositivo, CodiceUtente = legata.CodiceUtente, ScadeTra = 600, Intervallo = 3 }, DateTimeOffset.UtcNow);
            Check("altro computer → fine senza credenziali, non si ritenta da soli",
                  thief.PickupSucceeded(foreign, DateTimeOffset.UtcNow) is null && thief.Phase == LinkPhase.Rejected && thief.Detail == "la richiesta appartiene a un altro computer");
            mine.PollAsync().GetAwaiter().GetResult();
            Check("il computer giusto ritira lo stesso: il tentativo dell'altro non ha consumato la richiesta",
                  mine.Machine.Phase == LinkPhase.Linked && EquipeCredentials.Load(store4)?.Token == token);
            Check("richiesta mai esistita (404) → sconosciuta", direct.RitiraAsync(new string('Z', 43), DeviceId).GetAwaiter().GetResult().Kind == PickupKind.Unknown);
            var store4b = new InMemoryTokenStore();
            var refused = Flow(store4b);
            refused.BeginAsync().GetAwaiter().GetResult();
            server.RifiutaCollegamento(server.Collegamenti().Last().CodiceUtente);
            refused.PollAsync().GetAwaiter().GetResult();
            Check("rifiutata (410) → fine, nessuna credenziale", refused.Machine.Phase == LinkPhase.Rejected && refused.Machine.Detail == "richiesta rifiutata" && store4b.Count == 0);

            // 5. Errore di rete in mezzo, poi riuscita.
            var store5 = new InMemoryTokenStore();
            var flaky = new FlakyTransport();
            var bumpy = Flow(store5, flaky);
            bumpy.BeginAsync().GetAwaiter().GetResult();
            var incerta = server.Collegamenti().Last();
            bumpy.PollAsync().GetAwaiter().GetResult();
            flaky.FailNext = 2;
            ritiri = Count(DeviceLinkClient.PickupPath);
            bumpy.PollAsync().GetAwaiter().GetResult();
            Check("rete assente durante l'attesa → si resta in attesa con la stessa richiesta, l'errore si vede",
                  bumpy.Machine.Phase == LinkPhase.Waiting && bumpy.Machine.Detail?.StartsWith("Rete: ", StringComparison.Ordinal) == true
                  && bumpy.LinkUrl == $"{serverUrl}/collega/{incerta.CodiceUtente}" && Count(DeviceLinkClient.PickupPath) == ritiri, bumpy.Machine.Detail ?? "null");
            server.ApprovaCollegamento(incerta.CodiceUtente);
            bumpy.PollAsync().GetAwaiter().GetResult();
            Check("ancora senza rete: niente perso, niente credenziali", bumpy.Machine.Phase == LinkPhase.Waiting && store5.Count == 0 && flaky.Failed == 2);
            bumpy.PollAsync().GetAwaiter().GetResult();
            Check("torna la rete → collegato con la stessa richiesta, credenziali salvate",
                  bumpy.Machine.Phase == LinkPhase.Linked && EquipeCredentials.Load(store5) == new EquipeCredentials(serverUrl, token)
                  && server.Collegamenti().Last().CodiceUtente == incerta.CodiceUtente, $"{bumpy.Machine.Phase} {bumpy.Machine.Detail}");
            var offline = new FlakyTransport { FailNext = 1 };
            var cold = Flow(new InMemoryTokenStore(), offline);
            var richieste = server.Collegamenti().Count;
            Check("rete assente alla creazione → errore, nessuna richiesta, niente da aprire",
                  !cold.BeginAsync().GetAwaiter().GetResult() && cold.Machine.Phase == LinkPhase.Failed && cold.LinkUrl is null && server.Collegamenti().Count == richieste);
            Check("…e dal menu si riprova", cold.BeginAsync().GetAwaiter().GetResult() && cold.Machine.Phase == LinkPhase.Waiting);

            // 6. Troppe richieste (429).
            server.TroppeRichieste = true;
            var limited = Flow(new InMemoryTokenStore());
            richieste = server.Collegamenti().Count;
            var chiamate = server.RichiesteRicevute().Count + 1;
            Check("429 alla creazione → «riprova fra qualche minuto», nessuna richiesta creata",
                  !limited.BeginAsync().GetAwaiter().GetResult() && limited.Machine.Phase == LinkPhase.Failed
                  && limited.Machine.Detail?.Contains("riprova fra qualche minuto") == true && server.Collegamenti().Count == richieste, limited.Machine.Detail ?? "null");
            limited.PollAsync().GetAwaiter().GetResult();
            Check("429: nessun ritiro né nuovo tentativo da soli", server.RichiesteRicevute().Count == chiamate && limited.Machine.Phase == LinkPhase.Failed);
            server.TroppeRichieste = false;
            Check("più tardi, dal menu, la richiesta riesce", limited.BeginAsync().GetAwaiter().GetResult());
            var invalid = Flow(new InMemoryTokenStore(), device: new DeviceIdentity("non-è-un'impronta", "PC"));
            Check("400 (impronta di forma sbagliata) → errore col messaggio del server",
                  !invalid.BeginAsync().GetAwaiter().GetResult() && invalid.Machine.Detail?.Contains("64 cifre esadecimali") == true, invalid.Machine.Detail ?? "null");

            // 7. Il browser si apre da solo solo al primo avvio senza credenziali.
            var store7 = new InMemoryTokenStore();
            var linkAttempted = false;   // il flag nelle impostazioni
            var browser = new List<string>();
            DeviceLinkFlow Launch()
            {
                // Ciò che fa l'app all'avvio (DeviceLinkService.Start): la decisione è DeviceLinkPolicy.StartsAtLaunch.
                var f = Flow(store7);
                if (EquipeCredentials.Load(store7) is not null) f.Machine.MarkLinked();
                if (DeviceLinkPolicy.StartsAtLaunch(EquipeCredentials.Load(store7) is not null, linkAttempted) && f.BeginAsync().GetAwaiter().GetResult())
                {
                    linkAttempted = true;
                    browser.Add(f.LinkUrl!);
                }
                return f;
            }
            richieste = Count(DeviceLinkClient.RequestPath);
            var first = Launch();
            Check("primo avvio senza credenziali: una richiesta, il browser si apre una volta",
                  browser.Count == 1 && linkAttempted && first.Machine.Phase == LinkPhase.Waiting && Count(DeviceLinkClient.RequestPath) == richieste + 1);
            var second = Launch();
            var third = Launch();
            Check("avvii successivi senza credenziali: nessun secondo browser, nessuna richiesta nuova",
                  browser.Count == 1 && Count(DeviceLinkClient.RequestPath) == richieste + 1 && second.Machine.Phase == LinkPhase.NotLinked && third.Machine.Phase == LinkPhase.NotLinked);
            Check("…resta la voce «Collega questo computer»", DeviceLinkPolicy.OffersLink(EquipeCredentials.Load(store7) is not null, tokenRejected: false));
            Check("dal menu: la richiesta parte e c'è l'indirizzo da aprire", third.BeginAsync().GetAwaiter().GetResult() && third.LinkUrl is not null);
            var pending = third.LinkUrl;
            Check("di nuovo dal menu con la richiesta in attesa: stessa richiesta, stesso indirizzo",
                  !third.BeginAsync().GetAwaiter().GetResult() && third.LinkUrl == pending && Count(DeviceLinkClient.RequestPath) == richieste + 2);
            server.ApprovaCollegamento(server.Collegamenti().Last().CodiceUtente);
            third.PollAsync().GetAwaiter().GetResult();
            var linked = Launch();
            Check("collegato dal menu; all'avvio dopo, con le credenziali, niente browser e voce di menu nascosta",
                  third.Machine.Phase == LinkPhase.Linked && linked.Machine.Phase == LinkPhase.Linked && browser.Count == 1
                  && !DeviceLinkPolicy.OffersLink(EquipeCredentials.Load(store7) is not null, tokenRejected: false));

            // 8. Intestazioni e segreti.
            var tutte = server.RichiesteRicevute();
            var collega = tutte.Where(x => x.Percorso.StartsWith("/api/collega/", StringComparison.Ordinal)).ToList();
            Check("rotte del collegamento: JSON, nessun token, nessun cookie",
                  collega.Count > 0 && collega.All(x => x.Intestazioni.GetValueOrDefault("content-type")?.StartsWith("application/json", StringComparison.Ordinal) == true
                                                        && !x.Intestazioni.ContainsKey("authorization") && !x.Intestazioni.ContainsKey("cookie")));
            var agente = new SyncClient(baseUrl, token, "0.0.0-checks");
            agente.SessioneAsync().GetAwaiter().GetResult();
            agente.InviaSegmentiAsync(new InvioSegmenti(RomeClock.DayString(DateTimeOffset.UtcNow), [])).GetAwaiter().GetResult();
            agente.StopAsync(null).GetAwaiter().GetResult();
            var rotte = server.RichiesteRicevute().Where(x => x.Percorso.StartsWith("/api/agente/", StringComparison.Ordinal)).ToList();
            Check("X-Agente-Permessi: ok arriva al server su sessione, segmenti e stop",
                  rotte.Select(x => x.Percorso).ToHashSet().SetEquals(["/api/agente/sessione", "/api/agente/segmenti", "/api/agente/stop"])
                  && rotte.All(x => x.Intestazioni.GetValueOrDefault("x-agente-permessi") == "ok"), Join(rotte.Select(x => x.Percorso)));
            var righe = string.Join("\n", log);
            Check("il registro racconta il collegamento", righe.Contains("[Collegamento] richiesta creata") && righe.Contains("[Collegamento] computer collegato") && righe.Contains("[Collegamento] richiesta scaduta"));
            Check("nel registro mai il token, i codici del dispositivo, i codici pubblici o l'indirizzo",
                  !righe.Contains(token) && server.Collegamenti().All(c => !righe.Contains(c.CodiceDispositivo) && !righe.Contains(c.CodiceUtente)) && !righe.Contains("/collega/"));
        }
        catch (Exception e)
        {
            Check("giro del collegamento senza errori", false, e.ToString());
        }
    }
}

namespace ActivityTracker.Core;

/// <summary>
/// Deposito di segreti (stringhe per nome). Nell'app è Gestione credenziali di Windows (<c>CredentialManagerStore</c>);
/// nelle verifiche un'implementazione in memoria, così i test automatici non scrivono mai nel deposito reale.
/// </summary>
public interface ITokenStore
{
    string? Token(string account);
    void Save(string token, string account);
    void Delete(string account);
}

public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly Dictionary<string, string> _tokens = [];

    public string? Token(string account) => _tokens.TryGetValue(account, out var t) ? t : null;
    public void Save(string token, string account) => _tokens[account] = token;
    public void Delete(string account) => _tokens.Remove(account);
    public int Count => _tokens.Count;
}

/// <summary>
/// Le due credenziali dell'agente verso equipe-track, entrambe nel deposito sicuro (mai in file di impostazioni, mai nel
/// codice): URL del server (quello incorporato, <c>SyncClient.DefaultServerUrl</c>, salvo test) e token dell'agente,
/// ritirato dal collegamento nel browser (<see cref="DeviceLinkFlow"/>): nessuno lo vede né lo copia. URL e token vanno
/// in coppia: il token vale per quel server.
/// </summary>
public sealed record EquipeCredentials(string ServerUrl, string Token)
{
    public static class Account
    {
        public const string ServerUrl = "server-url";
        public const string Token = "agent-token";
        /// <summary>Voce delle versioni che mandavano il cookie del gate: non si usa più, si toglie a ogni salvataggio.</summary>
        public const string LegacyGateSecret = "gate-secret";
    }

    /// <summary>Le credenziali di un collegamento appena riuscito: il token ritirato, abbinato al server a cui ci si è
    /// collegati (validato come sempre: https, http solo per localhost con il flag). Errore se l'URL non è ammesso o
    /// se il token non ha la forma che il server accetta.</summary>
    public static (EquipeCredentials? Credentials, SyncFault? Error) ForLink(string serverUrl, string token, bool allowInsecureLocalhost)
    {
        var (url, error) = SyncClient.Validate(serverUrl, allowInsecureLocalhost);
        if (url is null) return (null, error);
        var trimmed = token.Trim();
        if (!SyncClient.IsWellFormedToken(trimmed)) return (null, new SyncFault(SyncErrorKind.Decoding));
        return (new EquipeCredentials(SyncClient.Text(url), trimmed), null);
    }

    /// <summary>null se manca l'URL o il token.</summary>
    public static EquipeCredentials? Load(ITokenStore store)
    {
        var url = store.Token(Account.ServerUrl);
        var token = store.Token(Account.Token);
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return null;
        return new EquipeCredentials(url, token);
    }

    public void Save(ITokenStore store)
    {
        store.Save(ServerUrl, Account.ServerUrl);
        store.Save(Token, Account.Token);
        store.Delete(Account.LegacyGateSecret);
    }

    public static void Delete(ITokenStore store)
    {
        store.Delete(Account.ServerUrl);
        store.Delete(Account.Token);
        store.Delete(Account.LegacyGateSecret);
    }

    /// <summary>Mai il token in un log o in un messaggio.</summary>
    public override string ToString() => $"EquipeCredentials({ServerUrl}, token ***)";
}

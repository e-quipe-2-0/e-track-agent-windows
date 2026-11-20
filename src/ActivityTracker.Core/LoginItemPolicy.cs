namespace ActivityTracker.Core;

public enum LoginItemAction
{
    /// <summary>Niente da fare: la voce è già giusta, oppure non va toccata.</summary>
    None,
    /// <summary>(Ri)scrivere la voce <c>Run</c> con il percorso di questa copia dell'app.</summary>
    Write,
    /// <summary>Togliere la voce: la persona l'ha spenta dalle Impostazioni e qualcun altro (l'installer di un
    /// aggiornamento) l'ha riscritta.</summary>
    Remove,
}

/// <summary>
/// Avvio automatico all'accensione: che cosa fare della voce <c>HKCU\…\Run</c> a ogni avvio dell'agente. Pura, così si
/// verifica senza registro. La voce si (ri)scrive sempre, non solo al primo avvio: così sopravvive a un aggiornamento
/// che cambia percorso e a chi la cancella per sbaglio. Si ferma solo davanti a una scelta esplicita della persona
/// (casella spenta nelle Impostazioni dell'agente) o a <c>AT_NO_LOGIN_ITEM=1</c>, che salta tutto.
/// </summary>
public static class LoginItemPolicy
{
    /// <param name="skippedByEnvironment"><c>AT_NO_LOGIN_ITEM=1</c>.</param>
    /// <param name="disabledByUser">La persona ha spento «Avvia all'accesso a Windows» dalle Impostazioni.</param>
    /// <param name="currentValue">Il valore presente in <c>Run</c>, null se non c'è.</param>
    /// <param name="expectedCommand">Il comando che ci deve essere: questa copia dell'app, con <c>--autostart</c>.</param>
    public static LoginItemAction AtLaunch(bool skippedByEnvironment, bool disabledByUser, string? currentValue, string expectedCommand)
    {
        if (skippedByEnvironment) return LoginItemAction.None;
        if (disabledByUser) return string.IsNullOrEmpty(currentValue) ? LoginItemAction.None : LoginItemAction.Remove;
        return string.Equals(currentValue, expectedCommand, StringComparison.OrdinalIgnoreCase) ? LoginItemAction.None : LoginItemAction.Write;
    }
}

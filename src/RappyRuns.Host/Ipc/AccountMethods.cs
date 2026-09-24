namespace RappyRuns.Host.Ipc;

/// <summary><c>account.link</c>: browser pairing (ui-shell §2.1). Progress goes to <c>state.token</c>.</summary>
internal sealed class AccountMethods(ClientHost host)
{
    public void Register(IIpcRegistry r) =>
        r.RegisterSync("account.link", _ =>
        {
            host.StartPairing();
            return null;
        });
}

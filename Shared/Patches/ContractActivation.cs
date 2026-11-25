using System;

namespace Shared.Patches;

internal sealed class ContractActivation
{
    [ThreadStatic]
    private static ContractActivation current;
    private readonly ContractActivation parent = current;

    public static ContractActivation Current => current;
    public IDisposable Deposit;
    public (long Contract, Guid Operation) PlayerReservation;
    public (long Contract, Guid Operation) Accepted;

    public static ContractActivation Enter() => current = new ContractActivation();

    public void Close(Action<long, Guid> withdraw)
    {
        if (current != this)
            return;
        try
        {
            Deposit?.Dispose();
            if (Accepted.Contract != 0)
                withdraw?.Invoke(Accepted.Contract, Accepted.Operation);
        }
        finally
        {
            current = parent;
        }
    }
}

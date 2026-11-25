using Shared.Patches;

var outer = ContractActivation.Enter();
var deposit = new Probe();
var operation = Guid.NewGuid();
outer.Deposit = deposit;
outer.PlayerReservation = (1, operation);
var rolledBack = new List<long>();
void Withdraw(long contract, Guid _) => rolledBack.Add(contract);

// An inner callback can return before opening a deposit.
ContractActivation.Enter().Close(Withdraw);
if (
    ContractActivation.Current != outer
    || deposit.Closed
    || outer.PlayerReservation != (1, operation)
)
    throw new Exception("An empty inner activation changed its caller");

var inner = ContractActivation.Enter();
var otherDeposit = new Probe();
inner.Deposit = otherDeposit;
inner.Accepted = (2, Guid.NewGuid());
inner.Close(Withdraw);
if (
    ContractActivation.Current != outer
    || deposit.Closed
    || !otherDeposit.Closed
    || !rolledBack.SequenceEqual([2L])
)
    throw new Exception("Inner cleanup did not stay within its own invocation");

outer.Close(Withdraw);
if (ContractActivation.Current != null || !deposit.Closed || rolledBack.Count != 1)
    throw new Exception("Outer cleanup did not complete exactly once");

Console.WriteLine("Nested activation scope checks passed");

sealed class Probe : IDisposable
{
    public bool Closed { get; private set; }

    public void Dispose() => Closed = true;
}

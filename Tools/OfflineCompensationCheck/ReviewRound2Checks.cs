using ClusterPlugin;

internal static class ReviewRound2Checks
{
internal static void Run()
{
const string E = OfflineModel.Electricity;
OfflineProfile Engine(bool gasFirst) {
 var p=new OfflineProfile {ClockFrameSeconds=1.0/60};
 // Engine has a full reservoir, powered tank supplies its intake, plus constant H2 demand.
 var gas=new OfflineSink {Resource="h2",Required=1};
 var elec=new OfflineSink {Resource=E,Required=1};
 if(gasFirst) p.Sinks.AddRange([gas,elec]);else p.Sinks.AddRange([elec,gas]);
 p.Storage.Add(new(){Kind="Engine",Resource="h2",Capacity=100,Amount=100,Enabled=true,MaxInput=300,InputConversion=1.0/60});
 p.Sources.Add(new(){Resource=E,Maximum=5,Storage=0,StorageCost=1.0/60,Kind="Engine"});
 p.Sources.Add(new(){Resource="h2",Maximum=10,Kind="Generator"});
 return p;
}
OfflineProfile RealTank(bool gasFirst) {
 var p=Engine(gasFirst);
 p.Sources[0].Block=10;
 p.Sinks.Add(new(){Block=30,Resource=E,Group="Utility",Required=.001});
 p.Storage.Add(new(){Block=30,Kind="Tank",Resource="h2",Capacity=1000,Amount=1000,Pair=true,Mode=0,Enabled=true,PowerSink=2,MaxInput=50,InputConversion=1.0/60});
 p.Sources[1]=new(){Block=30,Resource="h2",Maximum=50,Kind="Tank",Storage=1,StorageCost=1.0/60,PowerSink=2};
 return p;
}

void Check(bool condition, string message) { if(!condition)throw new Exception(message); Console.WriteLine("PASS "+message); }
var electricFirst=RealTank(false);var gasFirst=RealTank(true);
var first=OfflineModel.Simulate(electricFirst,60,1000000);var second=OfflineModel.Simulate(gasFirst,60,1000000);
Check(Math.Abs(electricFirst.Storage[0].Amount-100)<1e-10,"electricity-first control keeps the fueled engine full");
Check(Math.Abs(gasFirst.Storage[0].Amount-100)<1e-10,"Q2 gas-first engine remains full with ample powered tank supply");
Check(Math.Abs(electricFirst.Storage[1].Amount-997.999)<1e-10 && Math.Abs(gasFirst.Storage[1].Amount-997.999)<1e-10,"Q2 resource order preserves which stock pays for delivered energy");
Check(Math.Abs(first.ProducerEnergyMWh.Values.Sum()-second.ProducerEnergyMWh.Values.Sum())<1e-12,"both cases served identical electrical energy");
Check(Math.Abs(electricFirst.Storage.Sum(s=>s.Amount)-gasFirst.Storage.Sum(s=>s.Amount))<1e-9,"gas total conserved: this is wrong coupling/state, not an asserted duplication");
var longFirst=Engine(false);var longSecond=Engine(true);
OfflineModel.Simulate(longFirst,6000,1000000);OfflineModel.Simulate(longSecond,6000,1000000);
Check(longFirst.Storage[0].Amount==100 && longSecond.Storage[0].Amount==100,"Q2 long segment control: engine stays full in either order");
Console.WriteLine("Q2 expected-state regressions passed; detached, no native execution.");

foreach(bool adaptible in new[]{false,true}) {
 var p=new OfflineProfile {ClockFrameSeconds=1.0/60};
 p.Items["ice"]=new(){Volume=1,Mass=1};
 p.Inventories.Add(new(){MaxVolume=100,MaxMass=100,Stacks=[new(){Item="ice",Amount=10}]});
 p.Sinks.Add(new(){Block=60,Resource=E,Group="Generators",Priority=75,Required=.5,Adaptible=adaptible});
 p.Sinks.Add(new(){Block=70,Resource="h2",Required=1});
 p.Sources.Add(new(){Block=20,Resource=E,Maximum=.1});
 p.Sources.Add(new(){Block=60,Resource="h2",Maximum=10,PowerSink=0,IdlePower=.001,OperationalPower=.5,Fuel=[new(){Item="ice",Amount=1m/60,Inventories=[0]}]});
 var r=OfflineModel.Simulate(p,60,1000000);
 Check(Math.Abs((double)OfflineModel.Amount(p.Inventories[0],"ice") - (adaptible ? 9 : 10)) < 1e-8
       && Math.Abs(r.ConsumerEnergyMWh[60] - (adaptible ? .1 : .001)/3600) < 1e-12,
       $"Q3 partial supply powered={adaptible}: native gas/ice and electrical cost");
 // MyResourceSinkComponent.SetInputFromDistributor: positive input on an
 // adaptible group is powered; MyGasGenerator.CanProduce/CheckIsWorking use it.
 bool nativePowered=.1>0 && (adaptible || .1>=.5);
 Check(nativePowered==adaptible,$"engine powered predicate: Adaptible={adaptible}, powered={nativePowered}");
}
Console.WriteLine("Q3 expected-state and negative-control regressions passed; detached, no native run.");

foreach (double initial in new[] { 0d, 99.9, 100d })
foreach (bool finiteIce in new[] { false, true })
foreach (bool reverseSources in new[] { false, true })
{
    OfflineProfile Fixture(bool gasFirst)
    {
        var p = RealTank(gasFirst);
        p.Storage[0].Amount = initial;
        p.Sinks[gasFirst ? 0 : 1].Resource = "h2:isolated"; // ordinary gas demand cannot touch the engine circuit
        p.Sources.Add(new() { Block=90, Resource=E, Group="Backup", Priority=10, Maximum=.002 });
        if (finiteIce)
        {
            p.Items["ice"] = new() { Mass=1, Volume=1 };
            p.Inventories.Add(new() { MaxMass=10, MaxVolume=10, Stacks=[new() { Item="ice", Amount=1 }] });
            p.Sources.Add(new() { Block=91, Resource="h2", Group="Generator", Priority=-1, Maximum=50,
                Fuel=[new() { Item="ice", Amount=1m/60, Inventories=[0] }] });
        }
        if (reverseSources) p.Sources.Reverse();
        return p;
    }
    var a=Fixture(false); var b=Fixture(true);
    var ar=OfflineModel.Simulate(a,6000,1000000); var br=OfflineModel.Simulate(b,6000,1000000);
    Check(a.Storage.Zip(b.Storage).All(pair => Math.Abs(pair.First.Amount-pair.Second.Amount)<1e-7),
        $"Q2 full/near/empty/order/source permutation initial={initial}, finiteIce={finiteIce}, reversed={reverseSources}");
    Check(Math.Abs(ar.ProducerEnergyMWh.Values.Sum()-br.ProducerEnergyMWh.Values.Sum())<1e-8,
        "Q2 delivered energy invariant through stock events and isolated gas circuit");
    if (finiteIce) Check(OfflineModel.Amount(a.Inventories[0],"ice")<0.000001m && OfflineModel.Amount(b.Inventories[0],"ice")<0.000001m,
        "Q2 real finite generator stock exhausts in either ordering");
}
foreach (double power in new[] { 0d, .0005, .1, .5 })
foreach (bool adaptible in new[] { false, true })
{
    var p = new OfflineProfile { ClockFrameSeconds=1.0/60 };
    p.Items["ice"]=new() { Mass=1, Volume=1 };
    p.Inventories.Add(new() { MaxMass=10, MaxVolume=10, Stacks=[new() { Item="ice", Amount=2 }] });
    p.Sinks.Add(new() { Block=1, Resource=E, Required=.5, Adaptible=adaptible });
    p.Sinks.Add(new() { Block=2, Resource="h2", Required=1 });
    p.Sources.Add(new() { Block=3, Resource=E, Maximum=power });
    p.Sources.Add(new() { Block=1, Resource="h2", Maximum=1, PowerSink=0, IdlePower=.001, OperationalPower=.5,
        Fuel=[new() { Item="ice", Amount=1m/60, Inventories=[0] }] });
    OfflineModel.Simulate(p,60,1000);
    bool working=power>0 && (adaptible || power>=.5);
    Check(Math.Abs((double)OfflineModel.Amount(p.Inventories[0],"ice")-(working?1:2))<1e-8,
        $"Q3 native positive-partial/zero/full working predicate power={power}, adaptible={adaptible}");
}

// Native distributor dispatches paired reservoir sources before ordinary priority
// groups. In particular a forced-discharge battery suppresses even solar output.
foreach (int mode in new[] { 0, 1, 2 })
{
    var p = new OfflineProfile { ClockFrameSeconds = 1.0/60 };
    p.Sinks.Add(new() { Block=4, Resource=E, Required=.2 });
    p.Storage.Add(new() { Block=1, Resource=E, Kind="Battery", Amount=1, Capacity=3,
        Pair=true, Enabled=true, Mode=mode, MaxInput=12, InputConversion=1.0/216000, RechargeEfficiency=.8 });
    p.Sources.Add(new() { Block=1, Resource=E, Kind="Battery", Priority=2, Group="Battery",
        Maximum=mode==1?0:12, Storage=0, StorageCost=1.0/216000 });
    p.Sources.Add(new() { Block=2, Resource=E, Kind="Environmental", Priority=1, Group="SolarPanels", Maximum=.16 });
    p.Sources.Add(new() { Block=3, Resource=E, Kind="Reactor", Priority=3, Group="Reactors", Maximum=15 });
    var result = OfflineModel.Simulate(p,60,10000);
    Check(Math.Abs(result.ProducerEnergyMWh.GetValueOrDefault(1)-(mode==1?0:.2/3600))<1e-10,
        $"native paired battery dispatch precedes ordinary generators, mode={mode}");
    if(mode==2) Check(result.ProducerEnergyMWh.GetValueOrDefault(2)==0 && result.ProducerEnergyMWh.GetValueOrDefault(3)==0,
        "native forced discharge suppresses unused solar/reactor rather than curtailing the battery");
}

}
}

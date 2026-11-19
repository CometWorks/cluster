using System;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;

// An assembly as Magnetar loads a plugin: its name, and whether it has Essentials' grid commands class
static Assembly Plugin(string name, string type)
{
    var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
    assembly.DefineDynamicModule(name).DefineType(type, TypeAttributes.Public).CreateType();
    return assembly;
}

var hub = Plugin("Essentials-f130e28c_3ld0yvit.sqr", EssentialsAssembly.CommandsType); // the live cluster's
var local = Plugin("Essentials", EssentialsAssembly.CommandsType); // a local build
var other = Plugin("Essentials", "ServerPlugin.Plugin"); // same template, without the command
int failed = 0;
void Check(bool value, string what)
{
    Console.WriteLine((value ? "ok   " : "FAIL ") + what);
    if (!value)
        failed++;
}
Check(hub.GetName().Name != "Essentials", "the old name check misses the hub build");
Check(EssentialsAssembly.IsLoaded([typeof(object).Assembly, hub]), "hub build found");
Check(EssentialsAssembly.IsLoaded([local]), "local build found");
Check(!EssentialsAssembly.IsLoaded([typeof(object).Assembly, other]), "no Essentials, no takeover");
Check(EssentialsAssembly.IsLoaded(AppDomain.CurrentDomain.GetAssemblies()), "found in the AppDomain");
Console.WriteLine(failed == 0 ? "PASS" : $"FAIL {failed}");
return failed == 0 ? 0 : 1;

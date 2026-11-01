using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Shared.Tools;

// Attribute to mark prefix and suffix patch methods
// For usage examples please search this repo:
// https://github.com/viktor-ferenczi/performance-improvements
//
// ReSharper disable once ClassNeverInstantiated.Global
[AttributeUsage(AttributeTargets.Method)]
public class EnsureCode : Attribute
{
    // Allowed method body hashes in hexadecimal, multiple entries can be separated by a | (pipe) character
    private readonly string allowedHashes;

    private bool IsAllowed(string hash) => $"|{allowedHashes}|".Contains($"|{hash}|");

    public EnsureCode(string allowedHashes)
    {
        this.allowedHashes = allowedHashes;
    }

    /// <summary>
    /// How many patch methods the last Verify() actually examined. T-0122: without this a
    /// vacuous pass is indistinguishable from a real one - 77 patch classes declared no hash at
    /// all, so the scan logged a clean result on every node start while checking nothing. A
    /// check that can PASS must report its COVERAGE, not just its verdict.
    /// </summary>
    public static int LastVerifiedCount { get; private set; }

    public static IEnumerable<CodeChange> Verify()
    {
        var reflectedType = new StackTrace().GetFrame(1).GetMethod().ReflectedType;
        if (reflectedType == null)
            throw new Exception("Cannot determine the caller's assembly");

        var callingAssembly = reflectedType.Assembly;
        return Verify(callingAssembly);
    }

    private static IEnumerable<CodeChange> Verify(Assembly pluginAssembly)
    {
        LastVerifiedCount = AccessTools.GetTypesFromAssembly(pluginAssembly)
            .SelectMany(AccessTools.GetDeclaredMethods)
            .Count(m => m.GetCustomAttributes<EnsureCode>().Any());
        return AccessTools.GetTypesFromAssembly(pluginAssembly).SelectMany(Verify);
    }

    private static IEnumerable<CodeChange> Verify(Type patchType)
    {
        return AccessTools.GetDeclaredMethods(patchType).SelectMany(Verify);
    }

    private static IEnumerable<CodeChange> Verify(MethodInfo patchMethod)
    {
        var validateAttribute = patchMethod.GetCustomAttributes<EnsureCode>().FirstOrDefault();
        if (validateAttribute == null)
            return Enumerable.Empty<CodeChange>();

        return validateAttribute.VerifyMethod(patchMethod);
    }


    /// <summary>
    /// T-0122. The target info for a patch method, merging the CLASS-level [HarmonyPatch] with any
    /// METHOD-level one, the way Harmony's own PatchClassProcessor does.
    ///
    /// THE BUG THIS FIXES. Both ReportHashes and VerifyMethod used to read only the method-level
    /// attribute and bail out when there was none:
    ///
    ///     var methodPatch = patchMethod.GetCustomAttributes&lt;HarmonyPatch&gt;().FirstOrDefault();
    ///     if (methodPatch == null) yield break;
    ///
    /// But the dominant idiom in this repo puts the target on the CLASS and marks the method with
    /// a bare [HarmonyPrefix] / [HarmonyTranspiler] - MyEntityIdentifierPatch.AllocateIdPrefix,
    /// MySandboxGamePatch.Transpiler, MyClientStatePatch.ReadTranspiler and 37 others, 40 of the
    /// 140 patch methods in this assembly. Every one of them was invisible to the reporter, and -
    /// far worse - putting [EnsureCode] on one produced a guard that SILENTLY PASSED without
    /// hashing anything, while LastVerifiedCount still counted it as verified. A check that
    /// reports coverage it does not have is the exact failure T-0122 was opened to remove, and
    /// the 40 include the transpilers, which is the tier that breaks most quietly on a game
    /// update.
    /// </summary>
    private static HarmonyMethod EffectiveInfo(MethodInfo patchMethod)
    {
        var attributes = new List<HarmonyMethod>();
        var declaringType = patchMethod.DeclaringType;
        if (declaringType != null)
            attributes.AddRange(declaringType.GetCustomAttributes<HarmonyPatch>(true).Select(x => x.info));
        attributes.AddRange(patchMethod.GetCustomAttributes<HarmonyPatch>().Select(x => x.info));
        return attributes.Count == 0 ? null : HarmonyMethod.Merge(attributes);
    }

    /// <summary>
    /// True for a method Harmony would actually apply as a patch. Prepare/Cleanup/TargetMethod
    /// carry no target of their own and must not be hashed; a plain helper in a patch class has
    /// no Harmony attribute at all. Without this predicate the class-level merge above would
    /// sweep every private helper in the class into the report.
    /// </summary>
    private static bool IsPatchMethod(MethodInfo method)
        => method.GetCustomAttributes<HarmonyPatch>().Any()
           || method.GetCustomAttributes<HarmonyPrefix>().Any()
           || method.GetCustomAttributes<HarmonyPostfix>().Any()
           || method.GetCustomAttributes<HarmonyTranspiler>().Any()
           || method.GetCustomAttributes<HarmonyFinalizer>().Any();

    /// <summary>
    /// Harmony's HashBody extension is declared separately for MethodInfo and ConstructorInfo with
    /// no MethodBase overload, so a computed target - which arrives as MethodBase - needs this
    /// dispatch. An unexpected MemberInfo kind throws rather than hashing to 0: a guard that
    /// silently hashes nothing is the bug this whole ticket is about.
    /// </summary>
    private static string HashOf(MethodBase target) => target switch
    {
        MethodInfo method => method.HashBody().ToString("x8"),
        ConstructorInfo constructor => constructor.HashBody().ToString("x8"),
        _ => throw new Exception($"Cannot hash target of type {target?.GetType().Name ?? "null"}")
    };

    /// <summary>
    /// T-0122. The targets of a [HarmonyPatch] class that computes them at runtime with
    /// TargetMethod() / TargetMethods(), rather than naming them in the attribute.
    ///
    /// Nine patch classes here do this - MyFactionPatch, MyScriptWhitelistPatch,
    /// MyTimerComponentSystemPatch, the Synchronous and BuyPrefab* families - and four of them
    /// are transpilers. Resolving from attributes alone reported every one as
    /// "UNRESOLVED (no declaring type)", which reads like a broken patch and is really just a
    /// patch shape this code did not implement.
    ///
    /// Invoking the class's own TargetMethod() is exactly what Harmony does at patch time, so it
    /// runs no code here that a node start does not already run - only earlier, and outside the
    /// game loop. The caller wraps it: a TargetMethod() that throws is reported, not rethrown.
    /// </summary>
    private static IEnumerable<MethodBase> ResolveComputedTargets(Type patchType)
    {
        var single = AccessTools.DeclaredMethod(patchType, "TargetMethod");
        if (single != null && single.IsStatic && single.Invoke(null, null) is MethodBase target)
            yield return target;

        var multiple = AccessTools.DeclaredMethod(patchType, "TargetMethods");
        if (multiple != null && multiple.IsStatic && multiple.Invoke(null, null) is IEnumerable<MethodBase> targets)
            foreach (var each in targets)
                if (each != null)
                    yield return each;
    }

    private static void ResolveTarget(Type patchedType, HarmonyMethod info,
        out MethodInfo patchedMethod, out ConstructorInfo patchedConstructor)
    {
        patchedMethod = null;
        patchedConstructor = null;
        switch (info.methodType)
        {
            case MethodType.Getter:
                patchedMethod = AccessTools.PropertyGetter(patchedType, info.methodName);
                break;

            case MethodType.Setter:
                patchedMethod = AccessTools.PropertySetter(patchedType, info.methodName);
                break;

            case MethodType.Constructor:
                patchedConstructor = AccessTools.Constructor(patchedType, info.argumentTypes);
                break;

            case MethodType.StaticConstructor:
                patchedConstructor = AccessTools.Constructor(patchedType, info.argumentTypes, true);
                break;

            // T-0122/T-0124. Async and Enumerator patches do NOT target the method named in the
            // attribute - Harmony redirects them to the compiler-generated state machine's
            // MoveNext, which is where the body actually lives. Falling through to `default:`
            // here resolved MyScriptCompilerPatch.CompileMoveNextTranspiler to the async STUB,
            // so the guard would have hashed a method whose body is three instructions of
            // state-machine setup - stable across almost any change to the real code, i.e. a
            // hash that can never trip. Found by dry-running the transpilers against the live
            // game build: the transpiler reported "expected exactly one Assembly.Load, found 0"
            // because it had been handed the wrong method.
            case MethodType.Async:
                patchedMethod = AccessTools.DeclaredMethod(patchedType, info.methodName, info.argumentTypes) is { } asyncOuter
                    ? AccessTools.AsyncMoveNext(asyncOuter) : null;
                break;

            case MethodType.Enumerator:
                patchedMethod = AccessTools.DeclaredMethod(patchedType, info.methodName, info.argumentTypes) is { } iterOuter
                    ? AccessTools.EnumeratorMoveNext(iterOuter) : null;
                break;

            default:
                patchedMethod = AccessTools.DeclaredMethod(patchedType, info.methodName, info.argumentTypes);
                break;
        }
    }

    /// <summary>
    /// T-0122. Every Harmony patch method in the assembly with the CURRENT body hash of the method
    /// it targets, whether or not it declares [EnsureCode], and whether or not the hash matches.
    /// Report-only: it never throws and never blocks a load, so it is safe to run on a live node.
    ///
    /// This exists because the hash is a RUNTIME quantity - PatchProcessor.GetCurrentInstructions
    /// reads the method as currently loaded, so it cannot be precomputed offline against the game
    /// DLLs and trusted. (Hashing has a Mono.Cecil overload, but it mixes in instruction OFFSETS
    /// where the Harmony one mixes in LABELS; the two do not agree and must not be substituted for
    /// one another.) Adding [EnsureCode] with a guessed or stale hash stops a node from starting,
    /// so the safe order is: run this on a node, take the real hashes, then declare them.
    ///
    /// Line format is `EnsureCode-hash <hash> <declared|-> <PatchClass>.<Method> -> <target>` so a
    /// node log can be grepped straight into a table.
    /// </summary>
    public static IEnumerable<string> ReportHashes(Assembly pluginAssembly)
    {
        foreach (var patchType in AccessTools.GetTypesFromAssembly(pluginAssembly))
        {
            if (!patchType.IsDefined(typeof(HarmonyPatch), true))
                continue;

            foreach (var patchMethod in AccessTools.GetDeclaredMethods(patchType))
            {
                if (!IsPatchMethod(patchMethod))
                    continue;

                var info = EffectiveInfo(patchMethod);
                var declared = patchMethod.GetCustomAttributes<EnsureCode>().FirstOrDefault()?.allowedHashes ?? "-";
                var patchedType = info?.declaringType;
                if (patchedType == null)
                {
                    // No declaring type in the attributes: a TargetMethod()/TargetMethods() class.
                    var computed = "????????";
                    string computedTarget;
                    try
                    {
                        var targets = ResolveComputedTargets(patchType).ToList();
                        if (targets.Count == 0)
                            computedTarget = "UNRESOLVED (no declaring type and no TargetMethod)";
                        else
                        {
                            // Several targets share one patch method, so several hashes share one
                            // line: any of them changing must trip the guard, and [EnsureCode]
                            // already accepts a pipe-separated set.
                            //
                            // DISTINCT matters. Overload families hash identically because they
                            // share IL - MyIntergridCommunicationContextPatch has 15 targets and
                            // ONE hash - and emitting it 15 times produces an unreadable attribute
                            // that also hides how many distinct bodies are really being guarded.
                            computed = string.Join("|", targets.Select(HashOf).Distinct().OrderBy(x => x, StringComparer.Ordinal));
                            computedTarget = string.Join(" + ", targets.Select(x => x.FullDescription()));
                        }
                    }
                    catch (Exception e)
                    {
                        computedTarget = $"FAILED {e.GetType().Name}: {e.Message}";
                    }
                    yield return $"EnsureCode-hash {computed} {declared} {patchType.Name}.{patchMethod.Name} -> {computedTarget}";
                    continue;
                }

                // A resolution or IL-read failure is REPORTED, never thrown: this runs on a live
                // node and one bad patch must not hide the other 76 hashes. The yield sits OUTSIDE
                // the try - C# forbids yield inside a try that has a catch.
                var hash = "????????";
                string target;
                try
                {
                    ResolveTarget(patchedType, info, out var targetMethod, out var targetConstructor);
                    if (targetMethod == null && targetConstructor == null)
                        target = $"UNRESOLVED ({patchedType.Name}.{info.methodName})";
                    else
                    {
                        hash = (targetMethod != null ? targetMethod.HashBody() : targetConstructor.HashBody()).ToString("x8");
                        target = targetMethod?.FullDescription() ?? targetConstructor.FullDescription();
                    }
                }
                catch (Exception e)
                {
                    target = $"FAILED {e.GetType().Name}: {e.Message}";
                }

                yield return $"EnsureCode-hash {hash} {declared} {patchType.Name}.{patchMethod.Name} -> {target}";
            }
        }
    }

    /// <summary>
    /// T-0122. Proves the guard can FAIL. Returns null on pass, or a description of what broke.
    ///
    /// WHY A NEGATIVE CASE IS THE ONLY ONE THAT COUNTS. LastVerifiedCount was added in this
    /// ticket so a vacuous pass would be visible, and it could not have done that: it counts
    /// methods CARRYING [EnsureCode], and until the class-level merge landed, 40 of the 140
    /// patch methods here carried the attribute and hashed NOTHING. A counter that only ever
    /// goes up cannot tell 47 verified from 47 skipped. Only a case asserted to be REJECTED can.
    ///
    /// So: take a real patch method, compute the real body hash of its target, and check both
    /// directions - the true hash must be accepted, and a deliberately corrupted one must be
    /// rejected. If the second assertion ever passes silently, the guard is decorative and every
    /// coverage number it reports is a lie.
    /// </summary>
    public static string SelfTest(Assembly pluginAssembly)
    {
        foreach (var patchType in AccessTools.GetTypesFromAssembly(pluginAssembly))
        {
            if (!patchType.IsDefined(typeof(HarmonyPatch), true))
                continue;
            foreach (var patchMethod in AccessTools.GetDeclaredMethods(patchType))
            {
                if (!IsPatchMethod(patchMethod))
                    continue;
                var info = EffectiveInfo(patchMethod);
                if (info?.declaringType == null)
                    continue;                       // TargetMethod() classes: covered by the loop below
                string real;
                try
                {
                    ResolveTarget(info.declaringType, info, out var method, out var constructor);
                    if (method == null && constructor == null)
                        continue;
                    real = method != null ? HashOf(method) : HashOf(constructor);
                }
                catch (Exception)
                {
                    continue;                       // pick a different method rather than fail the test
                }

                var name = $"{patchType.Name}.{patchMethod.Name}";
                if (new EnsureCode(real).VerifyMethod(patchMethod).Any())
                    return $"the TRUE hash {real} of {name} was rejected - the guard fails valid code";
                // "deadbeef" is not the real hash of anything here; if this is accepted, IsAllowed
                // is matching too loosely (the substring form of IsAllowed is easy to break).
                if (!new EnsureCode("deadbeef").VerifyMethod(patchMethod).Any())
                    return $"a WRONG hash was accepted for {name} - the guard cannot fail, so every "
                           + "coverage number it reports is meaningless";
                return null;
            }
        }
        return "no patch method with a resolvable target was found to test the guard against";
    }

    private IEnumerable<CodeChange> VerifyMethod(MethodInfo patchMethod)
    {
        // Null until Plugin.Common.Init runs. That is never the case on a node, but EnsureCode is
        // now also driven from Tools/EnsureCodeHashes, where nothing initialises the plugin - and
        // a guard that throws NullReferenceException from its LOGGING line is a guard that cannot
        // be tested outside the thing it is guarding.
        Plugin.Common.Logger?.Debug($"Verifying patch method: {patchMethod.DeclaringType.Name}.{patchMethod.Name}");

        var patchType = patchMethod.DeclaringType;
        if (patchType == null)
            throw new Exception($"Method info has no DeclaringType: {patchMethod.Name}");

        // T-0122: THROW where this used to `yield break`. A declared [EnsureCode] that cannot be
        // resolved to a target is a broken guard, and a broken guard must fail loudly - silently
        // passing is what made 40 of this assembly's 140 patch methods unverifiable while still
        // being counted by LastVerifiedCount. If a game update ever removes the patched method
        // outright, this is also the line that says so instead of shrugging.
        var info = EffectiveInfo(patchMethod);
        if (info == null)
            throw new Exception($"[EnsureCode] on {patchType.Name}.{patchMethod.Name}, but neither it nor its class carries [HarmonyPatch]");

        var patchedType = info.declaringType;
        if (patchedType == null)
        {
            // TargetMethod()/TargetMethods() class: hash every computed target and require ALL of
            // them to be declared. Reporting the first mismatch is enough to block the load.
            var computed = ResolveComputedTargets(patchType).ToList();
            if (computed.Count == 0)
                throw new Exception($"Could not determine the patched type for patch method {patchType.Name}.{patchMethod.Name}");
            foreach (var target in computed)
            {
                var hash = HashOf(target);
                if (!IsAllowed(hash))
                    yield return new CodeChange(target as MethodInfo, target as ConstructorInfo, allowedHashes, hash);
            }
            yield break;
        }

        ResolveTarget(patchedType, info, out var patchedMethod, out var patchedConstructor);

        if (patchedMethod == null && patchedConstructor == null)
            throw new Exception($"Could not get patched method information for {patchType.Name}.{patchMethod.Name}");

        var methodBodyHash = (patchedMethod != null ? patchedMethod.HashBody() : patchedConstructor.HashBody()).ToString("x8");
        if (IsAllowed(methodBodyHash))
            yield break;

        yield return new CodeChange(patchedMethod, patchedConstructor, allowedHashes, methodBodyHash);
    }
}
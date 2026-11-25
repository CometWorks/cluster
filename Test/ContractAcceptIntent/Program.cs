using ClusterPlugin;

var folder = Directory.CreateTempSubdirectory();
try
{
    string intent = Path.Combine(folder.FullName, "one.intent");
    const string contents = """{"Decision":"92e7973e-0262-433a-9060-9d1bb41f727f"}""";
    File.WriteAllText(intent, contents);
    File.WriteAllText(Path.Combine(folder.FullName, "incomplete.tmp"), "");
    int reservations = 0;
    Task<bool> Reserve()
    {
        reservations++;
        return Task.FromResult(true);
    }

    var first = ContractAcceptIntentFile.Pending(folder.FullName).Single();
    if (first != intent || ContractAcceptIntentFile.IsReleasing(first))
        throw new InvalidOperationException("Expected the original pending intent");
    if (await ContractAcceptIntentFile.PrepareRelease(intent, () => Task.FromResult(false)) != null)
        throw new InvalidOperationException("A refused reservation advanced to release");

    string release = await ContractAcceptIntentFile.PrepareRelease(intent, Reserve);
    if (
        release != Path.ChangeExtension(intent, ".release")
        || File.Exists(intent)
        || File.ReadAllText(release) != contents
    )
        throw new InvalidOperationException("The release phase must retain the original decision");

    var recovered = ContractAcceptIntentFile.Pending(folder.FullName).Single();
    if (
        !ContractAcceptIntentFile.IsReleasing(recovered)
        || await ContractAcceptIntentFile.PrepareRelease(recovered, Reserve) != release
        || reservations != 1
    )
        throw new InvalidOperationException("Recovery must replay only the release");
    Console.WriteLine(
        "PASS: a resumed release uses the original decision and does not request a second reservation."
    );
}
finally
{
    folder.Delete(recursive: true);
}

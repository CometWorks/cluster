using System.Text;

namespace ClusterRegistry;

/// <summary>
/// Prefixes every console line with a wall-clock time.
/// </summary>
/// <remarks>
/// The gateway log was the only log in the cluster without timestamps, which
/// made it impossible to correlate a handover with anything happening at the
/// same moment elsewhere - notably whether pilot failures cluster around
/// handovers, a question the w16 campaign could not answer for exactly this
/// reason (findings/ejections-in-gravity-destroy-the-ship.md).  Stamping at the
/// writer keeps every existing Console.WriteLine call site untouched.
///
/// HH:mm:ss matches the rotation and endurance logs so the three can be read
/// and joined together.  Only the first line of a multi-line write is stamped,
/// which leaves stack traces indented under their own stamped header.
/// </remarks>
internal sealed class TimestampedWriter(TextWriter inner) : TextWriter
{
    private readonly Lock gate = new();
    private bool atLineStart = true;

    public override Encoding Encoding => inner.Encoding;

    private void Stamp()
    {
        if (!atLineStart) return;
        inner.Write(DateTime.Now.ToString("HH:mm:ss "));
        atLineStart = false;
    }

    public override void Write(char value)
    {
        lock (gate)
        {
            Stamp();
            inner.Write(value);
            if (value == '\n') atLineStart = true;
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (gate)
        {
            Stamp();
            inner.Write(value);
            if (value.EndsWith('\n')) atLineStart = true;
        }
    }

    public override void WriteLine(string? value)
    {
        lock (gate)
        {
            Stamp();
            inner.WriteLine(value);
            atLineStart = true;
        }
    }

    public override void Flush() => inner.Flush();
}

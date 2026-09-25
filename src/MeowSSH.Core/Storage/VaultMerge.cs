using MeowSSH.Core.Model;

namespace MeowSSH.Core.Storage;

/// <summary>
/// Folds another phone's copy of the same vault into this one, record by record.
/// </summary>
/// <remarks>
/// <para>
/// Each host and credential is decided on its own: the copy edited last wins,
/// and a deletion is an edit like any other (its tombstone carries the time),
/// so deleting a host on one phone removes it everywhere rather than being
/// resurrected by the next phone that still has it. Whole-vault last-writer-wins
/// would be simpler and would throw away every edit the losing phone made.
/// </para>
/// <para>
/// The rule must give the same answer on every phone, whichever copy it calls
/// "local", or two phones would each keep their own version forever. So the
/// comparison is a total order over (edit time, per-record revision, origin
/// device); only a record identical on all three -- one edit, seen twice --
/// falls through, and then either copy is the same edit.
/// </para>
/// <para>
/// Edit time comes first rather than revision: a phone that renamed a host
/// five times offline should not beat a later single edit made elsewhere just
/// by having counted higher.
/// </para>
/// </remarks>
public static class VaultMerge
{
    /// <param name="local">The vault open on this phone.</param>
    /// <param name="remote">Another phone's copy of the same vault.</param>
    /// <param name="deviceId">
    /// This install's id. A vault restored from another phone still carries
    /// that phone's id; adopting this one here means later edits are
    /// attributed to the phone that actually made them.
    /// </param>
    /// <returns>The merged document, and whether it differs from <paramref name="local"/>.</returns>
    public static (VaultDocument Merged, bool Changed) Merge(VaultDocument local, VaultDocument remote, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        var (hosts, hostsChanged) = MergeRecords(local.Hosts, remote.Hosts, static h => h.Id, static h => Stamp(h.UpdatedAt, h.Revision, h.OriginDeviceId));
        var (credentials, credentialsChanged) = MergeRecords(local.Credentials, remote.Credentials, static c => c.Id, static c => Stamp(c.UpdatedAt, c.Revision, c.OriginDeviceId));
        var changed = hostsChanged || credentialsChanged || !string.Equals(local.DeviceId, deviceId, StringComparison.Ordinal);

        return (local with
        {
            DeviceId = deviceId,
            // Never behind either copy: each section's tag is bound to the
            // revision, and a merged vault must still read as newer than both.
            Revision = Math.Max(local.Revision, remote.Revision),
            // Wrapped keys stay this phone's own. The other phone's device-key
            // copy is sealed to hardware this phone does not have, and a
            // recovery key rotated elsewhere must not silently replace the one
            // this phone's cloud credential was derived from.
            WrappedKeys = local.WrappedKeys,
            Hosts = hosts,
            Credentials = credentials,
        }, changed);
    }

    /// <summary>True when <paramref name="candidate"/> should replace <paramref name="current"/>.</summary>
    internal static bool Supersedes(RecordStamp candidate, RecordStamp current)
    {
        var byTime = candidate.UpdatedAt.CompareTo(current.UpdatedAt);
        if (byTime != 0) return byTime > 0;
        var byRevision = candidate.Revision.CompareTo(current.Revision);
        if (byRevision != 0) return byRevision > 0;
        return string.CompareOrdinal(candidate.OriginDeviceId ?? "", current.OriginDeviceId ?? "") > 0;
    }

    internal readonly record struct RecordStamp(DateTimeOffset UpdatedAt, long Revision, string? OriginDeviceId);

    private static RecordStamp Stamp(DateTimeOffset updatedAt, long revision, string? originDeviceId) =>
        new(updatedAt, revision, originDeviceId);

    private static (T[] Records, bool Changed) MergeRecords<T>(
        IReadOnlyList<T> local,
        IReadOnlyList<T> remote,
        Func<T, Guid> id,
        Func<T, RecordStamp> stamp)
    {
        var remoteById = new Dictionary<Guid, T>(remote.Count);
        foreach (var record in remote) remoteById[id(record)] = record;

        var changed = false;
        var merged = new List<T>(Math.Max(local.Count, remote.Count));
        foreach (var record in local)
        {
            if (remoteById.Remove(id(record), out var theirs) && Supersedes(stamp(theirs), stamp(record)))
            {
                merged.Add(theirs);
                changed = true;
            }
            else
            {
                merged.Add(record);
            }
        }

        // Records only the other phone has, in its order.
        foreach (var record in remote)
        {
            if (!remoteById.ContainsKey(id(record))) continue;
            merged.Add(record);
            changed = true;
        }

        return ([.. merged], changed);
    }
}

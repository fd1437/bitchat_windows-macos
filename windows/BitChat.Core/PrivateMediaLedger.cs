using System.Text.Json;

namespace BitChat.Core;

public enum MediaDecisionKind { Absent, Accepted, Deleted, Unavailable }
public sealed record MediaDecision(MediaDecisionKind Kind, string? Path = null, string? Name = null, string? Mime = null);

// Receiver decisions survive process restart independently of the visible
// timeline. Production supplies DPAPI protection; tests use a disposable root.
public sealed class PrivateMediaLedger
{
    private sealed record Record(bool Deleted, string? Payload, long At, string? Name = null, string? Mime = null);
    private sealed record Deletion(string[] Payloads, long At);
    private readonly object sync = new();
    private readonly string root, directory, journalPath;
    private readonly Func<byte[], byte[]> protect, unprotect;
    private readonly Action<string, byte[]> write;
    private readonly Func<long> now;
    private readonly int capacity;
    private readonly long ttl;
    private Dictionary<string, Record>? records;
    private readonly HashSet<string> quarantined = [];
    private Dictionary<string, Deletion> journal = [];

    public PrivateMediaLedger(string mediaRoot, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect,
        Action<string, byte[]>? atomicWrite = null, Func<long>? clock = null, int capacity = 4096, long ttlMs = 7L * 86400000)
    {
        root = Path.GetFullPath(mediaRoot); directory = Path.Combine(root, ".receipts");
        journalPath = Path.Combine(directory, ".deletions");
        this.protect = protect; this.unprotect = unprotect; write = atomicWrite ?? AtomicWrite;
        now = clock ?? (() => Bytes.Now); this.capacity = capacity; ttl = ttlMs;
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private string RecordPath(string id) => Path.Combine(directory, id + ".receipt");
    private string? PayloadPath(string? leaf)
    {
        // Every encrypted payload is a fresh UUID. Neither a receipt nor a
        // deletion journal may name an arbitrary file or traverse directories.
        if (leaf == null || leaf.Length != 38 || !leaf.EndsWith(".dpapi", StringComparison.Ordinal)
            || !Guid.TryParseExact(leaf[..32], "N", out _)) return null;
        string path = Path.Combine(root, leaf);
        if (File.Exists(path) && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
        return path;
    }
    private void Save<T>(string path, T value) => write(path, protect(JsonSerializer.SerializeToUtf8Bytes(value)));
    private T Read<T>(string path) => JsonSerializer.Deserialize<T>(unprotect(File.ReadAllBytes(path)))
        ?? throw new InvalidDataException("Invalid private-media receipt");

    private bool Load()
    {
        if (records != null) return true;
        try
        {
            Directory.CreateDirectory(directory);
            var loaded = new Dictionary<string, Record>(); quarantined.Clear();
            var files = Directory.GetFiles(directory);
            foreach (string path in files.Where(p => p.EndsWith(".receipt.corrupt", StringComparison.Ordinal)))
            { string id = Path.GetFileName(path)[..^16]; if (PrivateMediaIdentity.IsStable(id)) quarantined.Add(id); }
            foreach (string path in files.Where(p => p.EndsWith(".receipt", StringComparison.Ordinal)))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (!PrivateMediaIdentity.IsStable(id) || quarantined.Contains(id)) continue;
                try
                {
                    var record = Read<Record>(path);
                    if (!record.Deleted && PayloadPath(record.Payload) == null || record.Deleted && record.Payload != null)
                        throw new InvalidDataException("Unsafe receipt path");
                    loaded[id] = record;
                }
                catch
                {
                    quarantined.Add(id);
                    try { File.Move(path, path + ".corrupt", true); } catch { /* The ID stays fail-closed. */ }
                }
            }
            var pending = File.Exists(journalPath) ? Read<Dictionary<string, Deletion>>(journalPath) : [];
            if (pending.Count > capacity || pending.Any(p => !PrivateMediaIdentity.IsStable(p.Key)
                || p.Value.Payloads.Length > 2 || p.Value.Payloads.Any(s => PayloadPath(s) == null))) return false;
            journal = pending; records = loaded; Recover(); Prune(); return true;
        }
        catch { records = null; return false; }
    }

    private void Prune()
    {
        foreach (var (id, record) in records!.ToArray())
        {
            if (journal.ContainsKey(id) || now() - record.At <= ttl) continue;
            if (!record.Deleted && PayloadPath(record.Payload) is string path && File.Exists(path)) continue;
            try { File.Delete(RecordPath(id)); records!.Remove(id); } catch { }
        }
    }
    private void Recover()
    {
        var remaining = new Dictionary<string, Deletion>(journal);
        foreach (var (id, deletion) in journal)
        {
            try
            {
                var tombstone = new Record(true, null, deletion.At);
                if (!quarantined.Contains(id)) { Save(RecordPath(id), tombstone); records![id] = tombstone; }
                foreach (string leaf in deletion.Payloads)
                { string path = PayloadPath(leaf) ?? throw new InvalidDataException("Unsafe deletion path"); File.Delete(path); }
                remaining.Remove(id);
            }
            catch { /* The journal remains the authoritative deletion decision. */ }
        }
        if (remaining.Count == journal.Count) return;
        try
        {
            if (remaining.Count == 0) File.Delete(journalPath); else Save(journalPath, remaining);
            journal = remaining;
        }
        catch { }
    }

    public MediaDecision Get(string id)
    {
        lock (sync)
        {
            if (!PrivateMediaIdentity.IsStable(id)) return new(MediaDecisionKind.Absent);
            if (!Load()) return new(MediaDecisionKind.Unavailable);
            Recover(); Prune();
            if (journal.ContainsKey(id)) return new(MediaDecisionKind.Deleted);
            if (quarantined.Contains(id)) return new(MediaDecisionKind.Unavailable);
            if (!records!.TryGetValue(id, out var record)) return new(MediaDecisionKind.Absent);
            if (record.Deleted) return new(MediaDecisionKind.Deleted);
            string? path = PayloadPath(record.Payload);
            return path != null && File.Exists(path) ? new(MediaDecisionKind.Accepted, path, record.Name, record.Mime) : new(MediaDecisionKind.Absent);
        }
    }

    public MediaDecision Accept(string id, MediaPacket media, Func<MediaPacket, string> savePayload)
    {
        lock (sync)
        {
            var current = Get(id);
            if (!PrivateMediaIdentity.IsStable(id) || current.Kind != MediaDecisionKind.Absent) return current;
            if (records!.Values.Count(r => !r.Deleted) >= capacity && !records.ContainsKey(id)) return new(MediaDecisionKind.Unavailable);
            string? saved = null;
            try
            {
                saved = Path.GetFullPath(savePayload(media));
                string leaf = Path.GetFileName(saved);
                if (PayloadPath(leaf) != saved || !File.Exists(saved)
                    || records.Any(r => r.Key != id && r.Value.Payload == leaf)
                    || journal.Values.Any(r => r.Payloads.Contains(leaf))) throw new InvalidDataException("Unreserved media path required");
                var record = new Record(false, leaf, now(), media.Name, media.Mime);
                Save(RecordPath(id), record); records[id] = record;
                return new(MediaDecisionKind.Accepted, saved, media.Name, media.Mime);
            }
            catch
            {
                if (saved != null && PayloadPath(Path.GetFileName(saved)) == saved
                    && !records.Values.Any(r => r.Payload == Path.GetFileName(saved)))
                    try { File.Delete(saved); } catch { }
                return new(MediaDecisionKind.Unavailable);
            }
        }
    }

    public bool Delete(IReadOnlyDictionary<string, string?> messages)
    {
        lock (sync)
        {
            if (!Load()) return false;
            var batch = new Dictionary<string, Deletion>(journal);
            foreach (var (id, path) in messages)
            {
                if (!PrivateMediaIdentity.IsStable(id)) return false;
                var paths = new HashSet<string>();
                if (records!.TryGetValue(id, out var record) && record.Payload != null) paths.Add(record.Payload);
                if (path != null)
                {
                    string leaf = Path.GetFileName(path);
                    if (PayloadPath(leaf) != Path.GetFullPath(path)) return false;
                    paths.Add(leaf);
                }
                if (batch.TryGetValue(id, out var pending)) paths.UnionWith(pending.Payloads);
                if (paths.Count > 2) return false;
                batch[id] = new(paths.ToArray(), now());
            }
            if (batch.Count > capacity) return false;
            try { Save(journalPath, batch); } catch { return false; }
            journal = batch; Recover(); Prune();
            // Tombstones have their own budget. Accepted records are admission
            // controlled and never evicted while their payload still exists.
            foreach (var old in records!.Where(p => p.Value.Deleted && !journal.ContainsKey(p.Key))
                .OrderByDescending(p => p.Value.At).Skip(capacity).ToArray())
                try { File.Delete(RecordPath(old.Key)); records!.Remove(old.Key); } catch { }
            return true;
        }
    }
}

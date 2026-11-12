// SPDX-License-Identifier: GPL-2.0-or-later
// Bounded, asynchronous, current-user LAB audit log.
// Only low-frequency connection/lifecycle events. No screenshots, keys,
// passwords, tokens, certificate PFXs or per-frame inspection data.
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabAuditWriter : IAsyncDisposable
{
    private sealed record Entry(string TimestampUtc, string Type, string Detail);
    private readonly Channel<Entry> _queue;
    private readonly Task _worker;
    private readonly string _folder;
    private readonly int _maxBytes;
    private readonly int _backups;
    private long _drops, _writeErrors;
    internal long Dropped => Interlocked.Read(ref _drops);
    internal long WriteErrors => Interlocked.Read(ref _writeErrors);
    internal string LogFile => Path.Combine(_folder, "access.jsonl");

    internal LabAuditWriter(string? folder = null, int maxBytes = 2 * 1024 * 1024,
                            int backups = 3)
    {
        if (maxBytes < 512 || backups is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
        _backups = backups;
        _folder = folder ?? Path.Combine(LabCertificates.ConfigDirectory, "Audit");
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(256) {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessAsync);
    }

    internal bool TryRecord(string kind, string detail)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 32 ||
            kind.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Audit event name is invalid.", nameof(kind));
        string text = new string((detail ?? "").Take(320)
            .Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        var ok = _queue.Writer.TryWrite(
            new Entry(DateTimeOffset.UtcNow.ToString("O"), kind, text));
        if (!ok) Interlocked.Increment(ref _drops);
        return ok;
    }

    private async Task ProcessAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(_folder);
                string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
                int bytes = Encoding.UTF8.GetByteCount(line);
                if (bytes >= _maxBytes)
                    throw new InvalidDataException("Audit entry too long.");
                if (File.Exists(LogFile) &&
                    new FileInfo(LogFile).Length + bytes > _maxBytes)
                    Rotate();
                await File.AppendAllTextAsync(LogFile, line, new UTF8Encoding(false))
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException)
            {
                Interlocked.Increment(ref _writeErrors);
            }
        }
    }

    private void Rotate()
    {
        string oldest = LogFile + "." + _backups;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int i = _backups - 1; i >= 1; i--)
        {
            string source = LogFile + "." + i;
            if (File.Exists(source))
                File.Move(source, LogFile + "." + (i + 1));
        }
        File.Move(LogFile, LogFile + ".1");
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }
}

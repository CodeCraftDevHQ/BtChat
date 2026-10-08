using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BtChat;

// What the user picked when the two devices disagree about encryption.
public enum LocalChoice
{
    EnableEncryption,
    ContinuePlain,
    Cancel
}

public sealed class SecurityPrompts
{
    // This device has encryption OFF, the other one ON. The argument is the other device's id.
    public required Func<string, Task<LocalChoice>> LocalOff { get; init; }
    // This device has encryption ON, the other one chose to continue without it: confirm that.
    public required Func<string, Task<bool>> LocalOnConfirmPlain { get; init; }
    // The user chose "turn on encryption" in the prompt: switch the setting on for good.
    public required Action EnableEncryption { get; init; }
}

public sealed class SecurityResult
{
    public required Stream Stream { get; init; }
    public bool Encrypted { get; init; }
    public bool Cancelled { get; init; }
    // Localization key of the reason, when the connection was not set up.
    public string? FailKey { get; init; }
    // True when this device's user is the one who said no (so auto-reconnect should stop).
    public bool CancelledLocally { get; init; }
    public string PeerId { get; init; } = "";
    // Short fingerprint of the other device's identity key (only when encrypted).
    public string? PeerFingerprint { get; init; }
    // The raw identity key of the other device (only when encrypted), for later verification.
    public byte[]? PeerIdentityKey { get; init; }
}

// This device's long-term identity: an ECDSA P-256 key made once and kept in the app's private folder.
// It only signs the handshake, so the other side can tell who it is talking to; messages use fresh keys every time.
public static class DeviceIdentity
{
    static readonly object gate = new();
    static ECDsa? key;

    static string KeyPath => Path.Combine(FileSystem.AppDataDirectory, "identity.key");

    static ECDsa Key
    {
        get
        {
            lock (gate)
            {
                if (key != null) return key;
                var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                try
                {
                    if (File.Exists(KeyPath))
                    {
                        created.ImportPkcs8PrivateKey(File.ReadAllBytes(KeyPath), out _);
                        AppLog.Write("SEC", "identity key loaded");
                    }
                    else
                    {
                        File.WriteAllBytes(KeyPath, created.ExportPkcs8PrivateKey());
                        AppLog.Write("SEC", "identity key created");
                    }
                }
                catch (Exception ex)
                {
                    // A damaged key file: make a new identity rather than leaving encryption unusable.
                    AppLog.Error("SEC", "identity key unreadable, creating a new one", ex);
                    created.Dispose();
                    created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    try { File.WriteAllBytes(KeyPath, created.ExportPkcs8PrivateKey()); }
                    catch (Exception ex2) { AppLog.Error("SEC", "saving identity key failed", ex2); }
                }
                key = created;
                return key;
            }
        }
    }

    public static byte[] PublicKey => Key.ExportSubjectPublicKeyInfo();

    public static byte[] Sign(byte[] data) => Key.SignData(data, HashAlgorithmName.SHA256);

    public static string Fingerprint(byte[] publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        var hex = Convert.ToHexString(hash.AsSpan(0, 16));
        var sb = new StringBuilder();
        for (var i = 0; i < hex.Length; i += 4)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(hex, i, 4);
        }
        return sb.ToString();
    }

    public static string LocalFingerprint => Fingerprint(PublicKey);
}

// Encrypts everything that goes through a stream (Bluetooth or Wi-Fi) with AES-256-GCM.
// Data travels in records: [length u32][ciphertext][16-byte tag]. Every direction has its own key,
// and the record counter is the nonce, so a record can neither be changed, replayed nor reordered.
public sealed class SecureStream : Stream
{
    const int MaxPlain = 60 * 1024;
    const int TagSize = 16;

    readonly Stream inner;
    readonly AesGcm sendKey;
    readonly AesGcm receiveKey;
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly byte[] header = new byte[4];
    readonly byte[] sendNonce = new byte[12];
    readonly byte[] receiveNonce = new byte[12];
    ulong sendCounter;
    ulong receiveCounter;
    byte[] plain = new byte[MaxPlain];
    int plainPos;
    int plainLen;
    bool disposed;

    public SecureStream(Stream inner, byte[] sendKeyBytes, byte[] receiveKeyBytes)
    {
        this.inner = inner;
        sendKey = new AesGcm(sendKeyBytes, TagSize);
        receiveKey = new AesGcm(receiveKeyBytes, TagSize);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    static void MakeNonce(byte[] nonce, ulong counter) =>
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        if (plainPos >= plainLen && !await FillAsync(ct)) return 0;
        var n = Math.Min(buffer.Length, plainLen - plainPos);
        plain.AsMemory(plainPos, n).CopyTo(buffer);
        plainPos += n;
        return n;
    }

    // Reads and decrypts the next record. False = the other side closed the connection cleanly between records.
    async Task<bool> FillAsync(CancellationToken ct)
    {
        var got = 0;
        while (got < header.Length)
        {
            var n = await inner.ReadAsync(header.AsMemory(got), ct);
            if (n == 0)
            {
                if (got == 0) return false;
                throw new EndOfStreamException("connection closed in the middle of a record");
            }
            got += n;
        }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length < TagSize || length > MaxPlain + TagSize) throw new InvalidDataException("bad record size");
        var cipher = new byte[length];
        await inner.ReadExactlyAsync(cipher, ct);
        var textLength = (int)length - TagSize;
        MakeNonce(receiveNonce, receiveCounter);
        receiveKey.Decrypt(receiveNonce, cipher.AsSpan(0, textLength), cipher.AsSpan(textLength, TagSize), plain.AsSpan(0, textLength));
        receiveCounter++;
        plainPos = 0;
        plainLen = textLength;
        return true;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            var done = 0;
            while (done < data.Length)
            {
                var n = Math.Min(MaxPlain, data.Length - done);
                var record = new byte[4 + n + TagSize];
                BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(n + TagSize));
                MakeNonce(sendNonce, sendCounter);
                sendKey.Encrypt(sendNonce, data.Span.Slice(done, n), record.AsSpan(4, n), record.AsSpan(4 + n, TagSize));
                sendCounter++;
                // One write per record: on Bluetooth two small writes are noticeably slower than one.
                await inner.WriteAsync(record, ct);
                done += n;
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            inner.Dispose();
            sendKey.Dispose();
            receiveKey.Dispose();
        }
        base.Dispose(disposing);
    }
}

public static class SecureLink
{
    const byte MsgHello = 1;
    const byte MsgDecision = 2;
    const byte MsgKeyShare = 3;
    const byte MsgAuth = 4;
    const byte Version = 1;
    static readonly byte[] Magic = "BTSEC"u8.ToArray();
    static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);
    // The other device's user may need time to answer a question.
    static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(120);

    const byte DecisionCancel = 0;
    const byte DecisionEncrypt = 1;
    const byte DecisionPlain = 2;

    // Both devices tell each other whether they want encryption. Same answer = go on. Different answers:
    // the device with encryption OFF is asked first (turn it on, continue without, or cancel); if it picks
    // "continue without", the device with encryption ON has to agree too, so nobody is downgraded silently.
    public static async Task<SecurityResult> NegotiateAsync(Stream raw, bool inbound, bool wantEncryption, string name, SecurityPrompts prompts)
    {
        await WriteMessageAsync(raw, MsgHello, BuildHello(wantEncryption));
        var (type, payload) = await ReadMessageAsync(raw, StepTimeout);
        if (type != MsgHello || !TryParseHello(payload, out var peerWantsEncryption, out var peerId))
        {
            AppLog.Write("SEC", $"{name} peer did not answer the security hello (old version?)");
            return Failed(raw, "secUnsupported", "");
        }
        AppLog.Write("SEC", $"{name} security hello: local={(wantEncryption ? "on" : "off")} peer={(peerWantsEncryption ? "on" : "off")}");

        var encrypt = wantEncryption;
        if (wantEncryption != peerWantsEncryption)
        {
            if (!wantEncryption)
            {
                var choice = await prompts.LocalOff(peerId);
                if (choice == LocalChoice.Cancel)
                {
                    await TrySendDecisionAsync(raw, DecisionCancel);
                    return Failed(raw, "secCancelled", peerId, localCancel: true);
                }
                if (choice == LocalChoice.EnableEncryption)
                {
                    prompts.EnableEncryption();
                    await WriteMessageAsync(raw, MsgDecision, new[] { DecisionEncrypt });
                    encrypt = true;
                }
                else
                {
                    await WriteMessageAsync(raw, MsgDecision, new[] { DecisionPlain });
                    var answer = await ReadDecisionAsync(raw);
                    if (answer != DecisionPlain) return Failed(raw, "secPeerCancelled", peerId);
                    encrypt = false;
                }
            }
            else
            {
                var answer = await ReadDecisionAsync(raw);
                if (answer == DecisionCancel) return Failed(raw, "secPeerCancelled", peerId);
                if (answer == DecisionEncrypt)
                {
                    encrypt = true;
                }
                else
                {
                    var agree = await prompts.LocalOnConfirmPlain(peerId);
                    await TrySendDecisionAsync(raw, agree ? DecisionPlain : DecisionCancel);
                    if (!agree) return Failed(raw, "secCancelled", peerId, localCancel: true);
                    encrypt = false;
                }
            }
        }

        if (!encrypt)
        {
            AppLog.Write("SEC", $"{name} continuing without encryption");
            return new SecurityResult { Stream = raw, Encrypted = false, PeerId = peerId };
        }
        return await HandshakeAsync(raw, inbound, name, peerId);
    }

    static async Task<SecurityResult> HandshakeAsync(Stream raw, bool inbound, string name, string peerId)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeral = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var identity = DeviceIdentity.PublicKey;
        var nonce = RandomNumberGenerator.GetBytes(32);
        var myShare = BuildKeyShare(ephemeral, identity, nonce);

        await WriteMessageAsync(raw, MsgKeyShare, myShare);
        var (type, peerShare) = await ReadMessageAsync(raw, StepTimeout);
        if (type != MsgKeyShare || !TryParseKeyShare(peerShare, out var peerEphemeral, out var peerIdentity))
            return Failed(raw, "secFailed", peerId);

        // The transcript always lists the connecting side first, so both devices hash the same bytes.
        var clientShare = inbound ? peerShare : myShare;
        var serverShare = inbound ? myShare : peerShare;
        var transcript = SHA256.HashData(Concat(clientShare, serverShare));

        // Each side signs the transcript with its identity key and a label for its role (so a signature
        // can not be bounced back to its author).
        var myRole = inbound ? "server" : "client";
        var peerRole = inbound ? "client" : "server";
        await WriteMessageAsync(raw, MsgAuth, DeviceIdentity.Sign(SignedData(myRole, transcript)));
        var (authType, peerSignature) = await ReadMessageAsync(raw, StepTimeout);
        if (authType != MsgAuth) return Failed(raw, "secFailed", peerId);
        try
        {
            using var peerKey = ECDsa.Create();
            peerKey.ImportSubjectPublicKeyInfo(peerIdentity, out _);
            if (!peerKey.VerifyData(SignedData(peerRole, transcript), peerSignature, HashAlgorithmName.SHA256))
            {
                AppLog.Write("SEC", $"{name} peer signature is wrong");
                return Failed(raw, "secFailed", peerId);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("SEC", $"{name} checking the peer signature failed", ex);
            return Failed(raw, "secFailed", peerId);
        }

        byte[] secret;
        using (var peerEcdh = ECDiffieHellman.Create())
        {
            peerEcdh.ImportSubjectPublicKeyInfo(peerEphemeral, out _);
            secret = ecdh.DeriveRawSecretAgreement(peerEcdh.PublicKey);
        }
        var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, transcript, "BtChat v1 session keys"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        var clientToServer = keys.AsSpan(0, 32).ToArray();
        var serverToClient = keys.AsSpan(32, 32).ToArray();
        CryptographicOperations.ZeroMemory(keys);

        var stream = new SecureStream(raw, inbound ? serverToClient : clientToServer, inbound ? clientToServer : serverToClient);
        var fingerprint = DeviceIdentity.Fingerprint(peerIdentity);
        AppLog.Write("SEC", $"{name} encrypted, peer key {fingerprint}");
        return new SecurityResult
        {
            Stream = stream,
            Encrypted = true,
            PeerId = peerId,
            PeerFingerprint = fingerprint,
            PeerIdentityKey = peerIdentity
        };
    }

    static SecurityResult Failed(Stream raw, string key, string peerId, bool localCancel = false)
    {
        raw.Dispose();
        return new SecurityResult { Stream = raw, Cancelled = true, FailKey = key, CancelledLocally = localCancel, PeerId = peerId };
    }

    static byte[] SignedData(string role, byte[] transcript) =>
        Concat(Encoding.ASCII.GetBytes("BtChat v1 auth " + role + "\n"), transcript);

    static byte[] Concat(byte[] a, byte[] b)
    {
        var all = new byte[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all;
    }

    static byte[] BuildHello(bool wantEncryption)
    {
        var id = Encoding.UTF8.GetBytes(LocalDevice.Id);
        var payload = new byte[Magic.Length + 3 + id.Length];
        Magic.CopyTo(payload, 0);
        payload[Magic.Length] = Version;
        payload[Magic.Length + 1] = (byte)(wantEncryption ? 1 : 0);
        payload[Magic.Length + 2] = (byte)id.Length;
        id.CopyTo(payload, Magic.Length + 3);
        return payload;
    }

    static bool TryParseHello(byte[] payload, out bool wantsEncryption, out string peerId)
    {
        wantsEncryption = false;
        peerId = "";
        if (payload.Length < Magic.Length + 3 || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return false;
        if (payload[Magic.Length] != Version) return false;
        wantsEncryption = payload[Magic.Length + 1] == 1;
        var idLength = payload[Magic.Length + 2];
        if (payload.Length != Magic.Length + 3 + idLength) return false;
        peerId = Encoding.UTF8.GetString(payload, Magic.Length + 3, idLength);
        return true;
    }

    static byte[] BuildKeyShare(byte[] ephemeral, byte[] identity, byte[] nonce)
    {
        var payload = new byte[2 + ephemeral.Length + 2 + identity.Length + nonce.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)ephemeral.Length);
        ephemeral.CopyTo(payload, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2 + ephemeral.Length), (ushort)identity.Length);
        identity.CopyTo(payload, 4 + ephemeral.Length);
        nonce.CopyTo(payload, 4 + ephemeral.Length + identity.Length);
        return payload;
    }

    static bool TryParseKeyShare(byte[] payload, out byte[] ephemeral, out byte[] identity)
    {
        ephemeral = Array.Empty<byte>();
        identity = Array.Empty<byte>();
        if (payload.Length < 4) return false;
        int ephemeralLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (payload.Length < 4 + ephemeralLength) return false;
        int identityLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2 + ephemeralLength));
        if (payload.Length != 4 + ephemeralLength + identityLength + 32) return false;
        ephemeral = payload.AsSpan(2, ephemeralLength).ToArray();
        identity = payload.AsSpan(4 + ephemeralLength, identityLength).ToArray();
        return true;
    }

    static async Task<byte> ReadDecisionAsync(Stream raw)
    {
        var (type, payload) = await ReadMessageAsync(raw, AnswerTimeout);
        if (type != MsgDecision || payload.Length != 1) throw new InvalidDataException("unexpected message instead of a decision");
        return payload[0];
    }

    static async Task TrySendDecisionAsync(Stream raw, byte decision)
    {
        try
        {
            await WriteMessageAsync(raw, MsgDecision, new[] { decision });
        }
        catch (Exception ex)
        {
            AppLog.Error("SEC", "sending the decision failed", ex);
        }
    }

    // Setup messages: [type u8][length u16][payload]
    static async Task WriteMessageAsync(Stream stream, byte type, byte[] payload)
    {
        var message = new byte[3 + payload.Length];
        message[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(message, 3);
        await stream.WriteAsync(message);
        await stream.FlushAsync();
    }

    static async Task<(byte Type, byte[] Payload)> ReadMessageAsync(Stream stream, TimeSpan timeout)
    {
        var header = new byte[3];
        await ReadExactWithTimeoutAsync(stream, header, timeout);
        var payload = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
        if (payload.Length > 0) await ReadExactWithTimeoutAsync(stream, payload, timeout);
        return (header[0], payload);
    }

    // Bluetooth streams do not always honor cancellation, so a stuck read is ended by closing the stream.
    static async Task ReadExactWithTimeoutAsync(Stream stream, byte[] buffer, TimeSpan timeout)
    {
        var read = stream.ReadExactlyAsync(buffer).AsTask();
        var finished = await Task.WhenAny(read, Task.Delay(timeout));
        if (finished != read)
        {
            _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            stream.Dispose();
            throw new TimeoutException("no answer during the security setup");
        }
        await read;
    }
}
